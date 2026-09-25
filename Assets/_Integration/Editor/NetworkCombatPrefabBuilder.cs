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
        const string GameScenePath = "Assets/Scenes/Game.unity";

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

        public static void EnsureVfxRelayOnGameManager(bool force = false)
        {
            if (!File.Exists(GameScenePath))
                return;

            if (!force)
            {
                // Cheap check: if scene YAML already mentions NetworkVfxRelay script guid after we know meta.
                // Always open if forced; otherwise skip when play mode upcoming.
            }

            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isDirty)
                    return;
            }

            var scene = EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);
            var manager = Object.FindFirstObjectByType<AvocadoShark.InGame_Manager>();
            if (manager == null)
                return;

            var relay = manager.GetComponent<NetworkVfxRelay>();
            if (relay == null)
            {
                relay = manager.gameObject.AddComponent<NetworkVfxRelay>();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[CollarCali] Added NetworkVfxRelay to Game Manager.");
            }

            var bootstrap = manager.GetComponent<NetworkCombatBootstrap>();
            if (bootstrap == null)
                bootstrap = manager.gameObject.AddComponent<NetworkCombatBootstrap>();

            var pickupPrefab = EnsurePickupPrefab();
            if (pickupPrefab != null)
            {
                var so = new SerializedObject(bootstrap);
                so.FindProperty("networkWeaponPickupPrefab").objectReferenceValue = pickupPrefab;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            var netObj = manager.GetComponent<NetworkObject>();
            if (netObj != null)
                EditorUtility.SetDirty(netObj);
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
