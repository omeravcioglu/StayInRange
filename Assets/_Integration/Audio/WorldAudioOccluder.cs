using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Muffles a world AudioSource when something solid is between it and the listener.
    ///
    /// Unity does not occlude audio at all. A 3D source gets quieter with distance and nothing else,
    /// so a trap two rooms away is heard exactly as clearly as one in the open - which in a labyrinth
    /// is most of them. GameSfx does this for sounds it plays itself; this is the same treatment for
    /// sources it does not own, like the ones that ship inside the traps pack prefabs.
    ///
    /// Attached automatically at runtime by the installer below, so the pack's prefabs do not have to
    /// be edited to carry a component.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    public class WorldAudioOccluder : MonoBehaviour
    {
        AudioSource _source;
        AudioLowPassFilter _lowPass;

        float _baseVolume;
        float _openness = 1f;
        float _nextCheck;

        void Awake()
        {
            _source = GetComponent<AudioSource>();
            _baseVolume = _source.volume;

            _lowPass = GetComponent<AudioLowPassFilter>();
            if (_lowPass == null)
                _lowPass = gameObject.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = GameSfx.OpenCutoff;
        }

        /// <summary>
        /// Re-reads the authored volume. Called by the installer after it has clamped a source, so
        /// occlusion scales from the corrected level rather than the original one.
        /// </summary>
        public void RefreshBaseVolume()
        {
            if (_source != null)
                _baseVolume = _source.volume;
        }

        void LateUpdate()
        {
            if (_source == null || !_source.isPlaying)
                return;

            var listener = GameSfx.Listener;
            if (listener == null)
                return;

            if (Time.unscaledTime >= _nextCheck)
            {
                // Staggered per source, so a room full of traps does not cast on the same frame.
                _nextCheck = Time.unscaledTime +
                             GameSfx.OcclusionInterval * Random.Range(0.85f, 1.15f);

                var from = transform.position;
                var to = listener.position;

                // Anything inside its own full-volume radius counts as open - it is in the room with
                // you, and a pillar should not gate it.
                bool near = (to - from).sqrMagnitude < _source.minDistance * _source.minDistance;
                bool blocked = !near && Physics.Linecast(from, to, GameSfx.OccluderMask(),
                    QueryTriggerInteraction.Ignore);

                _target = blocked ? 0f : 1f;
            }

            _openness = Mathf.MoveTowards(_openness, _target,
                GameSfx.OcclusionLerpSpeed * Time.deltaTime);

            _source.volume = _baseVolume * Mathf.Lerp(GameSfx.OccludedVolume, 1f, _openness);
            _lowPass.cutoffFrequency =
                Mathf.Lerp(GameSfx.OccludedCutoff, GameSfx.OpenCutoff, _openness);
        }

        float _target = 1f;
    }

    /// <summary>
    /// Finds world AudioSources as scenes load and gives them occlusion, plus a sane ceiling on range
    /// and volume.
    ///
    /// Deliberately narrow about what it touches. It only takes SPATIAL sources - anything at
    /// spatialBlend below half is a 2D sound, which means UI, music or the player's own ambience, and
    /// forcing those into the world would break them. It also skips the player's own hierarchy and
    /// the GameSfx voice pool, which manages its own occlusion.
    /// </summary>
    public class WorldAudioInstaller : MonoBehaviour
    {
        /// <summary>Nothing in the level needs to be audible further than this.</summary>
        public static float MaxAudibleDistance = 22f;

        /// <summary>Ceiling on authored loudness for world sources.</summary>
        public static float MaxVolume = 0.65f;

        float _nextSweep;
        readonly HashSet<AudioSource> _seen = new HashSet<AudioSource>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Ensure();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

        static void Ensure()
        {
            if (FindFirstObjectByType<WorldAudioInstaller>() != null)
                return;

            var go = new GameObject("WorldAudioInstaller");
            DontDestroyOnLoad(go);
            go.AddComponent<WorldAudioInstaller>();
        }

        void Update()
        {
            if (Time.unscaledTime < _nextSweep)
                return;
            // Repeated rather than once per scene: traps and props are also spawned during play.
            _nextSweep = Time.unscaledTime + 2f;

            foreach (var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            {
                if (source == null || !_seen.Add(source))
                    continue;
                if (!ShouldManage(source))
                    continue;

                if (source.maxDistance > MaxAudibleDistance)
                    source.maxDistance = MaxAudibleDistance;
                if (source.volume > MaxVolume)
                    source.volume = MaxVolume;
                source.rolloffMode = AudioRolloffMode.Logarithmic;

                var occluder = source.GetComponent<WorldAudioOccluder>();
                if (occluder == null)
                    occluder = source.gameObject.AddComponent<WorldAudioOccluder>();
                occluder.RefreshBaseVolume();
            }
        }

        static bool ShouldManage(AudioSource source)
        {
            // 2D sounds are UI, music or the player's own ambience. Occluding those would be wrong.
            if (source.spatialBlend < 0.5f)
                return false;

            // GameSfx occludes its own pooled voices.
            if (source.GetComponent<SfxVoice>() != null)
                return false;

#if CMPSETUP_COMPLETE
            // The local player's own sources travel with the listener, so a wall can never be
            // between them and it - and testing would only waste a raycast.
            if (source.GetComponentInParent<FpsNetworkBridge>() != null)
                return false;
#endif
            if (source.GetComponentInParent<cowsins.PlayerMovement>() != null)
                return false;

            return true;
        }
    }
}
