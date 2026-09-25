#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CollarCali.Integration.Editor
{
    /// <summary>
    /// ParrelSync clones keep their own Library, so Malbers Shader Graphs that
    /// already compiled on the main editor stay pink on the client until they
    /// are imported there. Reimport the graphs on the clone; do not rewrite
    /// shared .mat files (those are linked back to the main project).
    /// </summary>
    public static class MalbersCloneShaderWarmup
    {
        const string ShaderFolder = "Assets/Malbers Animations/Common/Shaders";
        const string ProbeGraph = "Shader Graphs/Malbers4Gradient";

        static bool _ranThisSession;

        [InitializeOnLoadMethod]
        static void AutoRun()
        {
            if (!IsCloneProject())
                return;
            EditorApplication.delayCall += TryWarmup;
        }

        [MenuItem("Tools/CollarCali/Reimport Malbers Shaders (ParrelSync Clone)")]
        public static void ReimportFromMenu()
        {
            _ranThisSession = false;
            ReimportMalbersShaders();
        }

        static void TryWarmup()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryWarmup;
                return;
            }

            if (!IsCloneProject() || _ranThisSession)
                return;
            if (GraphsAreReady())
                return;

            _ranThisSession = true;
            ReimportMalbersShaders();
        }

        static void ReimportMalbersShaders()
        {
            if (!AssetDatabase.IsValidFolder(ShaderFolder))
            {
                Debug.LogWarning("[CollarCali] Malbers shader folder missing: " + ShaderFolder);
                return;
            }

            Debug.Log("[CollarCali] ParrelSync clone: reimporting Malbers Shader Graphs so Steve is not pink.");
            AssetDatabase.ImportAsset(ShaderFolder,
                ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
        }

        static bool GraphsAreReady()
        {
            var graph = Shader.Find(ProbeGraph);
            return graph != null && graph.isSupported;
        }

        static bool IsCloneProject()
        {
            var clones = System.Type.GetType("ParrelSync.ClonesManager, ParrelSync");
            if (clones != null)
            {
                var method = clones.GetMethod("IsClone", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                    return (bool)method.Invoke(null, null);
            }

            var root = Directory.GetParent(Application.dataPath);
            return root != null && File.Exists(Path.Combine(root.FullName, ".clone"));
        }
    }
}
#endif
