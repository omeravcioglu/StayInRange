#if CMPSETUP_COMPLETE
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace CollarCali.UI
{
    /// <summary>
    /// Escape opens the pause menu over the running game - in first person, third person or dead -
    /// and Escape or RESUME closes it. Nothing stops: the player is held still, the cursor is free,
    /// and LEAVE MATCH is the way back to the menu that the game never had.
    ///
    /// Created by HudRoot with the local player and gone with it. Cowsins' own pause menu stays
    /// switched off (FpsNetworkBridge does that); this reads its own key, since Cowsins' pause action
    /// is off in third person and while dead.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class PauseController : MonoBehaviour
    {
        public static PauseController Instance { get; private set; }

        FpsNetworkBridge _local;
        Canvas _backCanvas;
        Canvas _frontCanvas;
        PauseMenuView _view;
        SettingsView _settings;
        InputAction _toggle;
        bool _open;
        int _settingsClosedFrame = -1;

        public bool IsOpen => _open;

        DualPlayerController _offline;

        public static PauseController Create(FpsNetworkBridge local)
        {
            var go = new GameObject("PauseMenu");
            var controller = go.AddComponent<PauseController>();
            controller._local = local;
            return controller;
        }

        /// <summary>Game.unity played on its own: the scene's offline test player, no session.</summary>
        public static PauseController CreateOffline(DualPlayerController dual)
        {
            var go = new GameObject("PauseMenu");
            var controller = go.AddComponent<PauseController>();
            controller._offline = dual;
            return controller;
        }

        bool Live => _local != null
            ? _local.Object != null && _local.Object.IsValid && _local.IsLocalOwner
            : _offline != null && _offline.isActiveAndEnabled;

        DualPlayerController Dual => _local != null ? _local.DualPlayer : _offline;

        void Awake()
        {
            Instance = this;

            // The dim sits under the HUD so the team panel stays readable over it, as on the board;
            // the menu goes over everything but popups and the loading screen.
            _backCanvas = UiKit.CreateCanvas("Pause Back", UiLayers.HudFx + 20, parent: transform);
            _frontCanvas = UiKit.CreateCanvas("Pause", UiLayers.Screens, interactive: true, parent: transform);
            _view = PauseMenuView.Create(_backCanvas.transform, _frontCanvas.transform);
            _settings = SettingsView.Create(_frontCanvas.transform, overGame: true);
            _settings.Back += CloseSettings;
            _view.Settings += OpenSettings;
            _view.Resume += Resume;
            _view.Leave += LeaveMatch;
            _view.Exit += ExitToDesktop;

            _toggle = new InputAction("Pause", InputActionType.Button);
            _toggle.AddBinding("<Keyboard>/escape");
            _toggle.AddBinding("<Gamepad>/start");
            _toggle.Enable();
        }

        void OnDestroy()
        {
            // Gone with the player, usually on the way out of the match: the hold is let go, but the
            // cursor is left free for whatever screen comes next.
            if (_open)
                SetOpen(false, relockCursor: false);

            _toggle?.Dispose();
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            bool live = Live;
            // The error popup owns the screen, the cursor and the selection while it is up.
            var session = SessionDirector.Instance;
            bool errorShown = session != null && session.IsShowingError;
            if (!live || errorShown)
            {
                if (_open)
                    SetOpen(false, relockCursor: false);
                return;
            }

            // Settings keep Escape for themselves (back, or cancelling a rebind) - including the frame
            // they closed on, so one press never goes back twice.
            if (_settings.IsVisible || _settingsClosedFrame == Time.frameCount)
                return;
            if (_toggle.WasPressedThisFrame())
                SetOpen(!_open, relockCursor: true);
        }

        void OpenSettings()
        {
            if (!_open)
                return;
            _view.Hide();
            _settings.Show();
        }

        void CloseSettings()
        {
            _settings.Hide();
            _settingsClosedFrame = Time.frameCount;
            if (!_open)
                return;
            _view.Show();
            _view.SelectSettings();
        }

        void LateUpdate()
        {
            if (!_open)
                return;

            // Other code locks the cursor again - a revive, a switch between first and third person -
            // so the unlock and the hold are re-applied every frame the menu is up.
            if (Cursor.lockState != CursorLockMode.None)
                Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible)
                Cursor.visible = true;

            var dual = Dual;
            if (dual != null)
                dual.SetMenuOpen(true);

            // A click on empty space drops the selection; the keys and a gamepad need one to move from.
            if (!_settings.IsVisible && _view.LostFocus())
                _view.SelectFirst();
        }

        public void Resume() => SetOpen(false, relockCursor: true);

        void SetOpen(bool open, bool relockCursor)
        {
            if (_open == open)
                return;

            _open = open;
            UiInput.SetMenuOpen(this, open);
            var dual = Dual;

            if (open)
            {
                // The game scene has no event system of its own. This one stays at the scene root,
                // not under the menu: the menu goes with the player, and an error popup raised in the
                // same moment still needs one to be clicked.
                UiKit.EnsureEventSystem();

                if (dual != null)
                    dual.SetMenuOpen(true);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _view.Show();
                return;
            }

            _view.Hide();
            if (_settings.IsVisible)
                _settings.Hide();
            if (dual != null)
                dual.SetMenuOpen(false);

            // Back to the game: the gameplay cursor is always locked, alive or spectating.
            if (relockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            var events = EventSystem.current;
            if (events != null && events.currentSelectedGameObject != null &&
                events.currentSelectedGameObject.transform.IsChildOf(transform))
            {
                events.SetSelectedGameObject(null);
            }
        }

        void LeaveMatch()
        {
            SetOpen(false, relockCursor: false);
            var session = SessionDirector.Instance;
            if (session != null)
            {
                session.ReturnToMenu();
                return;
            }

            // No session director (a scene played on its own in the editor): straight to the menu.
            UnityEngine.SceneManagement.SceneManager.LoadScene(SceneNames.Menu);
        }

        void ExitToDesktop()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
#endif
