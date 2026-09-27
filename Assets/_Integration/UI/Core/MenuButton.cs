using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// A menu button the way the design draws one: just the word in Coming Soon, no box. Focus -
    /// hover, keyboard or gamepad selection - adds the hand-drawn pointer before it and a red brush
    /// stroke under it. Hovering moves the selection, so the mouse and the keys never show two
    /// focused items at once.
    /// </summary>
    public class MenuButton : MonoBehaviour, ISelectHandler, IDeselectHandler, IPointerEnterHandler, IPointerExitHandler
    {
        Button _button;
        TextMeshProUGUI _label;
        IconStack _arrow;
        Image _brush;
        bool _pointerOver;
        bool _selected;
        bool _alwaysFocused;

        static readonly Color DisabledInk = UiTheme.Rgb(0x4F5856);

        public Button Button => _button;

        /// <summary>A greyed-out button takes no focus and no clicks, as the kit's disabled state.</summary>
        public bool Interactable
        {
            get => _button.interactable;
            set
            {
                // Set every frame by screens that mirror game state; only a change redraws.
                if (_button.interactable == value)
                    return;
                _button.interactable = value;
                Refresh();
            }
        }

        public void SetLabel(string text) => _label.text = text;

        /// <summary>Draws the focus whatever the selection is: the popup's only button, gallery pages.</summary>
        public void ForceFocus(bool focused)
        {
            _alwaysFocused = focused;
            Refresh();
        }

        public static MenuButton Create(Transform parent, string label, float size, UnityAction onClick, bool alwaysFocused = false)
        {
            var theme = UiTheme.Active;
            var row = UiKit.CreateRow(parent, "Button " + label, 14f, TextAnchor.MiddleLeft);

            // A clear hit area over the whole row, so the gap between arrow and word still clicks.
            var hit = row.gameObject.AddComponent<Image>();
            hit.color = Color.clear;

            var view = row.gameObject.AddComponent<MenuButton>();
            view._alwaysFocused = alwaysFocused;
            view._arrow = IconStack.Create(row, "Arrow", new Vector2(size * 0.55f, size * 0.55f), theme.white,
                IconStack.PlainLayer(UiSprites.ArrowInk), IconStack.TintLayer(UiSprites.ArrowFill));
            view._label = UiKit.CreateText(row, "Label", label, TextStyle.Prompt.WithSize(size));

            // The brush hangs under the word and stretches with it; it is not part of the row's layout.
            view._brush = UiKit.CreateImage(view._label.transform, "Brush", UiSprites.Brush, theme.brush, sliced: true);
            var brush = view._brush.rectTransform;
            brush.anchorMin = new Vector2(0f, 0f);
            brush.anchorMax = new Vector2(1f, 0f);
            brush.pivot = new Vector2(0.5f, 1f);
            brush.offsetMin = new Vector2(-6f, -10f);
            brush.offsetMax = new Vector2(10f, 8f);
            view._brush.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            view._button = row.gameObject.AddComponent<Button>();
            view._button.transition = Selectable.Transition.None;
            view._button.targetGraphic = hit;
            if (onClick != null)
                view._button.onClick.AddListener(onClick);

            view.Refresh();
            return view;
        }

        public void OnSelect(BaseEventData eventData)
        {
            _selected = true;
            Refresh();
        }

        public void OnDeselect(BaseEventData eventData)
        {
            _selected = false;
            Refresh();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _pointerOver = true;
            var events = EventSystem.current;
            if (_button.interactable && events != null && events.currentSelectedGameObject != gameObject)
                events.SetSelectedGameObject(gameObject);
            Refresh();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _pointerOver = false;
            Refresh();
        }

        void Refresh()
        {
            bool enabled = _button == null || _button.interactable;
            // Without an event system hover cannot select, so it draws the focus itself.
            bool hovered = _pointerOver && EventSystem.current == null;
            bool focused = enabled && (_alwaysFocused || _selected || hovered);
            // The arrow keeps its space when hidden, so the word does not jump sideways on hover.
            foreach (var image in _arrow.GetComponentsInChildren<Image>(true))
                image.enabled = focused;
            _brush.enabled = focused;
            _label.color = !enabled ? DisabledInk : focused ? UiTheme.Active.white : UiTheme.Active.muted;
        }
    }
}
