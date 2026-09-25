#if UNITY_EDITOR && CMPSETUP_COMPLETE
using System.IO;
using AvocadoShark;
using CollarCali;
using Fusion;
using Fusion.Editor;
using StarterAssets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali.Editor
{
    public static class NetworkedFpsPlayerPrefabBuilder
    {
        const string CmpPlayerPath = "Assets/Clean Multiplayer Pro/Prefabs/Physical/Player Prefab Red.prefab";
        // The Game scene's authored player. Stock CowsinsFPSController is a different controller
        // (different weapons, materials and movement settings) and must not be used here.
        const string CowsinsPlayerPath = "Assets/LabPrefabs/MovementCowsinsFPSController Variant.prefab";
        const string StevePlayerPath = "Assets/Malbers Animations/Animal Controller/Human/Steve Player.prefab";
        const string CamerasPath = "Assets/Malbers Animations/Common/Cinemachine/Cameras CM3.prefab";
        const string OutputDir = "Assets/_Integration/Prefabs";
        const string OutputPath = OutputDir + "/NetworkedFPSPlayer.prefab";
        const string CharacterSoPath = "Assets/Clean Multiplayer Pro/Scripts/Player/Character Selection/All Characters.asset";
        const string LocalDualPlayerPath = "Assets/_Integration/Resources/LocalDualPlayer.prefab";

        /// <summary>Nested FPS child is named after its source prefab so a stale build is detectable.</summary>
        static string ExpectedFpsName => Path.GetFileNameWithoutExtension(CowsinsPlayerPath);

        [InitializeOnLoadMethod]
        static void AutoBuildIfMissing()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
                if (prefab == null)
                {
                    Rebuild();
                    return;
                }

                var net = prefab.GetComponent<NetworkObject>();
                if (net == null)
                {
                    Rebuild();
                    return;
                }

                if (prefab.transform.Find(ExpectedFpsName) == null)
                {
                    Debug.Log("[NetworkedFPSPlayer] Built from a different FPS controller — rebuilding from " +
                              ExpectedFpsName + ".");
                    Rebuild();
                    return;
                }

                if (prefab.transform.Find(FpsNetworkBridge.DistanceOriginName) == null)
                {
                    Debug.Log("[NetworkedFPSPlayer] Missing " + FpsNetworkBridge.DistanceOriginName +
                              " anchor — rebuilding.");
                    Rebuild();
                    return;
                }

                if (prefab.GetComponent<NetworkMecanimAnimator>() != null)
                {
                    Debug.Log("[NetworkedFPSPlayer] Stale NetworkMecanimAnimator would fight the " +
                              "replicated animation parameters — rebuilding.");
                    Rebuild();
                    return;
                }

                var asset = AssetDatabase.LoadAssetAtPath<CharacterSO>(CharacterSoPath);
                if (asset == null || asset.characters == null || asset.characters.Count == 0)
                    return;

                bool needsAssign = false;
                foreach (var entry in asset.characters)
                {
                    if (entry.character == null || entry.character != net)
                    {
                        needsAssign = true;
                        break;
                    }
                }

                if (needsAssign)
                {
                    AssignCharacterSo(net);
                    AssetDatabase.ImportAsset(
                        "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion",
                        ImportAssetOptions.ForceUpdate);
                    Debug.Log("[NetworkedFPSPlayer] Reassigned CharacterSO → NetworkedFPSPlayer and rebuilt Fusion prefab table.");
                }
            };
        }

        [MenuItem("Tools/CollarCali/Rebuild Networked FPS Player Prefab")]
        public static void Rebuild()
        {
            var cmpPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CmpPlayerPath);
            var cowsinsPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CowsinsPlayerPath);
            if (cmpPrefab == null || cowsinsPrefab == null)
            {
                Debug.LogError("[NetworkedFPSPlayer] Missing CMP or Cowsins player prefab.");
                return;
            }

            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);

            var preview = EditorSceneManager.NewPreviewScene();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(cmpPrefab, preview);
            // Unpack so NetworkObject gets a stable local fileID (variants break CharacterSO refs).
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = "NetworkedFPSPlayer";

            StripVoiceAndThirdPersonGameplay(instance);
            var fps = (GameObject)PrefabUtility.InstantiatePrefab(cowsinsPrefab, instance.transform);
            PrefabUtility.UnpackPrefabInstance(fps, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            fps.name = ExpectedFpsName;
            fps.transform.localPosition = Vector3.zero;
            fps.transform.localRotation = Quaternion.identity;
            fps.transform.localScale = Vector3.one;

            var bridge = instance.GetComponent<FpsNetworkBridge>();
            if (bridge == null)
                bridge = instance.AddComponent<FpsNetworkBridge>();

            EnsureDistanceOrigin(instance);
            DeactivateOwnerOnlyRoots(fps);

            WireBridge(bridge, instance, fps);
            WireDualPlayer(instance, fps);

            PrefabUtility.SaveAsPrefabAsset(instance, OutputPath);
            EditorSceneManager.ClosePreviewScene(preview);

            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
            if (saved != null)
            {
                AssetDatabase.SetLabels(saved, new[] { "FusionPrefab" });
                AssignCharacterSo(saved.GetComponent<NetworkObject>());
            }

            SyncLocalDualPlayerSources();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(
                "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion",
                ImportAssetOptions.ForceUpdate);
            NetworkProjectConfigUtilities.RebuildPrefabTable();
            Debug.Log("[NetworkedFPSPlayer] Prefab saved to " + OutputPath);
        }

        [MenuItem("Tools/CollarCali/Fix Player Spawn (CharacterSO + Prefab Table)")]
        public static void FixPlayerSpawn()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
            if (prefab == null)
            {
                Rebuild();
                return;
            }

            var net = prefab.GetComponent<NetworkObject>();
            if (net == null)
            {
                Debug.LogError("[NetworkedFPSPlayer] Prefab has no NetworkObject — rebuilding.");
                Rebuild();
                return;
            }

            AssignCharacterSo(net);
            AssetDatabase.SetLabels(prefab, new[] { "FusionPrefab" });
            AssetDatabase.ImportAsset(
                "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion",
                ImportAssetOptions.ForceUpdate);
            NetworkProjectConfigUtilities.RebuildPrefabTable();
            AssetDatabase.SaveAssets();
            Debug.Log("[NetworkedFPSPlayer] CharacterSO + Fusion prefab table repaired. Enter Play Mode and join again.");
        }

        /// <summary>
        /// The canonical, replicated position every machine measures this player from. It has to be a
        /// child of the network root so proxies and the owner read the exact same transform.
        /// </summary>
        static void EnsureDistanceOrigin(GameObject root)
        {
            var origin = root.transform.Find(FpsNetworkBridge.DistanceOriginName);
            if (origin == null)
            {
                var go = new GameObject(FpsNetworkBridge.DistanceOriginName);
                origin = go.transform;
                origin.SetParent(root.transform, false);
            }

            origin.localPosition = Vector3.zero;
            origin.localRotation = Quaternion.identity;
            origin.localScale = Vector3.one;
        }

        /// <summary>
        /// The FPS hierarchy is nested in the prefab, so Fusion runs every child Awake when it
        /// instantiates a remote player. Cowsins' UIController takes UIController.Instance there, and
        /// PoolManager/SoundManager/CoinManager destroy whichever copy loses their first-wins guard -
        /// which is how a proxy ended up killing the client's own managers and freezing its HUD.
        /// Authoring these roots inactive means only the owner ever runs them; FpsNetworkBridge
        /// already re-activates them in EnableLocalGameplay.
        /// </summary>
        static void DeactivateOwnerOnlyRoots(GameObject fps)
        {
            var playerUi = FindDeep(fps.transform, "PlayerUI");
            if (playerUi != null)
                playerUi.gameObject.SetActive(false);

            var managers = fps.transform.Find("GeneralManagers");
            if (managers != null)
                managers.gameObject.SetActive(false);
        }

        static void StripVoiceAndThirdPersonGameplay(GameObject root)
        {
            var voice = root.transform.Find("VoiceNetworkObject");
            if (voice != null)
                Object.DestroyImmediate(voice.gameObject);

            // FpsNetworkBridge replicates discrete animation parameters instead. Leaving Fusion's
            // whole-Animator sync in place would have two systems writing the same controller.
            DestroyComponent<NetworkMecanimAnimator>(root);

            DestroyComponent<ThirdPersonController>(root);
            DestroyComponent<GetPlayerCameraAndControls>(root);
            DestroyComponent<CharacterController>(root);
            DestroyComponent<PlayerInput>(root);
            DestroyComponent<BasicRigidBodyPush>(root);

            var tpcType = System.Type.GetType("StarterAssets.ThirdPersonController, Assembly-CSharp");
            if (tpcType != null)
            {
                var tpc = root.GetComponent(tpcType);
                if (tpc != null)
                    Object.DestroyImmediate(tpc);
            }
        }

        static void DestroyComponent<T>(GameObject root) where T : Component
        {
            var component = root.GetComponent<T>();
            if (component != null)
                Object.DestroyImmediate(component);
        }

        static void WireBridge(FpsNetworkBridge bridge, GameObject root, GameObject fps)
        {
            var so = new SerializedObject(bridge);
            so.FindProperty("fpsRoot").objectReferenceValue = fps;

            var movement = fps.GetComponentInChildren<cowsins.PlayerMovement>(true);
            if (movement != null)
                so.FindProperty("fpsBody").objectReferenceValue = movement.transform;

            var camera = fps.transform.Find("Camera");
            if (camera != null)
                so.FindProperty("fpsCameraRoot").objectReferenceValue = camera.gameObject;

            var input = fps.GetComponentInChildren<cowsins.InputManager>(true);
            if (input != null)
                so.FindProperty("fpsInputRoot").objectReferenceValue = input.gameObject;

            var playerUi = FindDeep(fps.transform, "PlayerUI");
            if (playerUi != null)
                so.FindProperty("fpsUiRoot").objectReferenceValue = playerUi.gameObject;

            var managers = fps.transform.Find("GeneralManagers");
            if (managers != null)
                so.FindProperty("fpsManagersRoot").objectReferenceValue = managers.gameObject;

            var render = root.transform.Find("Player Render");
            if (render != null)
                so.FindProperty("remoteBody").objectReferenceValue = render;

            var interp = root.transform.Find("Interpolation target");
            if (interp != null)
                so.FindProperty("interpolationTarget").objectReferenceValue = interp;

            var origin = root.transform.Find(FpsNetworkBridge.DistanceOriginName);
            if (origin != null)
                so.FindProperty("distanceOrigin").objectReferenceValue = origin;

            so.FindProperty("starterAssetsInputs").objectReferenceValue = root.GetComponent<StarterAssetsInputs>();
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// FpsNetworkBridge copies its runtime spawn sources from the LocalDualPlayer Resources
        /// prefab, so it has to point at the same controller the network prefab was built from.
        /// </summary>
        static void SyncLocalDualPlayerSources()
        {
            var localDual = AssetDatabase.LoadAssetAtPath<GameObject>(LocalDualPlayerPath);
            if (localDual == null)
                return;

            var dual = localDual.GetComponent<DualPlayerController>();
            if (dual == null)
                return;

            var so = new SerializedObject(dual);
            so.FindProperty("cowsinsPrefab").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<GameObject>(CowsinsPlayerPath);
            so.FindProperty("stevePrefab").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<GameObject>(StevePlayerPath);
            so.FindProperty("camerasPrefab").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<GameObject>(CamerasPath);
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(localDual);
            AssetDatabase.SaveAssetIfDirty(localDual);
        }

        static void WireDualPlayer(GameObject root, GameObject fps)
        {
            var dual = root.GetComponent<DualPlayerController>();
            if (dual == null)
                dual = root.AddComponent<DualPlayerController>();

            var stevePf = AssetDatabase.LoadAssetAtPath<GameObject>(StevePlayerPath);
            var camerasPf = AssetDatabase.LoadAssetAtPath<GameObject>(CamerasPath);
            var cowsinsPf = AssetDatabase.LoadAssetAtPath<GameObject>(CowsinsPlayerPath);

            var movement = fps.GetComponentInChildren<cowsins.PlayerMovement>(true);
            var camera = fps.transform.Find("Camera");
            dual.EditorWire(
                fps,
                movement != null ? movement.transform : null,
                camera != null ? camera.gameObject : null,
                null,
                null,
                null,
                cowsinsPf,
                stevePf,
                camerasPf);
        }

        static Transform FindDeep(Transform parent, string name)
        {
            if (parent.name == name)
                return parent;
            foreach (Transform child in parent)
            {
                var found = FindDeep(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        static void AssignCharacterSo(NetworkObject networkPrefab)
        {
            if (networkPrefab == null)
                return;

            var asset = AssetDatabase.LoadAssetAtPath<CharacterSO>(CharacterSoPath);
            if (asset == null)
                return;

            var so = new SerializedObject(asset);
            var characters = so.FindProperty("characters");
            for (int i = 0; i < characters.arraySize; i++)
            {
                var entry = characters.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("character").objectReferenceValue = networkPrefab;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }
    }
}
#endif
