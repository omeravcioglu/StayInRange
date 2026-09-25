using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Scene 1 only: per-object outlines on spears/cubes, plus a few FlatKit light planes.
    /// Does not touch Steve or the ground meshes.
    /// </summary>
    public sealed class Scene1FlatKitLook : MonoBehaviour
    {
        const string OutlineMatResource = "Scene1-ObjectOutline";
        const string LightMatResource = "Scene1-LightPlane";
        const string OutlineShaderName = "CollarCali/Scene1ObjectOutline";
        const string LightShaderName = "FlatKit/LightPlane";
        const string StylizedShaderName = "FlatKit/Stylized Surface";

        static readonly Vector3[] ShaftCenters =
        {
            new Vector3(-0.5f, 10f, -8f),
            new Vector3(35.7f, 28f, -9.7f),
            new Vector3(23f, 26f, -19.5f),
            new Vector3(-15f, 24f, -34f),
            new Vector3(-41f, 16f, -5f)
        };

        Material _outlineMat;
        Material _lightMat;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            TryAttach(SceneManager.GetActiveScene());
        }

        static void OnEnableHook()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            OnEnableHook();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            TryAttach(scene);
        }

        public static void TryAttach(Scene scene)
        {
            if (!scene.IsValid() || scene.name != "1")
                return;

            foreach (var existing in FindObjectsByType<Scene1FlatKitLook>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (existing != null && existing.gameObject.scene == scene)
                    return;
            }

            var hook = FindFirstObjectByType<Scene1FlatKitCameraHook>();
            GameObject go;
            if (hook != null && hook.gameObject.scene == scene)
            {
                go = hook.gameObject;
            }
            else
            {
                go = new GameObject("Scene1FlatKitLook");
                SceneManager.MoveGameObjectToScene(go, scene);
            }

            go.AddComponent<Scene1FlatKitLook>();
        }

        void Awake()
        {
            _outlineMat = LoadOrCreate(OutlineMatResource, OutlineShaderName);
            _lightMat = LoadOrCreate(LightMatResource, LightShaderName);
            ApplyObjectOutlines();
            SpawnLightPlanes();
        }

        static Material LoadOrCreate(string resourceName, string shaderName)
        {
            var fromResources = Resources.Load<Material>(resourceName);
            if (fromResources != null)
                return fromResources;

            var shader = Shader.Find(shaderName);
            if (shader == null || shader.name.Contains("InternalError"))
                return null;

            var created = new Material(shader) { name = resourceName + " (Runtime)" };
            if (shaderName == LightShaderName)
            {
                created.EnableKeyword("_ALLOWALPHAOVERFLOW_OFF");
                created.SetColor("_Color", new Color(1.35f, 1.15f, 0.72f, 0.38f));
                created.SetFloat("_Depth", 80f);
                created.SetFloat("_CameraDistanceFadeFar", 28f);
                created.SetFloat("_CameraDistanceFadeClose", 2f);
                created.SetFloat("_UvFadeX", 2.4f);
                created.SetFloat("_UvFadeY", 5.5f);
            }
            return created;
        }

        void ApplyObjectOutlines()
        {
            var stylized = Shader.Find(StylizedShaderName);
            bool stylizedOk = stylized != null && !stylized.name.Contains("InternalError");

            var renderers = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer.gameObject.scene != gameObject.scene)
                    continue;
                if (!WantsOutline(renderer.gameObject.name))
                    continue;

                var shared = renderer.sharedMaterial;
                if (stylizedOk && shared != null && shared.shader == stylized)
                {
                    EnableStylizedOutline(shared);
                    continue;
                }

                AppendOutlineMaterial(renderer);
            }
        }

        static bool WantsOutline(string name)
        {
            return name.StartsWith("Spears_4") || name == "Cube" || name.StartsWith("Cube ");
        }

        static void EnableStylizedOutline(Material mat)
        {
            mat.EnableKeyword("DR_OUTLINE_ON");
            if (mat.HasProperty("_OutlineEnabled"))
                mat.SetFloat("_OutlineEnabled", 1f);
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", 2.2f);
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(0.05f, 0.05f, 0.06f, 1f));
            mat.SetShaderPassEnabled("Outline", true);
        }

        void AppendOutlineMaterial(MeshRenderer renderer)
        {
            if (_outlineMat == null)
                return;

            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] != null && mats[i].shader != null && mats[i].shader.name == OutlineShaderName)
                    return;
            }

            var next = new Material[mats.Length + 1];
            for (int i = 0; i < mats.Length; i++)
                next[i] = mats[i];
            next[mats.Length] = _outlineMat;
            renderer.sharedMaterials = next;
        }

        void SpawnLightPlanes()
        {
            if (_lightMat == null)
                return;

            foreach (var root in gameObject.scene.GetRootGameObjects())
            {
                if (root.name == "Scene1LightPlanes")
                    return;
            }
            if (transform.Find("Scene1LightPlanes") != null)
                return;

            var parent = new GameObject("Scene1LightPlanes");
            parent.transform.SetParent(transform, false);

            for (int i = 0; i < ShaftCenters.Length; i++)
            {
                SpawnCrossedShaft(parent.transform, ShaftCenters[i], i);
            }
        }

        void SpawnCrossedShaft(Transform parent, Vector3 center, int index)
        {
            CreatePlane(parent, $"LightPlane-{index}A", center, Quaternion.identity, new Vector3(3.4f, 18f, 1f));
            CreatePlane(parent, $"LightPlane-{index}B", center, Quaternion.Euler(0f, 90f, 0f), new Vector3(3.4f, 18f, 1f));
        }

        void CreatePlane(Transform parent, string name, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = scale;

            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _lightMat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }
}
