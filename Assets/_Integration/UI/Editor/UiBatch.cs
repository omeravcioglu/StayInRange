#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CollarCali.UI.EditorTools
{
    /// <summary>
    /// Entry points for running the UI tools from the command line, without opening the editor:
    ///
    ///   Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod
    ///       CollarCali.UI.EditorTools.UiBatch.BuildAndCapture -quit -logFile &lt;file&gt;
    ///
    /// Builds the theme, then renders every gallery page to Builds/UiCaptures. Leave out
    /// -nographics: the captures need a graphics device.
    /// </summary>
    public static class UiBatch
    {
        public static void BuildAndCapture()
        {
            UiThemeBuilder.Build();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            UiGalleryTools.CaptureAll();
            Debug.Log("[UiBatch] done");
        }

        public static void CaptureOnly()
        {
            UiGalleryTools.CaptureAll();
            Debug.Log("[UiBatch] done");
        }
    }
}
#endif
