using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The title screen (board 13): STAY IN RANGE over the four players chained at the collar, the
    /// name and region, and ENTER. The support link and the studio line stay off until they exist.
    /// </summary>
    public class MainMenuView : MonoBehaviour
    {
        /// <summary>The regions Clean Multiplayer Pro connects to, in its order (FusionConnection.ChangeRegion).</summary>
        public static readonly string[] Regions = { "Auto", "Asia", "Europe", "Japan", "South Korea", "USA" };

        public const int NameLimit = 15;
        static readonly Vector2 Left = new Vector2(0f, 0.5f);

        CanvasGroup _group;
        TextField _name;
        ChoiceField _region;
        MenuButton _enter;
        MenuButton _settings;
        MenuButton _exit;

        public event Action Enter;
        public event Action Settings;
        public event Action Exit;

        public string PlayerName
        {
            get => _name.Text.Trim();
            set => _name.Text = value;
        }

        public int Region
        {
            get => _region.Selected;
            set => _region.SetSelected(value, notify: false);
        }

        public TextField NameField => _name;

        public static MainMenuView Create(Transform parent)
        {
            var root = UiKit.CreateRect("MainMenu", parent);
            root.Fill();
            var view = root.gameObject.AddComponent<MainMenuView>();
            view._group = root.gameObject.AddComponent<CanvasGroup>();
            view.Build(root);
            return view;
        }

        void Build(RectTransform root)
        {
            UiKit.CreateBackdrop(root, UiSprites.MenuKeyArt, Color.white);

            var splat = UiKit.CreateImage(root, "Splat", UiSprites.Splat, new Color(1f, 1f, 1f, 0.9f));
            splat.rectTransform.AtBoard(Left, 30f, 150f, 980f, 392f, fromTop: true);

            var stay = UiKit.CreateText(root, "Title Stay In", "STAY IN", TextStyle.Title.WithSize(150f));
            stay.rectTransform.AtBoard(Left, 110f, 36f, 900f, 158f).Tilt(-3f);
            var range = UiKit.CreateText(root, "Title Range", "RANGE", TextStyle.Title.WithSize(210f));
            range.rectTransform.AtBoard(Left, 128f, 176f, 900f, 220f).Tilt(-3f);

            var column = UiKit.CreateColumn(root, "Menu", 14f);
            column.AtBoard(Left, 120f, 470f, 560f, 0f, fromTop: true);

            _name = TextField.Create(column, "YOUR NAME", 560f, "enter a name…", NameLimit);
            _name.Submitted += _ => Enter?.Invoke();
            _region = ChoiceField.Create(column, "REGION", 560f, Regions, 0);

            UiKit.CreateRect("Gap", column).gameObject.AddComponent<LayoutElement>().preferredHeight = 8f;

            _enter = MenuButton.Create(column, "ENTER", 60f, () => Enter?.Invoke());
            _settings = MenuButton.Create(column, "SETTINGS", 50f, () => Settings?.Invoke());
            _exit = MenuButton.Create(column, "EXIT", 50f, () => Exit?.Invoke());
        }

        public void SetSettingsAvailable(bool available) => _settings.Interactable = available;

        /// <summary>Back from settings: the focus returns to where the player left.</summary>
        public void FocusSettings()
        {
            var events = EventSystem.current;
            if (events != null)
                events.SetSelectedGameObject(_settings.gameObject);
        }

        /// <summary>The name is the only thing that can be wrong here.</summary>
        public void ShowNameError(string message)
        {
            _name.SetError(message);
            _name.Focus();
        }

        public void SetVisible(bool visible)
        {
            _group.alpha = visible ? 1f : 0f;
            _group.interactable = visible;
            _group.blocksRaycasts = visible;
            gameObject.SetActive(visible);
        }

        /// <summary>ENTER takes the focus when there is a name to enter with, the name field otherwise.</summary>
        public void FocusDefault()
        {
            var events = EventSystem.current;
            if (events == null)
            {
                _enter.ForceFocus(true);
                return;
            }

            if (string.IsNullOrEmpty(PlayerName))
                _name.Focus();
            else
                events.SetSelectedGameObject(_enter.gameObject);
        }
    }
}
