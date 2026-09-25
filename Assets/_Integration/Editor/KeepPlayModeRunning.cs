#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CollarCali.Editor
{
    /// <summary>
    /// Overlay UI + dual cameras make Scene-view RectTransform handles spam
    /// "out of view frustum" and Error Pause freezes Play. Turn those handles
    /// and Error Pause off while playing.
    /// </summary>
    [InitializeOnLoad]
    static class KeepPlayModeRunning
    {
        static KeepPlayModeRunning()
        {
            SetErrorPause(false);
            EditorApplication.playModeStateChanged += OnPlayMode;
            SceneView.duringSceneGui += OnSceneGui;
            Selection.selectionChanged += OnSelectionChanged;
        }

        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.EnteredPlayMode)
                SetErrorPause(false);

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                StripUiSelection();
                SetSceneViewGizmos(false);
                EditorApplication.delayCall += () =>
                {
                    SetErrorPause(false);
                    StripUiSelection();
                    if (EditorApplication.isPlaying)
                        EditorApplication.isPaused = false;
                };
            }

            if (state == PlayModeStateChange.ExitingPlayMode)
                SetSceneViewGizmos(true);
        }

        static void OnSelectionChanged()
        {
            if (Application.isPlaying)
                StripUiSelection();
        }

        static void OnSceneGui(SceneView view)
        {
            int ui = LayerMask.NameToLayer("UI");
            if (ui >= 0)
                Tools.visibleLayers &= ~(1 << ui);

            if (Application.isPlaying)
                StripUiSelection();
        }

        static void StripUiSelection()
        {
            var objects = Selection.gameObjects;
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] != null && objects[i].GetComponent<RectTransform>() != null)
                {
                    Selection.activeObject = null;
                    return;
                }
            }
        }

        static void SetSceneViewGizmos(bool on)
        {
            var views = SceneView.sceneViews;
            for (int i = 0; i < views.Count; i++)
            {
                if (views[i] is SceneView view)
                    view.drawGizmos = on;
            }
        }

        static void SetErrorPause(bool enabled)
        {
            try
            {
                var consoleType = typeof(SceneView).Assembly.GetType("UnityEditor.ConsoleWindow");
                if (consoleType == null)
                    return;

                foreach (var name in new[] { "SetConsoleErrorPause", "SetErrorPause" })
                {
                    var method = consoleType.GetMethod(
                        name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (method != null && method.GetParameters().Length == 1)
                    {
                        method.Invoke(null, new object[] { enabled });
                        return;
                    }
                }

                foreach (var name in new[] { "ms_ConsoleErrorPause", "ms_ErrorPause", "errorPause" })
                {
                    var field = consoleType.GetField(
                        name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        field.SetValue(null, enabled);
                        return;
                    }
                }
            }
            catch
            {
            }
        }
    }
}
#endif
