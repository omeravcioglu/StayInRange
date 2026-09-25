#if UNITY_EDITOR && CMPSETUP_COMPLETE
using System.IO;
using CollarCali;
using Fusion;
using Fusion.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.Editor
{
    public static class MultiplayerPrefabBuilder
    {
        const string OutputDir = "Assets/_Integration/Prefabs";
        const string ResourcesDir = "Assets/_Integration/Resources";
        const string TeamLinkPath = OutputDir + "/TeamLink.prefab";
        const string WorldActorPath = OutputDir + "/NetworkWorldActor.prefab";

        [InitializeOnLoadMethod]
        static void AutoBuild()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                EnsurePrefabs();
            };
        }

        [MenuItem("Tools/CollarCali/Rebuild Multiplayer TeamLink Prefabs")]
        public static void RebuildFromMenu()
        {
            EnsurePrefabs(true);
        }

        public static void EnsurePrefabs(bool force = false)
        {
            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);
            if (!Directory.Exists(ResourcesDir))
                Directory.CreateDirectory(ResourcesDir);

            EnsureNetworkPrefab(
                TeamLinkPath,
                "TeamLink",
                force,
                go =>
                {
                    if (go.GetComponent<TeamDistanceManager>() == null)
                        go.AddComponent<TeamDistanceManager>();
                });

            EnsureNetworkPrefab(
                WorldActorPath,
                "NetworkWorldActor",
                force,
                go =>
                {
                    if (go.GetComponent<NetworkTransform>() == null)
                        go.AddComponent<NetworkTransform>();
                    if (go.GetComponent<NetworkWorldActor>() == null)
                        go.AddComponent<NetworkWorldActor>();
                });

            CopyToResources(TeamLinkPath, ResourcesDir + "/TeamLink.prefab");
            CopyToResources(WorldActorPath, ResourcesDir + "/NetworkWorldActor.prefab");

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(
                "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion",
                ImportAssetOptions.ForceUpdate);
            NetworkProjectConfigUtilities.RebuildPrefabTable();
        }

        static void EnsureNetworkPrefab(string path, string name, bool force, System.Action<GameObject> setup)
        {
            if (!force)
            {
                var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (existing != null && existing.GetComponent<NetworkObject>() != null)
                {
                    bool complete = name != "TeamLink" || existing.GetComponent<TeamDistanceManager>() != null;
                    complete = complete && (name != "NetworkWorldActor" ||
                                            (existing.GetComponent<NetworkTransform>() != null &&
                                             existing.GetComponent<NetworkWorldActor>() != null));
                    if (complete)
                    {
                        AssetDatabase.SetLabels(existing, new[] { "FusionPrefab" });
                        return;
                    }
                }
            }

            var preview = EditorSceneManager.NewPreviewScene();
            var instance = new GameObject(name);
            SceneManager.MoveGameObjectToScene(instance, preview);
            instance.AddComponent<NetworkObject>();
            setup?.Invoke(instance);

            PrefabUtility.SaveAsPrefabAsset(instance, path);
            EditorSceneManager.ClosePreviewScene(preview);

            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (saved != null && saved.GetComponent<NetworkObject>() != null)
                AssetDatabase.SetLabels(saved, new[] { "FusionPrefab" });
        }

        static void CopyToResources(string src, string dst)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(src) == null)
                return;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(dst) != null)
            {
                var copy = AssetDatabase.LoadAssetAtPath<GameObject>(dst);
                if (copy != null && copy.GetComponent<NetworkObject>() != null)
                    AssetDatabase.SetLabels(copy, new[] { "FusionPrefab" });
                return;
            }

            AssetDatabase.CopyAsset(src, dst);
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(dst);
            if (saved != null && saved.GetComponent<NetworkObject>() != null)
                AssetDatabase.SetLabels(saved, new[] { "FusionPrefab" });
        }
    }
}
#endif
