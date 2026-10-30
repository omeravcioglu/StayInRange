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
    /// <summary>
    /// Makes sure the TeamLink and NetworkWorldActor network prefabs exist - in Prefabs, and as the
    /// Resources copies the game loads - and carry the FusionPrefab label.
    ///
    /// Runs after every domain reload: opening the project and every script change. So when everything
    /// is already in place, which is nearly always, it does nothing. It used to save all assets,
    /// force-reimport the Fusion config and rebuild Fusion's prefab table every time regardless, and
    /// that rebuild loads every prefab in the project - the long "Hold on" on opening the project.
    /// Fusion's own NetworkObjectPostprocessor labels and registers a network prefab whenever one is
    /// imported anyway; the full rebuild is kept for when this really had to make or fix something,
    /// and for the menu item.
    /// </summary>
    public static class MultiplayerPrefabBuilder
    {
        const string OutputDir = "Assets/_Integration/Prefabs";
        const string ResourcesDir = "Assets/_Integration/Resources";
        const string TeamLinkPath = OutputDir + "/TeamLink.prefab";
        const string WorldActorPath = OutputDir + "/NetworkWorldActor.prefab";
        const string FusionPrefabLabel = "FusionPrefab";

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

            // |= rather than ||, so every step still runs after an earlier one has reported a change.
            bool changed = force;

            changed |= EnsureNetworkPrefab(
                TeamLinkPath,
                "TeamLink",
                force,
                go =>
                {
                    if (go.GetComponent<TeamDistanceManager>() == null)
                        go.AddComponent<TeamDistanceManager>();
                });

            changed |= EnsureNetworkPrefab(
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

            changed |= CopyToResources(TeamLinkPath, ResourcesDir + "/TeamLink.prefab");
            changed |= CopyToResources(WorldActorPath, ResourcesDir + "/NetworkWorldActor.prefab");

            // Everything was already in place: nothing to save, reimport or rebuild.
            if (!changed)
                return;

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(
                "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion",
                ImportAssetOptions.ForceUpdate);
            NetworkProjectConfigUtilities.RebuildPrefabTable();
        }

        /// <summary>True when the prefab had to be built again, or its label added.</summary>
        static bool EnsureNetworkPrefab(string path, string name, bool force, System.Action<GameObject> setup)
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
                        return EnsureFusionLabel(existing);
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
                EnsureFusionLabel(saved);
            return true;
        }

        /// <summary>True when the Resources copy had to be made, or its label added.</summary>
        static bool CopyToResources(string src, string dst)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(src) == null)
                return false;

            var copy = AssetDatabase.LoadAssetAtPath<GameObject>(dst);
            if (copy != null)
                return copy.GetComponent<NetworkObject>() != null && EnsureFusionLabel(copy);

            AssetDatabase.CopyAsset(src, dst);
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(dst);
            if (saved != null && saved.GetComponent<NetworkObject>() != null)
                EnsureFusionLabel(saved);
            return true;
        }

        /// <summary>
        /// Adds the FusionPrefab label when it is missing, keeping any other labels, and reports whether
        /// it did. It used to be written unconditionally, replacing all of the asset's labels each time.
        /// </summary>
        static bool EnsureFusionLabel(UnityEngine.Object asset) =>
            AssetDatabaseUtils.SetLabel(asset, FusionPrefabLabel, true);
    }
}
#endif
