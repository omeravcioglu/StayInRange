#if UNITY_EDITOR && CMPSETUP_COMPLETE
using System.IO;
using CollarCali;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CollarCali.Editor
{
    public static class TeamSavePointPrefabBuilder
    {
        const string OutputDir = "Assets/_Integration/Prefabs";
        public const string OutputPath = OutputDir + "/TeamSavePoint.prefab";

        [InitializeOnLoadMethod]
        static void AutoBuildIfMissing()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                if (AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath) == null)
                    Rebuild();
            };
        }

        [MenuItem("Tools/CollarCali/Rebuild Team Save Point Prefab")]
        public static void Rebuild()
        {
            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);

            var preview = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("TeamSavePoint", typeof(BoxCollider), typeof(NetworkCheckpoint));
            SceneManagerMove(root, preview);

            var box = root.GetComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = new Vector3(0f, 1f, 0f);
            box.size = new Vector3(1f, 2f, 1f);

            var checkpoint = root.GetComponent<NetworkCheckpoint>();
            checkpoint.SetId(1);
            checkpoint.EnsureVisuals();

            var pad = root.transform.Find("Pad");
            if (pad != null)
            {
                pad.localPosition = new Vector3(0f, 0.04f, 0f);
                pad.localScale = new Vector3(1f, 0.08f, 1f);
            }

            var label = root.transform.Find("Label");
            if (label != null)
            {
                label.localPosition = new Vector3(0f, 2.1f, 0f);
                label.localScale = Vector3.one;
                var tmp = label.GetComponent<TextMeshPro>();
                if (tmp != null)
                {
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.fontSize = 4f;
                    tmp.text = "SAVE 1";
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, OutputPath);
            EditorSceneManager.ClosePreviewScene(preview);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[TeamSavePoint] Prefab saved to " + OutputPath);
        }

        [MenuItem("GameObject/CollarCali/Team Save Point", false, 10)]
        public static void PlaceInScene()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
            if (prefab == null)
            {
                Rebuild();
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
            }

            if (prefab == null)
            {
                Debug.LogError("[TeamSavePoint] Prefab is missing.");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "TeamSavePoint";
            instance.transform.position = SceneViewPlacement();
            instance.transform.localScale = new Vector3(6f, 1f, 6f);

            var checkpoint = instance.GetComponent<NetworkCheckpoint>();
            if (checkpoint != null)
                checkpoint.SetId(NextId());

            Undo.RegisterCreatedObjectUndo(instance, "Create Team Save Point");
            Selection.activeGameObject = instance;
        }

        static int NextId()
        {
            int max = 0;
            foreach (var point in Object.FindObjectsByType<NetworkCheckpoint>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (point != null && point.Id > max)
                    max = point.Id;
            }

            return max + 1;
        }

        static Vector3 SceneViewPlacement()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null)
                return new Vector3(0f, 0.05f, 0f);

            var pos = view.pivot;
            pos.y = 0.05f;
            return pos;
        }

        static void SceneManagerMove(GameObject go, UnityEngine.SceneManagement.Scene scene)
        {
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
        }
    }
}
#endif
