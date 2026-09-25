using System.Collections.Generic;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// One-shot and looping gameplay sound, pooled.
    ///
    /// Shaped after ZombieBloodFx on purpose. Play() is local to this machine; PlayShared() plays
    /// here immediately and tells every other client to play it too. That distinction matters
    /// because enemy brains only run on the master client - a zombie's swing fired with Play()
    /// alone would be silent on everyone else's screen.
    ///
    /// Sounds that already happen on every machine (a bolt flying, blood spraying) use Play(),
    /// since they are each driven by their own local copy of the effect.
    /// </summary>
    public static class GameSfx
    {
        const string LibraryResource = "GameSfxLibrary";

        /// <summary>Generous: a horde plus hazards plus UI. Voices are recycled oldest-first.</summary>
        const int PoolSize = 28;

        static SfxLibrary _library;
        static bool _resolved;

        static Transform _root;
        static readonly List<SfxVoice> _voices = new List<SfxVoice>(PoolSize);

        /// <summary>Last clip index handed out per id, so a variation never repeats back to back.</summary>
        static readonly Dictionary<SfxId, int> _lastVariation = new Dictionary<SfxId, int>();

        /// <summary>Throttle keyed by id plus the instance that asked, so two zombies do not gate each other.</summary>
        static readonly Dictionary<long, float> _nextAllowed = new Dictionary<long, float>();

        public static SfxLibrary Library
        {
            get
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _library = Resources.Load<SfxLibrary>(LibraryResource);
                    if (_library == null)
                    {
                        Debug.LogWarning("[CollarCali] GameSfxLibrary missing from Resources - " +
                                         "gameplay audio is off. Run Tools/CollarCali/Build SFX Library.");
                    }
                }

                return _library;
            }
        }

        /// <summary>
        /// Statics outlive a play-mode session in the editor, so everything cached here has to be
        /// dropped before the next run or the pool would point at destroyed objects.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState()
        {
            _library = null;
            _resolved = false;
            _root = null;
            _voices.Clear();
            _lastVariation.Clear();
            _nextAllowed.Clear();
            _listener = null;
            _occluderMask = -1;
        }

        #region Public API

        /// <summary>Plays on this machine only, at a fixed world position.</summary>
        public static void Play(SfxId id, Vector3 position)
        {
            PlayLocal(id, position, null, 1f);
        }

        /// <summary>
        /// Plays on this machine only, following a moving object. Use this for anything on a body
        /// that keeps walking while the sound runs.
        /// </summary>
        public static void Play(SfxId id, Transform follow)
        {
            if (follow == null)
                return;
            PlayLocal(id, follow.position, follow, 1f);
        }

        /// <summary>
        /// Heard at full volume wherever the listener is. For UI, and for feedback about the local
        /// player themselves - being hurt, being grabbed - which should not fade with distance.
        /// </summary>
        public static void Play2D(SfxId id)
        {
            PlayLocal(id, Vector3.zero, null, 1f, force2D: true);
        }

        /// <summary>
        /// Plays here and on every other client. Local-first so whoever caused it hears it on the
        /// same frame rather than a round trip later.
        ///
        /// <paramref name="follow"/> is also how remote clients re-attach the sound to the right
        /// object: its hierarchy path is hashed into the same key NetworkWorldActor binds by, so a
        /// zombie's groan tracks that zombie on every machine instead of being stuck where it stood.
        /// </summary>
        public static void PlayShared(SfxId id, Vector3 position, Transform follow = null)
        {
            PlayLocal(id, position, follow, 1f);

#if CMPSETUP_COMPLETE
            var relay = NetworkVfxRelay.Instance;
            if (relay == null)
                return;

            int key = follow != null ? NetworkWorldActor.MakeKey(follow) : 0;
            relay.BroadcastSfx((int)id, position, key);
#endif
        }

        /// <summary>Remote entry point, called from the relay RPC. Never re-broadcasts.</summary>
        public static void PlayFromNetwork(SfxId id, Vector3 position, int sceneKey)
        {
            Transform follow = null;

            if (sceneKey != 0)
            {
                var source = NetworkWorldActor.FindSourceByKey(sceneKey);
                if (source != null)
                    follow = source.transform;
            }

            PlayLocal(id, position, follow, 1f);
        }

        /// <summary>
        /// Starts a looping sound attached to an object. Keep the handle and Stop() it - a loop
        /// whose owner is destroyed stops on its own, but only after a frame of noise.
        /// </summary>
        public static SfxLoop PlayLoop(SfxId id, Transform follow)
        {
            var entry = Library != null ? Library.Find(id) : null;
            if (entry == null || follow == null)
                return default;

            var voice = TakeVoice(entry, respectVoiceCap: false);
            if (voice == null)
                return default;

            var clip = PickClip(id, entry);
            voice.BeginLoop(clip, entry, Library.masterVolume, follow);
            return new SfxLoop(voice);
        }

        /// <summary>
        /// Plays a clip this system does not own, through the same pool.
        ///
        /// For audio that already exists elsewhere in the project and should not be duplicated - a
        /// remote player's gunshot, which uses that weapon's own Cowsins fire clip so a teammate's
        /// rifle sounds exactly like your own.
        /// </summary>
        public static void PlayClip(AudioClip clip, Vector3 position, float volume,
            float pitchJitter, float minDistance, float maxDistance)
        {
            if (clip == null)
                return;

            EnsureRoot();

            _scratch.id = SfxId.None;
            _scratch.volume = volume;
            _scratch.pitchMin = 1f - Mathf.Abs(pitchJitter);
            _scratch.pitchMax = 1f + Mathf.Abs(pitchJitter);
            _scratch.spatialBlend = 1f;
            _scratch.minDistance = minDistance;
            _scratch.maxDistance = maxDistance;
            _scratch.loop = false;

            var voice = TakeVoice(_scratch, respectVoiceCap: false);
            if (voice == null)
                return;

            voice.PlayOnce(clip, _scratch, Library != null ? Library.masterVolume : 1f,
                null, position, force2D: false);
        }

        /// <summary>
        /// Reused rather than allocated per shot: PlayOnce reads these fields immediately and keeps
        /// no reference to the entry, so one instance is enough for every ad-hoc clip.
        /// </summary>
        static readonly SfxLibrary.Entry _scratch = new SfxLibrary.Entry();

        /// <summary>
        /// True at most once per <paramref name="seconds"/> for a given owner and id. For sounds
        /// driven from Update or a coroutine that ticks far more often than the sound should fire.
        /// </summary>
        public static bool Ready(SfxId id, Object owner, float seconds)
        {
            long key = MakeThrottleKey(id, owner);
            float now = Time.unscaledTime;
            if (_nextAllowed.TryGetValue(key, out float next) && now < next)
                return false;

            _nextAllowed[key] = now + Mathf.Max(0f, seconds);
            return true;
        }

        #endregion

        #region Playback

        static void PlayLocal(SfxId id, Vector3 position, Transform follow, float volumeScale,
            bool force2D = false)
        {
            var library = Library;
            if (library == null)
                return;

            var entry = library.Find(id);
            if (entry == null)
                return;

            // Throttled against the object that owns the sound, so a crowd is not gated as one.
            Object owner = follow != null ? (Object)follow : null;
            if (entry.minRepeatSeconds > 0f && !Ready(id, owner, entry.minRepeatSeconds))
                return;

            var voice = TakeVoice(entry, respectVoiceCap: true);
            if (voice == null)
                return;

            var clip = PickClip(id, entry);
            voice.PlayOnce(clip, entry, library.masterVolume * volumeScale,
                force2D ? null : follow, force2D ? ListenerPosition() : position, force2D);
        }

        /// <summary>
        /// Random, but never the clip that played last. With three or four variations, plain
        /// Random.Range repeats often enough to be noticeable on a sound as frequent as a groan.
        /// </summary>
        static AudioClip PickClip(SfxId id, SfxLibrary.Entry entry)
        {
            var clips = entry.clips;
            if (clips.Length == 1)
                return clips[0];

            _lastVariation.TryGetValue(id, out int last);

            int index = Random.Range(0, clips.Length);
            if (index == last)
                index = (index + 1) % clips.Length;

            // A null slot in the middle of the array must not silence the event.
            for (int i = 0; i < clips.Length && clips[index] == null; i++)
                index = (index + 1) % clips.Length;

            _lastVariation[id] = index;
            return clips[index];
        }

        static SfxVoice TakeVoice(SfxLibrary.Entry entry, bool respectVoiceCap)
        {
            EnsureRoot();

            if (respectVoiceCap && entry.maxVoices > 0 && CountPlaying(entry.id) >= entry.maxVoices)
                return null;

            for (int i = 0; i < _voices.Count; i++)
            {
                if (_voices[i] != null && _voices[i].IsFree)
                    return _voices[i];
            }

            if (_voices.Count < PoolSize)
            {
                var created = SfxVoice.Create(_root);
                _voices.Add(created);
                return created;
            }

            // Pool exhausted: steal the oldest one-shot rather than dropping the new sound. Loops
            // are skipped - silently killing a laser hum is worse than losing one footstep.
            SfxVoice oldest = null;
            for (int i = 0; i < _voices.Count; i++)
            {
                var voice = _voices[i];
                if (voice == null || voice.IsLooping)
                    continue;
                if (oldest == null || voice.StartedAt < oldest.StartedAt)
                    oldest = voice;
            }

            return oldest;
        }

        static int CountPlaying(SfxId id)
        {
            int count = 0;
            for (int i = 0; i < _voices.Count; i++)
            {
                if (_voices[i] != null && !_voices[i].IsFree && _voices[i].CurrentId == id)
                    count++;
            }
            return count;
        }

        static void EnsureRoot()
        {
            if (_root != null)
                return;

            var go = new GameObject("[CollarCali SFX]");
            Object.DontDestroyOnLoad(go);
            _root = go.transform;

            // Deliberately does NOT add an AudioListener when a scene has none. This root is
            // persistent, and the local player - who brings their own listener - spawns later in a
            // session, so a helpful one added here would still be around to become a second
            // listener and drown Unity in warnings. Every scene that ships has one already.
        }

        static Vector3 ListenerPosition()
        {
            var listener = Object.FindFirstObjectByType<AudioListener>();
            return listener != null ? listener.transform.position : Vector3.zero;
        }

        static long MakeThrottleKey(SfxId id, Object owner)
        {
            long ownerId = owner != null ? owner.GetInstanceID() : 0;
            return ((long)id << 32) ^ (ownerId & 0xFFFFFFFFL);
        }

        #endregion

        #region Occlusion

        /// <summary>
        /// How much of a sound survives a wall between it and the listener. Not zero on purpose:
        /// a zombie you can hear faintly through a wall is information, a zombie that vanishes the
        /// moment it steps behind one is a bug the player can hear.
        /// </summary>
        public static float OccludedVolume = 0.25f;

        /// <summary>Low-pass cutoff applied while blocked. Walls eat treble long before bass.</summary>
        public static float OccludedCutoff = 750f;

        /// <summary>Effectively open. Above ~20 kHz the filter is doing nothing.</summary>
        public const float OpenCutoff = 22000f;

        /// <summary>Seconds between line-of-sight tests per voice. Staggered across voices.</summary>
        public static float OcclusionInterval = 0.12f;

        /// <summary>How fast a voice fades between blocked and open, to avoid audible stepping.</summary>
        public static float OcclusionLerpSpeed = 8f;

        static int _occluderMask = -1;

        /// <summary>
        /// What counts as a wall.
        ///
        /// Built as an exclusion list, the same way this project already builds its sight and ground
        /// masks: anything solid blocks sound except the things that are not architecture - players,
        /// enemies, items, effects and volumes. An allowlist of Default and Ground would miss every
        /// wall built on Object, Metal or Wood, which in a labyrinth is most of them.
        /// </summary>
        public static int OccluderMask()
        {
            if (_occluderMask != -1)
                return _occluderMask;

            int transparent = LayerMask.GetMask(
                "Ignore Raycast", "TransparentFX", "UI", "UITop", "Water",
                "Weapons", "Player", "Enemy", "Animal", "Item", "PostProcessing", "Effects");
            _occluderMask = ~transparent;
            return _occluderMask;
        }

        /// <summary>Cached listener, re-resolved when it goes away (scene change, player respawn).</summary>
        static Transform _listener;

        public static Transform Listener
        {
            get
            {
                if (_listener == null)
                {
                    var found = Object.FindFirstObjectByType<AudioListener>();
                    _listener = found != null ? found.transform : null;
                }
                return _listener;
            }
        }

        #endregion
    }

    /// <summary>Handle to a looping sound. Stop() is safe to call twice, or after the owner died.</summary>
    public readonly struct SfxLoop
    {
        readonly SfxVoice _voice;
        readonly int _generation;

        internal SfxLoop(SfxVoice voice)
        {
            _voice = voice;
            _generation = voice != null ? voice.Generation : 0;
        }

        public bool IsPlaying => _voice != null && _voice.Generation == _generation && !_voice.IsFree;

        public void Stop()
        {
            // The generation check is what stops a stale handle from cutting off whatever sound
            // has since been given that pooled voice.
            if (_voice != null && _voice.Generation == _generation)
                _voice.Release();
        }
    }

    /// <summary>
    /// One pooled AudioSource. Follows a target itself rather than being parented to it, because a
    /// parented voice is destroyed along with the object it was following - which would tear holes
    /// in the pool every time a zombie's corpse despawned mid-groan.
    /// </summary>
    public class SfxVoice : MonoBehaviour
    {
        AudioSource _source;
        AudioLowPassFilter _lowPass;
        Transform _follow;
        bool _looping;
        float _releaseAt;

        /// <summary>Volume before occlusion, so the two can be combined without drift.</summary>
        float _baseVolume = 1f;

        /// <summary>1 = clear line to the listener, 0 = fully blocked. Lerped, never stepped.</summary>
        float _openness = 1f;
        bool _occludable;
        float _nextOcclusionCheck;

        public SfxId CurrentId { get; private set; }
        public float StartedAt { get; private set; }

        /// <summary>Bumped on every claim so old SfxLoop handles cannot stop the wrong sound.</summary>
        public int Generation { get; private set; }

        public bool IsLooping => _looping;

        public bool IsFree =>
            _source == null || (!_looping && (!_source.isPlaying || Time.unscaledTime >= _releaseAt));

        public static SfxVoice Create(Transform parent)
        {
            var go = new GameObject("SfxVoice");
            go.transform.SetParent(parent, false);
            var voice = go.AddComponent<SfxVoice>();
            voice._source = go.AddComponent<AudioSource>();
            voice._source.playOnAwake = false;
            // Always present, parked wide open. Adding it per play would mean an allocation on a
            // sound that may last 300 ms.
            voice._lowPass = go.AddComponent<AudioLowPassFilter>();
            voice._lowPass.cutoffFrequency = GameSfx.OpenCutoff;
            return voice;
        }

        public void PlayOnce(AudioClip clip, SfxLibrary.Entry entry, float masterVolume,
            Transform follow, Vector3 position, bool force2D)
        {
            if (clip == null)
                return;

            Claim(entry, masterVolume, force2D);
            CurrentId = entry.id;
            _follow = follow;
            _looping = false;
            transform.position = follow != null ? follow.position : position;

            // Evaluated before Play so a blocked one-shot is already muffled on its first sample.
            ApplyOcclusion(immediate: true);

            _source.clip = clip;
            _source.loop = false;
            _source.Play();

            // isPlaying alone is not enough to reclaim a voice: it also reads false for a frame
            // right after Play(), which handed the same source out twice.
            StartedAt = Time.unscaledTime;
            _releaseAt = StartedAt + Mathf.Max(0.05f, clip.length / Mathf.Max(0.01f, _source.pitch));
        }

        public void BeginLoop(AudioClip clip, SfxLibrary.Entry entry, float masterVolume, Transform follow)
        {
            if (clip == null)
                return;

            Claim(entry, masterVolume, force2D: false);
            CurrentId = entry.id;
            _follow = follow;
            _looping = true;
            transform.position = follow != null ? follow.position : transform.position;

            ApplyOcclusion(immediate: true);

            _source.clip = clip;
            _source.loop = true;
            _source.Play();
            StartedAt = Time.unscaledTime;
            _releaseAt = float.MaxValue;
        }

        public void Release()
        {
            _looping = false;
            _follow = null;
            _releaseAt = 0f;
            if (_source != null)
            {
                _source.Stop();
                _source.clip = null;
            }
        }

        void Claim(SfxLibrary.Entry entry, float masterVolume, bool force2D)
        {
            Generation++;
            _source.Stop();
            _baseVolume = Mathf.Clamp(entry.volume * masterVolume, 0f, 1f);
            _source.volume = _baseVolume;
            _source.pitch = Random.Range(entry.pitchMin, entry.pitchMax);
            _source.spatialBlend = force2D ? 0f : entry.spatialBlend;
            _source.rolloffMode = AudioRolloffMode.Logarithmic;
            _source.minDistance = entry.minDistance;
            _source.maxDistance = entry.maxDistance;
            _source.dopplerLevel = 0f;

            // Only sounds that live in the world can be muffled by it. A 2D sound is either UI or
            // something happening to the player themselves, and walls have no business touching it.
            _occludable = !force2D && entry.spatialBlend >= 0.5f;
            _openness = 1f;
            _nextOcclusionCheck = 0f;
            _lowPass.cutoffFrequency = GameSfx.OpenCutoff;
        }

        /// <summary>
        /// Line of sight to the listener, turned into volume and a low-pass cutoff.
        ///
        /// <paramref name="immediate"/> snaps instead of lerping, which is what a one-shot needs: a
        /// 300 ms squelch fired from behind a wall is over before any fade could get there, so it
        /// has to start muffled or it may as well not be occluded at all.
        /// </summary>
        void ApplyOcclusion(bool immediate)
        {
            if (!_occludable)
                return;

            var listener = GameSfx.Listener;
            if (listener == null)
                return;

            float target = _openness;
            if (immediate || Time.unscaledTime >= _nextOcclusionCheck)
            {
                // Staggered so a burst of voices started on one frame do not all cast together
                // forever afterwards.
                _nextOcclusionCheck = Time.unscaledTime +
                                      GameSfx.OcclusionInterval * Random.Range(0.85f, 1.15f);

                var from = transform.position;
                var to = listener.position;

                // Anything close enough to be inside its own full-volume radius is treated as open:
                // it is in the room with you, and a stray pillar should not gate it.
                bool near = (to - from).sqrMagnitude < _source.minDistance * _source.minDistance;
                bool blocked = !near && Physics.Linecast(from, to, GameSfx.OccluderMask(),
                    QueryTriggerInteraction.Ignore);

                target = blocked ? 0f : 1f;
            }

            _openness = immediate
                ? target
                : Mathf.MoveTowards(_openness, target, GameSfx.OcclusionLerpSpeed * Time.deltaTime);

            float muffle = Mathf.Lerp(GameSfx.OccludedVolume, 1f, _openness);
            _source.volume = _baseVolume * muffle;
            _lowPass.cutoffFrequency =
                Mathf.Lerp(GameSfx.OccludedCutoff, GameSfx.OpenCutoff, _openness);
        }

        void LateUpdate()
        {
            if (_follow == null)
            {
                // The object went away. A loop has nothing left to track, so end it rather than
                // leaving a hum stuck at the last place a corpse stood.
                if (_looping)
                {
                    Release();
                    return;
                }
            }
            else
            {
                transform.position = _follow.position;
            }

            // Kept up while anything is audible, not just while following something: a one-shot
            // played at a fixed point still needs its occlusion maintained if the LISTENER moves,
            // which in a corridor happens constantly.
            if (!IsFree)
                ApplyOcclusion(immediate: false);
        }
    }
}
