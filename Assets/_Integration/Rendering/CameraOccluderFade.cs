using System.Collections.Generic;
using MalbersAnimations.Controller;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Rendering;

namespace CollarCali
{
    /// <summary>
    /// Whatever stands between the camera and the player dissolves away, instead of the camera jumping
    /// in front of it (the rig's obstacle avoidance is off, so the camera keeps its distance).
    ///
    /// Each frame, once the camera has moved, the view from the camera to the player - and to a point lower
    /// down, so the legs show - is searched with thin rays: the line of sight itself, both ways, and a ring
    /// of rays from the camera to points Ring round it, across the view. In the way is whatever any of them
    /// crosses, so what hides only part of the body counts too, whatever its shape: the flank of a boulder,
    /// a round pillar, a slanted wall. The ring is narrower than the gap the body keeps from a wall it
    /// touches, climbs or runs along, and its rays stop short of the player, so what is only next to the
    /// player stays: that wall, the crate in front of them. The renderers in the way put on the Occluder
    /// Dissolve material, made from each of their own materials (the colour texture, mapped the way the
    /// material maps it; the colour it really applies; normal map; alpha clipping), and dissolve out; once
    /// the view is clear they dissolve back in and get their own materials and property block again.
    ///
    /// Never faded: floors (up-facing surfaces the camera looks down on, and whatever the player stands
    /// on - CameraGroundCollider.StandingOn: the first thing under them, and the tunnel or hall under a puddle,
    /// a plate or a heap of rubbish lying on its floor), characters, bodies physics moves, triggers, terrain,
    /// objects bigger than Max Size (a whole level in one mesh), anything not on Layers - the level's surfaces -
    /// and renderers a copy could not look like, such as one with an outline-only material, which URP's forward
    /// pass does not draw. CameraGroundCollider keeps the camera out of what is never faded (CanSeeThrough): gone
    /// through, it would hide the player.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    public class CameraOccluderFade : MonoBehaviour
    {
        [Tooltip("CollarCali/Occluder Dissolve.")]
        public Shader dissolveShader;

        [Tooltip("What can fade: the level's surfaces.")]
        public LayerMask layers = 1 << 0;

        // Not called radius any more, on purpose: the rig prefab still holds the old thick ray's 0.35 m under
        // that name, and a ring that wide would reach the wall the player climbs or runs along.
        [Tooltip("How wide round each point of the player the view to them is kept clear (m): the radius of " +
                 "the ring of rays from the camera. Keep it under the ~0.2 m the body keeps from a wall it " +
                 "touches, climbs or runs along, or that wall fades.")]
        [Range(0.05f, 0.3f)] public float ring = 0.15f;

        [Tooltip("How far below the camera target the view is kept clear too, so the legs show (m). " +
                 "The point stays high enough over the ground the player stands on for its ring to clear it.")]
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

        /// <summary>How many rays the ring round each point of the player has.</summary>
        const int RingRays = 8;

        /// <summary>
        /// The ring's rays end this much short of the player's depth along the view (m): they reach the
        /// player's side of what is between, never what stands level with or beyond the player.
        /// </summary>
        const float RingShort = 0.1f;

        /// <summary>How far under the camera target the ground the player stands on is looked for (m).</summary>
        const float GroundSearch = 3f;

        /// <summary>The lowered point's ring stays this much above that ground (m).</summary>
        const float GroundClearance = 0.05f;

        /// <summary>Occluder Dissolve's object-space triplanar mapping of its colour texture.</summary>
        const string TriplanarKeyword = "_OCCLUDER_TRIPLANAR";

        // Made on first use, not in static field initializers: those run while Unity creates the component,
        // where some Unity calls are not allowed (ShaderTagId's constructor threw there once, and the type
        // initializer's exception broke every use of this class), so none is made there at all.
        static bool _idsMade;
        static int _dissolveId, _baseMapId, _baseColorId, _bumpMapId, _cutoffId, _cullId,
                   _edgeColorId, _edgeWidthId, _noiseScaleId, _triplanarScaleId, _triplanarFalloffId;
        static ShaderTagId _lightModeTag, _forwardPass, _forwardOnlyPass;

        class Faded
        {
            public Renderer renderer;
            public Material[] own;
            /// <summary>The renderer's own property block, or null when it had none.</summary>
            public MaterialPropertyBlock ownBlock;
            public float amount;
            public bool wanted;
        }

        enum Kind { Texture, Color, Number, Vector }

        /// <summary>What a dissolving copy of a material is: none could look like it, it is its own (see-through), or a made one.</summary>
        enum Copy { None, Itself, Made }

        CinemachineBrain _brain;
        readonly Dictionary<Renderer, Faded> _faded = new Dictionary<Renderer, Faded>();
        // Each material's dissolving copy; the material itself when it stays as it is (see-through), null
        // when no copy could look like it.
        readonly Dictionary<Material, Material> _dissolveOf = new Dictionary<Material, Material>();
        readonly List<Material> _made = new List<Material>();
        readonly Dictionary<Collider, Renderer[]> _renderersOf = new Dictionary<Collider, Renderer[]>();
        readonly HashSet<Renderer> _cannotFade = new HashSet<Renderer>();
        readonly HashSet<Renderer> _inTheWay = new HashSet<Renderer>();
        // The colliders already found in the way this frame: the other rays that cross them skip them.
        readonly HashSet<Collider> _counted = new HashSet<Collider>();
        readonly List<Renderer> _done = new List<Renderer>();
        readonly RaycastHit[] _hits = new RaycastHit[32];
        // What the player stands on this frame, and where each was met.
        readonly RaycastHit[] _ground = new RaycastHit[8];
        int _groundCount;
        // Whether the camera may go through each collider asked about, and what each material's copy would be.
        readonly Dictionary<Collider, bool> _seeThrough = new Dictionary<Collider, bool>();
        readonly Dictionary<Material, Copy> _copyOf = new Dictionary<Material, Copy>();
        readonly List<Renderer> _shown = new List<Renderer>();
        MaterialPropertyBlock _block;

        /// <summary>How many renderers are faded or fading right now.</summary>
        public int FadedCount => _faded.Count;

        /// <summary>Whether the renderer wears its dissolving copy right now: fading out, faded, or fading back in.</summary>
        public bool IsFaded(Renderer renderer) => renderer != null && _faded.ContainsKey(renderer);

        /// <summary>
        /// Whether the camera may go through that collider: in the way, what it shows would not hide the player - it
        /// dissolves, it is see-through anyway, or it shows nothing. Not so for terrain, characters, bodies physics
        /// moves, anything not on Layers, and what a renderer shows that this never fades: one bigger than Max Size
        /// across (a whole level, a hall in one mesh) or drawn opaque with a material no copy could look like. Nor for
        /// anything while this is off or has no dissolve shader. CameraGroundCollider stops the camera at those.
        /// </summary>
        public bool CanSeeThrough(Collider collider)
        {
            if (collider == null)
                return true;
            if (!isActiveAndEnabled || dissolveShader == null)
                return false;
            if (!_seeThrough.TryGetValue(collider, out bool through))
            {
                through = (layers.value & (1 << collider.gameObject.layer)) != 0 && MayFade(collider) && !Hides(collider);
                _seeThrough[collider] = through;
            }

            return through;
        }

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
            _cannotFade.Clear();
            _seeThrough.Clear();
        }

        void OnDestroy()
        {
            foreach (var material in _made)
            {
                if (material != null)
                    Destroy(material);
            }

            _made.Clear();
            _dissolveOf.Clear();
        }

        void LateUpdate()
        {
            if (dissolveShader == null)
                return;

            MakeIds();
            _inTheWay.Clear();
            _counted.Clear();
            if (TryGetTarget(out var target))
            {
                var eye = transform.position;
                // What the player stands on, as the camera's ground collider takes it: the first thing under them, and
                // the tunnel or hall under the puddle, plate or heap of rubbish that is.
                _groundCount = CameraGroundCollider.StandingOn(target, Vector3.up, GroundSearch, layers, _ground);
                Look(target, eye);
                if (downTo > 0f)
                {
                    // Crouched, the target is so low that downTo under it is in the floor, and the floor would
                    // be in the way: the point stays high enough for its ring to clear the ground.
                    var low = target - Vector3.up * downTo;
                    if (_groundCount > 0)
                        low.y = Mathf.Max(low.y, _ground[0].point.y + ring + GroundClearance);
                    if (target.y - low.y > 0.1f)
                        Look(low, eye);
                }

                // What the player stands on stays, even where it is in the way too (stairs below a landing,
                // a floor and its walls in one mesh): the floor going from under their feet is worse. Each of
                // them keeps the renderers of it that are where it was met under the player.
                for (int g = 0; g < _groundCount; g++)
                {
                    foreach (var renderer in RenderersOf(_ground[g].collider))
                    {
                        if (renderer == null)
                            continue;
                        var bounds = renderer.bounds;
                        bounds.Expand(0.1f);
                        if (bounds.Contains(_ground[g].point))
                            _inTheWay.Remove(renderer);
                    }
                }
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

        /// <summary>
        /// What is in the way between the eye and one point of the player: what the line of sight crosses,
        /// searched from both ends since a ray only meets the faces turned towards it, and what any ray of
        /// the ring round it crosses.
        /// </summary>
        void Look(Vector3 target, Vector3 eye)
        {
            var toEye = eye - target;
            float distance = toEye.magnitude;
            if (distance < 0.05f)
                return;
            var direction = toEye / distance;

            Line(target, direction, distance, eye);
            Line(eye, -direction, distance, eye);

            // The ring: rays from the eye to points Ring round the target, across the view, each ending
            // RingShort before the target's depth. A ray is judged by what it crosses, not by where a thick
            // one would first touch, so a round or slanted occluder that hides part of the body is found as
            // soon as it covers one of the points. What the player only stands next to is never crossed:
            // a wall at least Ring from the target, with the camera on the player's side of it, has every
            // ring point on that side too, and a ray between two points on one side of a wall does not reach
            // it; what stands level with the player or beyond them (the wall they climb, the crate in front
            // of them) is past where the rays end. A wall the camera is behind is crossed by the line of
            // sight itself, and fades.
            float shortened = distance - RingShort;
            if (ring <= 0f || shortened < 0.05f)
                return;
            float reach = shortened / distance;
            var side = Vector3.Cross(direction, Vector3.up);
            if (side.sqrMagnitude < 1e-6f)
                side = Vector3.Cross(direction, Vector3.forward);
            side.Normalize();
            var rise = Vector3.Cross(side, direction);
            for (int i = 0; i < RingRays; i++)
            {
                float angle = i * (2f * Mathf.PI / RingRays);
                var point = target + (side * Mathf.Cos(angle) + rise * Mathf.Sin(angle)) * ring;
                var toPoint = point - eye;
                float length = toPoint.magnitude;
                if (length > 0.05f)
                    Line(eye, toPoint / length, length * reach, eye);
            }
        }

        /// <summary>
        /// One thin ray, along the line of sight or one of the ring's: whatever it crosses hides (part of)
        /// the player. Floors under the view and what may not fade are passed over.
        /// </summary>
        void Line(Vector3 from, Vector3 direction, float distance, Vector3 eye)
        {
            int count = Physics.RaycastNonAlloc(from, direction, _hits, distance, layers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                var collider = hit.collider;
                if (collider == null || _counted.Contains(collider))
                    continue;
                if (!MayFade(collider) || IsFloor(hit, eye))
                    continue;
                _counted.Add(collider);
                AddRenderersOf(collider);
            }
        }

        /// <summary>
        /// A floor under the view: a surface facing up and towards the camera, below it. Judged against the
        /// camera whichever end of the view met it, so the steps of a staircase the player stands on, or the
        /// ledge they just came down, are floors from both sides.
        /// </summary>
        static bool IsFloor(RaycastHit hit, Vector3 eye) =>
            hit.normal.y > 0.7f && hit.point.y < eye.y && Vector3.Dot(hit.normal, eye - hit.point) > 0f;

        /// <summary>Level geometry: not terrain, not a character, not a body physics moves.</summary>
        static bool MayFade(Collider collider) =>
            collider != null && !(collider is TerrainCollider) && !IsCharacter(collider) &&
            !(collider.attachedRigidbody != null && !collider.attachedRigidbody.isKinematic);

        static bool IsCharacter(Collider collider) => collider.GetComponentInParent<MAnimal>() != null;

        void AddRenderersOf(Collider collider)
        {
            foreach (var renderer in RenderersOf(collider))
            {
                if (renderer != null)
                    _inTheWay.Add(renderer);
            }
        }

        /// <summary>The renderers that fade with that collider: those that show it, but none bigger than Max Size across.</summary>
        Renderer[] RenderersOf(Collider collider)
        {
            if (_renderersOf.TryGetValue(collider, out var renderers))
                return renderers;

            var found = new List<Renderer>();
            Shown(collider, found);
            found.RemoveAll(r => r.bounds.size.magnitude > maxSize);
            renderers = found.ToArray();
            _renderersOf[collider] = renderers;
            return renderers;
        }

        /// <summary>
        /// The mesh renderers that show a collider: its LOD group's, else its own and its children's, else the nearest one
        /// above it. Characters' are left out.
        /// </summary>
        static void Shown(Collider collider, List<Renderer> found)
        {
            found.Clear();
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

            found.RemoveAll(r => !(r is MeshRenderer || r is SkinnedMeshRenderer) || r.GetComponentInParent<MAnimal>() != null);
        }

        /// <summary>
        /// Whether something showing the collider would stay drawn in the way: a renderer with an opaque material that is
        /// bigger than Max Size across, or whose opaque materials are not all ones a dissolving copy could look like (Fade
        /// leaves such a renderer as it is).
        /// </summary>
        bool Hides(Collider collider)
        {
            Shown(collider, _shown);
            foreach (var renderer in _shown)
            {
                // Its own materials, even while it wears its dissolving copies.
                var materials = _faded.TryGetValue(renderer, out var faded) ? faded.own : renderer.sharedMaterials;
                bool opaque = false, copied = true;
                foreach (var material in materials)
                {
                    if (material == null)
                        continue;
                    if (!_copyOf.TryGetValue(material, out var copy))
                    {
                        copy = Classify(material, out _, out _);
                        _copyOf[material] = copy;
                    }

                    if (copy == Copy.Itself)
                        continue;
                    opaque = true;
                    copied &= copy == Copy.Made;
                }

                if (opaque && (!copied || renderer.bounds.size.magnitude > maxSize))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Puts the renderer's dissolving copies on. Null when it is not faded at all: a copy could not look
        /// like one of its materials, or none of them would dissolve. That is remembered until the next
        /// time this is enabled, so it is not tried every frame.
        /// </summary>
        Faded Fade(Renderer renderer)
        {
            if (_cannotFade.Contains(renderer))
                return null;

            var own = renderer.sharedMaterials;
            var dissolving = new Material[own.Length];
            bool dissolves = false;
            for (int i = 0; i < own.Length; i++)
            {
                // An empty slot stays empty.
                if (own[i] == null)
                    continue;
                var copy = DissolveOf(own[i]);
                if (copy == null)
                {
                    _cannotFade.Add(renderer);
                    return null;
                }

                dissolving[i] = copy;
                dissolves |= copy != own[i];
            }

            if (!dissolves)
            {
                _cannotFade.Add(renderer);
                return null;
            }

            var faded = new Faded { renderer = renderer, own = own, amount = 0f, wanted = true };
            if (renderer.HasPropertyBlock())
            {
                faded.ownBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(faded.ownBlock);
            }

            renderer.sharedMaterials = dissolving;
            _faded[renderer] = faded;
            return faded;
        }

        void PutBack(Faded faded)
        {
            if (faded.renderer == null)
                return;
            faded.renderer.sharedMaterials = faded.own;
            // Its own property block again, or none: a block left on it would keep it out of the SRP Batcher
            // for the rest of the session.
            faded.renderer.SetPropertyBlock(faded.ownBlock);
        }

        void SetAmount(Renderer renderer, float amount)
        {
            _block ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(_block);
            _block.SetFloat(_dissolveId, amount);
            renderer.SetPropertyBlock(_block);
        }

        /// <summary>
        /// The dissolving copy of a material: its colour texture (mapped as the material maps it), the
        /// colour it really applies, normal map and alpha clipping on Occluder Dissolve. A see-through
        /// material is its own copy: it is drawn as it was (and hides little) while the rest of the renderer
        /// dissolves. Null when no copy could look like it, so the renderer is left alone rather than drawn
        /// white: a shader URP's lit forward pass does not draw (an outline-only material would turn into a
        /// second, solid copy of the mesh), or one with neither a colour texture nor a colour that can be
        /// found.
        /// </summary>
        Material DissolveOf(Material own)
        {
            if (!_dissolveOf.TryGetValue(own, out var made))
            {
                made = MakeDissolving(own);
                _dissolveOf[own] = made;
            }

            return made;
        }

        Material MakeDissolving(Material own)
        {
            switch (Classify(own, out int texture, out int color))
            {
                case Copy.None:
                    return null;
                case Copy.Itself:
                    return own;
            }

            var shader = own.shader;
            bool clips = ClipsAlpha(own);

            // ToonScapes' Stone (the rocks, stone walls and pavements) does not map its surface texture by
            // the mesh's UV: it projects it along the object's own axes, 1 / Surface Texture Tiling per
            // unit, blended by Coverage Falloff. The copy maps it the same way, or the stone would jump to
            // another scale the moment the rock starts to fade. Only the copy's lit pass maps it so - its
            // depth and shadow passes use the texture just to clip alpha - so a material that also clipped
            // would keep the UV mapping throughout, its holes the same in every pass (none in the levels does).
            int triplanarTiling = texture >= 0 && shader.GetPropertyName(texture) == "_SurfaceTexture"
                ? Find(shader, Kind.Number, "_SurfaceTextureTiling")
                : -1;
            bool triplanar = triplanarTiling >= 0 && !clips;

            var made = new Material(dissolveShader) { name = own.name + " (dissolving)" };
            if (texture >= 0)
            {
                int id = shader.GetPropertyNameId(texture);
                made.SetTexture(_baseMapId, own.GetTexture(id));
                var scale = Vector2.one;
                var offset = Vector2.zero;
                if ((shader.GetPropertyFlags(texture) & ShaderPropertyFlags.NoScaleOffset) == 0)
                {
                    scale = own.GetTextureScale(id);
                    offset = own.GetTextureOffset(id);
                }

                // The castle pack tiles its colour texture with a vector of its own instead.
                int tiling = Find(shader, Kind.Vector, "_Tiling_Main_Texture");
                if (tiling >= 0)
                {
                    var tile = own.GetVector(shader.GetPropertyNameId(tiling));
                    scale = new Vector2(tile.x, tile.y);
                }

                made.SetTextureScale(_baseMapId, scale);
                made.SetTextureOffset(_baseMapId, offset);

                if (triplanar)
                {
                    float perUnit = own.GetFloat(shader.GetPropertyNameId(triplanarTiling));
                    made.SetFloat(_triplanarScaleId, 1f / Mathf.Max(perUnit, 0.001f));
                    int falloff = Find(shader, Kind.Number, "_CoverageFalloff");
                    if (falloff >= 0)
                        made.SetFloat(_triplanarFalloffId, Mathf.Max(own.GetFloat(shader.GetPropertyNameId(falloff)), 0.01f));
                    made.EnableKeyword(TriplanarKeyword);
                }
            }

            // Only a colour the material really applies: ToonScapes' shaders multiply their Base Tint in
            // only with Enable Color Tint on, and their materials keep an old tint while it is off (the
            // rocks keep a crimson one, the mushrooms a pink one). The original then looks untinted, so the
            // copy is white.
            if (color >= 0)
                made.SetColor(_baseColorId, TintApplied(own) ? own.GetColor(shader.GetPropertyNameId(color)) : Color.white);

            // Not on a triplanar copy: there the colour texture follows the object's axes, not the UV a
            // normal map follows (ToonScapes' own normal maps are triplanar too, under names not looked for).
            int normal = Find(shader, Kind.Texture, "_BumpMap", "_NormalMap", "_Normal");
            var normalMap = normal >= 0 && !triplanar ? own.GetTexture(shader.GetPropertyNameId(normal)) : null;
            if (normalMap != null)
            {
                made.SetTexture(_bumpMapId, normalMap);
                made.EnableKeyword("_NORMALMAP");
            }

            // Cut out (straw, banners, foliage): the copy clips at the same alpha in every pass, the shadow
            // too, instead of turning into solid quads.
            if (clips)
            {
                int cutoff = Find(shader, Kind.Number, "_Cutoff", "_AlphaCutoff", "_AlphaClipThreshold");
                made.SetFloat(_cutoffId, cutoff >= 0 ? own.GetFloat(shader.GetPropertyNameId(cutoff)) : 0.5f);
                made.EnableKeyword("_ALPHATEST_ON");
            }

            int cull = Find(shader, Kind.Number, "_Cull");
            if (cull >= 0)
                made.SetFloat(_cullId, own.GetFloat(shader.GetPropertyNameId(cull)));
            made.enableInstancing = own.enableInstancing;

            made.SetColor(_edgeColorId, edgeColor);
            made.SetFloat(_edgeWidthId, edgeWidth);
            made.SetFloat(_noiseScaleId, noiseScale);
            _made.Add(made);
            return made;
        }

        /// <summary>
        /// What a dissolving copy of the material would be, and the shader's colour texture and colour (-1 for none). A
        /// see-through material is its own copy. None could look like one with no shader, one URP's lit forward pass does
        /// not draw, or one with neither a colour texture nor a colour that can be found.
        /// </summary>
        static Copy Classify(Material own, out int texture, out int color)
        {
            texture = color = -1;
            var shader = own.shader;
            if (shader == null)
                return Copy.None;
            if (own.renderQueue > (int)RenderQueue.GeometryLast)
                return Copy.Itself;
            MakeIds();
            if (!DrawnForward(shader))
                return Copy.None;

            // Whatever the shader marks as its main texture and colour, else the names this project's level
            // shaders use: URP's, ToonScapes' (_MainTexture, _SurfaceTexture, _BaseTint) and the castle
            // pack's (_Base_Color is its texture, _Tint its colour).
            texture = Flagged(shader, Kind.Texture, ShaderPropertyFlags.MainTexture);
            if (texture < 0)
                texture = Find(shader, Kind.Texture, "_BaseMap", "_MainTex", "_MainTexture", "_SurfaceTexture",
                               "_Base_Color", "_BaseColorMap", "_Albedo", "_AlbedoMap");
            color = Flagged(shader, Kind.Color, ShaderPropertyFlags.MainColor);
            if (color < 0)
                color = Find(shader, Kind.Color, "_BaseColor", "_Color", "_MainColor", "_BaseTint", "_Tint");
            return texture < 0 && color < 0 ? Copy.None : Copy.Made;
        }

        static void MakeIds()
        {
            if (_idsMade)
                return;
            _dissolveId = Shader.PropertyToID("_Dissolve");
            _baseMapId = Shader.PropertyToID("_BaseMap");
            _baseColorId = Shader.PropertyToID("_BaseColor");
            _bumpMapId = Shader.PropertyToID("_BumpMap");
            _cutoffId = Shader.PropertyToID("_Cutoff");
            _cullId = Shader.PropertyToID("_Cull");
            _edgeColorId = Shader.PropertyToID("_EdgeColor");
            _edgeWidthId = Shader.PropertyToID("_EdgeWidth");
            _noiseScaleId = Shader.PropertyToID("_NoiseScale");
            _triplanarScaleId = Shader.PropertyToID("_TriplanarScale");
            _triplanarFalloffId = Shader.PropertyToID("_TriplanarFalloff");
            _lightModeTag = new ShaderTagId("LightMode");
            _forwardPass = new ShaderTagId("UniversalForward");
            _forwardOnlyPass = new ShaderTagId("UniversalForwardOnly");
            _idsMade = true;
        }

        /// <summary>Whether URP draws the shader lit in its forward pass, as the copy is drawn.</summary>
        static bool DrawnForward(Shader shader)
        {
            MakeIds();
            for (int pass = 0; pass < shader.passCount; pass++)
            {
                var mode = shader.FindPassTagValue(pass, _lightModeTag);
                if (mode.Equals(_forwardPass) || mode.Equals(_forwardOnlyPass))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the material cuts holes where its texture is clear: URP's Alpha Clipping (the toggle or
        /// its keyword, when the shader has that keyword), or a shader that calls itself a cut-out one.
        /// </summary>
        static bool ClipsAlpha(Material own)
        {
            var shader = own.shader;
            int toggle = Find(shader, Kind.Number, "_AlphaClip");
            if (toggle >= 0 && own.GetFloat(shader.GetPropertyNameId(toggle)) > 0.5f)
                return true;
            var keyword = shader.keywordSpace.FindKeyword("_ALPHATEST_ON");
            if (keyword.isValid && own.IsKeywordEnabled(keyword))
                return true;
            return own.GetTag("RenderType", true) == "TransparentCutout";
        }

        /// <summary>
        /// Whether the material really puts its colour on. ToonScapes' shaders apply their Base Tint only
        /// with their Enable Color Tint toggle on. Its keyword decides where the shader has one - the
        /// shader is drawn by the keyword, whatever the toggle's number says - else the toggle itself.
        /// </summary>
        static bool TintApplied(Material own)
        {
            var shader = own.shader;
            var keyword = shader.keywordSpace.FindKeyword("_ENABLECOLORTINT_ON");
            if (keyword.isValid)
                return own.IsKeywordEnabled(keyword);
            int toggle = Find(shader, Kind.Number, "_EnableColorTint");
            return toggle < 0 || own.GetFloat(shader.GetPropertyNameId(toggle)) > 0.5f;
        }

        /// <summary>
        /// The first of these names the shader itself has as a property of that kind, or -1. Asked of the
        /// shader, not the material: a material keeps values from shaders it had before.
        /// </summary>
        static int Find(Shader shader, Kind kind, params string[] names)
        {
            foreach (var name in names)
            {
                int index = shader.FindPropertyIndex(name);
                if (index >= 0 && Is(shader, index, kind))
                    return index;
            }

            return -1;
        }

        /// <summary>The shader's first property of that kind carrying the flag, or -1.</summary>
        static int Flagged(Shader shader, Kind kind, ShaderPropertyFlags flag)
        {
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if ((shader.GetPropertyFlags(i) & flag) != 0 && Is(shader, i, kind))
                    return i;
            }

            return -1;
        }

        static bool Is(Shader shader, int index, Kind kind)
        {
            var type = shader.GetPropertyType(index);
            switch (kind)
            {
                case Kind.Texture:
                    return type == ShaderPropertyType.Texture && shader.GetPropertyTextureDimension(index) == TextureDimension.Tex2D;
                case Kind.Color:
                    return type == ShaderPropertyType.Color;
                case Kind.Vector:
                    return type == ShaderPropertyType.Vector;
                default:
                    return type == ShaderPropertyType.Float || type == ShaderPropertyType.Range;
            }
        }
    }
}
