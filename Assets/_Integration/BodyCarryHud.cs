using CollarCali.UI;
using TMPro;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// On-screen feedback for telekinetic carrying: the interaction prompt, the throw charge meter
    /// and short status toasts ("Caught!", "Missed!").
    ///
    /// One HUD for both first and third person. Cowsins' interact prompt only exists in the first
    /// person HUD and is driven by its own crosshair raycast, and catching a flying body is decided
    /// by range rather than by what is under the crosshair - so this draws its own. It uses the
    /// redesign's kit: prompts come in as "[E]  Lift DOT" strings and are drawn with key caps on the
    /// same line as every other prompt, which is why the HUD hides its own prompt while this one is
    /// up (<see cref="IsShowingPrompt"/>).
    /// </summary>
    public class BodyCarryHud : MonoBehaviour
    {
        public enum Style
        {
            Normal,
            Urgent,
            Waiting,
            Aiming,
        }

        /// <summary>The local player's carry HUD, if there is one.</summary>
        public static BodyCarryHud Active { get; private set; }

        PromptLine _prompt;
        RectTransform _promptRect;
        Style _style;

        RectTransform _chargeRoot;
        SketchBar _chargeBar;
        TMP_Text _chargeLabel;
        int _shownSpeed = -1;

        TMP_Text _toast;
        float _toastUntil;

        RectTransform _reticle;
        Canvas _canvas;

        /// <summary>A carry prompt is on screen, so E belongs to the telekinesis right now.</summary>
        public bool IsShowingPrompt => _prompt != null && _prompt.gameObject.activeSelf;

        public static BodyCarryHud Create()
        {
            var go = new GameObject("BodyCarryHud");
            return go.AddComponent<BodyCarryHud>();
        }

        void Awake()
        {
            Active = this;
            Build();
            Hide();
        }

        void OnDestroy()
        {
            if (Active == this)
                Active = null;
        }

        public void Hide()
        {
            if (_prompt != null)
                _prompt.gameObject.SetActive(false);
            if (_chargeRoot != null)
                _chargeRoot.gameObject.SetActive(false);
            if (_reticle != null)
                _reticle.gameObject.SetActive(false);
        }

        /// <summary>A small aiming dot in the middle of the screen, for aiming from the eyes in third person.</summary>
        public void SetReticle(bool visible)
        {
            if (_reticle != null && _reticle.gameObject.activeSelf != visible)
                _reticle.gameObject.SetActive(visible);
        }

        public void SetPrompt(string text, Style style)
        {
            bool visible = !string.IsNullOrEmpty(text);
            if (_prompt.gameObject.activeSelf != visible)
                _prompt.gameObject.SetActive(visible);
            if (!visible)
                return;

            _style = style;
            var theme = UiTheme.Active;
            var colour = style switch
            {
                Style.Urgent => theme.warning,
                Style.Waiting => theme.muted,
                Style.Aiming => theme.cream,
                _ => theme.white,
            };

            // A catch is the one prompt that must be noticed within a fraction of a second.
            _prompt.Set(text, colour, style == Style.Urgent ? 46f : 38f, keysEnabled: style != Style.Waiting);
        }

        /// <summary>
        /// The charge meter above the prompt. Fills with the hold, shifting from yellow to red, and
        /// shows the launch speed the release would give.
        /// </summary>
        public void SetCharge(bool visible, float charge01, float launchSpeed)
        {
            if (_chargeRoot.gameObject.activeSelf != visible)
                _chargeRoot.gameObject.SetActive(visible);
            if (!visible)
                return;

            var colour = UiKit.ChargeColor(charge01);
            _chargeBar.SetValue(charge01);
            _chargeBar.SetFillColor(colour);
            _chargeLabel.color = colour;

            int speed = charge01 <= 0.001f ? 0 : Mathf.RoundToInt(launchSpeed);
            if (speed != _shownSpeed)
            {
                _shownSpeed = speed;
                if (speed == 0)
                    _chargeLabel.text = "THROW";
                else
                    _chargeLabel.SetText("{0} m/s", speed);
            }
        }

        public void Toast(string text, Color colour)
        {
            _toast.text = text;
            _toast.color = colour;
            _toast.gameObject.SetActive(true);
            _toastUntil = Time.unscaledTime + 1.3f;
        }

        void Update()
        {
            // The pause menu covers the prompts; they come back as they were when it closes.
            bool covered = UiInput.MenuOpen;
            if (_canvas.enabled == covered)
                _canvas.enabled = !covered;

            if (_prompt.gameObject.activeSelf)
            {
                float pulse = _style == Style.Urgent ? 1f + 0.08f * Mathf.Sin(Time.unscaledTime * 18f) : 1f;
                _promptRect.localScale = Vector3.one * pulse;
            }

            if (_toast.gameObject.activeSelf)
            {
                float remaining = _toastUntil - Time.unscaledTime;
                if (remaining <= 0f)
                {
                    _toast.gameObject.SetActive(false);
                }
                else
                {
                    var colour = _toast.color;
                    colour.a = Mathf.Clamp01(remaining / 0.35f);
                    _toast.color = colour;
                }
            }
        }

        #region Build

        void Build()
        {
            _canvas = UiKit.CreateCanvas("BodyCarryCanvas", UiLayers.Prompts, parent: transform);
            var root = _canvas.transform;

            // The prompt line every prompt shares: top 724 of 1080 on the boards.
            _prompt = PromptLine.Create(root, "Prompt");
            _promptRect = (RectTransform)_prompt.transform;
            _promptRect.anchorMin = _promptRect.anchorMax = new Vector2(0.5f, 0.5f);
            _promptRect.pivot = new Vector2(0.5f, 0.5f);
            _promptRect.anchoredPosition = new Vector2(0f, -211f);

            _toast = UiKit.CreateText(root, "Toast", string.Empty,
                TextStyle.Prompt.WithSize(52f).WithAlign(TextAlignmentOptions.Center));
            _toast.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, 110f), new Vector2(900f, 80f));
            _toast.gameObject.SetActive(false);

            // Charge meter: a sketch bar with the speed under it, just above the prompt.
            _chargeRoot = UiKit.CreateColumn(root, "Charge", 2f, TextAnchor.MiddleCenter);
            _chargeRoot.anchorMin = _chargeRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _chargeRoot.pivot = new Vector2(0.5f, 0.5f);
            _chargeRoot.anchoredPosition = new Vector2(0f, -80f);
            _chargeBar = SketchBar.Create(_chargeRoot, "Bar", new Vector2(260f, 22f), UiKit.ChargeColor(0f), shine: false);
            _chargeBar.SetTrackColor(new Color(0f, 0f, 0f, 0.6f));
            _chargeLabel = UiKit.CreateText(_chargeRoot, "Label", "THROW",
                TextStyle.Small.WithSize(24f).WithAlign(TextAlignmentOptions.Center));

            // Third-person aiming dot: the crosshair's own centre dot.
            _reticle = UiKit.CreateRect("Reticle", root);
            _reticle.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(96f, 96f));
            UiKit.CreateImage(_reticle, "Dot", UiSprites.CrossDotFill, UiTheme.Active.cream).rectTransform.Fill();
            UiKit.CreateImage(_reticle, "Ink", UiSprites.CrossDotInk, Color.white).rectTransform.Fill();
            _reticle.gameObject.SetActive(false);
        }

        #endregion
    }
}
