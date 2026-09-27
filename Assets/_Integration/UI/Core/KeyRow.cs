using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// A line of the key rebinding list (the menu kit's key_row): the action on the left, its key
    /// cap on the right. Pressing it listens for a new key - the line turns yellow and asks for one;
    /// a fixed key is greyed with a lock and cannot be pressed.
    /// </summary>
    public class KeyRow : MonoBehaviour
    {
        static readonly Color Label = UiTheme.Rgb(0xE8ECEA);
        static readonly Color Listening = UiTheme.Rgb(0xFFD447);
        static readonly Color LockedInk = UiTheme.Rgb(0x6E7876);

        Button _button;
        IconStack _mark;
        TextMeshProUGUI _label;
        TextMeshProUGUI _prompt;
        Image _lock;
        KeyCap _key;
        bool _locked;
        bool _listening;
        bool _focused;

        public Button Button => _button;
        public bool Locked => _locked;

        public event Action Pressed;

        public static KeyRow Create(Transform parent, string action, float width, bool locked)
        {
            var root = UiKit.CreateRect("Key " + action, parent);
            var element = root.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = width;
            element.minHeight = element.preferredHeight = 62f;

            var row = root.gameObject.AddComponent<KeyRow>();
            row._locked = locked;
            row.Build(root, action);
            return row;
        }

        void Build(RectTransform root, string action)
        {
            var hit = root.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            _button = root.gameObject.AddComponent<Button>();
            _button.transition = Selectable.Transition.None;
            _button.targetGraphic = hit;
            _button.interactable = !_locked;
            _button.onClick.AddListener(() => Pressed?.Invoke());
            root.gameObject.AddComponent<FocusRelay>().Changed = (target, focused) =>
            {
                _focused = focused;
                Refresh();
            };

            var left = UiKit.CreateRow(root, "Label", 10f);
            left.anchorMin = left.anchorMax = left.pivot = new Vector2(0f, 0.5f);
            left.anchoredPosition = Vector2.zero;
            _mark = IconStack.Create(left, "Mark", new Vector2(22f, 22f), Color.white,
                IconStack.PlainLayer(UiSprites.ArrowInk), IconStack.TintLayer(UiSprites.ArrowFill));
            _label = UiKit.CreateText(left, "Text", action, TextStyle.Prompt.WithSize(30f));

            var right = UiKit.CreateRow(root, "Key", 12f);
            right.anchorMin = right.anchorMax = right.pivot = new Vector2(1f, 0.5f);
            right.anchoredPosition = Vector2.zero;
            _prompt = UiKit.CreateText(right, "Prompt", "press a key…", TextStyle.Small.WithSize(24f));
            _prompt.color = Listening;
            _lock = UiKit.CreateImage(right, "Lock", UiSprites.LockGlyph, LockedInk).Sized(24f, 24f);
            _key = KeyCap.Create(right, "?", 50f, enabled: !_locked);

            Refresh();
        }

        public void SetKey(string key)
        {
            _key.Set(key, enabled: !_locked);
            Refresh();
        }

        public void SetListening(bool listening)
        {
            _listening = listening && !_locked;
            if (_listening)
                _key.Set("?", enabled: true);
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
            bool marked = _listening || (_focused && !_locked);
            foreach (var image in _mark.GetComponentsInChildren<Image>(true))
                image.enabled = marked;
            _mark.SetTint(_listening ? Listening : Color.white);

            _label.color = _locked ? LockedInk : _listening ? Listening : _focused ? Color.white : Label;
            _prompt.gameObject.SetActive(_listening);
            _lock.gameObject.SetActive(_locked);
        }
    }
}
