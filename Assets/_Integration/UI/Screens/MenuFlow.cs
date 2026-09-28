#if CMPSETUP_COMPLETE
using System.Collections;
using System.Collections.Generic;
using AvocadoShark;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.UI
{
    /// <summary>
    /// The redesigned front end in the Menu scene - the title screen and the room browser - driving
    /// Clean Multiplayer Pro's own connection code (FusionConnection) rather than replacing it, so
    /// rooms, passwords and the player spawn keep working exactly as they did.
    ///
    /// CMP's menu stays alive, because its scripts run the connection and its fades and room rows
    /// start coroutines that fail on inactive objects, but it draws nothing and takes no input: its
    /// canvas is switched off and its canvas group made inert.
    /// </summary>
    public class MenuFlow : MonoBehaviour
    {
        /// <summary>The player's name, saved on this machine by the title screen.</summary>
        public const string NameKey = "CollarCali.PlayerName";
        const float ConnectTimeoutSeconds = 20f;
        const float JoinTimeoutSeconds = 25f;

        enum State
        {
            Title,
            Connecting,
            Rooms,
            Joining,
        }

        FusionConnection _fc;
        Canvas _canvas;
        MainMenuView _main;
        RoomBrowserView _rooms;
        SettingsView _settings;
        State _state;
        float _stateAt;
        bool _verifying;
        readonly List<RoomListing> _listings = new List<RoomListing>();
        List<SessionInfo> _sessions = new List<SessionInfo>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            // The first scene finished loading before this ran.
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != SceneNames.Menu || FindFirstObjectByType<MenuFlow>() != null)
                return;
            // Only over CMP's menu, which carries the connection this drives.
            if (FindFirstObjectByType<FusionConnection>() == null)
                return;

            var go = new GameObject("MenuUi");
            SceneManager.MoveGameObjectToScene(go, scene);
            go.AddComponent<MenuFlow>();
        }

        void Awake()
        {
            _fc = FusionConnection.Instance != null ? FusionConnection.Instance : FindFirstObjectByType<FusionConnection>();

            _canvas = UiKit.CreateCanvas("Menu", UiLayers.Screens, interactive: true, parent: transform);
            _main = MainMenuView.Create(_canvas.transform);
            _rooms = RoomBrowserView.Create(_canvas.transform);

            _settings = SettingsView.Create(_canvas.transform, overGame: false);
            _settings.Back += CloseSettings;

            _main.Enter += OnEnter;
            _main.Exit += OnExit;
            _main.Settings += OpenSettings;

            _rooms.Join += OnJoin;
            _rooms.JoinWithPassword += OnJoinWithPassword;
            _rooms.CreateRoom += OnCreate;
            _rooms.Refresh += () => _rooms.SetRooms(Listings());
            _rooms.Back += OnBack;

            UiKit.EnsureEventSystem();
            SessionDirector.SessionsUpdated += OnSessions;
        }

        void OnDestroy()
        {
            SessionDirector.SessionsUpdated -= OnSessions;
        }

        void Start()
        {
            HideCmpMenu();

            string saved = PlayerPrefs.GetString(NameKey, string.Empty);
            _main.PlayerName = !string.IsNullOrEmpty(saved) ? saved : FusionConnection.LastPlayerName ?? string.Empty;
            _main.Region = PlayerPrefs.GetInt("region", 0);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            SetState(State.Title);
        }

        /// <summary>CMP's menu keeps running underneath - its scripts are the connection - but unseen and untouchable.</summary>
        void HideCmpMenu()
        {
            foreach (var menu in FindObjectsByType<MenuCanvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Silence(menu.gameObject);

            // The character preview is a separate world-space canvas with a 3D model on it.
            if (_fc != null && _fc.characterselectionobject != null)
                _fc.characterselectionobject.SetActive(false);
        }

        static void Silence(GameObject root)
        {
            var canvas = root.GetComponent<Canvas>();
            if (canvas != null)
                canvas.enabled = false;
            var group = root.GetComponent<CanvasGroup>();
            if (group == null)
                group = root.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        void SetState(State state)
        {
            _state = state;
            _stateAt = Time.unscaledTime;
            if (_settings.IsVisible)
                _settings.Hide();

            bool title = state == State.Title || state == State.Connecting;
            _main.SetVisible(title);
            _rooms.SetVisible(!title);

            if (state == State.Title)
                _main.FocusDefault();
            else if (state == State.Rooms)
                _rooms.FocusDefault();
        }

        void Update()
        {
            WatchCmpPopup();

            float waited = Time.unscaledTime - _stateAt;
            if (_state == State.Connecting && waited > ConnectTimeoutSeconds)
                Fail("We could not reach the server.", "ConnectTimeout");
            else if (_state == State.Joining && waited > JoinTimeoutSeconds && !InSession())
                Fail("Joining the room took too long.", "JoinTimeout");
        }

        /// <summary>The join went through: the scene load that follows is the loading screen's to watch.</summary>
        bool InSession()
        {
            var runner = _fc != null ? _fc.Runner : null;
            return runner != null && runner.SessionInfo != null && runner.SessionInfo.IsValid;
        }

        /// <summary>
        /// CMP reports every connection failure through its own popup, which is hidden with the rest
        /// of its menu; the message is passed on to the redesigned one.
        /// </summary>
        void WatchCmpPopup()
        {
            if (_fc == null || _fc.popup == null || !_fc.popup.gameObject.activeSelf)
                return;

            string raw = ReadPopupText(_fc.popup.gameObject);
            _fc.popup.gameObject.SetActive(false);
            Fail(SessionDirector.Describe(raw), raw);
        }

        static string ReadPopupText(GameObject popup)
        {
            string best = string.Empty;
            foreach (var text in popup.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (text.name == "Content")
                    return text.text;
                if (text.text.Length > best.Length)
                    best = text.text;
            }

            return best;
        }

        void Fail(string message, string code)
        {
            SetState(_state == State.Joining ? State.Rooms : State.Title);
            var session = SessionDirector.Instance;
            if (session != null)
                session.ShowError(message, code);
        }

        #region Title

        void OnEnter()
        {
            if (_state != State.Title)
                return;

            string name = _main.PlayerName;
            if (string.IsNullOrEmpty(name))
            {
                _main.ShowNameError("Pick a name first.");
                return;
            }

            if (_fc == null)
            {
                Fail("The menu lost its connection.", "NoFusionConnection");
                return;
            }

            PlayerPrefs.SetString(NameKey, name);
            PlayerPrefs.Save();

            // FusionConnection reads the name off its own field after its fade, and the region
            // must be set before it connects.
            _fc.nameField.text = name;
            FusionConnection.LastPlayerName = name;
            _fc.ChangeRegion(_main.Region);
            _fc.ConnectToRunner();

            SetState(State.Connecting);
            SessionDirector.Instance?.BeginMenuWait("Connecting…");

            // A list that is already in (a quick lobby) would otherwise be missed.
            var director = SessionDirector.Instance;
            if (director != null && director.HasSessionList)
                OnSessions(director.Sessions);
        }

        void OpenSettings()
        {
            if (_state != State.Title)
                return;
            _main.SetVisible(false);
            _settings.Show();
        }

        void CloseSettings()
        {
            _settings.Hide();
            if (_state != State.Title)
                return;
            _main.SetVisible(true);
            _main.FocusSettings();
        }

        void OnExit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        #endregion

        #region Rooms

        void OnSessions(List<SessionInfo> sessions)
        {
            _sessions = sessions ?? new List<SessionInfo>();
            // Rows first, so the default focus can land on a room rather than on CREATE.
            _rooms.SetRooms(Listings());
            if (_state == State.Connecting)
            {
                SessionDirector.Instance?.EndMenuWait();
                SetState(State.Rooms);
            }
        }

        List<RoomListing> Listings()
        {
            _listings.Clear();
            foreach (var session in _sessions)
            {
                if (session == null || !session.IsVisible)
                    continue;
                _listings.Add(new RoomListing
                {
                    Name = session.Name,
                    Players = session.PlayerCount,
                    MaxPlayers = session.MaxPlayers,
                    Password = RoomPassword.IsProtected(session),
                    Open = session.IsOpen,
                });
            }

            return _listings;
        }

        SessionInfo Find(string name)
        {
            foreach (var session in _sessions)
            {
                if (session != null && session.Name == name)
                    return session;
            }

            return null;
        }

        void OnJoin(RoomListing room)
        {
            if (_state != State.Rooms || room.Full || !room.Open)
                return;

            PlayerPrefs.SetInt("has_pass", 0);
            BeginJoining("Joining the room…");
            _fc.JoinRoom(room.Name);
        }

        void OnJoinWithPassword(RoomListing room, string password)
        {
            if (_state != State.Rooms || _verifying)
                return;

            var session = Find(room.Name);
            if (session == null)
            {
                _rooms.PasswordRejected(room.Name);
                return;
            }

            StartCoroutine(VerifyAndJoin(session, password ?? string.Empty));
        }

        /// <summary>
        /// Checking a password derives a key, which holds the main thread for a moment; the row shows
        /// busy and gets a frame to say so first, as CMP's own row does.
        /// </summary>
        IEnumerator VerifyAndJoin(SessionInfo session, string password)
        {
            _verifying = true;
            _rooms.SetBusy(session.Name, true);
            yield return null;

            // Something else may have started a join in that frame.
            if (_state != State.Rooms)
            {
                _rooms.SetBusy(session.Name, false);
                _verifying = false;
                yield break;
            }

            bool accepted = RoomPassword.Verify(session, password);
            _rooms.SetBusy(session.Name, false);
            _verifying = false;

            if (!accepted)
            {
                _rooms.PasswordRejected(session.Name);
                yield break;
            }

            PlayerPrefs.SetInt("has_pass", 1);
            BeginJoining("Joining the room…");
            _fc.JoinRoom(session.Name);
        }

        void OnCreate(string name, string password, int maxPlayers)
        {
            if (_state != State.Rooms)
                return;

            if (string.IsNullOrEmpty(name))
                name = "Room-" + Random.Range(1000, 9999);

            // Creating a room under a name that exists would join that room instead - past its
            // password, if it has one.
            if (Find(name) != null)
            {
                _rooms.ShowRoomNameError("That name is taken.");
                return;
            }

            bool locked = !string.IsNullOrEmpty(password);
            PlayerPrefs.SetInt("has_pass", locked ? 1 : 0);
            BeginJoining("Making the room…");
            _fc.JoinRoom(name, Mathf.Clamp(maxPlayers, RoomBrowserView.MinPlayers, RoomBrowserView.MaxPlayers),
                locked ? password : string.Empty);
        }

        void BeginJoining(string status)
        {
            SetState(State.Joining);
            SessionDirector.Instance?.BeginMenuWait(status);
        }

        /// <summary>Back to the title: CMP's own way, which drops the lobby connection and reloads the menu.</summary>
        void OnBack()
        {
            if (_state != State.Rooms)
                return;

            if (_fc != null && _fc.popup != null)
                _fc.popup.DisablePopup();
            else
                SceneManager.LoadScene(SceneNames.Menu);
        }

        #endregion
    }
}
#endif
