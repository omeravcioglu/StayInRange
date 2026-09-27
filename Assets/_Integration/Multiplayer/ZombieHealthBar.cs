using CollarCali.UI;
using TMPro;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// The health bar over a zombie's head, in the redesign's hand-drawn style: always red, so an
    /// enemy never reads like a teammate's green bar, with a white slice showing the damage that just
    /// landed as it drains away.
    ///
    /// Shows after the first hit and hides past 30 m and on death, so an untouched crowd stays clean.
    /// Health is read from the networked actor when this machine does not own the zombie, so the
    /// bar is correct for every player rather than only the host.
    ///
    /// Something that cannot be hurt (the creep stalker) shows a skull and RUN! instead, from the
    /// moment it is in range.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ZombieHealthBar : MonoBehaviour
    {
        [Header("Placement")]
        [SerializeField] float heightAboveHead = 0.45f;

        /// <summary>Bar width in metres. Height follows from the rect's aspect.</summary>
        [SerializeField] float worldWidth = 0.9f;

        // Sized in canvas units and scaled uniformly, so the hand-drawn frame never distorts.
        const float RectWidth = 300f;
        const float RectHeight = 44f;

        [Header("Visibility")]
        /// <summary>Beyond this the bar is hidden; a horde of distant bars is just noise.</summary>
        [SerializeField] float visibleDistance = 30f;

        /// <summary>Hidden until first damaged, so an untouched crowd stays clean.</summary>
        [SerializeField] bool hideUntilDamaged = true;

        static readonly Color Enemy = UiTheme.Rgb(0xFF5A52);
        static readonly Color NearlyDead = UiTheme.Rgb(0xC8160F);
        static readonly Color Slice = new Color(1f, 1f, 1f, 0.9f);

        ZombieHealth _health;
        Transform _head;
        Canvas _canvas;
        RectTransform _root;
        SketchBar _bar;
        Transform _cameraTransform;

        float _ghost01 = 1f;
        float _pop;
        bool _everDamaged;
        float _last01 = 1f;
        bool _unkillable;
        RectTransform _warning;

        /// <summary>The bar's width in metres; the creep's is a little wider than a zombie's.</summary>
        public void SetWorldWidth(float metres) => worldWidth = Mathf.Max(0.1f, metres);

        /// <summary>
        /// For an enemy that cannot be hurt: a skull and RUN! in place of the bar, shown whenever it
        /// is in range rather than after a hit, since there will never be one.
        /// </summary>
        public void ShowAsUnkillable()
        {
            _unkillable = true;
            if (_bar != null)
                BuildWarning();
        }

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

            if (_unkillable)
            {
                bool shown = !_health.IsDead && WithinRange();
                _canvas.enabled = shown;
                if (shown)
                    PositionBar();
                return;
            }

            float max = Mathf.Max(1f, _health.MaxHealth);
            float current = ResolveHealth();
            float value01 = Mathf.Clamp01(current / max);

            if (value01 < 0.999f)
                _everDamaged = true;

            bool dead = current <= 0f || _health.IsDead;
            if (dead || (hideUntilDamaged && !_everDamaged) || !WithinRange())
            {
                _canvas.enabled = false;
                return;
            }

            _canvas.enabled = true;

            // The bar drops at once; the white slice behind it drains, so a big hit reads as big.
            if (value01 < _last01 - 0.0001f)
                _pop = 1f;
            _last01 = value01;
            _ghost01 = Mathf.Max(value01, Mathf.MoveTowards(_ghost01, value01, Time.deltaTime * 0.9f));

            _bar.SetValue(value01);
            _bar.SetFillColor(value01 < 0.25f ? NearlyDead : Enemy);
            _bar.SetGhost(_ghost01, Slice);

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

        /// <summary>
        /// The camera drawing the screen: the player's, or the spectator's while dead - that one is
        /// untagged, so Camera.main alone left every bar invisible to a spectator.
        /// </summary>
        Transform ResolveCamera()
        {
            if (_cameraTransform != null && _cameraTransform.gameObject.activeInHierarchy)
                return _cameraTransform;

            var cam = Camera.main;
            if (cam == null || !cam.isActiveAndEnabled)
            {
                cam = null;
                foreach (var candidate in Camera.allCameras)
                {
                    if (candidate != null && candidate.isActiveAndEnabled && candidate.targetTexture == null)
                    {
                        cam = candidate;
                        break;
                    }
                }
            }

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

            // A small bounce on each hit, uniform so the frame never distorts.
            _pop = Mathf.MoveTowards(_pop, 0f, Time.deltaTime * 3f);
            float bounce = 1f + 0.18f * Mathf.Sin(_pop * Mathf.PI);
            float scale = (worldWidth / RectWidth) * bounce;
            _root.localScale = new Vector3(scale, scale, scale);
        }

        void Build()
        {
            var go = new GameObject("ZombieHealthBar", typeof(RectTransform));
            _root = go.GetComponent<RectTransform>();
            _root.sizeDelta = new Vector2(RectWidth, RectHeight);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 5;

            _bar = SketchBar.Create(_root, "Bar", new Vector2(RectWidth, RectHeight), Enemy, shine: false);
            ((RectTransform)_bar.transform).Fill();

            if (_unkillable)
                BuildWarning();
        }

        /// <summary>The world sheet's stalker: a skull with red eyes and RUN! in red, where the bar would be.</summary>
        void BuildWarning()
        {
            if (_warning != null)
                return;

            _bar.gameObject.SetActive(false);
            _warning = UiKit.CreateRow(_root, "Run", 12f, TextAnchor.MiddleCenter);
            _warning.anchorMin = _warning.anchorMax = _warning.pivot = new Vector2(0.5f, 0.5f);
            _warning.anchoredPosition = Vector2.zero;
            UiKit.CreateImage(_warning, "Skull", UiSprites.Skull, Color.white).Sized(60f, 60f);
            UiKit.CreateText(_warning, "Text", "RUN!",
                TextStyle.Prompt.WithSize(68f).WithTint(ColorRole.Danger).WithAlign(TextAlignmentOptions.Center));
        }
    }
}
