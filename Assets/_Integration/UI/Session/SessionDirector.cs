#if CMPSETUP_COMPLETE
using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.UI
{
    /// <summary>
    /// The loading mechanic, and what happens when a session breaks.
    ///
    /// Lives for the whole run (DontDestroyOnLoad) and listens to every Fusion runner as it appears.
    /// Every networked scene load - joining a room (Menu → Lobby), starting the match (Lobby → Game),
    /// joining late - brings up the loading screen, and it stays up until the destination is really
    /// ready: in Game, until the local player has spawned and the HUD exists, not merely until the
    /// scene file has loaded. A watchdog lets go after a while either way, so a hiccup can never
    /// trap anyone behind it.
    ///
    /// It is an overlay, not an intermediate scene: Fusion owns scene switching (clients unload the
    /// old scene and may park in a temporary one), and the game's hooks key on the scene names.
    ///
    /// A session that ends without being asked to - a disconnect, a timeout, the host vanishing -
    /// raises board 17 with a way back to the menu. In the Menu scene the menu flow reports its own
    /// errors through <see cref="ShowError"/>, and covers connecting and joining with
    /// <see cref="BeginMenuWait"/> until a scene load takes over.
    /// </summary>
    public class SessionDirector : MonoBehaviour, INetworkRunnerCallbacks
    {
        const float MinimumSeconds = 1f;
        const float SceneTimeoutSeconds = 60f;
        const float SpawnTimeoutSeconds = 25f;
        const float SettleSeconds = 0.6f;
        const float LobbySettleSeconds = 0.4f;
        const float LeaveTimeoutSeconds = 6f;
        const float MenuWaitTimeoutSeconds = 30f;

        public static SessionDirector Instance { get; private set; }

        enum Waiting
        {
            None,
            SceneLoad,
            Lobby,
            LocalPlayer,
            Leaving,
            Hiding,
            Menu,
        }

        /// <summary>The room list the lobby last sent; raised again on every change.</summary>
        public static event Action<List<SessionInfo>> SessionsUpdated;

        public List<SessionInfo> Sessions { get; private set; } = new List<SessionInfo>();

        /// <summary>True once the lobby has sent a room list on this connection.</summary>
        public bool HasSessionList { get; private set; }

        float _hideAt;

        Canvas _loadingCanvas;
        Canvas _popupCanvas;
        LoadingOverlayView _loading;
        ErrorPopupView _popup;
        GameObject _eventSystem;

        readonly List<NetworkRunner> _attached = new List<NetworkRunner>();
        Waiting _waiting;
        float _shownAt;
        float _waitStartedAt;
        float _readyAt = -1f;
        bool _returningToMenu;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Instance != null)
                return;

            var go = new GameObject("SessionUi");
            DontDestroyOnLoad(go);
            go.AddComponent<SessionDirector>();
        }

        void Awake()
        {
            Instance = this;
            _loadingCanvas = UiKit.CreateCanvas("Loading", UiLayers.Loading, interactive: true, parent: transform);
            _loading = LoadingOverlayView.Create(_loadingCanvas.transform);
            _popupCanvas = UiKit.CreateCanvas("Popups", UiLayers.Popups, interactive: true, parent: transform);
            _popup = ErrorPopupView.Create(_popupCanvas.transform);
            SceneManager.sceneLoaded += OnUnitySceneLoaded;
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnUnitySceneLoaded;
            foreach (var runner in _attached)
            {
                if (runner != null)
                    runner.RemoveCallbacks(this);
            }

            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            AttachToRunners();
            KeepPopupReachable();

            float now = Time.unscaledTime;
            switch (_waiting)
            {
                case Waiting.SceneLoad:
                    if (now - _waitStartedAt > SceneTimeoutSeconds)
                        Fail("Loading took far too long.", "SceneTimeout");
                    break;

                case Waiting.Lobby:
                    if (now - _waitStartedAt > LobbySettleSeconds)
                        Finish();
                    break;

                case Waiting.LocalPlayer:
                    if (LocalPlayerReady())
                    {
                        if (_readyAt < 0f)
                        {
                            _readyAt = now;
                            _loading.SetStatus("Found you!");
                        }
                        if (now - _readyAt > SettleSeconds)
                            Finish();
                    }
                    else if (now - _waitStartedAt > SpawnTimeoutSeconds)
                    {
                        Finish();
                    }
                    break;

                case Waiting.Leaving:
                    if (now - _waitStartedAt > LeaveTimeoutSeconds)
                        Finish();
                    break;

                case Waiting.Menu:
                    // The menu raises its own errors sooner; this only makes sure nothing stays covered.
                    if (now - _waitStartedAt > MenuWaitTimeoutSeconds)
                        Finish();
                    break;

                case Waiting.Hiding:
                    if (now >= _hideAt)
                    {
                        _waiting = Waiting.None;
                        _loading.Hide();
                    }
                    break;
            }
        }

        void AttachToRunners()
        {
            for (int i = _attached.Count - 1; i >= 0; i--)
            {
                if (_attached[i] == null)
                    _attached.RemoveAt(i);
            }

            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner == null || _attached.Contains(runner))
                    continue;
                runner.AddCallbacks(this);
                _attached.Add(runner);
            }
        }

        #region Loading

        void BeginLoading(string status)
        {
            if (_waiting == Waiting.None)
                _shownAt = Time.unscaledTime;
            _waiting = Waiting.SceneLoad;
            _waitStartedAt = Time.unscaledTime;
            _readyAt = -1f;
            _loading.Show(status, RegionName());
        }

        void OnSceneArrived(string scene)
        {
            if (_waiting == Waiting.None || _waiting == Waiting.Hiding)
                return;

            _waitStartedAt = Time.unscaledTime;
            if (scene == SceneNames.Game)
            {
                _waiting = Waiting.LocalPlayer;
                _readyAt = -1f;
                _loading.SetStatus("Finding your team…");
            }
            else if (scene == SceneNames.Lobby)
            {
                _waiting = Waiting.Lobby;
            }
            else
            {
                Finish();
            }
        }

        /// <summary>The destination is ready: hide - but never as a flash, a quick load still shows for a moment.</summary>
        void Finish()
        {
            _waiting = Waiting.Hiding;
            _hideAt = Mathf.Max(Time.unscaledTime, _shownAt + MinimumSeconds);
        }

        static bool LocalPlayerReady()
        {
            foreach (var bridge in FpsNetworkBridge.All)
            {
                if (bridge != null && bridge.IsLocalOwner && bridge.DualPlayer != null &&
                    bridge.GetComponent<HudRoot>() != null)
                    return true;
            }

            return false;
        }

        static string StatusForLoadFrom(string scene)
        {
            if (scene == SceneNames.Menu)
                return "Joining the room…";
            if (scene == SceneNames.Lobby)
                return "Waking up the hospital…";
            return "Loading…";
        }

        /// <summary>The region Clean Multiplayer Pro connects to, as its menu names it.</summary>
        static string RegionName()
        {
            switch (PlayerPrefs.GetInt("region", 0))
            {
                case 1: return "Asia";
                case 2: return "Europe";
                case 3: return "Japan";
                case 4: return "South Korea";
                case 5: return "USA";
                default: return "Best region";
            }
        }

        void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // A fresh menu means a fresh connection: the old room list is gone with it.
            if (scene.name == SceneNames.Menu)
            {
                HasSessionList = false;
                Sessions = new List<SessionInfo>();
            }

            if (_returningToMenu && scene.name == SceneNames.Menu)
            {
                _returningToMenu = false;
                Finish();
                return;
            }

            if (_waiting == Waiting.Leaving && scene.name == SceneNames.Menu)
            {
                Finish();
                return;
            }

            OnSceneArrived(scene.name);
        }

        #endregion

        #region Menu

        /// <summary>
        /// A wait the menu starts before any scene loads - connecting, joining a room. The overlay
        /// shows <paramref name="status"/> until <see cref="EndMenuWait"/>, or until a scene load
        /// takes it over; failures are the menu's to report.
        /// </summary>
        public void BeginMenuWait(string status)
        {
            if (_waiting == Waiting.None || _waiting == Waiting.Hiding)
                _shownAt = Time.unscaledTime;
            _waiting = Waiting.Menu;
            _waitStartedAt = Time.unscaledTime;
            _readyAt = -1f;
            _loading.Show(status, RegionName());
        }

        public void EndMenuWait()
        {
            if (_waiting == Waiting.Menu)
                Finish();
        }

        /// <summary>Board 17 raised by the menu; <paramref name="action"/> defaults to reloading the menu.</summary>
        public void ShowError(string message, string code, Action action = null)
        {
            _waiting = Waiting.None;
            _loading.Hide();
            _loading.Snap();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _popup.Show(message, code, action ?? ReturnToMenu);
        }

        public bool IsShowingError => _popup.IsVisible;

        /// <summary>
        /// The popup must stay clickable whatever took the scene's event system with it - the
        /// player's objects are destroyed in the same frame a disconnect raises it.
        /// </summary>
        void KeepPopupReachable()
        {
            if (!_popup.IsVisible || UnityEngine.EventSystems.EventSystem.current != null)
                return;

            _eventSystem = UiKit.EnsureEventSystem();
            if (_eventSystem != null)
                DontDestroyOnLoad(_eventSystem);
            _popup.Reselect();
        }

        /// <summary>A plain sentence for one of Fusion's shutdown reasons.</summary>
        public static string Describe(string reason) => MessageFor(reason);

        #endregion

        #region Errors and leaving

        void Fail(string message, string code)
        {
            _waiting = Waiting.None;
            _loading.Hide();
            _loading.Snap();

            if (_eventSystem == null)
                _eventSystem = UiKit.EnsureEventSystem();
            if (_eventSystem != null)
                DontDestroyOnLoad(_eventSystem);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            _popup.Show(message, code, ReturnToMenu);
        }

        /// <summary>Shuts every session down and goes back to the menu, behind the loading screen.</summary>
        public void ReturnToMenu()
        {
            _popup.Hide();
            if (_eventSystem != null)
            {
                Destroy(_eventSystem);
                _eventSystem = null;
            }

            _shownAt = Time.unscaledTime;
            _waiting = Waiting.Leaving;
            _waitStartedAt = Time.unscaledTime;
            _returningToMenu = true;
            _loading.Show("Going back to the menu…", string.Empty);

            foreach (var runner in new List<NetworkRunner>(NetworkRunner.Instances))
            {
                if (runner != null)
                    runner.Shutdown();
            }

            SceneManager.LoadScene(SceneNames.Menu);
        }

        static string MessageFor(string reason)
        {
            switch (reason)
            {
                case "GameNotFound": return "That room is gone.";
                case "GameIsFull": return "That room is full.";
                case "GameClosed": return "That room has closed.";
                case "ConnectionRefused": return "The server turned us away.";
                case "ConnectionTimeout":
                case "PhotonCloudTimeout":
                case "Timeout":
                    return "We lost the connection to the server.";
                case "DisconnectedByPluginLogic": return "The room closed on us.";
                default: return "The session ended unexpectedly.";
            }
        }

        static bool InMenu() => SceneManager.GetActiveScene().name == SceneNames.Menu;

        #endregion

        #region Fusion callbacks

        public void OnSceneLoadStart(NetworkRunner runner)
        {
            if (!_returningToMenu)
                BeginLoading(StatusForLoadFrom(SceneManager.GetActiveScene().name));
        }

        public void OnSceneLoadDone(NetworkRunner runner)
        {
            OnSceneArrived(SceneManager.GetActiveScene().name);
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            _attached.Remove(runner);
            HasSessionList = false;
            if (_returningToMenu)
                return;

            if (shutdownReason == ShutdownReason.Ok)
            {
                // Asked for: leaving the lobby or the match. Cover the trip back to the menu.
                if (!InMenu())
                {
                    _shownAt = Time.unscaledTime;
                    _waiting = Waiting.Leaving;
                    _waitStartedAt = Time.unscaledTime;
                    _loading.Show("Leaving…", string.Empty);
                }
                return;
            }

            if (!InMenu())
                Fail(MessageFor(shutdownReason.ToString()), shutdownReason.ToString());
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            if (!InMenu() && !_returningToMenu)
                Fail("We lost the connection to the server.", reason.ToString());
        }

        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
            if (!InMenu() && !_returningToMenu)
                Fail("We could not reach the server.", reason.ToString());
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            Sessions = sessionList ?? new List<SessionInfo>();
            HasSessionList = true;
            SessionsUpdated?.Invoke(Sessions);
        }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }

#pragma warning disable CS0618 // Still part of the interface in Fusion 2.1, though unused.
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
#pragma warning restore CS0618

        #endregion
    }
}
#endif
