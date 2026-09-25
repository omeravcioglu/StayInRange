#if UNITY_EDITOR && CMPSETUP_COMPLETE
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.Editor
{
    /// <summary>
    /// CMP used to load Game (HUD/managers) then Environment 1 additively.
    /// CollarCali uses two exclusive scenes: Menu, then Game with the world inside it.
    /// </summary>
    public static class MergeEnvironmentIntoGame
    {
        const string GamePath = "Assets/Clean Multiplayer Pro/Scenes/Game.unity";
        const string EnvPath = "Assets/Clean Multiplayer Pro/Scenes/Environment 1.unity";

        [InitializeOnLoadMethod]
        static void AutoMergeIfNeeded()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;

                for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                {
                    if (EditorSceneManager.GetSceneAt(i).isDirty)
                        return;
                }

                TryMerge();
            };
        }

        [MenuItem("Tools/CollarCali/Merge Environment 1 Into Game Scene")]
        public static void MergeFromMenu()
        {
            TryMerge();
        }

        public static void TryMerge()
        {
            if (!System.IO.File.Exists(GamePath) || !System.IO.File.Exists(EnvPath))
                return;

            var gameYaml = System.IO.File.ReadAllText(GamePath);
            if (gameYaml.Contains("m_Name: Playground") && gameYaml.Contains("m_Name: Environment 1"))
                return;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            var game = EditorSceneManager.OpenScene(GamePath, OpenSceneMode.Single);
            foreach (var existing in game.GetRootGameObjects())
            {
                if (existing.name == "Environment 1")
                    return;
            }

            var env = EditorSceneManager.OpenScene(EnvPath, OpenSceneMode.Additive);
            foreach (var root in env.GetRootGameObjects())
                SceneManager.MoveGameObjectToScene(root, game);

            EditorSceneManager.CloseScene(env, true);
            EditorSceneManager.MarkSceneDirty(game);
            EditorSceneManager.SaveScene(game);
            AssetDatabase.SaveAssets();
            Debug.Log("[CollarCali] Environment 1 is now inside Game.unity. Play from Menu — only Menu or Game will be loaded.");
        }
    }
}
#endif
