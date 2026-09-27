using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// The arc a throw would follow, drawn while aiming, with a ring where it would land.
    ///
    /// Simulated with the same gravity and the same launch velocity the throw itself uses, and cut
    /// off at the first wall or floor it meets, so what the player sees is where the body goes -
    /// give or take the tumbling a ragdoll adds on the way.
    ///
    /// Drawn the way the world sheet draws it: a dashed line with an ink edge, yellow while charging
    /// and red at full charge, and an inked ring where it lands. Each stroke is two lines - the ink
    /// one wider and underneath - so it reads against any floor.
    ///
    /// A plain class rather than a component: it is owned by BodyTelekinesis and only ever needs
    /// a handful of line renderers of its own.
    /// </summary>
    public class ThrowTrajectory
    {
        const int RingSegments = 28;
        const float RingRadius = 0.45f;

        // The sheet's dashes are 14 on, 10 off; in the world that is a quarter metre of line and a
        // gap a little shorter.
        const float DashMetres = 0.26f;
        const float GapMetres = 0.18f;

        const float ArcWidth = 0.05f;
        const float RingWidth = 0.045f;
        const float InkExtra = 0.045f;

        static readonly Color Charging = new Color(1f, 0.894f, 0.353f, 1f);   // #FFE45A
        static readonly Color FullCharge = new Color(1f, 0.302f, 0.2f, 1f);   // #FF4D33
        static readonly Color Ink = new Color(0.043f, 0.043f, 0.047f, 0.9f);  // #0B0B0C

        static Texture2D _dashTexture;

        readonly GameObject _root;
        readonly LineRenderer _arcInk;
        readonly LineRenderer _arc;
        readonly LineRenderer _ringInk;
        readonly LineRenderer _ring;
        readonly Vector3[] _ringPoints = new Vector3[RingSegments];
        Vector3[] _points = new Vector3[64];

        public ThrowTrajectory()
        {
            _root = new GameObject("ThrowTrajectory");
            var solid = CreateMaterial("ThrowTrajectory");
            var dashed = CreateMaterial("ThrowTrajectory Dashes");
            if (dashed != null)
            {
                // Tile mode repeats the texture once per world unit; the tiling makes that one dash
                // and one gap.
                dashed.mainTexture = DashTexture();
                dashed.mainTextureScale = new Vector2(1f / (DashMetres + GapMetres), 1f);
            }

            // Ink first and one sort step behind, so the coloured line always draws on top of it.
            _arcInk = CreateLine("Arc Ink", dashed, ArcWidth + InkExtra, 0, dashes: true);
            _arc = CreateLine("Arc", dashed, ArcWidth, 1, dashes: true);
            _ringInk = CreateLine("LandingRing Ink", solid, RingWidth + InkExtra, 0, dashes: false);
            _ring = CreateLine("LandingRing", solid, RingWidth, 1, dashes: false);
            _arcInk.startColor = _arcInk.endColor = Ink;
            _ringInk.startColor = _ringInk.endColor = Ink;
            foreach (var ring in new[] { _ringInk, _ring })
            {
                ring.loop = true;
                ring.positionCount = RingSegments;
            }

            Hide();
        }

        public void Show(Vector3 origin, Vector3 velocity, float charge01, PlayerTuning.ThrowSettings settings)
        {
            if (_root == null)
                return;

            int steps = Mathf.Max(2, settings.trajectorySteps);
            if (_points.Length < steps + 1)
                _points = new Vector3[steps + 1];

            int mask = HiddenSpawnUtility.DefaultSightBlockers();
            var gravity = Physics.gravity;
            _points[0] = origin;
            int count = 1;
            bool landed = false;
            var landing = Vector3.zero;
            var normal = Vector3.up;

            for (int i = 1; i <= steps; i++)
            {
                float t = i * settings.trajectoryStepSeconds;
                var next = origin + velocity * t + 0.5f * t * t * gravity;
                var previous = _points[count - 1];

                if (Physics.Linecast(previous, next, out var hit, mask, QueryTriggerInteraction.Ignore))
                {
                    _points[count++] = hit.point;
                    landed = true;
                    landing = hit.point;
                    normal = hit.normal;
                    break;
                }

                _points[count++] = next;
            }

            // Yellow while charging, red once it is at full strength.
            var colour = charge01 >= 0.999f ? FullCharge : Color.Lerp(Charging, FullCharge, charge01 * 0.6f);
            _arc.startColor = _arc.endColor = colour;
            SetArc(_arcInk, count);
            SetArc(_arc, count);

            _ring.enabled = _ringInk.enabled = landed;
            if (landed)
            {
                _ring.startColor = _ring.endColor = colour;
                var rotation = Quaternion.FromToRotation(Vector3.up, normal);
                for (int i = 0; i < RingSegments; i++)
                {
                    float angle = i / (float)RingSegments * Mathf.PI * 2f;
                    var local = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * RingRadius;
                    _ringPoints[i] = landing + normal * 0.03f + rotation * local;
                }

                _ringInk.SetPositions(_ringPoints);
                _ring.SetPositions(_ringPoints);
            }
        }

        void SetArc(LineRenderer line, int count)
        {
            line.positionCount = count;
            line.SetPositions(_points);
            line.enabled = true;
        }

        public void Hide()
        {
            foreach (var line in new[] { _arcInk, _arc, _ringInk, _ring })
            {
                if (line != null)
                    line.enabled = false;
            }
        }

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
        }

        LineRenderer CreateLine(string name, Material material, float width, int order, bool dashes)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.widthMultiplier = width;
            line.numCapVertices = dashes ? 0 : 2;
            line.textureMode = dashes ? LineTextureMode.Tile : LineTextureMode.Stretch;
            line.sortingOrder = order;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (material != null)
                line.sharedMaterial = material;
            return line;
        }

        /// <summary>One dash and one gap, left to right: opaque white, then clear.</summary>
        static Texture2D DashTexture()
        {
            if (_dashTexture != null)
                return _dashTexture;

            const int width = 44;
            int on = Mathf.RoundToInt(width * DashMetres / (DashMetres + GapMetres));
            _dashTexture = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                name = "ThrowTrajectory Dash",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            for (int x = 0; x < width; x++)
                _dashTexture.SetPixel(x, 0, x < on ? Color.white : new Color(1f, 1f, 1f, 0f));
            _dashTexture.Apply(false, true);
            return _dashTexture;
        }

        /// <summary>
        /// An unlit, vertex-coloured material. Tried in order because a build only contains shaders
        /// something references; if none survive, the line still draws with Unity's fallback.
        /// </summary>
        static Material CreateMaterial(string name)
        {
            foreach (var shader in new[] { "Sprites/Default", "Universal Render Pipeline/Unlit", "Unlit/Color" })
            {
                var found = Shader.Find(shader);
                if (found != null)
                    return new Material(found) { name = name };
            }

            return null;
        }
    }
}
