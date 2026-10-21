#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Finds the project's scenes and assets by name rather than by a fixed path, for the editor
    /// tools. Folders get reorganised - in October 2026 the scenes moved from Assets/Scenes to
    /// Assets/A_StayInRange/Scenes - and a tool holding a fixed path silently stops working
    /// when they do.
    /// </summary>
    public static class ProjectPaths
    {
        /// <summary>
        /// The scene called <paramref name="name"/> ("Game", "Lobby", "1"), or null when the
        /// project has none. A scene in Build Settings wins over a same-named one elsewhere, such
        /// as an asset pack's demo.
        /// </summary>
        public static string Scene(string name)
        {
            string disabled = null;
            foreach (var entry in EditorBuildSettings.scenes)
            {
                if (entry == null || !IsNamed(entry.path, name) ||
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(entry.path) == null)
                    continue;
                if (entry.enabled)
                    return entry.path;
                disabled ??= entry.path;
            }

            return disabled ?? Asset(name, "Scene");
        }

        /// <summary>
        /// The asset of <paramref name="type"/> ("Prefab", "Scene") whose file is called
        /// <paramref name="name"/>, or null when there is none.
        /// </summary>
        public static string Asset(string name, string type)
        {
            var matches = AssetDatabase.FindAssets("t:" + type + " " + name)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => IsNamed(path, name))
                .Distinct()
                .OrderBy(path => path, System.StringComparer.Ordinal)
                .ToList();

            if (matches.Count > 1)
                Debug.LogWarning("[CollarCali] More than one " + type.ToLowerInvariant() + " is called '" + name +
                                 "'; using " + matches[0] + ". Also found: " + string.Join(", ", matches.Skip(1)));

            return matches.Count > 0 ? matches[0] : null;
        }

        /// <summary>
        /// The folder Unity keeps a scene's own data in (NavMesh, lighting): beside the scene,
        /// named after it. Null when <paramref name="scenePath"/> is.
        /// </summary>
        public static string SceneDataFolder(string scenePath)
        {
            return scenePath == null ? null : Path.ChangeExtension(scenePath, null).Replace('\\', '/');
        }

        static bool IsNamed(string path, string name)
        {
            return !string.IsNullOrEmpty(path) && Path.GetFileNameWithoutExtension(path) == name;
        }
    }
}
#endif
