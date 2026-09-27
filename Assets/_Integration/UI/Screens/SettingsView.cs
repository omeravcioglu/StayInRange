using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// Settings (boards 18 and 22): VIDEO, SOUND and CONTROLS, one screen for both the main menu
    /// and the pause menu. Every change applies at once and is kept by GameSettings; RESET puts the
    /// open tab back. CONTROLS carries the key rebinding list - press a line, then the new key.
    ///
    /// Escape (or a gamepad's B) goes back, except while listening for a key, where it cancels.
    /// </summary>
    public class SettingsView : MonoBehaviour
    {
        public static readonly string[] Tabs = { "VIDEO", "SOUND", "CONTROLS" };

        static readonly Vector2 Left = new Vector2(0f, 0.5f);
        static readonly Color Note = UiTheme.Rgb(0x8A9492);
        static readonly string[] OffOn = { "OFF", "ON" };

        CanvasGroup _group;
        TabBar _tabs;
        MenuButton _reset;
        MenuButton _back;
        TextMeshProUGUI _hint;
        readonly RectTransform[] _panels = new RectTransform[3];
        RectTransform _keysPanel;

        SelectorField _quality;
        SelectorField _resolution;
        SelectorField _fullscreen;
        SelectorField _vsync;
        SelectorField _frameRate;
        SliderField _volume;
        SliderField _mouseX;
        SliderField _mouseY;
        SliderField _pad;
        SelectorField _invert;
        readonly List<KeyRow> _keys = new List<KeyRow>();

        List<Vector2Int> _resolutions = new List<Vector2Int>();
        InputAction _backAction;
        InputActionRebindingExtensions.RebindingOperation _rebind;
        int _rebindEndedFrame = -1;

        public bool IsVisible => gameObject.activeSelf && _group.alpha > 0f;
        public bool IsRebinding => _rebind != null;

        public event Action Back;

        /// <param name="overGame">Drawn over the running game (pause) rather than on the menu's corridor.</param>
        public static SettingsView Create(Transform parent, bool overGame)
        {
            var root = UiKit.CreateRect("Settings", parent);
            root.Fill();
            var view = root.gameObject.AddComponent<SettingsView>();
            view._group = root.gameObject.AddComponent<CanvasGroup>();
            view.Build(root, overGame);
            view.gameObject.SetActive(false);
            return view;
        }

        void Build(RectTransform root, bool overGame)
        {
            if (!overGame)
                UiKit.CreateBackdrop(root, UiSprites.MenuCorridor, Color.white);
            var dim = UiKit.CreateImage(root, "Dim", UiSprites.White, new Color(0f, 0f, 0f, overGame ? 0.72f : 0.5f));
            dim.rectTransform.Fill();
            dim.raycastTarget = true;
            var scrim = UiKit.CreateImage(root, "Scrim", UiSprites.Scrim, Color.white);
            scrim.rectTransform.anchorMin = new Vector2(0f, 0f);
            scrim.rectTransform.anchorMax = new Vector2(0f, 1f);
            scrim.rectTransform.pivot = new Vector2(0f, 0.5f);
            scrim.rectTransform.sizeDelta = new Vector2(1100f, 0f);

            var title = UiKit.CreateText(root, "Title", "SETTINGS", TextStyle.Title.WithSize(110f));
            title.rectTransform.AtBoard(Left, 110f, 36f, 900f, 116f).Tilt(-3f);

            var header = UiKit.CreateRow(root, "Header", 120f, TextAnchor.LowerLeft);
            header.AtBoard(Left, 120f, 204f, 1200f, 0f, fromTop: true);
            _tabs = TabBar.Create(header, Tabs);
            _tabs.Changed += ShowTab;
            _reset = MenuButton.Create(header, "RESET", 32f, ResetTab);

            BuildVideo(root);
            BuildSound(root);
            BuildControls(root);

            _hint = UiKit.CreateText(root, "Hint", "← → change the value · Esc goes back", TextStyle.Small.WithSize(24f));
            _hint.color = Note;
            _hint.rectTransform.AtBoard(Left, 120f, 760f, 900f, 32f);

            var bottom = UiKit.CreateRow(root, "Actions", 70f, TextAnchor.UpperLeft);
            bottom.AtBoard(Left, 120f, 956f, 400f, 0f, fromTop: true);
            _back = MenuButton.Create(bottom, "BACK", 46f, () => Back?.Invoke());

            _backAction = new InputAction("Settings Back", InputActionType.Button);
            _backAction.AddBinding("<Keyboard>/escape");
            _backAction.AddBinding("<Gamepad>/buttonEast");
        }

        RectTransform Panel(RectTransform root, string name, float width)
        {
            var panel = UiKit.CreateColumn(root, name, 16f);
            panel.AtBoard(Left, 120f, 330f, width, 0f, fromTop: true);
            return panel;
        }

        void BuildVideo(RectTransform root)
        {
            var panel = _panels[0] = Panel(root, "Video", 900f);
            _quality = SelectorField.Create(panel, "Graphics quality", 900f, Upper(GameSettings.QualityNames), 0);
            _quality.Changed += GameSettings.SetQuality;
            _resolution = SelectorField.Create(panel, "Screen resolution", 900f, new[] { "-" }, 0);
            _resolution.Changed += i =>
            {
                if (i >= 0 && i < _resolutions.Count)
                    GameSettings.SetResolution(_resolutions[i]);
            };
            _fullscreen = SelectorField.Create(panel, "Fullscreen", 900f, OffOn, 1);
            _fullscreen.Changed += i => GameSettings.SetFullscreen(i == 1);
            _vsync = SelectorField.Create(panel, "V-Sync", 900f, OffOn, 0);
            _vsync.Changed += i => GameSettings.SetVSync(i == 1);

            var rates = new string[GameSettings.FrameRates.Length];
            for (int i = 0; i < rates.Length; i++)
                rates[i] = GameSettings.FrameRates[i] > 0 ? GameSettings.FrameRates[i].ToString() : "UNLIMITED";
            _frameRate = SelectorField.Create(panel, "Max framerate", 900f, rates, rates.Length - 1);
            _frameRate.Changed += i => GameSettings.SetMaxFrameRate(GameSettings.FrameRates[i]);
        }

        void BuildSound(RectTransform root)
        {
            var panel = _panels[1] = Panel(root, "Sound", 900f);
            // Twenty steps of five percent.
            _volume = SliderField.Create(panel, "Master volume", 900f, 20, 20, step => (step * 5) + "%");
            _volume.Changed += step => GameSettings.SetMasterVolume(step / 20f);
        }

        void BuildControls(RectTransform root)
        {
            var panel = _panels[2] = Panel(root, "Controls", 780f);
            // Mouse: quarter steps from 0.25 to 10 (Cowsins' default 4 is step 15).
            _mouseX = SliderField.Create(panel, "Mouse sensitivity X", 780f, 39, 15, MouseLabel);
            _mouseX.Changed += step => GameSettings.SetMouseSensitivityX(MouseValue(step));
            _mouseY = SliderField.Create(panel, "Mouse sensitivity Y", 780f, 39, 15, MouseLabel);
            _mouseY.Changed += step => GameSettings.SetMouseSensitivityY(MouseValue(step));
            // Controller: steps of five from 5 to 100 (the default 35 is step 6).
            _pad = SliderField.Create(panel, "Controller sensitivity", 780f, 19, 6, step => PadValue(step).ToString("0"));
            _pad.Changed += step => GameSettings.SetControllerSensitivity(PadValue(step));
            _invert = SelectorField.Create(panel, "Invert Y", 780f, OffOn, 0);
            _invert.Changed += i => GameSettings.SetInvertY(i == 1);

            // The key list sits to the right of the sliders and shows with the CONTROLS tab.
            _keysPanel = UiKit.CreateRect("Keys", root);
            _keysPanel.Fill();
            var heading = UiKit.CreateText(_keysPanel, "Heading", "KEY REBINDING", TextStyle.Heading.WithSize(52f));
            heading.rectTransform.AtBoard(Left, 1000f, 214f, 800f, 58f);

            var first = UiKit.CreateColumn(_keysPanel, "Keys A", 0f);
            first.AtBoard(Left, 1000f, 300f, 400f, 0f, fromTop: true);
            var second = UiKit.CreateColumn(_keysPanel, "Keys B", 0f);
            second.AtBoard(Left, 1440f, 300f, 400f, 0f, fromTop: true);

            var lines = GameSettings.KeyActions;
            int split = (lines.Length + 1) / 2;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var row = KeyRow.Create(i < split ? first : second, line.Label, 400f, line.Locked);
                row.Pressed += () => BeginRebind(row, line.Action);
                _keys.Add(row);
            }

            var note = UiKit.CreateText(_keysPanel, "Note", "Esc cancels a rebind. Grey keys are fixed.",
                TextStyle.Small.WithSize(24f));
            note.color = Note;
            note.rectTransform.AtBoard(Left, 1000f, 880f, 840f, 32f);
        }

        static float MouseValue(int step) => (step + 1) * 0.25f;
        static string MouseLabel(int step) => MouseValue(step).ToString("0.0#");
        static float PadValue(int step) => (step + 1) * 5f;

        static int MouseStep(float value) => Mathf.Clamp(Mathf.RoundToInt(value / 0.25f) - 1, 0, 39);
        static int PadStep(float value) => Mathf.Clamp(Mathf.RoundToInt(value / 5f) - 1, 0, 19);

        static string[] Upper(string[] names)
        {
            var upper = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
                upper[i] = names[i].ToUpperInvariant();
            return upper;
        }

        #region Showing

        public void Show(int tab = 0)
        {
            gameObject.SetActive(true);
            _group.alpha = 1f;
            _group.interactable = true;
            _group.blocksRaycasts = true;
            Load();
            _tabs.Select(tab, notify: false);
            ShowTab(tab);
            _backAction.Enable();
        }

        public void Hide()
        {
            CancelRebind();
            _backAction.Disable();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;
            gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            CancelRebind();
            _backAction?.Dispose();
        }

        void ShowTab(int tab)
        {
            for (int i = 0; i < _panels.Length; i++)
                _panels[i].gameObject.SetActive(i == tab);
            _keysPanel.gameObject.SetActive(tab == 2);
            _hint.gameObject.SetActive(true);
            FocusFirst(tab);
        }

        void FocusFirst(int tab)
        {
            var events = EventSystem.current;
            if (events == null)
                return;

            Selectable first = tab == 0 ? _quality.Selectable
                : tab == 1 ? _volume.Selectable
                : _mouseX.Selectable;
            events.SetSelectedGameObject(first.gameObject);
        }

        /// <summary>Reads every value back from the game, so the screen shows what is really in force.</summary>
        void Load()
        {
            _quality.SetSelected(GameSettings.Quality, notify: false);

            _resolutions = GameSettings.Resolutions();
            var labels = new string[_resolutions.Count];
            for (int i = 0; i < labels.Length; i++)
                labels[i] = _resolutions[i].x + " × " + _resolutions[i].y;
            _resolution.SetChoices(labels, _resolutions.IndexOf(GameSettings.CurrentResolution));

            _fullscreen.SetSelected(GameSettings.Fullscreen ? 1 : 0, notify: false);
            _vsync.SetSelected(GameSettings.VSync ? 1 : 0, notify: false);
            int rate = Array.IndexOf(GameSettings.FrameRates, GameSettings.MaxFrameRate);
            _frameRate.SetSelected(rate >= 0 ? rate : GameSettings.FrameRates.Length - 1, notify: false);

            _volume.SetStep(Mathf.RoundToInt(GameSettings.MasterVolume * 20f));
            _mouseX.SetStep(MouseStep(GameSettings.MouseSensitivityX));
            _mouseY.SetStep(MouseStep(GameSettings.MouseSensitivityY));
            _pad.SetStep(PadStep(GameSettings.ControllerSensitivity));
            _invert.SetSelected(GameSettings.InvertY ? 1 : 0, notify: false);

            RefreshKeys();
        }

        void RefreshKeys()
        {
            var lines = GameSettings.KeyActions;
            for (int i = 0; i < _keys.Count && i < lines.Length; i++)
            {
                _keys[i].SetListening(false);
                _keys[i].SetKey(GameSettings.KeyLabel(lines[i].Action));
            }
        }

        void ResetTab()
        {
            CancelRebind();
            switch (_tabs.Active)
            {
                case 0: GameSettings.ResetVideo(); break;
                case 1: GameSettings.ResetSound(); break;
                default: GameSettings.ResetControls(); break;
            }

            Load();
        }

        #endregion

        #region Rebinding

        void BeginRebind(KeyRow row, string action)
        {
            if (_rebind != null || row.Locked)
                return;

            row.SetListening(true);
            _rebind = GameSettings.StartRebind(action, EndRebind, EndRebind);
            if (_rebind == null)
                row.SetListening(false);
        }

        void EndRebind()
        {
            _rebind = null;
            _rebindEndedFrame = Time.frameCount;
            RefreshKeys();
        }

        void CancelRebind()
        {
            if (_rebind == null)
                return;
            var operation = _rebind;
            _rebind = null;
            operation.Cancel();
        }

        #endregion

        void Update()
        {
            // Escape while listening cancels the rebind (the operation hears it itself), and must not
            // also leave the screen in the same frame.
            if (_rebind != null || _rebindEndedFrame >= Time.frameCount - 1)
                return;
            if (_backAction.WasPressedThisFrame())
                Back?.Invoke();
        }

        /// <summary>For gallery pages: one key line listening, as the board shows it.</summary>
        public void PreviewListening(string label)
        {
            var lines = GameSettings.KeyActions;
            for (int i = 0; i < _keys.Count && i < lines.Length; i++)
            {
                if (lines[i].Label == label)
                    _keys[i].SetListening(true);
            }
        }

        /// <summary>For gallery pages: the focused look on a tab's first row.</summary>
        public void PreviewFocus(int tab)
        {
            if (tab == 0)
                _quality.ShowFocused(true);
            else if (tab == 1)
                _volume.ShowFocused(true);
            else
                _mouseX.ShowFocused(true);
        }
    }
}
