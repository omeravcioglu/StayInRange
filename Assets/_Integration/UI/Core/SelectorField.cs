using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The design's ‹ value › picker (settings rows, max players): the label on the left with the
    /// pointer while focused, the value between two chevrons on the right. Left and right change it
    /// from the keys or a gamepad; the chevrons take clicks.
    /// </summary>
    public class SelectorField : MonoBehaviour, ISelectHandler, IDeselectHandler, IMoveHandler, IPointerEnterHandler
    {
        static readonly Color Idle = UiTheme.Rgb(0xD5DAD8);

        Button _row;
        IconStack _mark;
        TextMeshProUGUI _label;
        TextMeshProUGUI _value;
        string[] _choices = Array.Empty<string>();
        int _selected;
        bool _focused;

        public int Selected => _selected;
        public Selectable Selectable => _row;

        public event Action<int> Changed;

        public static SelectorField Create(Transform parent, string label, float width, string[] choices, int selected)
        {
            var root = UiKit.CreateRect("Selector " + label, parent);
            var element = root.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = width;
            element.minHeight = element.preferredHeight = 60f;

            var field = root.gameObject.AddComponent<SelectorField>();
            field.Build(root, label, choices);
            field.SetSelected(selected, notify: false);
            return field;
        }

        void Build(RectTransform root, string label, string[] choices)
        {
            _choices = choices ?? Array.Empty<string>();

            var hit = root.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            _row = root.gameObject.AddComponent<Button>();
            _row.transition = Selectable.Transition.None;
            _row.targetGraphic = hit;
            // Up and down move between rows; left and right belong to the value.
            _row.navigation = new Navigation { mode = Navigation.Mode.Vertical };

            var left = UiKit.CreateRow(root, "Label", 10f);
            left.anchorMin = left.anchorMax = left.pivot = new Vector2(0f, 0.5f);
            left.anchoredPosition = Vector2.zero;
            _mark = IconStack.Create(left, "Mark", new Vector2(24f, 24f), Color.white,
                IconStack.PlainLayer(UiSprites.ArrowInk), IconStack.TintLayer(UiSprites.ArrowFill));
            _label = UiKit.CreateText(left, "Text", label, TextStyle.Prompt.WithSize(32f));

            var right = UiKit.CreateRow(root, "Value", 4f);
            right.anchorMin = right.anchorMax = right.pivot = new Vector2(1f, 0.5f);
            right.anchoredPosition = Vector2.zero;

            Chevron(right, "Previous", UiSprites.ChevronLeftInk, UiSprites.ChevronLeftLine, -1);
            _value = UiKit.CreateText(right, "Text", string.Empty,
                TextStyle.Prompt.WithSize(32f).WithAlign(TextAlignmentOptions.Center));
            _value.gameObject.AddComponent<LayoutElement>().minWidth = 180f;
            Chevron(right, "Next", UiSprites.ChevronRightInk, UiSprites.ChevronRightLine, 1);

            Refresh();
        }

        void Chevron(Transform parent, string name, string ink, string line, int step)
        {
            var holder = UiKit.CreateRect(name, parent);
            holder.gameObject.AddComponent<LayoutElement>().preferredWidth = 42f;
            var hit = holder.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            var button = holder.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hit;
            // Clicks only: the keys go through the row, so the chevrons never take the focus.
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => Step(step));

            var icon = UiKit.CreateIcon(holder, "Icon", 30f, ink, line, UiTheme.Active.cream);
            var rect = (RectTransform)icon.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(20f, 32f);
        }

        /// <summary>Swaps the choices (the screen sizes on offer, say) and shows <paramref name="selected"/>.</summary>
        public void SetChoices(string[] choices, int selected)
        {
            _choices = choices ?? Array.Empty<string>();
            SetSelected(Mathf.Max(0, selected), notify: false);
        }

        /// <summary>For gallery pages: the focused look without an event system.</summary>
        public void ShowFocused(bool focused)
        {
            _focused = focused;
            Refresh();
        }

        public void SetSelected(int index, bool notify = true)
        {
            if (_choices.Length == 0)
                return;
            _selected = Mathf.Clamp(index, 0, _choices.Length - 1);
            _value.text = _choices[_selected];
            if (notify)
                Changed?.Invoke(_selected);
        }

        void Step(int step)
        {
            if (_choices.Length == 0)
                return;
            int next = Mathf.Clamp(_selected + step, 0, _choices.Length - 1);
            if (next != _selected)
                SetSelected(next);
        }

        public void OnMove(AxisEventData eventData)
        {
            if (eventData.moveDir == MoveDirection.Left)
                Step(-1);
            else if (eventData.moveDir == MoveDirection.Right)
                Step(1);
        }

        public void OnSelect(BaseEventData eventData)
        {
            _focused = true;
            Refresh();
        }

        public void OnDeselect(BaseEventData eventData)
        {
            _focused = false;
            Refresh();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            var events = EventSystem.current;
            if (events != null && events.currentSelectedGameObject != gameObject)
                events.SetSelectedGameObject(gameObject);
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
