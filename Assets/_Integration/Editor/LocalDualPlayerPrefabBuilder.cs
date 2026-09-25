#if UNITY_EDITOR
using System.IO;
using CollarCali;
using cowsins;
using MalbersAnimations.Controller;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.Editor
{
    public static class LocalDualPlayerPrefabBuilder
    {
        const string CowsinsPath = "Assets/Cowsins/Prefabs/PlayerControllers/CowsinsFPSController.prefab";
        const string StevePath = "Assets/Malbers Animations/Animal Controller/Human/Steve Player.prefab";
        const string CamerasPath = "Assets/Malbers Animations/Common/Cinemachine/Cameras CM3.prefab";
        const string OutputDir = "Assets/_Integration/Prefabs";
        const string OutputPath = OutputDir + "/LocalDualPlayer.prefab";
        const string ResourcesDir = "Assets/_Integration/Resources";
        const string ResourcesPath = ResourcesDir + "/LocalDualPlayer.prefab";
        const string GameScenePath = "Assets/Scenes/Game.unity";

        [MenuItem("Tools/CollarCali/Build Local Dual Player Prefab")]
        public static void BuildFromMenu()
        {
            BuildPrefab();
            SetupGameScene();
        }

        [InitializeOnLoadMethod]
        static void AutoBuildIfMissing()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;

                // Only create a lightweight shell if the asset is completely missing.
                if (!File.Exists(Path.GetFullPath(OutputPath)) &&
                    AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath) == null)
                {
                    BuildLightweightShell();
                }

                // Do not auto-open or rewrite Game.unity. Use the menu item if needed.
            };
        }

        /// <summary>
        /// Lightweight prefab: DualPlayerController + source prefab refs.
        /// Children (FPS/Steve/Cameras) spawn at runtime — avoids SaveAsPrefabAsset failures
        /// from huge nested Malbers/Cinemachine graphs.
        /// </summary>
        public static GameObject BuildPrefab() => BuildLightweightShell();

        public static GameObject BuildLightweightShell()
        {
            var cowsins = AssetDatabase.LoadAssetAtPath<GameObject>(CowsinsPath);
            var steve = AssetDatabase.LoadAssetAtPath<GameObject>(StevePath);
            var cameras = AssetDatabase.LoadAssetAtPath<GameObject>(CamerasPath);
            if (cowsins == null || steve == null || cameras == null)
            {
                Debug.LogError("[LocalDualPlayer] Missing Cowsins, Steve, or Cameras CM3 prefab.");
                return null;
            }

            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);

            var preview = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("LocalDualPlayer");
            SceneManager.MoveGameObjectToScene(root, preview);

            var dual = root.AddComponent<DualPlayerController>();
            dual.EditorWire(null, null, null, null, null, null, cowsins, steve, cameras);

            bool ok = PrefabUtility.SaveAsPrefabAsset(root, OutputPath, out bool success);
            EditorSceneManager.ClosePreviewScene(preview);

            if (!success || ok == false)
            {
                Debug.LogError("[LocalDualPlayer] SaveAsPrefabAsset failed.");
                return null;
            }

            SyncResourcesCopy();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
            Debug.Log("[LocalDualPlayer] Prefab saved to " + OutputPath);
            return saved;
        }

        static void SyncResourcesCopy()
        {
            if (!File.Exists(Path.GetFullPath(OutputPath)))
                return;

            if (!Directory.Exists(ResourcesDir))
                Directory.CreateDirectory(ResourcesDir);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(ResourcesPath) != null)
                AssetDatabase.DeleteAsset(ResourcesPath);

            if (!AssetDatabase.CopyAsset(OutputPath, ResourcesPath))
                Debug.LogWarning("[LocalDualPlayer] Could not copy prefab into Resources (spawn still works via scene reference).");
        }

        public static void SetupGameScene()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
            if (prefab == null)
                prefab = BuildLightweightShell();
            if (prefab == null)
                return;

            var dual = prefab.GetComponent<DualPlayerController>();
            if (dual == null)
                return;

            var scene = EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);

            var old = GameObject.Find("Malbers Mode Switch");
            if (old != null)
                Object.DestroyImmediate(old);

            var spawnGo = GameObject.Find("Local Dual Player Spawn");
            if (spawnGo == null)
                spawnGo = new GameObject("Local Dual Player Spawn");

            var spawn = spawnGo.GetComponent<LocalDualPlayerSpawn>();
            if (spawn == null)
                spawn = spawnGo.AddComponent<LocalDualPlayerSpawn>();

            var spawnPointTf = spawnGo.transform.Find("SpawnPoint");
            if (spawnPointTf == null)
            {
                var sp = new GameObject("SpawnPoint");
                sp.transform.SetParent(spawnGo.transform, false);
                sp.transform.position = new Vector3(0f, 0f, -35f);
                spawnPointTf = sp.transform;
            }

            var spawnSo = new SerializedObject(spawn);
            spawnSo.FindProperty("dualPlayerPrefab").objectReferenceValue = dual;
            spawnSo.FindProperty("spawnPoint").objectReferenceValue = spawnPointTf;
            spawnSo.ApplyModifiedPropertiesWithoutUndo();

            EnsureTrigger(
                spawnGo.transform,
                "Enter Third Person Trigger",
                PlayerModeSwitchTrigger.SwitchMode.EnterThirdPerson,
                new Vector3(0f, 1.25f, -32f),
                new Vector3(6f, 3f, 4f),
                new Color(0.2f, 0.85f, 0.35f, 0.4f));

            EnsureTrigger(
                spawnGo.transform,
                "Exit To First Person Trigger",
                PlayerModeSwitchTrigger.SwitchMode.ExitToFirstPerson,
                new Vector3(6f, 1.25f, -32f),
                new Vector3(6f, 3f, 4f),
                new Color(0.9f, 0.35f, 0.2f, 0.4f));

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[LocalDualPlayer] Game scene spawn + triggers ready near z=-32.");
        }

        static void EnsureTrigger(
            Transform parent,
            string name,
            PlayerModeSwitchTrigger.SwitchMode mode,
            Vector3 localPos,
            Vector3 scale,
            Color tint)
        {
            var existing = parent.Find(name);
            GameObject go;
            if (existing != null)
                go = existing.gameObject;
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.SetParent(parent, false);
            }

            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;

            var col = go.GetComponent<Collider>();
            col.isTrigger = true;

            var trigger = go.GetComponent<PlayerModeSwitchTrigger>();
            if (trigger == null)
                trigger = go.AddComponent<PlayerModeSwitchTrigger>();

            var so = new SerializedObject(trigger);
            so.FindProperty("mode").enumValueIndex = (int)mode;
            so.ApplyModifiedPropertiesWithoutUndo();

            var rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                var shader = Shader.Find("Standard");
                if (shader != null)
                {
                    var mat = new Material(shader) { color = tint };
                    mat.SetFloat("_Mode", 3);
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.EnableKeyword("_ALPHABLEND_ON");
                    mat.renderQueue = 3000;
                    rend.sharedMaterial = mat;
                }
            }

            EditorUtility.SetDirty(go);
        }
    }
}
#endif
