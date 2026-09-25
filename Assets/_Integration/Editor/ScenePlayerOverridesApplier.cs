#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using cowsins;
using MalbersAnimations.Controller;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.Editor
{
    /// <summary>
    /// The Game scene tunes the FPS player and Malbers Steve on their scene instances rather than
    /// on the prefabs. The networked players are built from the prefab assets, so that tuning never
    /// reaches them. This pushes the component overrides down into each prefab.
    /// </summary>
    public static class ScenePlayerOverridesApplier
    {
        const string GameScenePath = "Assets/Scenes/Game.unity";

        [MenuItem("Tools/CollarCali/Apply Scene Player Tuning To Prefabs")]
        public static void Apply()
        {
            if (!TryGetGameScene(out var scene))
                return;

            var roots = new List<GameObject>();
            AddInstanceRoot(roots, FindComponentInScene<PlayerMovement>(scene));
            AddInstanceRoot(roots, FindSteve(scene));

            if (roots.Count == 0)
            {
                Debug.LogError("[CollarCali] Could not find the FPS or Steve prefab instance in Game.unity.");
                return;
            }

            int total = 0;
            foreach (var root in roots)
            {
                var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                if (string.IsNullOrEmpty(assetPath))
                    continue;

                int applied = ApplyComponentOverrides(root, assetPath);
                total += applied;
                Debug.Log($"[CollarCali] Applied {applied} override(s) from '{root.name}' to {assetPath}.");
            }

            if (total == 0)
            {
                Debug.Log("[CollarCali] Scene players already match their prefabs; nothing to apply.");
                return;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

#if CMPSETUP_COMPLETE
            NetworkedFpsPlayerPrefabBuilder.Rebuild();
#endif
        }

        static bool TryGetGameScene(out Scene scene)
        {
            scene = SceneManager.GetSceneByPath(GameScenePath);
            if (scene.isLoaded)
                return true;

            if (!EditorUtility.DisplayDialog(
                    "Apply Scene Player Tuning",
                    "Game.unity needs to be open. Open it now? You will be prompted to save the current scene.",
                    "Open Game.unity",
                    "Cancel"))
                return false;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return false;

            scene = EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);
            return scene.isLoaded;
        }

        static T FindComponentInScene<T>(Scene scene) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<T>(true);
                if (found != null)
                    return found;
            }

            return null;
        }

        static MAnimal FindSteve(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var animal in root.GetComponentsInChildren<MAnimal>(true))
                {
                    if (animal != null &&
                        animal.transform.root.name.IndexOf("Steve", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return animal;
                }
            }

            return null;
        }

        static void AddInstanceRoot(List<GameObject> roots, Component component)
        {
            if (component == null)
                return;

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(component.gameObject);
            if (root != null && !roots.Contains(root))
                roots.Add(root);
        }

        /// <summary>
        /// Applies overrides on components only. Transform and GameObject overrides (position, name,
        /// active state) stay on the scene instance, and modifications pointing at scene objects are
        /// skipped because a prefab asset cannot reference them.
        /// </summary>
        static int ApplyComponentOverrides(GameObject instanceRoot, string assetPath)
        {
            var modifications = PrefabUtility.GetPropertyModifications(instanceRoot);
            if (modifications == null)
                return 0;

            int applied = 0;

            foreach (var objectOverride in PrefabUtility.GetObjectOverrides(instanceRoot))
            {
                var component = objectOverride.instanceObject as Component;
                if (component == null || component is Transform)
                    continue;

                var source = PrefabUtility.GetCorrespondingObjectFromSource(component);
                if (source == null)
                    continue;

                var serialized = new SerializedObject(component);
                var paths = modifications
                    .Where(m => m.target == source && IsApplicable(m))
                    .Select(m => m.propertyPath)
                    .Distinct()
                    .ToList();

                foreach (var path in paths)
                {
                    var property = serialized.FindProperty(path);
                    if (property == null)
                        continue;

                    PrefabUtility.ApplyPropertyOverride(property, assetPath, InteractionMode.AutomatedAction);
                    applied++;
                }
            }

            return applied;
        }

        static bool IsApplicable(PropertyModification modification)
        {
            // A prefab asset cannot hold a reference to an object that only exists in the scene.
            if (modification.objectReference != null && !EditorUtility.IsPersistent(modification.objectReference))
                return false;

            // Editor-only inspector state; applying it just creates noise in the prefab diff.
            var path = modification.propertyPath;
            return !path.StartsWith("Editor_") &&
                   !path.StartsWith("Selected") &&
                   !path.EndsWith("Tabs") &&
                   !path.EndsWith("Tabs1") &&
                   !path.EndsWith("Tabs2");
        }
    }
}
#endif
