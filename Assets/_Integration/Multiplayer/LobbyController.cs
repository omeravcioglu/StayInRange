#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace CollarCali
{
    /// <summary>
    /// Pre-game waiting room. The session now starts by loading this scene instead of Game, so
    /// creating a room drops the host here and every joiner lands in the same place. Nothing
    /// enters gameplay until the host presses Start, which hands the whole session over through
    /// Fusion's own scene sync - the one transition every client is guaranteed to follow.
    /// </summary>
    public class LobbyController : MonoBehaviour
    {
        public const string LobbySceneName = "Lobby";
        public const string GameSceneName = "Game";
        public const string MenuSceneName = "Menu";

        /// <summary>Host may start alone; solo playtesting stays a single click.</summary>
        const int MinPlayersToStart = 1;

        /// <summary>Shared-mode spawns can sit queued for a while; retry slowly, not per frame.</summary>
        const float SpawnRetrySeconds = 2f;

        /// <summary>
        /// If the scene handover has not taken by now something went wrong. Releasing the lock
        /// beats leaving the room frozen behind a dead "Starting..." button.
        /// </summary>
        const float StartTimeoutSeconds = 15f;

        /// <summary>
        /// How long the roster may lag session membership before the host is allowed to start
        /// anyway. Covers the normal replication window without letting one client that never
        /// spawns its record hold the whole room hostage.
        /// </summary>
        const float RosterSyncGraceSeconds = 10f;

        struct Row
        {
            public RectTransform Root;
            public Image Swatch;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Status;
        }

        readonly List<Row> _rows = new List<Row>();
        readonly List<LobbyPlayer> _sorted = new List<LobbyPlayer>();

        RectTransform _list;
        TextMeshProUGUI _title;
        TextMeshProUGUI _subtitle;
        TextMeshProUGUI _hint;
        Button _readyButton;
        TextMeshProUGUI _readyLabel;
        Button _startButton;
        TextMeshProUGUI _startLabel;
        Button _leaveButton;

        // Character selector (live 3D preview rendered to a texture, cycled with arrows).
        RawImage _previewImage;
        TextMeshProUGUI _charName;
        Camera _previewCamera;
        RenderTexture _previewRt;
        GameObject _previewModel;
        Light _previewLight;
        int _previewIndex;
        bool _previewReady;

        // The preview world sits far from the origin so its own little camera sees nothing but the
        // model and its light, no matter what else the lobby scene contains.
        static readonly Vector3 PreviewWorldOrigin = new Vector3(2000f, 2000f, 2000f);

        bool _spawnPending;
        bool _startIssued;
        float _nextSpawnAttempt;
        float _startDeadline;
        float _rosterMismatchSince;

        #region Scene hook

        // Fusion loads Lobby.unity after StartGame resolves, so a plain RuntimeInitializeOnLoad
        // never fires for the Menu -> Lobby path. Listening for the scene load covers both the
        // networked entry and opening the scene directly in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == LobbySceneName)
                Ensure();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            if (SceneManager.GetActiveScene().name == LobbySceneName)
                Ensure();
        }

        static void Ensure()
        {
            if (FindFirstObjectByType<LobbyController>(FindObjectsInactive.Include) != null)
                return;

            new GameObject("LobbyController").AddComponent<LobbyController>();

            // #region agent log
            AgentDebugLog.Write("L0", "LobbyController.Ensure", "lobby_controller_created", "{}");
            // #endregion
        }

        #endregion

        void Awake()
        {
            EnsureCamera();
            EnsureEventSystem();
            BuildCanvas();

            // Gameplay leaves the cursor locked and hidden. Without restoring it here the host
            // physically cannot click Start after returning from a match.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Time.timeScale = 1f;
        }

        void Update()
        {
            UpdatePreviewSpin();

            var runner = NetworkCombatHooks.FindRunner();

            if (runner == null || !runner.IsRunning)
            {
                SetStatus("Connecting to session...");
                if (_readyButton != null)
                {
                    _readyButton.gameObject.SetActive(false);
                    _readyButton.interactable = false;
                }
                if (_startButton != null)
                {
                    _startButton.gameObject.SetActive(false);
                    _startButton.interactable = false;
                }
                return;
            }

            TickStartWatchdog();
            EnsureLocalLobbyPlayer(runner);
            RefreshRoster(runner);
        }

        #region Start lifecycle

        /// <summary>
        /// LoadScene is asynchronous, so a failure inside Fusion's coroutine never reaches the
        /// call site. If we are still standing in the lobby well past the handover, release the
        /// lock so the host can retry instead of staring at a dead button.
        /// </summary>
        void TickStartWatchdog()
        {
            if (!_startIssued)
                return;
            if (Time.realtimeSinceStartup < _startDeadline)
                return;
            if (SceneManager.GetActiveScene().name != LobbySceneName)
                return;

            AbortStart("Match did not start. Try again.");
        }

        void AbortStart(string reason)
        {
            _startIssued = false;
            _startDeadline = 0f;
            LobbyPlayer.Local()?.ClearMatchStarting();
            SetStatus(reason);

            // #region agent log
            AgentDebugLog.Write("L4", "LobbyController.AbortStart", "match_start_aborted",
                "{\"reason\":\"" + reason.Replace("\"", "'") + "\"}");
            // #endregion
        }

        #endregion

        #region Roster

        void EnsureLocalLobbyPlayer(NetworkRunner runner)
        {
            // Once the match is committed the lobby is on its way out. Spawning past this point
            // produced records that materialised inside the Game scene and never went away.
            if (_startIssued || LobbyPlayer.AnyMatchStarting())
                return;
            if (SceneManager.GetActiveScene().name != LobbySceneName)
                return;

            if (LobbyPlayer.Local() != null)
            {
                _spawnPending = false;
                return;
            }

            if (_spawnPending && Time.realtimeSinceStartup < _nextSpawnAttempt)
                return;

            var prefab = LoadLobbyPlayerPrefab();
            if (prefab == null)
            {
                SetStatus("LobbyPlayer prefab missing from Resources.");
                return;
            }

            _spawnPending = true;
            _nextSpawnAttempt = Time.realtimeSinceStartup + SpawnRetrySeconds;

            try
            {
                runner.Spawn(prefab, Vector3.zero, Quaternion.identity, runner.LocalPlayer);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[CollarCali] Failed to spawn LobbyPlayer: " + e.Message);
            }
        }

        static NetworkObject _cachedPrefab;

        static NetworkObject LoadLobbyPlayerPrefab()
        {
            if (_cachedPrefab != null)
                return _cachedPrefab;

            var go = Resources.Load<GameObject>("LobbyPlayer");
            if (go != null)
                _cachedPrefab = go.GetComponent<NetworkObject>();

#if UNITY_EDITOR
            if (_cachedPrefab == null)
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/_Integration/Resources/LobbyPlayer.prefab");
                if (asset != null)
                    _cachedPrefab = asset.GetComponent<NetworkObject>();
            }
#endif
            return _cachedPrefab;
        }

        /// <summary>Session membership, which leads the lobby roster while records replicate.</summary>
        static int CountSessionPlayers(NetworkRunner runner)
        {
            int active = 0;
            foreach (var player in runner.ActivePlayers)
            {
                if (player.IsRealPlayer)
                    active++;
            }

            var info = runner.SessionInfo;
            int room = info != null ? info.PlayerCount : 0;

            // Deliberately the larger of the two. A joiner is in the Photon room before the
            // simulation registers them, and taking the smaller number would let Start go live
            // during exactly the window this count exists to cover.
            int count = Mathf.Max(active, room);
            return count > 0 ? count : 1;
        }

        void RefreshRoster(NetworkRunner runner)
        {
            LobbyPlayer.DespawnDuplicateLocals();

            _sorted.Clear();
            _sorted.AddRange(LobbyPlayer.All);

            // FindObjectsByType / spawn order is not stable across machines, so sorting by the
            // Fusion player id is what keeps every client showing the same list in the same order.
            _sorted.Sort((a, b) => a.Owner.PlayerId.CompareTo(b.Owner.PlayerId));

            while (_rows.Count < _sorted.Count)
                _rows.Add(CreateRow(_rows.Count));
            for (int i = _sorted.Count; i < _rows.Count; i++)
                _rows[i].Root.gameObject.SetActive(false);

            int readyCount = 0;

            for (int i = 0; i < _sorted.Count; i++)
            {
                var entry = _sorted[i];
                var row = _rows[i];
                row.Root.gameObject.SetActive(true);

                // Swatch shows the chosen character's tint when the skin library is available, so the
                // roster reflects who picked what; otherwise it falls back to a stable per-id colour.
                var lib = CharacterSkinLibrary.Load();
                if (lib != null && lib.Count > 0)
                    row.Swatch.color = lib.TintOf(entry.CharacterIndex);
                else
                    row.Swatch.color = PlayerColorPalette.Get(
                        entry.Owner.IsRealPlayer ? entry.Owner.PlayerId : i);

                string baseName = entry.IsLocal
                    ? entry.DisplayName + "  (you)"
                    : entry.DisplayName;
                row.Name.text = lib != null && lib.Count > 0
                    ? baseName + "   -   " + lib.NameOf(entry.CharacterIndex)
                    : baseName;

                if (entry.IsHost)
                {
                    row.Status.text = "HOST";
                    row.Status.color = new Color(1f, 0.82f, 0.25f, 1f);
                    readyCount++;
                }
                else if (entry.IsReady)
                {
                    row.Status.text = "READY";
                    row.Status.color = new Color(0.35f, 0.9f, 0.45f, 1f);
                    readyCount++;
                }
                else
                {
                    row.Status.text = "NOT READY";
                    row.Status.color = new Color(0.85f, 0.85f, 0.85f, 0.65f);
                }
            }

            // Master-client status is the authority for who may start, not the replicated IsHost
            // flag: that flag only lands after the host's own record spawns and ticks, and gating
            // the button on it can leave a room with nobody able to start at all.
            bool isMaster = runner.IsSharedModeMasterClient;
            bool starting = _startIssued || LobbyPlayer.AnyMatchStarting();

            int connected = CountSessionPlayers(runner);
            int listed = _sorted.Count;

            // Sentinel start, so the count this client sees on its own first refresh does not
            // announce itself as an arrival.
            if (_seenConnectedCount >= 0 && connected > _seenConnectedCount)
                GameSfx.Play2D(SfxId.UiPlayerJoined);
            _seenConnectedCount = connected;

            // A joiner is a session member before their lobby record replicates. Comparing the two
            // counts stops Start from going live during that window and dragging someone into the
            // match before they ever saw a Ready button.
            bool rosterSynced = listed >= connected;

            // ...but only for a while. A client whose record never spawns would otherwise disable
            // Start permanently for everyone, with no way out but Leave.
            if (rosterSynced)
                _rosterMismatchSince = 0f;
            else if (_rosterMismatchSince <= 0f)
                _rosterMismatchSince = Time.realtimeSinceStartup;

            bool rosterStale = !rosterSynced && _rosterMismatchSince > 0f &&
                               Time.realtimeSinceStartup - _rosterMismatchSince > RosterSyncGraceSeconds;

            bool rosterComplete = rosterSynced || rosterStale;
            bool allReady = listed > 0 && readyCount == listed && rosterComplete;
            bool enoughPlayers = connected >= MinPlayersToStart;

            _subtitle.text = ResolveRoomName(runner) + "   -   " +
                             connected + "/" + ResolveMaxPlayers(runner) + " players";

            if (starting)
                SetStatus("Starting match...");
            else if (!isMaster)
                SetStatus("Waiting for the host to start the match...");
            else if (!enoughPlayers)
                SetStatus("Waiting for players to join...");
            else if (!rosterSynced && !rosterStale)
                SetStatus("Syncing players...");
            else if (rosterStale)
                SetStatus("A player is not responding (" + listed + "/" + connected +
                          " synced). You can start without them.");
            else if (!allReady)
                SetStatus("Waiting for everyone to ready up (" + readyCount + "/" + listed + ").");
            else
                SetStatus("Everyone is ready. Press Start Game.");

            var local = LobbyPlayer.Local();

            _readyButton.gameObject.SetActive(!isMaster);
            _startButton.gameObject.SetActive(isMaster);

            if (!isMaster)
            {
                _readyLabel.text = local != null && local.IsReady ? "Cancel Ready" : "Ready Up";
                _readyButton.interactable = !starting && local != null;
            }
            else
            {
                _startLabel.text = starting ? "Starting..." : "Start Game";
                _startButton.interactable = !starting && allReady && enoughPlayers;
            }

            // Deliberately always interactable: a start that fails must never strand someone in a
            // lobby with every control greyed out.
            _leaveButton.interactable = true;
        }

        static string ResolveRoomName(NetworkRunner runner)
        {
            var info = runner.SessionInfo;
            return info != null && !string.IsNullOrEmpty(info.Name) ? info.Name : "Lobby";
        }

        static int ResolveMaxPlayers(NetworkRunner runner)
        {
            var info = runner.SessionInfo;
            return info != null && info.MaxPlayers > 0 ? info.MaxPlayers : 2;
        }

        #endregion

        #region Actions

        /// <summary>-1 until the first roster refresh has run. See the arrival check in Refresh.</summary>
        int _seenConnectedCount = -1;

        void OnReadyPressed()
        {
            var local = LobbyPlayer.Local();

            // Read before the toggle: the networked flag does not turn around until the next tick,
            // so afterwards it would still report the old state and play the wrong one of the two.
            bool readyingUp = local == null || !local.IsReady;
            GameSfx.Play2D(readyingUp ? SfxId.UiReady : SfxId.UiUnready);

            local?.ToggleReady();
        }

        void OnStartPressed()
        {
            if (_startIssued)
                return;

            var runner = NetworkCombatHooks.FindRunner();
            if (runner == null || !runner.IsRunning || !runner.IsSharedModeMasterClient)
                return;

            int index = FindBuildIndex(GameSceneName);
            if (index < 0)
            {
                SetStatus("Game scene is not in Build Settings.");
                Debug.LogError("[CollarCali] Cannot start: 'Game' is missing from Build Settings.");
                GameSfx.Play2D(SfxId.UiError);
                return;
            }

            GameSfx.Play2D(SfxId.UiMatchStart);
            _startIssued = true;
            _startDeadline = Time.realtimeSinceStartup + StartTimeoutSeconds;
            LobbyPlayer.Local()?.FlagMatchStarting();

            // #region agent log
            AgentDebugLog.Write("L2", "LobbyController.OnStartPressed", "match_start_requested",
                "{\"players\":" + _sorted.Count + ",\"sceneIndex\":" + index + "}");
            // #endregion

            try
            {
                // Shared mode routes this through the master client's networked scene state, so
                // every client - including one that joined seconds ago - follows the same load.
                // This is the only transition path; no client ever loads Game on its own.
                var op = runner.LoadScene(SceneRef.FromIndex(index), LoadSceneMode.Single,
                    LocalPhysicsMode.None, true);

                // Fusion runs the load in a coroutine and surfaces failures on the op rather than
                // throwing here, so the completion handler - not the catch - is what catches a
                // real scene-load failure.
                op.AddOnCompleted(completed =>
                {
                    if (completed.Error != null)
                        AbortStart("Could not load the game scene. Try again.");
                });
            }
            catch (System.Exception e)
            {
                AbortStart("Could not start the match. Try again.");
                Debug.LogError("[CollarCali] LoadScene(Game) failed: " + e);
            }
        }

        void OnLeavePressed()
        {
            var runner = NetworkCombatHooks.FindRunner();
            if (runner != null)
                runner.Shutdown();

            int menu = FindBuildIndex(MenuSceneName);
            if (menu >= 0)
                SceneManager.LoadScene(menu);
        }

        /// <summary>
        /// Build-index lookup by name. Shared with FusionConnection so the session start and the
        /// lobby transition can never disagree about which scene they mean.
        /// </summary>
        public static int FindBuildIndex(string sceneName)
        {
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                var path = SceneUtility.GetScenePathByBuildIndex(i);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == sceneName)
                    return i;
            }
            return -1;
        }

        #endregion

        #region UI construction

        void SetStatus(string text)
        {
            if (_hint != null)
                _hint.text = text;
        }

        static void EnsureCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Lobby Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            // Applied whether the camera came from the scene or was just created, so the lobby
            // never flashes a skybox behind the panel.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
            cam.cullingMask = 0;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            // The project runs the new Input System exclusively, so the legacy standalone module
            // would leave every button dead.
            go.AddComponent<InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        void BuildCanvas()
        {
            var root = new GameObject("LobbyCanvas", typeof(RectTransform));
            root.transform.SetParent(transform, false);

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            root.AddComponent<GraphicRaycaster>();

            var backdrop = CreateImage(root.transform, "Backdrop", new Color(0.05f, 0.06f, 0.08f, 1f));
            Stretch(backdrop.rectTransform);

            var panel = CreateImage(root.transform, "Panel", new Color(0.10f, 0.12f, 0.15f, 0.96f));
            var panelRt = panel.rectTransform;
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(820f, 700f);
            panelRt.anchoredPosition = Vector2.zero;

            _title = CreateText(panelRt, "Title", "LOBBY", 46f, TextAlignmentOptions.Center);
            Anchor(_title.rectTransform, new Vector2(0.5f, 1f), Center, new Vector2(0f, -60f),
                new Vector2(760f, 60f));
            _title.fontStyle = FontStyles.Bold;

            _subtitle = CreateText(panelRt, "Subtitle", "", 24f, TextAlignmentOptions.Center);
            Anchor(_subtitle.rectTransform, new Vector2(0.5f, 1f), Center, new Vector2(0f, -118f),
                new Vector2(760f, 34f));
            _subtitle.color = new Color(1f, 1f, 1f, 0.6f);

            _list = new GameObject("PlayerList", typeof(RectTransform)).GetComponent<RectTransform>();
            _list.SetParent(panelRt, false);
            _list.anchorMin = new Vector2(0.5f, 1f);
            _list.anchorMax = new Vector2(0.5f, 1f);
            _list.pivot = new Vector2(0.5f, 1f);
            _list.anchoredPosition = new Vector2(0f, -170f);
            _list.sizeDelta = new Vector2(720f, 360f);
            // Rooms can hold more players than the panel has room for; clip rather than letting
            // row seven paint over the hint and buttons.
            _list.gameObject.AddComponent<RectMask2D>();

            _hint = CreateText(panelRt, "Hint", "", 22f, TextAlignmentOptions.Center);
            Anchor(_hint.rectTransform, new Vector2(0.5f, 0f), Center, new Vector2(0f, 150f),
                new Vector2(760f, 34f));
            _hint.color = new Color(1f, 1f, 1f, 0.75f);

            _readyButton = CreateButton(panelRt, "ReadyButton", "Ready Up",
                new Color(0.20f, 0.45f, 0.85f, 1f), out _readyLabel);
            Anchor(_readyButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), Center,
                new Vector2(0f, 90f), new Vector2(300f, 62f));
            _readyButton.onClick.AddListener(OnReadyPressed);

            _startButton = CreateButton(panelRt, "StartButton", "Start Game",
                new Color(0.20f, 0.62f, 0.32f, 1f), out _startLabel);
            Anchor(_startButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), Center,
                new Vector2(0f, 90f), new Vector2(300f, 62f));
            _startButton.onClick.AddListener(OnStartPressed);

            // Hover only for these two: their handlers play something more specific than a click,
            // and claiming them here is what stops UiSfxAutoWire adding the generic one as well.
            UiSfxButton.HoverOnly(_readyButton);
            UiSfxButton.HoverOnly(_startButton);

            _leaveButton = CreateButton(panelRt, "LeaveButton", "Leave",
                new Color(0.35f, 0.16f, 0.18f, 1f), out _);
            Anchor(_leaveButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), Center,
                new Vector2(0f, 32f), new Vector2(200f, 44f));
            _leaveButton.onClick.AddListener(OnLeavePressed);

            _startButton.gameObject.SetActive(false);

            BuildCharacterSelector(root.transform);
        }

        #endregion

        #region Character selector

        void BuildCharacterSelector(Transform canvas)
        {
            // A standalone card to the left of the roster panel so the existing layout is untouched.
            var card = CreateImage(canvas, "CharacterCard", new Color(0.10f, 0.12f, 0.15f, 0.96f));
            var cardRt = card.rectTransform;
            cardRt.anchorMin = new Vector2(0.5f, 0.5f);
            cardRt.anchorMax = new Vector2(0.5f, 0.5f);
            cardRt.pivot = new Vector2(1f, 0.5f);
            // Sit just to the left of the 820-wide roster panel (410 half-width + a 20 gap).
            cardRt.anchoredPosition = new Vector2(-430f, 0f);
            cardRt.sizeDelta = new Vector2(360f, 620f);

            var heading = CreateText(cardRt, "CharacterHeading", "CHARACTER", 26f,
                TextAlignmentOptions.Center);
            Anchor(heading.rectTransform, new Vector2(0.5f, 1f), Center, new Vector2(0f, -36f),
                new Vector2(320f, 40f));
            heading.fontStyle = FontStyles.Bold;

            // Live 3D preview surface.
            _previewImage = CreateRawImage(cardRt, "Preview");
            Anchor(_previewImage.rectTransform, new Vector2(0.5f, 1f), Center, new Vector2(0f, -300f),
                new Vector2(300f, 460f));
            _previewImage.color = Color.white;

            _charName = CreateText(cardRt, "CharacterName", "", 24f, TextAlignmentOptions.Center);
            Anchor(_charName.rectTransform, new Vector2(0.5f, 0f), Center, new Vector2(0f, 118f),
                new Vector2(320f, 40f));
            _charName.fontStyle = FontStyles.Bold;

            var prev = CreateButton(cardRt, "CharPrev", "<", new Color(0.20f, 0.45f, 0.85f, 1f), out _);
            Anchor(prev.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), Center,
                new Vector2(-115f, 60f), new Vector2(80f, 60f));
            prev.onClick.AddListener(() => StepCharacter(-1));

            var next = CreateButton(cardRt, "CharNext", ">", new Color(0.20f, 0.45f, 0.85f, 1f), out _);
            Anchor(next.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), Center,
                new Vector2(115f, 60f), new Vector2(80f, 60f));
            next.onClick.AddListener(() => StepCharacter(1));

            EnsurePreviewWorld();
        }

        /// <summary>
        /// Spins up a tiny self-contained render world (camera + light + model) far from everything
        /// else and points a RenderTexture at it. Degrades to a hidden card if the character assets
        /// have not been built yet (Tools/CollarCali/Build Character Visuals).
        /// </summary>
        void EnsurePreviewWorld()
        {
            if (_previewReady)
                return;

            var lib = CharacterSkinLibrary.Load();
            var modelPrefab = Resources.Load<GameObject>("CharacterPreview");
            if (lib == null || lib.Count == 0 || modelPrefab == null)
            {
                // Nothing to show yet - hide the selector rather than render an empty box.
                if (_previewImage != null)
                    _previewImage.transform.parent.gameObject.SetActive(false);
                return;
            }

            _previewIndex = CharacterSelection.SelectedIndex;

            _previewRt = new RenderTexture(512, 780, 16, RenderTextureFormat.ARGB32)
            {
                name = "LobbyCharacterPreview",
                antiAliasing = 2,
            };
            _previewRt.Create();
            if (_previewImage != null)
                _previewImage.texture = _previewRt;

            var camGo = new GameObject("CharacterPreviewCamera");
            camGo.transform.position = PreviewWorldOrigin + new Vector3(0f, 1f, 3.2f);
            camGo.transform.rotation = Quaternion.Euler(3f, 180f, 0f);
            _previewCamera = camGo.AddComponent<Camera>();
            _previewCamera.targetTexture = _previewRt;
            _previewCamera.clearFlags = CameraClearFlags.SolidColor;
            _previewCamera.backgroundColor = new Color(0.06f, 0.07f, 0.10f, 1f);
            _previewCamera.fieldOfView = 32f;
            _previewCamera.nearClipPlane = 0.05f;
            // Small far plane so nothing beyond this pocket of the scene can leak into the shot.
            _previewCamera.farClipPlane = 12f;

            var lightGo = new GameObject("CharacterPreviewLight");
            lightGo.transform.position = PreviewWorldOrigin + new Vector3(1.5f, 3f, 2.5f);
            lightGo.transform.rotation = Quaternion.Euler(40f, 200f, 0f);
            _previewLight = lightGo.AddComponent<Light>();
            _previewLight.type = LightType.Directional;
            _previewLight.intensity = 1.15f;
            _previewLight.cullingMask = ~0;

            _previewModel = Instantiate(modelPrefab);
            _previewModel.name = "CharacterPreviewModel";
            _previewModel.transform.position = PreviewWorldOrigin;
            _previewModel.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            _previewReady = true;
            SetPreviewIndex(_previewIndex);
        }

        void StepCharacter(int direction)
        {
            var lib = CharacterSkinLibrary.Load();
            if (lib == null || lib.Count == 0)
                return;

            SetPreviewIndex(lib.ClampIndex(_previewIndex + direction));
        }

        void SetPreviewIndex(int index)
        {
            var lib = CharacterSkinLibrary.Load();
            if (lib == null || lib.Count == 0)
                return;

            _previewIndex = lib.ClampIndex(index);

            if (_previewModel != null)
                CharacterSelection.Apply(_previewModel, _previewIndex);

            if (_charName != null)
                _charName.text = lib.NameOf(_previewIndex);

            // Commit the pick: PlayerPrefs (carried into Game) + networked record (shown to others).
            var local = LobbyPlayer.Local();
            if (local != null)
                local.SetCharacter(_previewIndex);
            else
                CharacterSelection.SelectedIndex = _previewIndex;
        }

        void UpdatePreviewSpin()
        {
            if (_previewModel != null)
                _previewModel.transform.Rotate(0f, 18f * Time.unscaledDeltaTime, 0f, Space.World);
        }

        void ReleasePreview()
        {
            if (_previewModel != null)
                Destroy(_previewModel);
            if (_previewCamera != null)
                Destroy(_previewCamera.gameObject);
            if (_previewLight != null)
                Destroy(_previewLight.gameObject);
            if (_previewRt != null)
            {
                _previewRt.Release();
                Destroy(_previewRt);
            }

            _previewReady = false;
        }

        void OnDestroy()
        {
            ReleasePreview();
        }

        static RawImage CreateRawImage(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.AddComponent<RawImage>();
        }

        Row CreateRow(int index)
        {
            var go = new GameObject("Row" + index, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(_list, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -index * 62f);
            rt.sizeDelta = new Vector2(0f, 54f);

            var bg = go.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.05f);

            var swatch = CreateImage(rt, "Swatch", Color.white);
            Anchor(swatch.rectTransform, new Vector2(0f, 0.5f), Center,
                new Vector2(34f, 0f), new Vector2(20f, 20f));

            var name = CreateText(rt, "Name", "", 26f, TextAlignmentOptions.MidlineLeft);
            Anchor(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(64f, 0f), new Vector2(420f, 40f));

            var status = CreateText(rt, "Status", "", 22f, TextAlignmentOptions.MidlineRight);
            Anchor(status.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-30f, 0f), new Vector2(220f, 40f));

            return new Row { Root = rt, Swatch = swatch, Name = name, Status = status };
        }

        static Image CreateImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        static TextMeshProUGUI CreateText(Transform parent, string name, string text, float size,
            TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.alignment = alignment;
            label.raycastTarget = false;
            label.color = Color.white;
            return label;
        }

        static Button CreateButton(Transform parent, string name, string text, Color color,
            out TextMeshProUGUI label)
        {
            var image = CreateImage(parent, name, color);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            label = CreateText(image.rectTransform, "Label", text, 26f, TextAlignmentOptions.Center);
            Stretch(label.rectTransform);
            label.fontStyle = FontStyles.Bold;

            return button;
        }

        static readonly Vector2 Center = new Vector2(0.5f, 0.5f);

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // Pivot is a parameter rather than a fixed centre because anchoredPosition is measured
        // from the pivot: setting it afterwards silently shifts the element by half its size.
        static void Anchor(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 position,
            Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
        }

        #endregion
    }
}
#endif
