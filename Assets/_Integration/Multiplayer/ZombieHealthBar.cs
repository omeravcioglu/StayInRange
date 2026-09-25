using UnityEngine;
using UnityEngine.UI;

namespace CollarCali
{
    /// <summary>
    /// Chunky cartoon health pill above a zombie's head. Rounded, thick-outlined and a bit bouncy
    /// rather than a thin serious bar.
    ///
    /// The sprite is generated once at runtime, so this needs no art assets and no prefab wiring.
    /// Health is read from the networked actor when this machine does not own the zombie, so the
    /// bar is correct for both players rather than only the host.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ZombieHealthBar : MonoBehaviour
    {
        [Header("Placement")]
        [SerializeField] float heightAboveHead = 0.45f;

        /// <summary>Bar width in metres. Height follows from the rect's aspect.</summary>
        [SerializeField] float worldWidth = 0.9f;

        // The rect is sized in PIXELS and the transform is scaled UNIFORMLY. Scaling x and y by
        // different amounts is what smeared the 9-sliced rounded caps into flat streaks - the
        // corner slices stretched with the bar. 300x64 also keeps the 30px corner slices inside
        // the bar's height, so the caps stay properly round.
        const float RectWidth = 300f;
        const float RectHeight = 64f;

        [Header("Visibility")]
        /// <summary>Beyond this the bar is hidden; a horde of distant bars is just noise.</summary>
        [SerializeField] float visibleDistance = 30f;

        /// <summary>Hidden until first damaged, so an untouched crowd stays clean.</summary>
        [SerializeField] bool hideUntilDamaged = true;

        static Sprite _pill;

        ZombieHealth _health;
        Transform _head;
        Canvas _canvas;
        RectTransform _root;
        Image _fill;
        Transform _cameraTransform;

        float _shown01 = 1f;
        float _pop;
        bool _everDamaged;

        void Start()
        {
            _health = GetComponent<ZombieHealth>();

            var animator = GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
                _head = animator.GetBoneTransform(HumanBodyBones.Head);

            Build();
        }

        void OnDestroy()
        {
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }

        void LateUpdate()
        {
            if (_canvas == null || _health == null)
                return;

            float max = Mathf.Max(1f, _health.MaxHealth);
            float current = ResolveHealth();
            float target01 = Mathf.Clamp01(current / max);

            if (target01 < 0.999f)
                _everDamaged = true;

            bool dead = current <= 0f || _health.IsDead;
            if (dead || (hideUntilDamaged && !_everDamaged) || !WithinRange())
            {
                _canvas.enabled = false;
                return;
            }

            _canvas.enabled = true;

            // Fill chases the real value so a big hit drains visibly instead of snapping.
            if (target01 < _shown01)
                _pop = 1f;
            _shown01 = Mathf.MoveTowards(_shown01, target01, Time.deltaTime * 1.6f);

            _fill.fillAmount = _shown01;
            _fill.color = BandColour(_shown01);

            PositionBar();
        }

        /// <summary>
        /// On a client the local ZombieHealth is never damaged - the master owns it - so the value
        /// comes off the networked actor, which carries rounded HP in HitsLeft.
        /// </summary>
        float ResolveHealth()
        {
#if CMPSETUP_COMPLETE
            var actor = NetworkWorldActor.FindFor(gameObject);
            if (actor != null && actor.Object != null && actor.Object.IsValid &&
                !actor.Object.HasStateAuthority)
            {
                return Mathf.Max(0, actor.HitsLeft);
            }
#endif
            return _health.Health;
        }

        bool WithinRange()
        {
            var cam = ResolveCamera();
            if (cam == null)
                return false;
            return (cam.position - transform.position).sqrMagnitude <= visibleDistance * visibleDistance;
        }

        Transform ResolveCamera()
        {
            if (_cameraTransform != null && _cameraTransform.gameObject.activeInHierarchy)
                return _cameraTransform;

            var cam = Camera.main;
            _cameraTransform = cam != null ? cam.transform : null;
            return _cameraTransform;
        }

        void PositionBar()
        {
            var anchor = _head != null
                ? _head.position
                : transform.position + Vector3.up * 1.8f;

            _root.position = anchor + Vector3.up * heightAboveHead;

            var cam = ResolveCamera();
            if (cam != null)
            {
                // Billboard on the camera's forward rather than looking at it, so a row of bars
                // stays parallel instead of fanning out at the screen edges.
                _root.rotation = Quaternion.LookRotation(cam.forward, Vector3.up);
            }

            // Pop on damage, applied uniformly so the pill never distorts.
            _pop = Mathf.MoveTowards(_pop, 0f, Time.deltaTime * 3f);
            float bounce = 1f + 0.18f * Mathf.Sin(_pop * Mathf.PI);
            float scale = (worldWidth / RectWidth) * bounce;
            _root.localScale = new Vector3(scale, scale, scale);
        }

        static Color BandColour(float fill01)
        {
            // Saturated cartoon greens through to red - no muddy midtones.
            if (fill01 > 0.6f)
                return new Color(0.42f, 0.86f, 0.28f);
            if (fill01 > 0.3f)
                return new Color(0.98f, 0.78f, 0.18f);
            return new Color(0.93f, 0.27f, 0.24f);
        }

        #region Build

        void Build()
        {
            var go = new GameObject("ZombieHealthBar", typeof(RectTransform));
            _root = go.GetComponent<RectTransform>();
            _root.sizeDelta = new Vector2(RectWidth, RectHeight);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 5;

            // Outline first, then track, then fill - three nested pills gives the thick cartoon
            // border without needing a separate outline shader.
            var outline = CreateImage(_root, new Color(0.06f, 0.05f, 0.08f, 0.95f), Vector2.zero);
            var track = CreateImage(outline.rectTransform, new Color(0.20f, 0.19f, 0.24f, 1f),
                new Vector2(-10f, -10f));
            _fill = CreateImage(track.rectTransform, Color.green, new Vector2(-8f, -8f));

            _fill.type = Image.Type.Filled;
            _fill.fillMethod = Image.FillMethod.Horizontal;
            _fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _fill.fillAmount = 1f;
            // A filled image ignores slicing anyway; setting it explicitly keeps the sprite from
            // being drawn with border geometry it will not use.
            _fill.preserveAspect = false;
        }

        Image CreateImage(Transform parent, Color colour, Vector2 inset)
        {
            var go = new GameObject("Layer", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(-inset.x * 0.5f, -inset.y * 0.5f);
            rt.offsetMax = new Vector2(inset.x * 0.5f, inset.y * 0.5f);

            var image = go.AddComponent<Image>();
            image.sprite = PillSprite();
            image.type = Image.Type.Sliced;
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// A 64x64 rounded-rect drawn in code and sliced, so the pill keeps its round ends at any
        /// width. Generated once and shared by every zombie.
        /// </summary>
        static Sprite PillSprite()
        {
            if (_pill != null)
                return _pill;

            const int size = 64;
            const float radius = 30f;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "ZombieHealthBarPill",
            };

            var pixels = new Color32[size * size];
            var centre = new Vector2(size * 0.5f - 0.5f, size * 0.5f - 0.5f);
            var half = new Vector2(centre.x, centre.y);

            // Rounded-rectangle signed distance, not a circle: a circle would leave the middle of
            // each edge transparent, and the 9-slice stretches exactly that middle strip.
            var straight = half - Vector2.one * radius;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(Mathf.Abs(x - centre.x), Mathf.Abs(y - centre.y)) - straight;
                    float outside = Vector2.Max(p, Vector2.zero).magnitude;
                    float inside = Mathf.Min(Mathf.Max(p.x, p.y), 0f);
                    float distance = outside + inside - radius;

                    // Half-pixel feather keeps the round ends smooth instead of stair-stepped.
                    float alpha = Mathf.Clamp01(0.5f - distance);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            _pill = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(30f, 30f, 30f, 30f));
            _pill.name = "ZombieHealthBarPill";
            return _pill;
        }

        #endregion
    }
}
