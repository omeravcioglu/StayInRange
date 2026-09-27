using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The design's text input (the menu kit's "field"): a spaced caption with an optional n/max
    /// counter, the words in Coming Soon with no box around them, on a sketchy underline that turns
    /// white while typing and red, with a line of explanation, on an error.
    /// </summary>
    public class TextField : MonoBehaviour
    {
        static readonly Color LineIdle = UiTheme.Rgb(0xB9C0BE);
        static readonly Color LineError = UiTheme.Rgb(0xFF5A52);
        static readonly Color PlaceholderInk = UiTheme.Rgb(0x8A9492);

        TMP_InputField _input;
        TextMeshProUGUI _counter;
        TextMeshProUGUI _error;
        Image _line;
        int _limit;
        bool _focused;
        bool _hasError;

        public TMP_InputField Input => _input;

        public string Text
        {
            get => _input.text;
            set
            {
                _input.SetTextWithoutNotify(value ?? string.Empty);
                RefreshCounter();
            }
        }

        public event Action<string> Changed;
        public event Action<string> Submitted;

        public static TextField Create(Transform parent, string caption, float width, string placeholder, int limit,
            bool showCounter = true, bool password = false)
        {
            var root = UiKit.CreateColumn(parent, "Field " + caption, 4f, TextAnchor.UpperLeft, fitToContent: false);
            // Every line of the field spans its width: the counter sits at the right end, the input fills it.
            root.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            var element = root.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = width;
            // The input reports itself flexible; the field is exactly as wide as the board draws it.
            element.flexibleWidth = 0f;

            var field = root.gameObject.AddComponent<TextField>();
            field.Build(root, caption, width, placeholder, limit, showCounter, password);
            return field;
        }

        void Build(RectTransform root, string caption, float width, string placeholder, int limit, bool showCounter,
            bool password)
        {
            _limit = limit;

            // Caption on the left, the counter on the right, on one line.
            var head = UiKit.CreateRect("Head", root);
            head.gameObject.AddComponent<LayoutElement>().preferredHeight = 28f;
            var label = UiKit.CreateText(head, "Caption", caption, TextStyle.Caption.WithSize(22f));
            label.rectTransform.Fill();
            _counter = UiKit.CreateText(head, "Counter", string.Empty,
                TextStyle.Small.WithSize(22f).WithTint(ColorRole.Muted).WithAlign(TextAlignmentOptions.Right));
            _counter.rectTransform.Fill();
            _counter.gameObject.SetActive(showCounter && limit > 0);

            // The input itself: a clear hit area over the line, the text clipped to it.
            var box = UiKit.CreateRect("Input", root);
            var boxElement = box.gameObject.AddComponent<LayoutElement>();
            boxElement.minHeight = boxElement.preferredHeight = 56f;
            var hit = box.gameObject.AddComponent<Image>();
            hit.color = Color.clear;

            var area = UiKit.CreateRect("Text Area", box);
            area.Fill();
            // Room for the ink rim, which reaches past the glyphs.
            area.offsetMin = new Vector2(-6f, -8f);
            area.offsetMax = new Vector2(6f, 8f);
            area.gameObject.AddComponent<RectMask2D>();

            var hint = UiKit.CreateText(area, "Placeholder", placeholder,
                TextStyle.Body.WithSize(42f).WithTint(ColorRole.Muted));
            hint.color = PlaceholderInk;
            hint.rectTransform.Fill(8f);
            hint.alignment = TextAlignmentOptions.Left;

            var text = UiKit.CreateText(area, "Text", string.Empty, TextStyle.Prompt.WithSize(42f));
            text.rectTransform.Fill(8f);
            text.alignment = TextAlignmentOptions.Left;

            _input = box.gameObject.AddComponent<TMP_InputField>();
            _input.transition = Selectable.Transition.None;
            _input.targetGraphic = hit;
            _input.textViewport = area;
            _input.textComponent = text;
            _input.placeholder = hint;
            _input.richText = false;
            _input.lineType = TMP_InputField.LineType.SingleLine;
            _input.characterLimit = limit;
            _input.contentType = password ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
            _input.customCaretColor = true;
            _input.caretColor = Color.white;
            _input.caretWidth = 3;
            _input.selectionColor = new Color(1f, 0.35f, 0.32f, 0.45f);
            // Added at runtime the component enabled before its text was assigned, so it never made
            // its caret; a second enable sets it up with everything in place.
            _input.enabled = false;
            _input.enabled = true;

            _input.onValueChanged.AddListener(OnChanged);
            _input.onSubmit.AddListener(value => Submitted?.Invoke(value));
            _input.onSelect.AddListener(_ => SetFocused(true));
            _input.onDeselect.AddListener(_ => SetFocused(false));

            _line = UiKit.CreateImage(root, "Line", UiSprites.SketchLine, LineIdle);
            var lineElement = _line.gameObject.AddComponent<LayoutElement>();
            lineElement.minHeight = lineElement.preferredHeight = 12f;

            _error = UiKit.CreateText(root, "Error", string.Empty,
                TextStyle.Small.WithSize(22f).WithTint(ColorRole.Danger));
            _error.gameObject.SetActive(false);

            RefreshCounter();
        }

        void OnChanged(string value)
        {
            if (_hasError)
                SetError(null);
            RefreshCounter();
            Changed?.Invoke(value);
        }

        void RefreshCounter()
        {
            if (_counter.gameObject.activeSelf)
                _counter.text = $"{_input.text.Length}/{_limit}";
        }

        void SetFocused(bool focused)
        {
            _focused = focused;
            RefreshLine();
        }

        /// <summary>Shows <paramref name="message"/> under a red line; null clears it.</summary>
        public void SetError(string message)
        {
            _hasError = !string.IsNullOrEmpty(message);
            _error.text = message ?? string.Empty;
            _error.gameObject.SetActive(_hasError);
            RefreshLine();
        }

        void RefreshLine()
        {
            _line.color = _hasError ? LineError : _focused ? Color.white : LineIdle;
        }

        public void Focus()
        {
            _input.Select();
            _input.ActivateInputField();
        }

        /// <summary>For gallery pages: draws the typing look without an event system.</summary>
        public void ShowFocused(bool focused) => SetFocused(focused);
    }
}
