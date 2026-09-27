using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The settings slider (the menu kit's): the label on the left with the pointer while focused,
    /// and on the right the hand-drawn bar with the skull for a knob and the value beside it.
    ///
    /// It moves in whole steps, so the keys and a gamepad cover the range in a sensible number of
    /// presses; the owner maps a step to its value and says how to write it.
    /// </summary>
    public class SliderField : MonoBehaviour
    {
        const float BarWidth = 300f;
        const float BarHeight = 24f;
        static readonly Color Idle = UiTheme.Rgb(0xD5DAD8);

        Slider _slider;
        IconStack _mark;
        TextMeshProUGUI _label;
        TextMeshProUGUI _value;
        Func<int, string> _format;
        bool _focused;

        public Selectable Selectable => _slider;
        public int Step => Mathf.RoundToInt(_slider.value);

        /// <summary>Raised as the knob moves, with the new step.</summary>
        public event Action<int> Changed;

        public static SliderField Create(Transform parent, string label, float width, int steps, int step,
            Func<int, string> format)
        {
            var root = UiKit.CreateRect("Slider " + label, parent);
            var element = root.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = width;
            element.minHeight = element.preferredHeight = 60f;

            var field = root.gameObject.AddComponent<SliderField>();
            field._format = format;
            field.Build(root, label, steps);
            field.SetStep(step);
            return field;
        }

        void Build(RectTransform root, string label, int steps)
        {
            var theme = UiTheme.Active;

            var left = UiKit.CreateRow(root, "Label", 10f);
            left.anchorMin = left.anchorMax = left.pivot = new Vector2(0f, 0.5f);
            left.anchoredPosition = Vector2.zero;
            _mark = IconStack.Create(left, "Mark", new Vector2(24f, 24f), Color.white,
                IconStack.PlainLayer(UiSprites.ArrowInk), IconStack.TintLayer(UiSprites.ArrowFill));
            _label = UiKit.CreateText(left, "Text", label, TextStyle.Prompt.WithSize(32f));

            var right = UiKit.CreateRow(root, "Value", 16f);
            right.anchorMin = right.anchorMax = right.pivot = new Vector2(1f, 0.5f);
            right.anchoredPosition = Vector2.zero;

            var bar = UiKit.CreateRect("Bar", right);
            bar.Sized(BarWidth, BarHeight);
            var hit = bar.gameObject.AddComponent<Image>();
            hit.color = Color.clear;

            var track = UiKit.CreateImage(bar, "Track", UiSprites.White, new Color(0f, 0f, 0f, 0.6f));
            track.rectTransform.Fill();
            track.rectTransform.offsetMin = new Vector2(4f, 7f);
            track.rectTransform.offsetMax = new Vector2(-4f, -7f);

            var fillArea = UiKit.CreateRect("Fill Area", bar);
            fillArea.Fill();
            fillArea.offsetMin = new Vector2(4f, 7f);
            fillArea.offsetMax = new Vector2(-4f, -7f);
            var fill = UiKit.CreateImage(fillArea, "Fill", UiSprites.White, theme.cream);
            fill.rectTransform.Fill();

            var frame = UiKit.CreateImage(bar, "Frame", UiSprites.BarFrame, theme.cream, sliced: true);
            frame.rectTransform.Fill();

            var handleArea = UiKit.CreateRect("Handle Area", bar);
            handleArea.Fill();
            handleArea.offsetMin = new Vector2(4f, 0f);
            handleArea.offsetMax = new Vector2(-4f, 0f);
            var handle = UiKit.CreateImage(handleArea, "Knob", UiSprites.SkullHandle, Color.white);
            // The slider stretches the knob to the bar's height and moves its anchors; the size is
            // what it adds to that - a 26x30 skull on a 24-high bar.
            handle.rectTransform.anchorMin = new Vector2(0f, 0f);
            handle.rectTransform.anchorMax = new Vector2(0f, 1f);
            handle.rectTransform.sizeDelta = new Vector2(26f, 30f - BarHeight);
            handle.rectTransform.anchoredPosition = new Vector2(0f, 1f);

            _slider = bar.gameObject.AddComponent<Slider>();
            _slider.transition = Selectable.Transition.None;
            _slider.targetGraphic = hit;
            _slider.fillRect = fill.rectTransform;
            _slider.handleRect = handle.rectTransform;
            _slider.direction = Slider.Direction.LeftToRight;
            _slider.wholeNumbers = true;
            _slider.minValue = 0;
            _slider.maxValue = Mathf.Max(1, steps);
            _slider.onValueChanged.AddListener(OnMoved);
            bar.gameObject.AddComponent<FocusRelay>().Changed = OnFocus;

            _value = UiKit.CreateText(right, "Number", string.Empty,
                TextStyle.Prompt.WithSize(30f).WithAlign(TextAlignmentOptions.Right));
            _value.gameObject.AddComponent<LayoutElement>().minWidth = 90f;

            Refresh();
        }

        public void SetStep(int step)
        {
            _slider.SetValueWithoutNotify(step);
            _value.text = _format != null ? _format(Step) : Step.ToString();
        }

        void OnMoved(float value)
        {
            _value.text = _format != null ? _format(Step) : Step.ToString();
            Changed?.Invoke(Step);
        }

        void OnFocus(GameObject target, bool focused)
        {
            _focused = focused;
            Refresh();
        }

        /// <summary>For gallery pages: the focused look without an event system.</summary>
        public void ShowFocused(bool focused)
        {
            _focused = focused;
            Refresh();
        }

        void Refresh()
        {
            foreach (var image in _mark.GetComponentsInChildren<Image>(true))
                image.enabled = _focused;
            var colour = _focused ? Color.white : Idle;
            _label.color = colour;
            _value.color = colour;
        }
    }
}
