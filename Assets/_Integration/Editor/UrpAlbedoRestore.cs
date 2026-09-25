#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CollarCali.Integration.Editor
{
    /// <summary>
    /// After Built-in → URP shader swaps, albedo lived on _Color/_MainTex while
    /// URP Lit/Unlit read _BaseColor/_BaseMap and defaulted to white.
    /// </summary>
    public static class UrpAlbedoRestore
    {
        const string PrefsKey = "CollarCali.UrpAlbedoRestore.v1";

        [InitializeOnLoadMethod]
        static void AutoRun()
        {
            EditorPrefs.SetBool(PrefsKey, true);
        }

        [MenuItem("Tools/CollarCali/Restore URP Albedo From Built-in")]
        public static void RestoreFromMenu()
        {
            RestoreAll();
        }

        static void TryRestore()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryRestore;
                return;
            }

            RestoreAll();
        }

        public static void RestoreAll()
        {
            int fixedCount = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/"))
                    continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                    continue;

                if (CopyBuiltInAlbedo(mat))
                {
                    EditorUtility.SetDirty(mat);
                    fixedCount++;
                }
            }

            AssetDatabase.SaveAssets();
            EditorPrefs.SetBool(PrefsKey, true);
            Debug.Log($"[CollarCali] Restored Built-in albedo on {fixedCount} URP materials.");
        }

        public static bool CopyBuiltInAlbedo(Material mat)
        {
            if (mat.shader == null)
                return false;

            var name = mat.shader.name;
            if (!name.StartsWith("Universal Render Pipeline/"))
                return false;

            bool changed = false;

            if (mat.HasProperty("_BaseColor") && mat.HasProperty("_Color"))
            {
                var color = mat.GetColor("_Color");
                var baseColor = mat.GetColor("_BaseColor");
                if (IsDefaultWhite(baseColor) && !IsDefaultWhite(color))
                {
                    mat.SetColor("_BaseColor", color);
                    changed = true;
                }
            }

            if (mat.HasProperty("_BaseMap"))
            {
                if (mat.GetTexture("_BaseMap") == null)
                {
                    Texture src = null;
                    Vector2 scale = Vector2.one;
                    Vector2 offset = Vector2.zero;

                    if (mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null)
                    {
                        src = mat.GetTexture("_MainTex");
                        scale = mat.GetTextureScale("_MainTex");
                        offset = mat.GetTextureOffset("_MainTex");
                    }
                    else if (mat.HasProperty("_BaseColorMap") && mat.GetTexture("_BaseColorMap") != null)
                    {
                        src = mat.GetTexture("_BaseColorMap");
                        scale = mat.GetTextureScale("_BaseColorMap");
                        offset = mat.GetTextureOffset("_BaseColorMap");
                    }

                    if (src != null)
                    {
                        mat.SetTexture("_BaseMap", src);
                        mat.SetTextureScale("_BaseMap", scale);
                        mat.SetTextureOffset("_BaseMap", offset);
                        changed = true;
                    }
                }
            }

            if (mat.HasProperty("_Smoothness") && mat.HasProperty("_Glossiness"))
            {
                float smoothness = mat.GetFloat("_Smoothness");
                float gloss = mat.GetFloat("_Glossiness");
                if (Mathf.Abs(smoothness - 0.5f) < 0.001f && Mathf.Abs(gloss - 0.5f) > 0.001f)
                {
                    mat.SetFloat("_Smoothness", gloss);
                    changed = true;
                }
            }

            return changed;
        }

        static bool IsDefaultWhite(Color c)
        {
            return c.r > 0.99f && c.g > 0.99f && c.b > 0.99f && c.a > 0.99f;
        }
    }
}
#endif
