using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// Screen-edge feedback: red splats creep in from the sides when you are hurt, a short green
    /// flush when you heal, and under 30% health the edges pulse dark red while the world drains of
    /// colour.
    ///
    /// Cowsins drew a single flat tint for hurt and heal, only in first person; this works in both
    /// modes because it listens to the player's health rather than to a controller. The grey-out is a
    /// URP volume, so it shows wherever the camera renders post-processing; the red pulse is plain UI
    /// and always shows.
    /// </summary>
    public class DamageFxView : MonoBehaviour
    {
        static readonly Color HurtTint = UiTheme.Rgb(0xE0141B);
        static readonly Color HealTint = UiTheme.Rgb(0x59FF8C);
        static readonly Color CriticalTint = UiTheme.Rgb(0x7A0A0E);

        const float SplatSeconds = 0.9f;
        const float FlashSeconds = 0.6f;
        const float HealSeconds = 0.5f;

        Image _left;
        Image _right;
        Image _edges;

        float _hurtAt = -10f;
        float _hurtStrength;
        float _healAt = -10f;
        bool _critical;

        Volume _greyOut;
        VolumeProfile _profile;

        public static DamageFxView Create(Transform parent)
        {
            var rect = UiKit.CreateRect("DamageFx", parent);
            rect.Fill();
            var view = rect.gameObject.AddComponent<DamageFxView>();
            view.Build(rect);
            return view;
        }

        void Build(RectTransform root)
        {
            _edges = UiKit.CreateImage(root, "Edges", UiSprites.Vignette, Color.clear);
            _edges.rectTransform.Fill();

            // The splat is drawn lying down; stood on end it runs up the side of the screen.
            _left = Splat(root, "Left", new Vector2(0f, 0.5f), 90f);
            _right = Splat(root, "Right", new Vector2(1f, 0.5f), -90f);
        }

        static Image Splat(RectTransform root, string name, Vector2 anchor, float angle)
        {
            var image = UiKit.CreateImage(root, name, UiSprites.Splat, Color.clear);
            var rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(760f, 300f);
            rect.anchoredPosition = new Vector2(anchor.x < 0.5f ? 70f : -70f, 0f);
            rect.localRotation = Quaternion.Euler(0f, 0f, angle);
            return image;
        }

        /// <summary>A hit: <paramref name="severity01"/> is the share of max health it took.</summary>
        public void Hurt(float severity01)
        {
            _hurtAt = Time.unscaledTime;
            _hurtStrength = Mathf.Clamp(0.45f + severity01 * 2f, 0.45f, 1f);
        }

        public void Heal()
        {
            _healAt = Time.unscaledTime;
        }

        public void SetCritical(bool critical)
        {
            _critical = critical;
        }

        /// <summary>Clears everything at once, e.g. on death or revive.</summary>
        public void Clear()
        {
            _hurtAt = -10f;
            _healAt = -10f;
            _critical = false;
        }

        void Update()
        {
            Refresh();
        }

        /// <summary>Applies the current effects now; the gallery calls it, since Update does not run outside play mode.</summary>
        public void Refresh()
        {
            float now = Time.unscaledTime;

            float splat = Fade(now - _hurtAt, SplatSeconds) * _hurtStrength;
            var splatColour = new Color(1f, 1f, 1f, splat * 0.85f);
            _left.color = splatColour;
            _right.color = splatColour;

            float hurt = Fade(now - _hurtAt, FlashSeconds) * 0.45f * _hurtStrength;
            float heal = Fade(now - _healAt, HealSeconds) * 0.35f;
            float pulse = _critical ? 0.3f + 0.22f * (0.5f + 0.5f * Mathf.Sin(now * Mathf.PI * 2.4f)) : 0f;

            // Whichever is strongest this frame owns the edges.
            if (hurt >= heal && hurt >= pulse)
                _edges.color = new Color(HurtTint.r, HurtTint.g, HurtTint.b, hurt);
            else if (heal >= pulse)
                _edges.color = new Color(HealTint.r, HealTint.g, HealTint.b, heal);
            else
                _edges.color = new Color(CriticalTint.r, CriticalTint.g, CriticalTint.b, pulse);

            UpdateGreyOut(now);
        }

        static float Fade(float elapsed, float seconds)
        {
            return elapsed < 0f || elapsed > seconds ? 0f : 1f - elapsed / seconds;
        }

        void UpdateGreyOut(float now)
        {
            if (!Application.isPlaying)
                return;

            if (_greyOut == null)
            {
                if (!_critical)
                    return;
                CreateGreyOut();
            }

            float target = _critical ? 1f : 0f;
            _greyOut.weight = Mathf.MoveTowards(_greyOut.weight, target, Time.unscaledDeltaTime * 2f);
        }

        void CreateGreyOut()
        {
            var go = new GameObject("HudCriticalGreyOut");
            _greyOut = go.AddComponent<Volume>();
            _greyOut.isGlobal = true;
            // Above the level's own volume, so the grey-out wins while it is up.
            _greyOut.priority = 50f;
            _greyOut.weight = 0f;

            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var colour = _profile.Add<ColorAdjustments>(true);
            colour.saturation.Override(-75f);
            colour.contrast.Override(12f);
            _greyOut.profile = _profile;
        }

        void OnDestroy()
        {
            if (_greyOut != null)
                Destroy(_greyOut.gameObject);
            if (_profile != null)
                Destroy(_profile);
        }
    }
}
