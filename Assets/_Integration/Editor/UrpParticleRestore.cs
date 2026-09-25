#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CollarCali.Integration.Editor
{
    /// <summary>
    /// Cowsins VFX was remapped to URP Particles/Unlit but left Opaque,
    /// so muzzle/smoke/trails drew as solid flat quads.
    /// </summary>
    public static class UrpParticleRestore
    {
        const string PrefsKey = "CollarCali.UrpParticleRestore.v1";

        [InitializeOnLoadMethod]
        static void AutoRun()
        {
            if (EditorPrefs.GetBool(PrefsKey, false))
                return;
            EditorApplication.delayCall += TryFix;
        }

        [MenuItem("Tools/CollarCali/Restore Cowsins Particle Materials")]
        public static void RestoreFromMenu()
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

            if (Shader.Find("Universal Render Pipeline/Particles/Unlit") == null)
            {
                EditorApplication.delayCall += TryFix;
                return;
            }

            FixAll();
        }

        public static void FixAll()
        {
            var particles = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (particles == null)
                return;

            int fixedCount = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[]
            {
                "Assets/Cowsins/Materials/VFX",
                "Assets/Cowsins/VFX"
            }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                    continue;

                if (mat.shader == null || mat.shader.name == "Sprites/Default" || mat.shader.name.StartsWith("UI/"))
                    continue;

                if (mat.shader != particles)
                    mat.shader = particles;

                ApplyTransparent(mat);
                EditorUtility.SetDirty(mat);
                fixedCount++;
            }

            AssetDatabase.SaveAssets();
            EditorPrefs.SetBool(PrefsKey, true);
            Debug.Log($"[CollarCali] Restored {fixedCount} Cowsins particle materials to transparent URP.");
        }

        static void ApplyTransparent(Material mat)
        {
            bool additive = !IsAlphaBlend(mat.name);

            if (mat.HasProperty("_MainTex") && mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") == null)
                mat.SetTexture("_BaseMap", mat.GetTexture("_MainTex"));

            if (mat.HasProperty("_TintColor") && mat.HasProperty("_BaseColor"))
            {
                var tint = mat.GetColor("_TintColor");
                var baseColor = mat.GetColor("_BaseColor");
                if (baseColor.maxColorComponent <= 0.51f && tint.a > 0f)
                    mat.SetColor("_BaseColor", tint);
            }

            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Blend", additive ? 2f : 0f);
            mat.SetFloat("_SrcBlend", 5f);
            mat.SetFloat("_DstBlend", additive ? 1f : 10f);
            if (mat.HasProperty("_SrcBlendAlpha"))
                mat.SetFloat("_SrcBlendAlpha", 1f);
            if (mat.HasProperty("_DstBlendAlpha"))
                mat.SetFloat("_DstBlendAlpha", additive ? 1f : 10f);
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 0f);

            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = 3000;
        }

        static bool IsAlphaBlend(string name)
        {
            return name.IndexOf("Smoke", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Dust", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Pebble", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Wood", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Grass", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
#endif
