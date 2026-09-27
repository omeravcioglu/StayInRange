using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The design's hand-drawn bar: a dark track, a flat fill with a faint shine along its top, and
    /// the wobbly cream frame drawn over both.
    ///
    /// Health, the throw charge, stamina and the settings sliders are all this one bar, so they
    /// share one look and one place to change it. It draws what it is told; smoothing or draining
    /// belongs to the widget that owns it.
    /// </summary>
    public class SketchBar : MonoBehaviour
    {
        Image _track;
        Image _fill;
        Image _shine;
        Image _frame;
        Image _ghost;
        float _inset;

        public float Value { get; private set; }

        public static SketchBar Create(Transform parent, string name, Vector2 size, Color fill, bool shine = true)
        {
            var rect = UiKit.CreateRect(name, parent);
            rect.sizeDelta = size;

            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = size.x;
            element.minHeight = element.preferredHeight = size.y;

            var bar = rect.gameObject.AddComponent<SketchBar>();
            bar.Build(size, fill, shine);
            return bar;
        }

        void Build(Vector2 size, Color fill, bool shine)
        {
            var theme = UiTheme.Active;
            // The team row's bar is 30 px tall with the fill inset 5 px: the same proportion at any size.
            float inset = Mathf.Round(size.y / 6f);
            _inset = inset;

            _track = UiKit.CreateImage(transform, "Track", UiSprites.White, theme.track);
            _track.rectTransform.Fill(inset);

            _fill = UiKit.CreateImage(transform, "Fill", UiSprites.White, fill);
            _fill.rectTransform.Fill(inset);
            MakeHorizontalFill(_fill);

            if (shine)
            {
                _shine = UiKit.CreateImage(transform, "Shine", UiSprites.White, new Color(1f, 1f, 1f, 0.4f));
                // A thin strip along the top of the fill, as on the team row: 4 px of a 30 px bar.
                var rect = _shine.rectTransform;
                float top = inset * 1.6f;
                float height = Mathf.Max(2f, size.y * 0.13f);
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.offsetMin = new Vector2(inset * 2f, -top - height);
                rect.offsetMax = new Vector2(-inset, -top);
                MakeHorizontalFill(_shine);
            }

            _frame = UiKit.CreateImage(transform, "Frame", UiSprites.BarFrame, theme.cream, sliced: true);
            _frame.rectTransform.Fill();

            SetValue(1f);
        }

        static void MakeHorizontalFill(Image image)
        {
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = (int)Image.OriginHorizontal.Left;
        }

        public void SetValue(float value01)
        {
            Value = Mathf.Clamp01(value01);
            _fill.fillAmount = Value;
            if (_shine != null)
                _shine.fillAmount = Value;
        }

        /// <summary>
        /// A second fill behind the first, for the damage that just landed: the bar drops at once
        /// and this slice drains after it. Created on first use.
        /// </summary>
        public void SetGhost(float value01, Color colour)
        {
            if (_ghost == null)
            {
                _ghost = UiKit.CreateImage(transform, "Ghost", UiSprites.White, colour);
                _ghost.rectTransform.Fill(_inset);
                MakeHorizontalFill(_ghost);
                // Between the track and the fill.
                _ghost.transform.SetSiblingIndex(_track.transform.GetSiblingIndex() + 1);
            }

            _ghost.color = colour;
            _ghost.fillAmount = Mathf.Clamp01(value01);
        }

        public void SetFillColor(Color colour) => _fill.color = colour;
        public void SetTrackColor(Color colour) => _track.color = colour;
        public void SetFrameColor(Color colour) => _frame.color = colour;
    }
}
