#if UNITY_EDITOR && CMPSETUP_COMPLETE
using System.IO;
using CollarCali;
using cowsins;
using Fusion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.Editor
{
    public static class NetworkCombatPrefabBuilder
    {
        const string GenericPickupPath = "Assets/Cowsins/Prefabs/DragAndDropExtras/WeaponPickeableGeneric.prefab";
        const string OutputDir = "Assets/_Integration/Prefabs";
        const string OutputPath = OutputDir + "/NetworkWeaponPickup.prefab";
        /// <summary>Found by name, wherever the scenes folder is; null when there is no Game scene.</summary>
        static string GameScenePath => ProjectPaths.Scene("Game");

        [InitializeOnLoadMethod]
        static void AutoBuild()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                EnsurePickupPrefab();
                // Do not open Game.unity on refresh — only patch it when that scene is already active.
                var active = SceneManager.GetActiveScene();
                if (active.IsValid() && active.path == GameScenePath)
                    EnsureVfxRelayOnGameManager();
            };
        }

        [MenuItem("Tools/CollarCali/Rebuild Network Weapon Pickup Prefab")]
        public static void RebuildFromMenu()
        {
            EnsurePickupPrefab(true);
            EnsureVfxRelayOnGameManager(true);
        }

        public static NetworkObject EnsurePickupPrefab(bool force = false)
        {
            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);

            if (!force)
            {
                var existing = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
                if (existing != null)
                {
                    var net = existing.GetComponent<NetworkObject>();
                    if (net != null && existing.GetComponent<NetworkWeaponPickup>() != null)
                        return net;
                }
            }

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(GenericPickupPath);
            if (source == null)
            {
                Debug.LogError("[CollarCali] Missing WeaponPickeableGeneric prefab.");
                return null;
            }

            var preview = EditorSceneManager.NewPreviewScene();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source, preview);
            instance.name = "NetworkWeaponPickup";

            if (instance.GetComponent<NetworkObject>() == null)
                instance.AddComponent<NetworkObject>();
            if (instance.GetComponent<NetworkTransform>() == null)
                instance.AddComponent<NetworkTransform>();
            if (instance.GetComponent<NetworkWeaponPickup>() == null)
                instance.AddComponent<NetworkWeaponPickup>();

            PrefabUtility.SaveAsPrefabAsset(instance, OutputPath);
            EditorSceneManager.ClosePreviewScene(preview);

            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
            if (saved != null)
            {
                // Only label after NetworkObject exists — broken FusionPrefab entries break all spawns.
                if (saved.GetComponent<NetworkObject>() != null)
                    AssetDatabase.SetLabels(saved, new[] { "FusionPrefab" });
                else
                    AssetDatabase.ClearLabels(saved);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(
                "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion",
                ImportAssetOptions.ForceUpdate);
            Debug.Log("[CollarCali] NetworkWeaponPickup prefab ready at " + OutputPath);
            return saved != null ? saved.GetComponent<NetworkObject>() : null;
        }

        /// <summary>
        /// Makes sure the Game scene's manager carries a NetworkVfxRelay and a NetworkCombatBootstrap
        /// pointing at the pickup prefab.
        ///
        /// The automatic check after a reload only runs with Game already open, so it works on that
        /// scene as it is, and saves only when it actually changed something. It used to re-open the
        /// whole scene from disk and save it after every reload while it was open - a long freeze after
        /// every script change.
        /// </summary>
        public static void EnsureVfxRelayOnGameManager(bool force = false)
        {
            var gamePath = GameScenePath;
            if (!File.Exists(gamePath))
                return;

            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isDirty)
                    return;
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.path != gamePath)
                scene = EditorSceneManager.OpenScene(gamePath, OpenSceneMode.Single);

            AvocadoShark.InGame_Manager manager = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                manager = root.GetComponentInChildren<AvocadoShark.InGame_Manager>(true);
                if (manager != null)
                    break;
            }
            if (manager == null)
                return;

            bool changed = false;

            if (manager.GetComponent<NetworkVfxRelay>() == null)
            {
                manager.gameObject.AddComponent<NetworkVfxRelay>();
                changed = true;
                Debug.Log("[CollarCali] Added NetworkVfxRelay to Game Manager.");
            }

            var bootstrap = manager.GetComponent<NetworkCombatBootstrap>();
            if (bootstrap == null)
            {
                bootstrap = manager.gameObject.AddComponent<NetworkCombatBootstrap>();
                changed = true;
            }

            var pickupPrefab = EnsurePickupPrefab();
            if (pickupPrefab != null)
            {
                var so = new SerializedObject(bootstrap);
                var slot = so.FindProperty("networkWeaponPickupPrefab");
                if (slot != null && slot.objectReferenceValue != pickupPrefab)
                {
                    slot.objectReferenceValue = pickupPrefab;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    changed = true;
                }
            }

            // Already set up, which is nearly always: nothing to save.
            if (!changed && !force)
                return;

            var netObj = manager.GetComponent<NetworkObject>();
            if (netObj != null)
                EditorUtility.SetDirty(netObj);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
