#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Rebuilds GameSfxLibrary.asset from the clips on disk.
    ///
    /// The mapping lives in Assets/Audio/SFX/sfx_manifest.json next to the clips, written by the
    /// generator that produced them. Keeping it in a manifest rather than a table in here means
    /// filenames can stay descriptive without this tool having to guess which event
    /// "Zombie_Attack_Swing_02.wav" belongs to, and adding a variation is a one-line edit followed
    /// by this menu item.
    ///
    /// Also sets the import settings every clip wants, so a hand-dropped file behaves like a
    /// generated one.
    /// </summary>
    public static class SfxLibraryBuilder
    {
        const string SfxRoot = "Assets/Audio/SFX";
        const string ManifestPath = SfxRoot + "/sfx_manifest.json";
        const string LibraryPath = "Assets/_Integration/Resources/GameSfxLibrary.asset";

        [MenuItem("Tools/CollarCali/Build SFX Library")]
        public static void Build()
        {
            var manifest = LoadManifest();
            if (manifest == null)
                return;

            var library = AssetDatabase.LoadAssetAtPath<SfxLibrary>(LibraryPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<SfxLibrary>();
                AssetDatabase.CreateAsset(library, LibraryPath);
            }

            var entries = new List<SfxLibrary.Entry>(manifest.events.Length);
            var missing = new List<string>();
            int clipCount = 0;

            foreach (var row in manifest.events)
            {
                if (!System.Enum.TryParse(row.id, out SfxId id) || id == SfxId.None)
                {
                    missing.Add("unknown id '" + row.id + "'");
                    continue;
                }

                var clips = new List<AudioClip>(row.files.Length);
                foreach (var file in row.files)
                {
                    string path = SfxRoot + "/" + file;
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                    if (clip == null)
                    {
                        missing.Add(path);
                        continue;
                    }

                    ApplyImportSettings(path, row.spatialBlend >= 0.5f);
                    clips.Add(clip);
                    clipCount++;
                }

                if (clips.Count == 0)
                    continue;

                entries.Add(new SfxLibrary.Entry
                {
                    id = id,
                    clips = clips.ToArray(),
                    volume = row.volume,
                    pitchMin = row.pitchMin,
                    pitchMax = row.pitchMax,
                    spatialBlend = row.spatialBlend,
                    minDistance = row.minDistance,
                    maxDistance = row.maxDistance,
                    loop = row.loop,
                    minRepeatSeconds = row.minRepeatSeconds,
                    maxVoices = row.maxVoices,
                });
            }

            library.masterVolume = manifest.masterVolume <= 0f ? 1f : manifest.masterVolume;
            library.entries = entries.ToArray();
            library.Rebuild();

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[CollarCali] SFX library rebuilt: " + entries.Count + " events, " +
                      clipCount + " clips.");

            if (missing.Count > 0)
            {
                Debug.LogWarning("[CollarCali] SFX entries the manifest asked for but could not be " +
                                 "found:\n - " + string.Join("\n - ", missing));
            }

            ReportUnreferenced(manifest);
        }

        /// <summary>
        /// Short one-shots are cheapest fully decompressed in memory; anything positioned in the
        /// world is forced to mono, because a stereo clip on a 3D source wastes half the data and
        /// pans oddly as you walk around it.
        /// </summary>
        static void ApplyImportSettings(string path, bool spatial)
        {
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null)
                return;

            // preloadAudioData lives on the sample settings in Unity 6, not on the importer - the
            // old importer-level property is now a compile error rather than a warning.
            var current = importer.defaultSampleSettings;
            var settings = current;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = 0.7f;
            settings.preloadAudioData = true;

            bool changed = importer.forceToMono != spatial
                           || current.loadType != settings.loadType
                           || current.compressionFormat != settings.compressionFormat
                           || !Mathf.Approximately(current.quality, settings.quality)
                           || current.preloadAudioData != settings.preloadAudioData;

            if (!changed)
                return;

            importer.forceToMono = spatial;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// Clips that belong to Cowsins rather than to this library.
        ///
        /// The weapon set is assigned directly into Weapon_SO.audioSFX, which Cowsins plays itself,
        /// so no SfxId points at those files and the orphan check below would report every one of
        /// them as dead weight.
        /// </summary>
        const string WeaponFolder = SfxRoot + "/Weapons";

        /// <summary>
        /// Catches the opposite mistake to a missing file: a clip sitting in the folder that no
        /// event points at, which would otherwise be silently dead weight in the build.
        /// </summary>
        static void ReportUnreferenced(Manifest manifest)
        {
            var referenced = new HashSet<string>();
            foreach (var row in manifest.events)
            {
                foreach (var file in row.files)
                    referenced.Add((SfxRoot + "/" + file).ToLowerInvariant());
            }

            var orphans = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { SfxRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith(WeaponFolder, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!referenced.Contains(path.ToLowerInvariant()))
                    orphans.Add(path);
            }

            if (orphans.Count > 0)
            {
                Debug.LogWarning("[CollarCali] Clips in " + SfxRoot + " that no event uses:\n - " +
                                 string.Join("\n - ", orphans));
            }
        }

        static Manifest LoadManifest()
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
            if (json == null)
            {
                Debug.LogError("[CollarCali] No SFX manifest at " + ManifestPath);
                return null;
            }

            var manifest = JsonUtility.FromJson<Manifest>(json.text);
            if (manifest == null || manifest.events == null || manifest.events.Length == 0)
            {
                Debug.LogError("[CollarCali] " + ManifestPath + " has no events.");
                return null;
            }

            return manifest;
        }

        #region Manifest shape

        [System.Serializable]
        class Manifest
        {
            public float masterVolume = 1f;
            public Row[] events;
        }

        [System.Serializable]
        class Row
        {
            public string id;
            public string[] files;
            public float volume = 1f;
            public float pitchMin = 0.95f;
            public float pitchMax = 1.05f;
            public float spatialBlend = 1f;
            public float minDistance = 3f;
            public float maxDistance = 35f;
            public bool loop;
            public float minRepeatSeconds = 0.06f;
            public int maxVoices;
        }

        #endregion
    }
}
#endif
