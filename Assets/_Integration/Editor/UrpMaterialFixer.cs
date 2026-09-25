#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CollarCali.Integration.Editor
{
    /// <summary>
    /// Remaps leftover Built-in / Amplify materials to URP so they are not pink.
    /// </summary>
    public static class UrpMaterialFixer
    {
        const string PrefsKey = "CollarCali.UrpMaterialFixer.v2";

        static readonly Dictionary<string, string> NameMap = new Dictionary<string, string>
        {
            { "Malbers/Color3x3", "Shader Graphs/Malbers3x3" },
            { "Malbers/Color4x4", "Shader Graphs/Malbers4x4" },
            { "Malbers/Color4x4 Unlit", "Shader Graphs/Malbers4x4" },
            { "Malbers/Color4x3", "Shader Graphs/Malbers4x4" },
            { "Malbers/Gradient4", "Shader Graphs/Malbers4Gradient" },
            { "Malbers/Fur", "Shader Graphs/MalbersStandardFur" },
            { "Malbers/Wind", "Shader Graphs/MalbersStandard" },
            { "Malbers/MWater2", "Universal Render Pipeline/Lit" },
            { "Malbers/Anisotropic/Ward", "Shader Graphs/MalbersStandardFurMasked Anisotropic" },
            { "Malbers/Anisotropic/Ward2", "Shader Graphs/MalbersStandardFurMasked Anisotropic" },
            { "Malbers/Anisotropic/Circular", "Shader Graphs/MalbersStandardFurMasked Anisotropic" },
            { "Malbers/Mask4Realistic", "Shader Graphs/MalbersStandard" },
            { "Malbers/Mask8Realistic", "Shader Graphs/MalbersStandard" },
            { "Malbers/Masks4Toon", "Shader Graphs/MalbersStandard" },
            { "Malbers/Masks8Toon", "Shader Graphs/MalbersStandard" },
            { "Malbers/Golem PA", "Shader Graphs/MalbersStandard" },
            { "Malbers/DragonEggs", "Shader Graphs/MalbersStandard" },
            { "Standard", "Universal Render Pipeline/Lit" },
            { "Standard (Specular setup)", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Diffuse", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Specular", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Bumped Diffuse", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Bumped Specular", "Universal Render Pipeline/Lit" },
            { "Legacy Shaders/Transparent/Diffuse", "Universal Render Pipeline/Lit" },
            { "Mobile/Diffuse", "Universal Render Pipeline/Lit" },
            { "Mobile/Bumped Diffuse", "Universal Render Pipeline/Lit" },
            { "Mobile/Bumped Specular", "Universal Render Pipeline/Lit" },
            { "Unlit/Texture", "Universal Render Pipeline/Unlit" },
            { "Unlit/Color", "Universal Render Pipeline/Unlit" },
            { "Unlit/Transparent", "Universal Render Pipeline/Unlit" },
            { "Unlit/Transparent Cutout", "Universal Render Pipeline/Unlit" },
            { "Custom/UIBlur", "UI/Default" },
            { "Particles/Additive", "Universal Render Pipeline/Particles/Unlit" },
            { "Particles/Additive (Soft)", "Universal Render Pipeline/Particles/Unlit" },
            { "Particles/Alpha Blended", "Universal Render Pipeline/Particles/Unlit" },
            { "Particles/Alpha Blended Premultiply", "Universal Render Pipeline/Particles/Unlit" },
            { "Particles/Multiply", "Universal Render Pipeline/Particles/Unlit" },
            { "Particles/Standard Unlit", "Universal Render Pipeline/Particles/Unlit" },
            { "Particles/Standard Surface", "Universal Render Pipeline/Particles/Unlit" },
            { "Mobile/Particles/Additive", "Universal Render Pipeline/Particles/Unlit" },
            { "Mobile/Particles/Alpha Blended", "Universal Render Pipeline/Particles/Unlit" },
        };

        [InitializeOnLoadMethod]
        static void AutoRun()
        {
            if (EditorPrefs.GetBool(PrefsKey, false))
                return;
            EditorApplication.delayCall += TryFix;
        }

        [MenuItem("Tools/CollarCali/Fix Pink Materials (URP)")]
        public static void FixFromMenu()
        {
            FixAll();
        }

        static void TryFix()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryFix;
                return;
            }

            if (Shader.Find("Universal Render Pipeline/Lit") == null)
            {
                EditorApplication.delayCall += TryFix;
                return;
            }

            FixAll();
        }

        public static void FixAll()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            var particles = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (lit == null)
            {
                Debug.LogWarning("[CollarCali] URP Lit not found; skipped material fix.");
                return;
            }

            int converted = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/"))
                    continue;
                if (path.StartsWith("Assets/Cowsins/Materials/UI/"))
                    continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                    continue;

                var current = mat.shader;
                var currentName = current != null ? current.name : string.Empty;
                // Never persist a Lit remap onto Shader Graphs. ParrelSync shares
                // Assets with the main editor; pink Malbers on the clone is a
                // Library compile miss, not a bad material assignment.
                if (currentName.StartsWith("Shader Graphs/"))
                    continue;
                if (IsUsableUrpShader(current))
                    continue;

                Shader target = null;
                if (!string.IsNullOrEmpty(currentName) && NameMap.TryGetValue(currentName, out var mapped))
                    target = Shader.Find(mapped);

                if (target == null)
                    target = GuessFallback(currentName, lit, unlit, particles);

                if (target == null || target == current)
                    continue;

                mat.shader = target;
                UrpAlbedoRestore.CopyBuiltInAlbedo(mat);
                EditorUtility.SetDirty(mat);
                converted++;
            }

            AssetDatabase.SaveAssets();
            EditorPrefs.SetBool(PrefsKey, true);
            Debug.Log($"[CollarCali] Remapped {converted} pink/Built-in materials to URP.");
        }

        static bool IsUsableUrpShader(Shader shader)
        {
            if (shader == null || shader.name == "Hidden/InternalErrorShader")
                return false;
            if (shader.name.StartsWith("Hidden/InternalError"))
                return false;

            if (shader.name.StartsWith("Universal Render Pipeline/"))
                return shader.isSupported;
            if (shader.name.StartsWith("Shader Graphs/"))
                return shader.isSupported;
            if (shader.name.StartsWith("FlatKit/"))
                return true;
            if (shader.name.StartsWith("Skybox/"))
                return true;
            if (shader.name.StartsWith("Sprites/"))
                return true;
            if (shader.name.StartsWith("UI/"))
                return true;
            if (shader.name == "Custom/UIBlur")
                return true;
            if (shader.name.StartsWith("TextMeshPro/"))
                return true;
            if (shader.name.StartsWith("GUI/"))
                return true;
            return false;
        }

        static Shader GuessFallback(string currentName, Shader lit, Shader unlit, Shader particles)
        {
            if (string.IsNullOrEmpty(currentName))
                return lit;
            if (currentName.Contains("Particle") || currentName.Contains("VFX"))
                return particles != null ? particles : unlit != null ? unlit : lit;
            if (currentName.Contains("Unlit") || currentName.Contains("Additive"))
                return unlit != null ? unlit : lit;
            return lit;
        }
    }
}
#endif
