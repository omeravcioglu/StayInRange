using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The design's dropdown (the menu kit's region picker): a caption, the chosen value with a
    /// chevron on a sketchy underline, and - while open - the choices listed under it in the flow,
    /// the chosen one marked with the hand-drawn pointer.
    ///
    /// The list opens in place rather than floating, so it pushes what is below it down instead of
    /// covering it; with a handful of choices that reads better than a popup and needs no masking.
    /// </summary>
    public class ChoiceField : MonoBehaviour
    {
        static readonly Color LineIdle = UiTheme.Rgb(0xB9C0BE);
        static readonly Color OptionIdle = UiTheme.Rgb(0x9AA3A1);

        Button _head;
        TextMeshProUGUI _value;
        IconStack _chevron;
        Image _line;
        RectTransform _list;
        Button[] _options = Array.Empty<Button>();
        TextMeshProUGUI[] _optionLabels = Array.Empty<TextMeshProUGUI>();
        IconStack[] _optionMarks = Array.Empty<IconStack>();
        string[] _choices = Array.Empty<string>();
        int _selected;
        bool _open;
        GameObject _focused;

        public int Selected => _selected;
        public bool IsOpen => _open;
        public Button Head => _head;

        public event Action<int> Changed;

        public static ChoiceField Create(Transform parent, string caption, float width, string[] choices, int selected)
        {
            var root = UiKit.CreateColumn(parent, "Choice " + caption, 4f, TextAnchor.UpperLeft, fitToContent: false);
            root.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            var element = root.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = width;
            // The input reports itself flexible; the field is exactly as wide as the board draws it.
            element.flexibleWidth = 0f;

            var field = root.gameObject.AddComponent<ChoiceField>();
            field.Build(root, caption, choices);
            field.SetSelected(selected, notify: false);
            return field;
        }

        void Build(RectTransform root, string caption, string[] choices)
        {
            _choices = choices ?? Array.Empty<string>();

            var label = UiKit.CreateText(root, "Caption", caption, TextStyle.Caption.WithSize(22f));
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = 28f;

            var head = UiKit.CreateRect("Head", root);
            var headElement = head.gameObject.AddComponent<LayoutElement>();
            headElement.minHeight = headElement.preferredHeight = 56f;
            var hit = head.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            _head = head.gameObject.AddComponent<Button>();
            _head.transition = Selectable.Transition.None;
            _head.targetGraphic = hit;
            _head.onClick.AddListener(Toggle);
            head.gameObject.AddComponent<FocusRelay>().Changed = OnFocus;

            _value = UiKit.CreateText(head, "Value", string.Empty, TextStyle.Prompt.WithSize(42f));
            _value.rectTransform.Fill();
            _value.rectTransform.offsetMax = new Vector2(-44f, 0f);

            // The kit's chevron points right; turned a quarter it points down (and up while open).
            _chevron = UiKit.CreateIcon(head, "Chevron", 30f, UiSprites.ChevronRightInk, UiSprites.ChevronRightLine,
                UiTheme.Active.cream);
            var chevron = (RectTransform)_chevron.transform;
            chevron.anchorMin = chevron.anchorMax = chevron.pivot = new Vector2(1f, 0.5f);
            chevron.anchoredPosition = new Vector2(-8f, 0f);
            chevron.sizeDelta = new Vector2(22f, 34f);
            chevron.localRotation = Quaternion.Euler(0f, 0f, -90f);

            _line = UiKit.CreateImage(root, "Line", UiSprites.SketchLine, LineIdle);
            var lineElement = _line.gameObject.AddComponent<LayoutElement>();
            lineElement.minHeight = lineElement.preferredHeight = 12f;

            _list = UiKit.CreateColumn(root, "Choices", 2f, TextAnchor.UpperLeft, fitToContent: false);
            _list.gameObject.AddComponent<LayoutElement>();
            var padding = _list.GetComponent<VerticalLayoutGroup>();
            padding.padding = new RectOffset(0, 0, 6, 0);

            _options = new Button[_choices.Length];
            _optionLabels = new TextMeshProUGUI[_choices.Length];
            _optionMarks = new IconStack[_choices.Length];
            for (int i = 0; i < _choices.Length; i++)
            {
                int index = i;
                var row = UiKit.CreateRow(_list, "Choice " + _choices[i], 10f, TextAnchor.MiddleLeft, fitToContent: false);
                var rowHit = row.gameObject.AddComponent<Image>();
                rowHit.color = Color.clear;
                var button = row.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.targetGraphic = rowHit;
                button.onClick.AddListener(() => Pick(index));
                row.gameObject.AddComponent<FocusRelay>().Changed = OnFocus;

                _optionMarks[i] = IconStack.Create(row, "Mark", new Vector2(22f, 22f), Color.white,
                    IconStack.PlainLayer(UiSprites.ArrowInk), IconStack.TintLayer(UiSprites.ArrowFill));
                _optionLabels[i] = UiKit.CreateText(row, "Label", _choices[i], TextStyle.Prompt.WithSize(32f));
                _options[i] = button;
            }

            _list.gameObject.SetActive(false);
        }

        public void SetSelected(int index, bool notify = true)
        {
            if (_choices.Length == 0)
                return;

            _selected = Mathf.Clamp(index, 0, _choices.Length - 1);
            _value.text = _choices[_selected];
            Refresh();
            if (notify)
                Changed?.Invoke(_selected);
        }

        void Toggle() => SetOpen(!_open);

        public void SetOpen(bool open)
        {
            _open = open;
            _list.gameObject.SetActive(open);
            _chevron.transform.localRotation = Quaternion.Euler(0f, 0f, open ? 90f : -90f);

            var events = EventSystem.current;
            if (events != null)
                events.SetSelectedGameObject(open && _options.Length > 0 ? _options[_selected].gameObject : _head.gameObject);
            Refresh();
        }

        void Pick(int index)
        {
            SetSelected(index);
            SetOpen(false);
        }

        void OnFocus(GameObject target, bool focused)
        {
            if (focused)
                _focused = target;
            else if (_focused == target)
                _focused = null;
            Refresh();
        }

        void Refresh()
        {
            var current = _focused;
            bool headFocused = current == _head.gameObject;
            _line.color = headFocused || _open ? Color.white : LineIdle;

            for (int i = 0; i < _options.Length; i++)
            {
                bool on = i == _selected;
                bool focused = current == _options[i].gameObject;
                _optionLabels[i].color = on || focused ? Color.white : OptionIdle;
                foreach (var image in _optionMarks[i].GetComponentsInChildren<Image>(true))
                    image.enabled = on || focused;
            }
        }

    }

    /// <summary>Tells its owner when a selectable gains or loses the focus.</summary>
    public class FocusRelay : MonoBehaviour, ISelectHandler, IDeselectHandler, IPointerEnterHandler
    {
        public Action<GameObject, bool> Changed;

        public void OnSelect(BaseEventData eventData) => Changed?.Invoke(gameObject, true);
        public void OnDeselect(BaseEventData eventData) => Changed?.Invoke(gameObject, false);

        public void OnPointerEnter(PointerEventData eventData)
        {
            // Hover moves the selection, as on the menu buttons.
            var selectable = GetComponent<Selectable>();
            var events = EventSystem.current;
            if (selectable != null && selectable.interactable && events != null &&
                events.currentSelectedGameObject != gameObject)
            {
                events.SetSelectedGameObject(gameObject);
            }
        }
    }
}
