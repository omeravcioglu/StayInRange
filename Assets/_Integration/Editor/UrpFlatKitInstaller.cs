#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using FlatKit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace CollarCali.Integration.Editor
{
    /// <summary>
    /// Assigns the URP pipeline, adds FlatKit features to the Scene 1 renderer,
    /// converts Built-in Standard materials, and styles Scene 1 environment meshes.
    /// Does not open Game.unity.
    /// </summary>
    public static class UrpFlatKitInstaller
    {
        const string PrefsKey = "CollarCali.UrpFlatKit.v1";
        const string PipelinePath = "Assets/Settings/URP-Pipeline.asset";
        const string DefaultRendererPath = "Assets/Settings/URP-Default-Renderer.asset";
        const string Scene1RendererPath = "Assets/Settings/URP-Scene1-Renderer.asset";
        const string OutlinePath = "Assets/Settings/Scene1-OutlineSettings.asset";
        const string FogPath = "Assets/Settings/Scene1-FogSettings.asset";
        const string GroundMatPath = "Assets/Settings/FK-Scene1-Ground.mat";
        const string StoneMatPath = "Assets/Settings/FK-Scene1-Stone.mat";
        const string SpearsMatPath = "Assets/Settings/FK-Scene1-Spears.mat";
        const string Scene1Path = "Assets/Scenes/1.unity";
        const string SpikeMatPath = "Assets/AK Studio Art/Traps Pack/Materials/Spike Trap A.mat";
        const string LightPlaneMatPath = "Assets/Settings/Scene1-LightPlane.mat";
        const string ObjectOutlineMatPath = "Assets/Settings/Scene1-ObjectOutline.mat";

        static int _retries;

        [InitializeOnLoadMethod]
        static void AutoRun()
        {
            if (EditorPrefs.GetBool(PrefsKey, false))
                return;
            EditorApplication.delayCall += TryInstall;
        }

        [MenuItem("Tools/CollarCali/Install URP + FlatKit Scene 1")]
        public static void InstallFromMenu()
        {
            Install(force: true);
        }

        public static void Install()
        {
            Install(force: true);
        }

        static void TryInstall()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryInstall;
                return;
            }

            if (Shader.Find("Universal Render Pipeline/Lit") == null ||
                Shader.Find("FlatKit/Stylized Surface") == null)
            {
                if (++_retries < 40)
                    EditorApplication.delayCall += TryInstall;
                return;
            }

            Install(force: false);
        }

        static void Install(bool force)
        {
            if (!force && EditorPrefs.GetBool(PrefsKey, false))
                return;

            EnsureSettingsFolder();
            var defaultRenderer = LoadOrCreateRenderer(DefaultRendererPath, "URP-Default-Renderer");
            var scene1Renderer = LoadOrCreateRenderer(Scene1RendererPath, "URP-Scene1-Renderer");
            var pipeline = LoadOrCreatePipeline(defaultRenderer, scene1Renderer);

            AssignGraphics(pipeline);
            EnsureScene1Features(scene1Renderer);
            ConvertStandardMaterials();
            ApplyScene1Materials();

            EditorUtility.SetDirty(defaultRenderer);
            EditorUtility.SetDirty(scene1Renderer);
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            EditorPrefs.SetBool(PrefsKey, true);
            Debug.Log("[CollarCali] URP + FlatKit Scene 1 install complete.");
        }

        static void EnsureSettingsFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Settings"))
                AssetDatabase.CreateFolder("Assets", "Settings");
        }

        static UniversalRendererData LoadOrCreateRenderer(string path, string name)
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
            if (existing != null)
                return existing;

            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            renderer.name = name;
            AssetDatabase.CreateAsset(renderer, path);
            return renderer;
        }

        static UniversalRenderPipelineAsset LoadOrCreatePipeline(
            UniversalRendererData defaultRenderer,
            UniversalRendererData scene1Renderer)
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
                pipeline.name = "URP-Pipeline";
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }

            var listField = typeof(UniversalRenderPipelineAsset).GetField(
                "m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
            listField?.SetValue(pipeline, new ScriptableRendererData[] { defaultRenderer, scene1Renderer });

            var indexField = typeof(UniversalRenderPipelineAsset).GetField(
                "m_DefaultRendererIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            indexField?.SetValue(pipeline, 0);

            var requireDepth = typeof(UniversalRenderPipelineAsset).GetField(
                "m_RequireDepthTexture", BindingFlags.NonPublic | BindingFlags.Instance);
            requireDepth?.SetValue(pipeline, true);

            var requireOpaque = typeof(UniversalRenderPipelineAsset).GetField(
                "m_RequireOpaqueTexture", BindingFlags.NonPublic | BindingFlags.Instance);
            requireOpaque?.SetValue(pipeline, true);

            return pipeline;
        }

        static void AssignGraphics(UniversalRenderPipelineAsset pipeline)
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;
            var current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
            }
            QualitySettings.SetQualityLevel(current, false);
            QualitySettings.renderPipeline = pipeline;
            EditorUtility.SetDirty(pipeline);
        }

        static void EnsureScene1Features(UniversalRendererData renderer)
        {
            var outlineSettings = LoadOrCreateSettings<OutlineSettings>(OutlinePath, ConfigureOutline);
            var fogSettings = LoadOrCreateSettings<FogSettings>(FogPath, ConfigureFog);

            EnsureFeature<FlatKitOutline>(renderer, outlineSettings);
            EnsureFeature<FlatKitFog>(renderer, fogSettings);
            DisableFeature<FlatKitPixelation>(renderer);
            EnsureObjectOutline(renderer);
            renderer.SetDirty();
        }

        static T LoadOrCreateSettings<T>(string path, System.Action<T> configure) where T : ScriptableObject
        {
            var settings = AssetDatabase.LoadAssetAtPath<T>(path);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<T>();
                AssetDatabase.CreateAsset(settings, path);
            }
            configure(settings);
            EditorUtility.SetDirty(settings);
            return settings;
        }

        static void ConfigureOutline(OutlineSettings s)
        {
            s.edgeColor = new Color(0.07f, 0.07f, 0.08f, 0.9f);
            s.thickness = 1;
            s.useDepth = true;
            s.useNormals = true;
            s.useColor = false;
            s.applyInSceneView = true;
        }

        static void ConfigureFog(FogSettings s)
        {
            s.useDistance = true;
            s.near = 20f;
            s.far = 90f;
            s.distanceFogIntensity = 0.4f;
            s.useHeight = false;
            s.applyInSceneView = true;
            s.distanceGradient = new Gradient
            {
                colorKeys = new[]
                {
                    new GradientColorKey(new Color(0.62f, 0.68f, 0.72f), 0f),
                    new GradientColorKey(new Color(0.55f, 0.6f, 0.66f), 1f)
                },
                alphaKeys = new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.45f, 1f)
                }
            };
        }

        static void EnsureObjectOutline(UniversalRendererData renderer)
        {
            ObjectOutlineRendererFeature feature = null;
            foreach (var existing in renderer.rendererFeatures)
            {
                if (existing is ObjectOutlineRendererFeature typed)
                {
                    feature = typed;
                    break;
                }
            }

            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<ObjectOutlineRendererFeature>();
                feature.name = "Flat Kit Per Object Outline";
                AssetDatabase.AddObjectToAsset(feature, renderer);
                renderer.rendererFeatures.Add(feature);
            }

            feature.autoReferenceMaterials = false;
            feature.SetActive(true);
            EditorUtility.SetDirty(feature);
        }

        static void DisableFeature<T>(UniversalRendererData renderer)
            where T : ScriptableRendererFeature
        {
            foreach (var existing in renderer.rendererFeatures)
            {
                if (existing is T typed)
                {
                    typed.SetActive(false);
                    EditorUtility.SetDirty(typed);
                }
            }
        }

        static void EnsureFeature<T>(UniversalRendererData renderer, ScriptableObject settings)
            where T : ScriptableRendererFeature
        {
            T feature = null;
            foreach (var existing in renderer.rendererFeatures)
            {
                if (existing is T typed)
                {
                    feature = typed;
                    break;
                }
            }

            if (feature == null)
            {
                feature = ScriptableObject.CreateInstance<T>();
                feature.name = typeof(T).Name;
                AssetDatabase.AddObjectToAsset(feature, renderer);
                renderer.rendererFeatures.Add(feature);
            }

            feature.SetActive(true);
            var so = new SerializedObject(feature);
            var settingsProp = so.FindProperty("settings");
            if (settingsProp != null)
                settingsProp.objectReferenceValue = settings;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(feature);
        }

        static void ConvertStandardMaterials()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
            {
                Debug.LogWarning("[CollarCali] URP Lit shader not found; skipped material convert.");
                return;
            }

            int converted = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/"))
                    continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null)
                    continue;

                var shaderName = mat.shader.name;
                if (shaderName != "Standard" && shaderName != "Standard (Specular setup)")
                    continue;

                mat.shader = lit;
                UrpAlbedoRestore.CopyBuiltInAlbedo(mat);
                EditorUtility.SetDirty(mat);
                converted++;
            }

            Debug.Log($"[CollarCali] Converted {converted} Built-in Standard materials to URP Lit.");
        }

        static void ApplyScene1Materials()
        {
            var ground = CreateLit(GroundMatPath, new Color(0.55f, 0.50f, 0.42f, 1f), null);
            var stone = CreateLit(StoneMatPath, new Color(0.59f, 0.59f, 0.59f, 1f), null);

            Texture spearTex = null;
            var spike = AssetDatabase.LoadAssetAtPath<Material>(SpikeMatPath);
            if (spike != null)
            {
                if (spike.HasProperty("_MainTex"))
                    spearTex = spike.GetTexture("_MainTex");
                else if (spike.HasProperty("_BaseMap"))
                    spearTex = spike.GetTexture("_BaseMap");
            }
            var spears = CreateLit(SpearsMatPath, Color.white, spearTex);
            EnsureLightPlaneMaterial();
            EnsureObjectOutlineMaterial();
            var objectOutline = AssetDatabase.LoadAssetAtPath<Material>(ObjectOutlineMatPath);

            if (!System.IO.File.Exists(System.IO.Path.Combine(Application.dataPath, "Scenes/1.unity")))
                return;

            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.OpenScene(Scene1Path, OpenSceneMode.Additive);
            try
            {
                int assigned = 0;
                foreach (var renderer in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (renderer.gameObject.scene != scene)
                        continue;

                    var n = renderer.gameObject.name;
                    Material target = null;
                    if (n.StartsWith("Spears_4"))
                        target = spears;
                    else if (n.StartsWith("pb_Mesh"))
                        target = ground;
                    else if (n == "Cube" || n.StartsWith("Cube "))
                        target = stone;

                    if (target == null)
                        continue;

                    if (objectOutline != null && (n.StartsWith("Spears_4") || n == "Cube" || n.StartsWith("Cube ")))
                        renderer.sharedMaterials = new[] { target, objectOutline };
                    else
                        renderer.sharedMaterial = target;
                    EditorUtility.SetDirty(renderer);
                    assigned++;
                }

                SpawnScene1LightPlanes(scene);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[CollarCali] Assigned FlatKit materials to {assigned} Scene 1 environment renderers.");
            }
            finally
            {
                if (scene.IsValid() && scene != previous)
                    EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid() && previous.isLoaded)
                    EditorSceneManager.SetActiveScene(previous);
            }
        }

        static Material CreateLit(string path, Color albedo, Texture albedoMap)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = shader;
            }

            mat.SetColor("_BaseColor", albedo);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", albedo);
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", albedoMap);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material CreateStylized(string path, Color albedo, Texture albedoMap, bool outline = false)
        {
            var shader = Shader.Find("FlatKit/Stylized Surface");
            if (shader == null || shader.name.Contains("InternalError"))
                return CreateLit(path, albedo, albedoMap);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = shader;
            }

            mat.DisableKeyword("_CELPRIMARYMODE_NONE");
            mat.DisableKeyword("_CELPRIMARYMODE_STEPS");
            mat.DisableKeyword("_CELPRIMARYMODE_CURVE");
            mat.EnableKeyword("_CELPRIMARYMODE_SINGLE");
            mat.SetFloat("_CelPrimaryMode", 1f);
            mat.SetColor("_BaseColor", albedo);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", albedo);
            mat.SetColor("_ColorDim", new Color(albedo.r * 0.72f, albedo.g * 0.72f, albedo.b * 0.72f, 0.85f));
            mat.SetFloat("_SelfShadingSize", 0.45f);
            mat.SetFloat("_ShadowEdgeSize", 0.05f);
            mat.SetFloat("_Flatness", 1f);
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", albedoMap);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", albedoMap);

            if (outline)
            {
                mat.EnableKeyword("DR_OUTLINE_ON");
                mat.SetFloat("_OutlineEnabled", 1f);
                mat.SetFloat("_OutlineWidth", 2.2f);
                mat.SetColor("_OutlineColor", new Color(0.05f, 0.05f, 0.06f, 1f));
                mat.SetShaderPassEnabled("Outline", true);
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void EnsureLightPlaneMaterial()
        {
            var shader = Shader.Find("FlatKit/LightPlane");
            if (shader == null)
                return;

            var mat = AssetDatabase.LoadAssetAtPath<Material>(LightPlaneMatPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "Scene1-LightPlane" };
                AssetDatabase.CreateAsset(mat, LightPlaneMatPath);
            }
            else
            {
                mat.shader = shader;
            }

            mat.EnableKeyword("_ALLOWALPHAOVERFLOW_OFF");
            mat.SetColor("_Color", new Color(1.35f, 1.15f, 0.72f, 0.38f));
            mat.SetFloat("_Depth", 80f);
            mat.SetFloat("_CameraDistanceFadeFar", 28f);
            mat.SetFloat("_CameraDistanceFadeClose", 2f);
            mat.SetFloat("_UvFadeX", 2.4f);
            mat.SetFloat("_UvFadeY", 5.5f);
            EditorUtility.SetDirty(mat);
        }

        static void EnsureObjectOutlineMaterial()
        {
            var shader = Shader.Find("CollarCali/Scene1ObjectOutline");
            if (shader == null)
                return;

            var mat = AssetDatabase.LoadAssetAtPath<Material>(ObjectOutlineMatPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "Scene1-ObjectOutline" };
                AssetDatabase.CreateAsset(mat, ObjectOutlineMatPath);
            }
            else
            {
                mat.shader = shader;
            }

            mat.SetColor("_OutlineColor", new Color(0.05f, 0.05f, 0.06f, 1f));
            mat.SetFloat("_OutlineWidth", 2.2f);
            mat.SetShaderPassEnabled("Outline", true);
            EditorUtility.SetDirty(mat);
        }

        static void SpawnScene1LightPlanes(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "Scene1LightPlanes")
                    return;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(LightPlaneMatPath);
            if (mat == null)
                return;

            var parent = new GameObject("Scene1LightPlanes");
            SceneManager.MoveGameObjectToScene(parent, scene);

            var centers = new[]
            {
                new Vector3(-0.5f, 10f, -8f),
                new Vector3(35.7f, 28f, -9.7f),
                new Vector3(23f, 26f, -19.5f),
                new Vector3(-15f, 24f, -34f),
                new Vector3(-41f, 16f, -5f)
            };

            for (int i = 0; i < centers.Length; i++)
            {
                CreateLightPlaneQuad(parent.transform, $"LightPlane-{i}A", centers[i], Quaternion.identity, mat);
                CreateLightPlaneQuad(parent.transform, $"LightPlane-{i}B", centers[i], Quaternion.Euler(0f, 90f, 0f), mat);
            }
        }

        static void CreateLightPlaneQuad(Transform parent, string name, Vector3 position, Quaternion rotation, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = new Vector3(3.4f, 18f, 1f);

            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);

            var meshRenderer = go.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = mat;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }
    }
}
#endif
