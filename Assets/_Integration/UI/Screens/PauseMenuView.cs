using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CollarCali.UI
{
    /// <summary>
    /// The pause menu (board 21). In multiplayer nothing actually pauses, and the menu says so: the
    /// game dims behind it, the team panel stays live over the dim, and the player is held still
    /// while it is up.
    ///
    /// The dim and the words live on two layers - the dim under the HUD, the menu over everything -
    /// so the caller passes both.
    /// </summary>
    public class PauseMenuView : MonoBehaviour
    {
        const float FadeSeconds = 0.12f;

        CanvasGroup _back;
        CanvasGroup _front;
        MenuButton _resume;
        MenuButton _settings;
        MenuButton _leave;
        MenuButton _exit;
        float _target;

        public event Action Resume;
        public event Action Settings;
        public event Action Leave;
        public event Action Exit;

        public bool IsVisible => _target > 0f;

        public static PauseMenuView Create(Transform backLayer, Transform frontLayer)
        {
            var back = UiKit.CreateRect("PauseBack", backLayer);
            back.Fill();
            var front = UiKit.CreateRect("PauseMenu", frontLayer);
            front.Fill();

            var view = front.gameObject.AddComponent<PauseMenuView>();
            view._back = back.gameObject.AddComponent<CanvasGroup>();
            view._back.blocksRaycasts = false;
            view._front = front.gameObject.AddComponent<CanvasGroup>();
            view.Build(back, front);
            view.Hide(instant: true);
            return view;
        }

        void OnDestroy()
        {
            // The dim lives on another canvas; it goes with the menu.
            if (_back != null)
                UiKit.DestroyObject(_back.gameObject);
        }

        void Build(RectTransform back, RectTransform front)
        {
            var dim = UiKit.CreateImage(back, "Dim", UiSprites.White, new Color(0f, 0f, 0f, 0.55f));
            dim.rectTransform.Fill();

            // Laid out from the board's own numbers (its top-left corner at 1920x1080), pinned to the
            // screen's middle so the group stays together on any aspect.
            var title = UiKit.CreateText(front, "Title", "PAUSED",
                TextStyle.Title.WithSize(130f).WithAlign(TextAlignmentOptions.Left));
            At(title.rectTransform, 700f, 210f, 900f, 136f).Tilt(-3f);

            var aside = UiKit.CreateText(front, "Aside", "…not really.",
                TextStyle.Heading.WithSize(56f).WithTint(ColorRole.Warning).WithAlign(TextAlignmentOptions.Left));
            At(aside.rectTransform, 1130f, 318f, 520f, 62f).Tilt(-7f);

            var line = UiKit.CreateText(front, "Line", "The game keeps running. Your team can still see you.",
                TextStyle.Body.WithSize(28f).WithAlign(TextAlignmentOptions.Left));
            line.color = UiTheme.Rgb(0xD5DAD8);
            At(line.rectTransform, 712f, 404f, 1000f, 36f);

            var column = UiKit.CreateColumn(front, "Buttons", 12f);
            column.anchorMin = column.anchorMax = new Vector2(0.5f, 0.5f);
            column.pivot = new Vector2(0f, 1f);
            column.anchoredPosition = new Vector2(712f - 960f, 540f - 490f);

            _resume = MenuButton.Create(column, "RESUME", 60f, () => Resume?.Invoke());
            _settings = MenuButton.Create(column, "SETTINGS", 50f, () => Settings?.Invoke());
            _leave = MenuButton.Create(column, "LEAVE MATCH", 50f, () => Leave?.Invoke());
            _exit = MenuButton.Create(column, "EXIT TO DESKTOP", 50f, () => Exit?.Invoke());
        }

        /// <summary>
        /// Places a one-line label the way the board's CSS does: <paramref name="left"/> and
        /// <paramref name="top"/> are the line box's corner, <paramref name="height"/> its line height,
        /// and the rect pivots on its left middle, where the board's titles turn.
        /// </summary>
        static RectTransform At(RectTransform rect, float left, float top, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(left - 960f, 540f - top - height * 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        /// <summary>Greys SETTINGS out until the settings screen exists.</summary>
        public void SetSettingsAvailable(bool available) => _settings.Interactable = available;

        public void Show(bool instant = false)
        {
            _target = 1f;
            _front.blocksRaycasts = true;
            _front.interactable = true;
            if (instant)
                _back.alpha = _front.alpha = 1f;
            SelectFirst();
        }

        public void Hide(bool instant = false)
        {
            _target = 0f;
            _front.blocksRaycasts = false;
            _front.interactable = false;
            if (instant)
                _back.alpha = _front.alpha = 0f;

            var events = EventSystem.current;
            if (events != null && events.currentSelectedGameObject != null &&
                events.currentSelectedGameObject.transform.IsChildOf(transform))
            {
                events.SetSelectedGameObject(null);
            }
        }

        /// <summary>
        /// RESUME takes the focus, so Escape-then-Enter is a round trip and the keys and a gamepad
        /// always have something to move from.
        /// </summary>
        public void SelectFirst()
        {
            var events = EventSystem.current;
            if (events != null)
                events.SetSelectedGameObject(_resume.gameObject);
            else
                _resume.ForceFocus(true);
        }

        /// <summary>Back from settings: the focus returns to SETTINGS.</summary>
        public void SelectSettings()
        {
            var events = EventSystem.current;
            if (events != null)
                events.SetSelectedGameObject(_settings.gameObject);
        }

        /// <summary>True when nothing in the menu holds the focus - the mouse clicked empty space.</summary>
        public bool LostFocus()
        {
            var events = EventSystem.current;
            if (events == null || _target <= 0f)
                return false;
            var selected = events.currentSelectedGameObject;
            return selected == null || !selected.transform.IsChildOf(transform);
        }

        void Update()
        {
            // Unscaled, so a zero timescale somewhere else cannot leave the menu half faded.
            float step = Time.unscaledDeltaTime / FadeSeconds;
            _front.alpha = Mathf.MoveTowards(_front.alpha, _target, step);
            _back.alpha = _front.alpha;
        }
    }
}
