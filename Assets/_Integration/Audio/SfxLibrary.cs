using System.Collections.Generic;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Maps every SfxId onto its clips and playback settings.
    ///
    /// Lives in Resources so GameSfx can load it the same way ZombieBloodFx and WeaponFlashlight
    /// load theirs, while the clips themselves stay organised under Assets/Audio/SFX. Referencing
    /// them from here is also what pulls them into a build - a folder outside Resources is only
    /// included because something reachable points at it.
    ///
    /// Rebuild it from disk with Tools/CollarCali/Build SFX Library after adding or removing clips.
    /// </summary>
    [CreateAssetMenu(menuName = "CollarCali/Game SFX Library", fileName = "GameSfxLibrary")]
    public class SfxLibrary : ScriptableObject
    {
        [System.Serializable]
        public class Entry
        {
            public SfxId id = SfxId.None;

            [Tooltip("Variations. One is picked at random per play, never the same one twice in a row.")]
            public AudioClip[] clips = new AudioClip[0];

            [Range(0f, 2f)] public float volume = 1f;

            [Tooltip("Random pitch window. Widen it for sounds heard constantly, like footsteps.")]
            [Range(0.1f, 3f)] public float pitchMin = 0.95f;
            [Range(0.1f, 3f)] public float pitchMax = 1.05f;

            [Tooltip("0 = heard everywhere at full volume (UI, the local player), 1 = positioned in the world.")]
            [Range(0f, 1f)] public float spatialBlend = 1f;

            [Tooltip("Inside this radius the sound is at full volume.")]
            public float minDistance = 3f;

            [Tooltip("Beyond this radius it is silent. Keep enemy vocals well under the size of a room.")]
            public float maxDistance = 35f;

            public bool loop;

            [Tooltip("Ignores repeat requests on the same object within this many seconds. " +
                     "Stops a shotgun's pellets stacking one grunt per pellet.")]
            public float minRepeatSeconds = 0.06f;

            [Tooltip("Hard cap on copies of this sound playing at once across the whole game. " +
                     "0 means no limit. A horde of zombies is what this exists for.")]
            public int maxVoices;
        }

        [Tooltip("Scales every sound this system plays. Cowsins' own audio is untouched by it.")]
        [Range(0f, 2f)] public float masterVolume = 1f;

        public Entry[] entries = new Entry[0];

        Dictionary<SfxId, Entry> _byId;

        public Entry Find(SfxId id)
        {
            if (_byId == null)
                Rebuild();

            return _byId.TryGetValue(id, out var entry) && entry.clips != null && entry.clips.Length > 0
                ? entry
                : null;
        }

        /// <summary>Called on first lookup, and again by the editor tool after it rewrites entries.</summary>
        public void Rebuild()
        {
            _byId = new Dictionary<SfxId, Entry>(entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null || entry.id == SfxId.None)
                    continue;
                // First definition wins so a duplicated row cannot silently shadow a good one.
                if (!_byId.ContainsKey(entry.id))
                    _byId[entry.id] = entry;
            }
        }

        void OnValidate()
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null)
                    continue;
                if (entry.pitchMax < entry.pitchMin)
                    entry.pitchMax = entry.pitchMin;
                if (entry.maxDistance < entry.minDistance + 0.1f)
                    entry.maxDistance = entry.minDistance + 0.1f;
            }

            _byId = null;
        }
    }
}
