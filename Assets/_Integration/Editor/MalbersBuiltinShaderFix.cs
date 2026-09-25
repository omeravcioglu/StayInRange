#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CollarCali.Integration.Editor
{
    /// <summary>
    /// Built-in RP fix for pink/purple Malbers materials (URP Shader Graph / URP Amplify).
    /// Prefer: Tools → Malbers Animations → Malbers Standard Shaders
    /// Or: Tools → CollarCali → Fix Malbers Purple Materials (Built-in)
    /// </summary>
    public static class MalbersBuiltinShaderFix
    {
        const string StandardPackage =
            "Assets/Malbers Animations/Common/Shaders/Malbers_Standard.unitypackage";

        [MenuItem("Tools/CollarCali/Fix Malbers Purple Materials (Built-in)", false, 50)]
        public static void FixAll()
        {
            string full = Path.GetFullPath(StandardPackage);
            if (File.Exists(full))
                AssetDatabase.ImportPackage(StandardPackage, false);
            else
                Debug.LogWarning($"[CollarCali] Missing package: {StandardPackage}");

            // Ensure key Amplify shaders resolve on Built-in
            string[] names =
            {
                "Malbers/Gradient4", "Malbers/Color3x3", "Malbers/Color4x4",
                "Malbers/Fur", "Malbers/MWater2", "Malbers/Wind", "Standard", "Particles/Additive"
            };
            foreach (string n in names)
            {
                if (Shader.Find(n) == null)
                    Debug.LogWarning($"[CollarCali] Shader not found after import: {n}");
            }

            AssetDatabase.Refresh();
            Debug.Log("[CollarCali] Malbers Standard (Built-in) shaders imported. Pink materials should clear after refresh.");
        }
    }
}
#endif
