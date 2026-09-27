using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The loading screen (board 16): the chain-link spinner, what is happening ("Connecting…"),
    /// the region, and a tip that changes every few seconds.
    ///
    /// Opaque on purpose: it covers the scene swap underneath, which in Fusion unloads the old scene
    /// before the new one arrives. Fades in and out on unscaled time, so it works while the game is
    /// paused or the time scale is anything.
    /// </summary>
    public class LoadingOverlayView : MonoBehaviour
    {
        const float FadeIn = 0.25f;
        const float FadeOut = 0.35f;
        const float TipSeconds = 6f;
        const float SpinnerStepSeconds = 0.09f;

        CanvasGroup _group;
        RectTransform _spinner;
        TextMeshProUGUI _status;
        TextMeshProUGUI _region;
        TextMeshProUGUI _tip;

        bool _visible;
        float _nextTipAt;
        int _tipIndex;
        float _nextStepAt;
        int _step;

        public bool IsVisible => _visible;
        public bool IsFullyHidden => !_visible && _group.alpha <= 0.001f;

        public static LoadingOverlayView Create(Transform parent)
        {
            var rect = UiKit.CreateRect("Loading", parent);
            rect.Fill();
            var view = rect.gameObject.AddComponent<LoadingOverlayView>();
            view._group = rect.gameObject.AddComponent<CanvasGroup>();
            view._group.alpha = 0f;
            view._group.blocksRaycasts = false;
            view.Build(rect);
            return view;
        }

        void Build(RectTransform root)
        {
            var theme = UiTheme.Active;

            var ground = UiKit.CreateImage(root, "Ground", UiSprites.White, UiTheme.Rgb(0x050707));
            ground.rectTransform.Fill();
            var edges = UiKit.CreateImage(root, "Edges", UiSprites.Vignette, new Color(0f, 0f, 0f, 0.8f));
            edges.rectTransform.Fill();

            var spinner = UiKit.CreateImage(root, "Spinner", UiSprites.Spinner, Color.white);
            _spinner = spinner.rectTransform;
            _spinner.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, 150f), new Vector2(180f, 180f));

            _status = UiKit.CreateText(root, "Status", string.Empty,
                TextStyle.Prompt.WithSize(50f).WithAlign(TextAlignmentOptions.Center));
            _status.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -20f), new Vector2(1600f, 70f));

            _region = UiKit.CreateText(root, "Region", string.Empty,
                TextStyle.Body.WithSize(28f).WithTint(ColorRole.Muted).WithAlign(TextAlignmentOptions.Center));
            _region.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -78f), new Vector2(1200f, 40f));

            var tipRow = UiKit.CreateRow(root, "Tip", 18f, TextAnchor.MiddleCenter);
            tipRow.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -430f), new Vector2(1600f, 44f));
            var label = UiKit.CreateText(tipRow, "Label", "TIP", TextStyle.Small.WithSize(22f).WithTint(ColorRole.Warning));
            label.characterSpacing = 3f;
            _tip = UiKit.CreateText(tipRow, "Text", string.Empty, TextStyle.Body.WithSize(30f));
            _tip.color = UiTheme.Rgb(0xD5DAD8);

            NextTip(theme);
        }

        /// <summary>Shows the screen with a status line; <paramref name="region"/> may be empty.</summary>
        public void Show(string status, string region)
        {
            if (!_visible)
                NextTip(UiTheme.Active);
            _visible = true;
            _group.blocksRaycasts = true;
            SetStatus(status);
            _region.text = region ?? string.Empty;
        }

        public void SetStatus(string status)
        {
            if (_status.text != (status ?? string.Empty))
                _status.text = status ?? string.Empty;
        }

        public void Hide()
        {
            _visible = false;
            _group.blocksRaycasts = false;
        }

        /// <summary>Jumps straight to the shown or hidden state, skipping the fade (the gallery uses it).</summary>
        public void Snap()
        {
            _group.alpha = _visible ? 1f : 0f;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float target = _visible ? 1f : 0f;
            _group.alpha = Mathf.MoveTowards(_group.alpha, target, dt / (_visible ? FadeIn : FadeOut));
            if (_group.alpha <= 0f)
                return;

            float now = Time.unscaledTime;

            // Stepped, not smooth: the lead link jumps to the next place, like a ticking chain.
            if (now >= _nextStepAt)
            {
                _nextStepAt = now + SpinnerStepSeconds;
                _step = (_step + 1) % 12;
                _spinner.localRotation = Quaternion.Euler(0f, 0f, -30f * _step);
            }

            if (now >= _nextTipAt)
                NextTip(UiTheme.Active);
        }

        void NextTip(UiTheme theme)
        {
            _nextTipAt = Time.unscaledTime + TipSeconds;
            var tips = theme.loadingTips;
            if (tips == null || tips.Length == 0 || _tip == null)
                return;
            _tipIndex = (_tipIndex + 1) % tips.Length;
            _tip.text = tips[_tipIndex];
        }
    }
}
