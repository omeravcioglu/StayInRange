using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The settings tabs (the menu kit's): words in a row, the open one white with the red brush
    /// under it, the others grey.
    /// </summary>
    public class TabBar : MonoBehaviour
    {
        static readonly Color Idle = UiTheme.Rgb(0x8A9492);

        Button[] _tabs = Array.Empty<Button>();
        TextMeshProUGUI[] _labels = Array.Empty<TextMeshProUGUI>();
        Image[] _brushes = Array.Empty<Image>();
        int _active = -1;

        public int Active => _active;
        public Button this[int index] => _tabs[index];

        public event Action<int> Changed;

        public static TabBar Create(Transform parent, string[] names)
        {
            var row = UiKit.CreateRow(parent, "Tabs", 44f, TextAnchor.LowerLeft);
            var bar = row.gameObject.AddComponent<TabBar>();
            bar.Build(row, names);
            return bar;
        }

        void Build(RectTransform row, string[] names)
        {
            _tabs = new Button[names.Length];
            _labels = new TextMeshProUGUI[names.Length];
            _brushes = new Image[names.Length];

            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var tab = UiKit.CreateRect("Tab " + names[i], row);
                var hit = tab.gameObject.AddComponent<Image>();
                hit.color = Color.clear;

                var label = UiKit.CreateText(tab, "Label", names[i], TextStyle.Prompt.WithSize(40f));
                var size = label.GetPreferredValues(names[i]);
                tab.gameObject.AddComponent<LayoutElement>().preferredWidth = size.x;
                tab.GetComponent<LayoutElement>().preferredHeight = 60f;
                label.rectTransform.anchorMin = new Vector2(0f, 1f);
                label.rectTransform.anchorMax = new Vector2(1f, 1f);
                label.rectTransform.pivot = new Vector2(0.5f, 1f);
                label.rectTransform.sizeDelta = new Vector2(0f, 48f);
                label.rectTransform.anchoredPosition = Vector2.zero;

                var brush = UiKit.CreateImage(tab, "Brush", UiSprites.Brush, UiTheme.Active.brush, sliced: true);
                brush.rectTransform.anchorMin = new Vector2(0f, 0f);
                brush.rectTransform.anchorMax = new Vector2(1f, 0f);
                brush.rectTransform.pivot = new Vector2(0.5f, 0f);
                brush.rectTransform.sizeDelta = new Vector2(8f, 16f);
                brush.rectTransform.anchoredPosition = new Vector2(0f, 0f);

                var button = tab.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.targetGraphic = hit;
                button.onClick.AddListener(() => Select(index));
                tab.gameObject.AddComponent<FocusRelay>().Changed = (target, focused) => Refresh(target, focused);

                _tabs[i] = button;
                _labels[i] = label;
                _brushes[i] = brush;
            }
        }

        /// <summary>Opens a tab; <paramref name="notify"/> false when the owner is setting it itself.</summary>
        public void Select(int index, bool notify = true)
        {
            index = Mathf.Clamp(index, 0, _tabs.Length - 1);
            bool changed = index != _active;
            _active = index;
            Refresh(null, false);
            if (changed && notify)
                Changed?.Invoke(index);
        }

        void Refresh(GameObject focusedTab, bool focused)
        {
            for (int i = 0; i < _tabs.Length; i++)
            {
                bool on = i == _active;
                bool hover = focused && focusedTab == _tabs[i].gameObject;
                _labels[i].color = on || hover ? Color.white : Idle;
                _brushes[i].enabled = on;
            }
        }
    }
}
