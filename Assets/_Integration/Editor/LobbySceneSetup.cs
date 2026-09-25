#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Keeps Assets/Scenes/Lobby.unity registered in Build Settings. Room creation resolves the
    /// lobby by build index, so if the scene ever drops out of the list every host silently falls
    /// back to loading Game directly - which is exactly the behaviour the lobby replaced.
    /// </summary>
    [InitializeOnLoad]
    public static class LobbySceneSetup
    {
        const string LobbyScenePath = "Assets/Scenes/Lobby.unity";
        const string GameScenePath = "Assets/Scenes/Game.unity";

        static LobbySceneSetup()
        {
            // Deferred: EditorBuildSettings is not safe to touch during the static constructor
            // that runs as part of domain reload.
            EditorApplication.delayCall += () => Verify(false);
        }

        [MenuItem("Tools/CollarCali/Verify Lobby Setup")]
        static void VerifyFromMenu() => Verify(true);

        static void Verify(bool verbose)
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(LobbyScenePath) == null)
            {
                Debug.LogError("[CollarCali] " + LobbyScenePath + " is missing from the project.");
                return;
            }

            var scenes = EditorBuildSettings.scenes.ToList();
            int lobby = scenes.FindIndex(s => s.path == LobbyScenePath);

            if (lobby >= 0 && scenes[lobby].enabled)
            {
                if (verbose)
                    Debug.Log("[CollarCali] Lobby scene is registered and enabled. Build index order: " +
                              string.Join(", ", EnabledSceneNames()));
                return;
            }

            if (lobby >= 0)
            {
                scenes[lobby] = new EditorBuildSettingsScene(LobbyScenePath, true);
            }
            else
            {
                // Appended, never inserted: build indices follow list order, so placing it beside
                // Game would renumber every scene after it.
                scenes.Add(new EditorBuildSettingsScene(LobbyScenePath, true));
            }

            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("[CollarCali] Registered " + LobbyScenePath + " in Build Settings.");

            if (scenes.All(s => s.path != GameScenePath || !s.enabled))
                Debug.LogError("[CollarCali] 'Game' is not an enabled Build Settings scene - " +
                               "Start Game will fail.");
        }

        static IEnumerable<string> EnabledSceneNames()
        {
            return EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => System.IO.Path.GetFileNameWithoutExtension(s.path));
        }
    }
}
#endif
