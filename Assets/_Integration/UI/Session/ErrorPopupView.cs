using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The error popup (board 17): SOMETHING BROKE, a plain sentence about what happened, the
    /// technical reason small underneath for bug reports, and one way out.
    /// </summary>
    public class ErrorPopupView : MonoBehaviour
    {
        CanvasGroup _group;
        TextMeshProUGUI _message;
        TextMeshProUGUI _code;
        MenuButton _button;
        Action _action;

        public bool IsVisible => _group.alpha > 0.001f;

        public static ErrorPopupView Create(Transform parent)
        {
            var rect = UiKit.CreateRect("ErrorPopup", parent);
            rect.Fill();
            var view = rect.gameObject.AddComponent<ErrorPopupView>();
            view._group = rect.gameObject.AddComponent<CanvasGroup>();
            view.Build(rect);
            view.Hide();
            return view;
        }

        void Build(RectTransform root)
        {
            var dim = UiKit.CreateImage(root, "Dim", UiSprites.White, new Color(0f, 0f, 0f, 0.82f));
            dim.rectTransform.Fill();
            dim.raycastTarget = true;

            var splat = UiKit.CreateImage(root, "Splat", UiSprites.Splat, new Color(1f, 1f, 1f, 0.85f));
            splat.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, 130f), new Vector2(800f, 320f));

            var title = UiKit.CreateText(root, "Title", "SOMETHING BROKE",
                TextStyle.Title.WithSize(88f).WithAlign(TextAlignmentOptions.Center));
            title.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, 162f), new Vector2(1600f, 110f));
            title.rectTransform.Tilt(-2f);

            _message = UiKit.CreateText(root, "Message", string.Empty,
                TextStyle.Body.WithSize(38f).WithAlign(TextAlignmentOptions.Center));
            _message.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(1600f, 56f));

            _code = UiKit.CreateText(root, "Code", string.Empty,
                TextStyle.Small.WithSize(24f).WithAlign(TextAlignmentOptions.Center));
            _code.color = UiTheme.Rgb(0x8A9492);
            _code.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -31f), new Vector2(1600f, 36f));

            var holder = UiKit.CreateRect("Action", root);
            holder.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -150f), new Vector2(600f, 90f));
            var layout = holder.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            _button = MenuButton.Create(holder, "RELOAD GAME", 56f, OnButton, alwaysFocused: true);
        }

        /// <summary>Shows the popup; <paramref name="action"/> runs when the button is pressed.</summary>
        public void Show(string message, string code, Action action)
        {
            _message.text = message ?? string.Empty;
            _code.text = code ?? string.Empty;
            _action = action;
            _group.alpha = 1f;
            _group.blocksRaycasts = true;
            _group.interactable = true;
            _button.Button.Select();
        }

        /// <summary>Gives the button the focus again, after the event system was replaced.</summary>
        public void Reselect()
        {
            if (IsVisible)
                _button.Button.Select();
        }

        public void Hide()
        {
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }

        void OnButton()
        {
            var action = _action;
            _action = null;
            action?.Invoke();
        }
    }
}
