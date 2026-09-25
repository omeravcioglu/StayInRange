using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// ParrelSync clones often render the world pink because the URP pipeline is
    /// missing on the clone or leftover Built-in / error shaders never compiled.
    /// Re-assigns the project pipeline and remaps broken materials at scene load.
    /// </summary>
    public static class RuntimeUrpVisualRepair
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            Repair(SceneManager.GetActiveScene());
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Repair(scene);
        }

        public static void Repair(Scene scene)
        {
            EnsurePipeline();
            RepairMaterials();
            if (Application.isPlaying)
                DelayedRepair.Schedule();
        }

        sealed class DelayedRepair : MonoBehaviour
        {
            float _at;
            bool _ran;

            public static void Schedule()
            {
                if (Object.FindFirstObjectByType<DelayedRepair>() != null)
                    return;
                var go = new GameObject("UrpVisualRepair");
                Object.DontDestroyOnLoad(go);
                var delay = go.AddComponent<DelayedRepair>();
                delay._at = Time.unscaledTime + 1.5f;
            }

            void Update()
            {
                if (_ran || Time.unscaledTime < _at)
                    return;
                _ran = true;
                EnsurePipeline();
                RepairMaterials();
                Destroy(gameObject);
            }
        }

        static void EnsurePipeline()
        {
            var current = GraphicsSettings.currentRenderPipeline;
            if (current != null)
            {
                if (QualitySettings.renderPipeline == null)
                    QualitySettings.renderPipeline = current;
                return;
            }

#if UNITY_EDITOR
            var pipeline = UnityEditor.AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                "Assets/Settings/URP-Pipeline.asset");
            if (pipeline == null)
                return;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
#endif
        }

        static void RepairMaterials()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                return;

            var renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                    continue;

                var shared = renderer.sharedMaterials;
                if (shared == null || shared.Length == 0)
                    continue;

                bool changed = false;
                for (int m = 0; m < shared.Length; m++)
                {
                    var mat = shared[m];
                    if (mat == null)
                        continue;
                    if (!NeedsUrpRemap(mat.shader))
                        continue;

                    var fixedMat = new Material(lit);
                    CopyCommonMaps(mat, fixedMat);
                    shared[m] = fixedMat;
                    changed = true;
                }

                if (changed)
                    renderer.sharedMaterials = shared;
            }
        }

        static bool NeedsUrpRemap(Shader shader)
        {
            if (shader == null)
                return true;

            var name = shader.name;
            if (name == "Hidden/InternalErrorShader" || name.StartsWith("Hidden/InternalError"))
                return true;
            if (name == "Standard" || name == "Standard (Specular setup)")
                return true;

            // Clone Library often keeps the Shader Graph name but never compiled it.
            // Only remap Malbers so the main editor's working graphs stay untouched.
            return IsMalbersShader(name) && !shader.isSupported;
        }

        static bool IsMalbersShader(string name)
        {
            return name.StartsWith("Shader Graphs/Malbers")
                   || name.StartsWith("Malbers/");
        }

        static void CopyCommonMaps(Material from, Material to)
        {
            if (to.HasProperty("_BaseColor"))
            {
                if (from.HasProperty("_Color1Top"))
                    to.SetColor("_BaseColor", from.GetColor("_Color1Top"));
                else if (from.HasProperty("_Tint"))
                    to.SetColor("_BaseColor", from.GetColor("_Tint"));
                else if (from.HasProperty("_Color"))
                    to.SetColor("_BaseColor", from.GetColor("_Color"));
                else if (from.HasProperty("_BaseColor"))
                    to.SetColor("_BaseColor", from.GetColor("_BaseColor"));
            }

            if (!to.HasProperty("_BaseMap"))
                return;

            if (from.HasProperty("_MainTexture"))
                to.SetTexture("_BaseMap", from.GetTexture("_MainTexture"));
            else if (from.HasProperty("_MainTex"))
                to.SetTexture("_BaseMap", from.GetTexture("_MainTex"));
            else if (from.HasProperty("_BaseMap"))
                to.SetTexture("_BaseMap", from.GetTexture("_BaseMap"));
        }
    }
}
