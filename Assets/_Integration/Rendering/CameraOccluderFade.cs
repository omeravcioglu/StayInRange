using System.Collections.Generic;
using MalbersAnimations.Controller;
using Unity.Cinemachine;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Whatever stands between the camera and the player dissolves away, instead of the camera jumping
    /// in front of it (the rig's obstacle avoidance is off, so the camera keeps its distance).
    ///
    /// Each frame, once the camera has moved, a thick ray from the player to the camera and one back
    /// find the level objects in the way. Their renderers put on the Occluder Dissolve material, made
    /// from each of their own materials (texture, colour, normal map), and dissolve out; once the view
    /// is clear they dissolve back in and get their own materials again. Left alone: floors (anything
    /// the ray meets from above), characters, triggers, terrain, objects bigger than Max Size (a whole
    /// level in one mesh) and anything not on Layers - the level's surfaces.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    public class CameraOccluderFade : MonoBehaviour
    {
        [Tooltip("CollarCali/Occluder Dissolve.")]
        public Shader dissolveShader;

        [Tooltip("What can fade: the level's surfaces.")]
        public LayerMask layers = 1 << 0;

        [Tooltip("How thick the view to the player is kept clear (m).")]
        [Range(0.05f, 1f)] public float radius = 0.35f;

        [Tooltip("How far below the camera target the view is kept clear too, so the legs show (m).")]
        [Range(0f, 1.5f)] public float downTo = 0.7f;

        [Tooltip("Seconds to dissolve away, and to come back.")]
        [Range(0.02f, 2f)] public float fadeOut = 0.25f, fadeIn = 0.4f;

        [Tooltip("Objects larger than this across are never faded (m).")]
        [Min(1f)] public float maxSize = 25f;

        [Header("Look")]
        [ColorUsage(false, true)] public Color edgeColor = new Color(0.6f, 1.4f, 2.0f);
        [Range(0f, 0.3f)] public float edgeWidth = 0.08f;
        [Tooltip("Blotches per metre.")]
        [Range(0.2f, 10f)] public float noiseScale = 2.5f;

        static readonly int DissolveId = Shader.PropertyToID("_Dissolve");

        class Faded
        {
            public Renderer renderer;
            public Material[] own;
            public float amount;
            public bool wanted;
        }

        CinemachineBrain _brain;
        readonly Dictionary<Renderer, Faded> _faded = new Dictionary<Renderer, Faded>();
        readonly Dictionary<Material, Material> _dissolveOf = new Dictionary<Material, Material>();
        readonly Dictionary<Collider, Renderer[]> _renderersOf = new Dictionary<Collider, Renderer[]>();
        readonly HashSet<Renderer> _inTheWay = new HashSet<Renderer>();
        readonly List<Renderer> _done = new List<Renderer>();
        readonly RaycastHit[] _hits = new RaycastHit[32];
        MaterialPropertyBlock _block;

        /// <summary>How many renderers are faded or fading right now.</summary>
        public int FadedCount => _faded.Count;

        void Awake()
        {
            _brain = GetComponent<CinemachineBrain>();
            if (dissolveShader == null)
                dissolveShader = Shader.Find("CollarCali/Occluder Dissolve");
        }

        void OnDisable()
        {
            foreach (var faded in _faded.Values)
                PutBack(faded);
            _faded.Clear();
        }

        void OnDestroy()
        {
            foreach (var material in _dissolveOf.Values)
            {
                if (material != null)
                    Destroy(material);
            }

            _dissolveOf.Clear();
        }

        void LateUpdate()
        {
            if (dissolveShader == null)
                return;

            _inTheWay.Clear();
            if (TryGetTarget(out var target))
            {
                var eye = transform.position;
                Look(target, eye);
                if (downTo > 0f)
                    Look(target - Vector3.up * downTo, eye);
            }

            float dt = Time.unscaledDeltaTime;
            foreach (var renderer in _inTheWay)
            {
                if (!_faded.TryGetValue(renderer, out var faded))
                {
                    faded = Fade(renderer);
                    if (faded == null)
                        continue;
                }

                faded.wanted = true;
            }

            _done.Clear();
            foreach (var pair in _faded)
            {
                var faded = pair.Value;
                if (faded.renderer == null)
                {
                    _done.Add(pair.Key);
                    continue;
                }

                if (!_inTheWay.Contains(pair.Key))
                    faded.wanted = false;
                float speed = faded.wanted ? 1f / fadeOut : 1f / fadeIn;
                faded.amount = Mathf.MoveTowards(faded.amount, faded.wanted ? 1f : 0f, dt * speed);
                if (!faded.wanted && faded.amount <= 0f)
                {
                    PutBack(faded);
                    _done.Add(pair.Key);
                    continue;
                }

                SetAmount(faded.renderer, faded.amount);
            }

            foreach (var renderer in _done)
                _faded.Remove(renderer);
        }

        /// <summary>The point the live camera follows: the player's camera target.</summary>
        bool TryGetTarget(out Vector3 target)
        {
            target = default;
            if (_brain == null)
                _brain = GetComponent<CinemachineBrain>();
            var live = _brain != null ? _brain.ActiveVirtualCamera as CinemachineVirtualCameraBase : null;
            var follow = live != null ? (live.Follow != null ? live.Follow : live.LookAt) : null;
            if (follow == null)
                return false;
            target = follow.position;
            return true;
        }

        /// <summary>Everything a thick ray between the player and the camera meets, both ways.</summary>
        void Look(Vector3 target, Vector3 eye)
        {
            var toEye = eye - target;
            float distance = toEye.magnitude;
            if (distance < 0.05f)
                return;
            var direction = toEye / distance;

            Collect(target, direction, distance, fromPlayer: true);
            Collect(eye, -direction, distance, fromPlayer: false);
        }

        void Collect(Vector3 from, Vector3 direction, float distance, bool fromPlayer)
        {
            int count = Physics.SphereCastNonAlloc(from, radius, direction, _hits, distance, layers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                var collider = hit.collider;
                if (collider == null || collider is TerrainCollider)
                    continue;

                // A floor under the view, met from above: never faded.
                bool started = hit.distance <= 0f && hit.point == Vector3.zero;
                if (!started)
                {
                    var facing = fromPlayer ? -direction : direction;
                    if (hit.normal.y > 0.7f && Vector3.Dot(hit.normal, facing) > -0.2f && hit.point.y < from.y)
                        continue;
                }

                if (collider.GetComponentInParent<MAnimal>() != null || collider.attachedRigidbody != null && !collider.attachedRigidbody.isKinematic)
                    continue;

                foreach (var renderer in RenderersOf(collider))
                    _inTheWay.Add(renderer);
            }
        }

        Renderer[] RenderersOf(Collider collider)
        {
            if (_renderersOf.TryGetValue(collider, out var renderers))
                return renderers;

            var found = new List<Renderer>();
            var lod = collider.GetComponentInParent<LODGroup>();
            if (lod != null)
            {
                foreach (var level in lod.GetLODs())
                    foreach (var r in level.renderers)
                        if (r != null)
                            found.Add(r);
            }
            else
            {
                foreach (var r in collider.GetComponentsInChildren<Renderer>())
                    found.Add(r);
                if (found.Count == 0)
                {
                    var parent = collider.GetComponentInParent<Renderer>();
                    if (parent != null)
                        found.Add(parent);
                }
            }

            found.RemoveAll(r => !(r is MeshRenderer || r is SkinnedMeshRenderer) || r.bounds.size.magnitude > maxSize ||
                                 r.GetComponentInParent<MAnimal>() != null);
            renderers = found.ToArray();
            _renderersOf[collider] = renderers;
            return renderers;
        }

        Faded Fade(Renderer renderer)
        {
            var own = renderer.sharedMaterials;
            if (own == null || own.Length == 0)
                return null;

            var dissolving = new Material[own.Length];
            for (int i = 0; i < own.Length; i++)
                dissolving[i] = DissolveOf(own[i]);

            renderer.sharedMaterials = dissolving;
            var faded = new Faded { renderer = renderer, own = own, amount = 0f, wanted = true };
            _faded[renderer] = faded;
            return faded;
        }

        void PutBack(Faded faded)
        {
            if (faded.renderer == null)
                return;
            SetAmount(faded.renderer, 0f);
            faded.renderer.sharedMaterials = faded.own;
        }

        void SetAmount(Renderer renderer, float amount)
        {
            _block ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(_block);
            _block.SetFloat(DissolveId, amount);
            renderer.SetPropertyBlock(_block);
        }

        /// <summary>The dissolving copy of a material: its texture, colour and normal map on Occluder Dissolve.</summary>
        Material DissolveOf(Material own)
        {
            if (own == null)
                own = null;
            if (own != null && _dissolveOf.TryGetValue(own, out var made))
                return made;

            made = new Material(dissolveShader) { name = (own != null ? own.name : "none") + " (dissolving)" };
            if (own != null)
            {
                string texture = First(own, "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo", "_AlbedoMap");
                if (texture != null)
                {
                    made.SetTexture("_BaseMap", own.GetTexture(texture));
                    made.SetTextureScale("_BaseMap", own.GetTextureScale(texture));
                    made.SetTextureOffset("_BaseMap", own.GetTextureOffset(texture));
                }

                string color = First(own, "_BaseColor", "_Color", "_MainColor", "_Tint");
                if (color != null)
                    made.SetColor("_BaseColor", own.GetColor(color));

                string normal = First(own, "_BumpMap", "_NormalMap");
                if (normal != null && own.GetTexture(normal) != null)
                {
                    made.SetTexture("_BumpMap", own.GetTexture(normal));
                    made.EnableKeyword("_NORMALMAP");
                }

                if (own.HasProperty("_Cull"))
                    made.SetFloat("_Cull", own.GetFloat("_Cull"));
                made.enableInstancing = own.enableInstancing;
            }

            made.SetColor("_EdgeColor", edgeColor);
            made.SetFloat("_EdgeWidth", edgeWidth);
            made.SetFloat("_NoiseScale", noiseScale);
            if (own != null)
                _dissolveOf[own] = made;
            return made;
        }

        static string First(Material material, params string[] names)
        {
            foreach (var name in names)
            {
                if (!material.HasProperty(name))
                    continue;
                var kind = material.shader.FindPropertyIndex(name);
                if (kind < 0)
                    continue;
                var type = material.shader.GetPropertyType(kind);
                bool isTexture = type == UnityEngine.Rendering.ShaderPropertyType.Texture;
                bool isColor = type == UnityEngine.Rendering.ShaderPropertyType.Color;
                bool wantsTexture = name.Contains("Map") || name.Contains("Tex") || name == "_Albedo";
                if (wantsTexture ? isTexture : isColor)
                    return name;
            }

            return null;
        }
    }
}
