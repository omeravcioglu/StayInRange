#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using CollarCali.UI;
using Fusion;
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

        readonly List<LobbyPlayer> _sorted = new List<LobbyPlayer>();

        LobbyView _view;
        readonly List<LobbyRowData> _rowData = new List<LobbyRowData>();
        readonly string[] _takenBy = new string[LobbyView.Slots];

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
            var runner = NetworkCombatHooks.FindRunner();

            if (runner == null || !runner.IsRunning)
            {
                SetStatus("Connecting to the room…");
                _view.SetPrimary(string.Empty, visible: false, enabled: false, note: null);
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

            ResolveLocalColour();

            int readyCount = 0;
            _rowData.Clear();
            for (int i = 0; i < _takenBy.Length; i++)
                _takenBy[i] = null;

            for (int i = 0; i < _sorted.Count; i++)
            {
                var entry = _sorted[i];
                LobbySlot status;
                if (entry.IsHost)
                {
                    status = LobbySlot.Host;
                    readyCount++;
                }
                else if (entry.IsReady)
                {
                    status = LobbySlot.Ready;
                    readyCount++;
                }
                else
                {
                    status = LobbySlot.NotReady;
                }

                int colour = ColourOf(entry, i);
                _rowData.Add(new LobbyRowData
                {
                    Name = entry.DisplayName,
                    Colour = colour,
                    IsYou = entry.IsLocal,
                    Status = status,
                });

                if (!entry.IsLocal && colour >= 0 && colour < _takenBy.Length && _takenBy[colour] == null)
                    _takenBy[colour] = entry.DisplayName;
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

            int capacity = Mathf.Min(ResolveMaxPlayers(runner), LobbyView.Slots);
            _view.SetSubtitle(ResolveRoomName(runner) + " · " + connected + "/" + ResolveMaxPlayers(runner) + " players");
            _view.SetRoster(_rowData, capacity);

            if (starting)
                SetStatus("Starting the match…");
            else if (!isMaster)
                SetStatus("Waiting for the host to start the match…");
            else if (!enoughPlayers)
                SetStatus("Waiting for players to join…");
            else if (!rosterSynced && !rosterStale)
                SetStatus("Syncing players…");
            else if (rosterStale)
                SetStatus("A player is not responding (" + listed + "/" + connected +
                          " synced). You can start without them.");
            else if (!allReady)
                SetStatus("Waiting for everyone to ready up (" + readyCount + "/" + listed + ").");
            else
                SetStatus("Everyone is ready. Start when you like.");

            var local = LobbyPlayer.Local();

            if (!isMaster)
            {
                string label = local != null && local.IsReady ? "CANCEL READY" : "READY";
                _view.SetPrimary(label, visible: true, enabled: !starting && local != null, note: null);
            }
            else
            {
                bool canStart = !starting && allReady && enoughPlayers;
                string note = starting || canStart ? null
                    : !enoughPlayers ? "waiting for players"
                    : "not everyone is ready yet";
                _view.SetPrimary(starting ? "STARTING…" : "START GAME", visible: true, enabled: canStart, note: note);
            }

            int chosen = local != null && local.ColorIndex >= 0 ? local.ColorIndex : Mathf.Max(0, PlayerColorPalette.SavedChoice);
            _view.SetColour(chosen, _takenBy);
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

        /// <summary>One button, two jobs: the host starts the match, everyone else readies up.</summary>
        void OnPrimaryPressed()
        {
            var runner = NetworkCombatHooks.FindRunner();
            if (runner != null && runner.IsRunning && runner.IsSharedModeMasterClient)
                OnStartPressed();
            else
                OnReadyPressed();
        }

        #endregion

        #region Colours

        /// <summary>The colour a roster entry shows: its pick, or a stable stand-in until it has one.</summary>
        static int ColourOf(LobbyPlayer entry, int order)
        {
            if (entry.ColorIndex >= 0)
                return entry.ColorIndex % PlayerColorPalette.Count;
            return (entry.Owner.IsRealPlayer ? entry.Owner.PlayerId : order) % PlayerColorPalette.Count;
        }

        /// <summary>
        /// Gives the local player a colour nobody else holds: a first pick if they have none, and a
        /// move if two players picked the same one at once - the lower player id keeps it, so both
        /// machines agree on who moves without talking.
        /// </summary>
        void ResolveLocalColour()
        {
            var local = LobbyPlayer.Local();
            if (local == null || _startIssued || LobbyPlayer.AnyMatchStarting())
                return;

            int mine = local.ColorIndex;
            bool clash = false;
            foreach (var other in _sorted)
            {
                if (other == null || other == local || other.ColorIndex != mine)
                    continue;
                if (other.Owner.PlayerId < local.Owner.PlayerId)
                {
                    clash = true;
                    break;
                }
            }

            if (mine >= 0 && mine < PlayerColorPalette.Count && !clash)
                return;

            int free = FirstFreeColour(local, mine >= 0 ? mine : Mathf.Max(0, PlayerColorPalette.SavedChoice));
            if (free >= 0)
                local.SetColor(free);
        }

        /// <summary>The first colour from <paramref name="start"/> on (wrapping) that no other player holds.</summary>
        int FirstFreeColour(LobbyPlayer local, int start)
        {
            for (int step = 0; step < PlayerColorPalette.Count; step++)
            {
                int candidate = (start + step) % PlayerColorPalette.Count;
                if (!TakenByOther(local, candidate))
                    return candidate;
            }

            return -1;
        }

        bool TakenByOther(LobbyPlayer local, int colour)
        {
            foreach (var other in _sorted)
            {
                if (other != null && other != local && other.ColorIndex == colour)
                    return true;
            }

            return false;
        }

        void StepColour(int direction)
        {
            var local = LobbyPlayer.Local();
            if (local == null || _startIssued)
                return;

            int start = local.ColorIndex >= 0 ? local.ColorIndex : 0;
            for (int step = 1; step <= PlayerColorPalette.Count; step++)
            {
                int candidate = ((start + direction * step) % PlayerColorPalette.Count + PlayerColorPalette.Count) %
                                PlayerColorPalette.Count;
                if (!TakenByOther(local, candidate))
                {
                    local.SetColor(candidate);
                    return;
                }
            }
        }

        void PickColour(int colour)
        {
            var local = LobbyPlayer.Local();
            if (local == null || _startIssued || TakenByOther(local, colour))
                return;
            local.SetColor(colour);
        }

        #endregion

        #region Ready and start

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
            if (_view != null)
                _view.SetHint(text);
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
            var canvas = UiKit.CreateCanvas("LobbyCanvas", UiLayers.Screens, interactive: true, parent: transform);
            _view = LobbyView.Create(canvas.transform);
            _view.Primary += OnPrimaryPressed;
            _view.Leave += OnLeavePressed;
            _view.ColourStep += StepColour;
            _view.ColourPick += PickColour;

            // Hover only for the primary button: its handlers play something more specific than a
            // click, and claiming it here is what stops UiSfxAutoWire adding the generic one as well.
            UiSfxButton.HoverOnly(_view.PrimaryButton);

            _view.SetColour(Mathf.Max(0, PlayerColorPalette.SavedChoice), _takenBy);
            _view.SetPrimary(string.Empty, visible: false, enabled: false, note: null);
        }

        void LateUpdate()
        {
            // Keys and a gamepad need a selection to move from: the primary button, whenever
            // nothing else holds the focus and it can be pressed.
            var events = EventSystem.current;
            if (_view != null && events != null && events.currentSelectedGameObject == null)
                _view.FocusDefault();
        }

        #endregion
    }
}
#endif
