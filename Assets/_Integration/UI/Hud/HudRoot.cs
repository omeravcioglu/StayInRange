#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using cowsins;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.UI
{
    /// <summary>
    /// The one in-game HUD for the local player, whichever controller is live.
    ///
    /// Each player has two controllers - Cowsins in first person, Malbers in third - and each came
    /// with its own HUD that only worked in its own mode. This replaces both: it reads the game's
    /// state (the networked player data, the collar, Cowsins' weapon and interaction data, Malbers'
    /// stamina) and draws the redesigned widgets, and it switches the vendor HUDs off without
    /// editing them, so a vendor update or a player-prefab rebuild cannot bring them back.
    ///
    /// Added to the local player's bridge by FpsNetworkBridge, and gone with it.
    /// </summary>
    [DisallowMultipleComponent]
    public class HudRoot : MonoBehaviour
    {
        const float RebindSeconds = 0.5f;
        const float NameRefreshSeconds = 1f;
        const float FailedSeconds = 2.5f;
        const int NameLength = 12;

        FpsNetworkBridge _local;
        readonly CowsinsHudSource _cowsins = new CowsinsHudSource();

        Canvas _fxCanvas;
        Canvas _hudCanvas;
        Canvas _promptCanvas;
        Canvas[] _canvases;
        DamageFxView _fx;
        TeamPanelView _team;
        WeaponHudView _weapon;
        MovementView _movement;
        CollarWarningView _collar;
        CompassView _compass;
        readonly List<CompassMarker> _markers = new List<CompassMarker>();
        Camera _camera;
        NoticesView _notices;
        CrosshairView _crosshair;
        InteractionPromptView _prompt;
        MomentDirector _moments;
        PauseController _pause;

        readonly List<FpsNetworkBridge> _players = new List<FpsNetworkBridge>();
        readonly List<TeamRowData> _rows = new List<TeamRowData>();
        readonly Dictionary<FpsNetworkBridge, string> _names = new Dictionary<FpsNetworkBridge, string>();
        readonly HashSet<FpsNetworkBridge> _knownDown = new HashSet<FpsNetworkBridge>();
        bool _downsSeeded;

        float _nextRebind;
        float _nextNames;

        SpectatorController _spectator;
        MalbersAnimations.Stat _stamina;
        GameObject _staminaOwner;
        bool _malbersHudHidden;

        float _lastHealth = -1f;
        bool _wasDead;

        int _seenFailureSeq = int.MinValue;
        float _failedUntil;
        int _seenSaveSeq = int.MinValue;
        Color[] _padColours = new Color[0];
        bool[] _padArrived = new bool[0];

        void Awake()
        {
            _local = GetComponent<FpsNetworkBridge>();
            Build();
            // YOU DIED, spectating, grabbed, the wipe: the full-screen moments, one at a time.
            _moments = MomentDirector.Create(_local);
            // Escape: the pause menu over the running game.
            _pause = PauseController.Create(_local);
            // Look and key settings onto whichever controller is live.
            if (GetComponent<LocalPlayerSettings>() == null)
                gameObject.AddComponent<LocalPlayerSettings>();
            CombatFeedback.Hit += OnHit;
            CombatFeedback.Kill += OnKill;
        }

        void OnDestroy()
        {
            CombatFeedback.Hit -= OnHit;
            CombatFeedback.Kill -= OnKill;
            _cowsins.Unbind();

            foreach (var canvas in _canvases)
            {
                if (canvas != null)
                    Destroy(canvas.gameObject);
            }

            if (_moments != null)
                Destroy(_moments.gameObject);
            if (_pause != null)
                Destroy(_pause.gameObject);
        }

        void Build()
        {
            // Three canvases so the layers sort against the rest of the UI: effects under the HUD,
            // prompts and the crosshair over it.
            _fxCanvas = UiKit.CreateCanvas("HUD Fx", UiLayers.HudFx);
            _hudCanvas = UiKit.CreateCanvas("HUD", UiLayers.Hud);
            _promptCanvas = UiKit.CreateCanvas("HUD Prompts", UiLayers.Prompts);
            _canvases = new[] { _fxCanvas, _hudCanvas, _promptCanvas };

            _fx = DamageFxView.Create(_fxCanvas.transform);
            _team = TeamPanelView.Create(_hudCanvas.transform);
            _weapon = WeaponHudView.Create(_hudCanvas.transform);
            _movement = MovementView.Create(_hudCanvas.transform);
            _collar = CollarWarningView.Create(_hudCanvas.transform);
            _compass = CompassView.Create(_hudCanvas.transform);
            _notices = NoticesView.Create(_hudCanvas.transform);
            _crosshair = CrosshairView.Create(_promptCanvas.transform);
            _prompt = InteractionPromptView.Create(_promptCanvas.transform);
        }

        void LateUpdate()
        {
            bool live = _local != null && _local.Object != null && _local.Object.IsValid && _local.IsLocalOwner;
            // Under the pause menu only the team panel, the collar and the notices stay, as on the
            // board: the crosshair, prompts, weapon and dashes would be noise behind a menu.
            bool paused = live && _pause != null && _pause.IsOpen;
            SetCanvasesEnabled(live, paused);
            if (_moments != null)
                _moments.Covered = paused;
            if (!live)
                return;

            CombatFeedback.Tick();

            float now = Time.unscaledTime;
            if (now >= _nextRebind)
            {
                _nextRebind = now + RebindSeconds;
                Rebind();
            }

            var dual = _local.DualPlayer;
            bool dead = _local.IsDead;
            bool thirdPerson = dual != null && dual.IsThirdPerson;
            bool suspended = dual != null && dual.IsSuspended;

            _cowsins.ReadHealth(out float health, out float maxHealth, out float shield01);
            if (_cowsins.Dependencies == null)
            {
                health = _local.SyncedHealth;
                maxHealth = 100f;
            }
            UpdateDamageFx(health, maxHealth, dead);

            RefreshPlayers(now);
            FillTeam(health, maxHealth, shield01);
            _team.Set(_rows);
            WatchTeammatesGoingDown();

            bool armed = !dead && !thirdPerson && !suspended && !paused;
            _weapon.Set(armed ? _cowsins.ReadWeapon() : default);
            _movement.Set(dead || suspended || paused ? default : _cowsins.ReadMovement(thirdPerson, ReadStamina(thirdPerson)));
            _crosshair.Set(armed, _cowsins.EnemySpotted);

            // The carry prompts win: while one is up, E belongs to the telekinesis.
            bool carryPrompt = BodyCarryHud.Active != null && BodyCarryHud.Active.IsShowingPrompt;
            _prompt.Set(!dead && !thirdPerson && !carryPrompt ? _cowsins.ReadPrompt() : default);

            _collar.Set(ReadCollar(dead, now));
            UpdateCompass(dead);
            _notices.SetCheckpoint(ReadCheckpoint());
        }

        void SetCanvasesEnabled(bool live, bool paused)
        {
            SetEnabled(_fxCanvas, live);
            SetEnabled(_hudCanvas, live);
            SetEnabled(_promptCanvas, live && !paused);
        }

        static void SetEnabled(Canvas canvas, bool enabled)
        {
            if (canvas != null && canvas.enabled != enabled)
                canvas.enabled = enabled;
        }

        #region Compass

        /// <summary>
        /// Where everyone is, relative to where the camera faces: teammates (ringed red past the
        /// collar's danger distance), the next save pad, and the nearest revive station while
        /// anyone is down. Hidden while dead - the spectator camera faces somewhere else.
        /// </summary>
        void UpdateCompass(bool dead)
        {
            var cam = ResolveCamera();
            if (dead || cam == null)
            {
                _compass.SetVisible(false);
                return;
            }

            _compass.SetVisible(true);
            var eye = cam.transform.position;
            var self = _local.transform.position;
            _markers.Clear();

            bool anyoneDown = false;
            foreach (var player in _players)
            {
                if (player.IsDead)
                    anyoneDown = true;
                if (player == _local)
                    continue;

                var position = player.transform.position;
                _markers.Add(new CompassMarker
                {
                    Bearing = BearingTo(eye, position),
                    Kind = CompassMark.Teammate,
                    Colour = PlayerColorPalette.Get(player.ColorIndex),
                    TooFar = FlatDistance(self, position) >= UiTheme.TetherDangerMetres,
                });
            }

            var team = TeamDistanceManager.Instance;
            var pad = team != null ? NetworkCheckpoint.Next(team.SavedCheckpointId) : null;
            if (pad != null)
                _markers.Add(new CompassMarker { Bearing = BearingTo(eye, pad.transform.position), Kind = CompassMark.Checkpoint });

            if (anyoneDown)
            {
                ReviveStation nearest = null;
                float best = float.MaxValue;
                foreach (var station in ReviveStation.Active)
                {
                    if (station == null)
                        continue;
                    float distance = (station.transform.position - self).sqrMagnitude;
                    if (distance < best)
                    {
                        best = distance;
                        nearest = station;
                    }
                }

                if (nearest != null)
                    _markers.Add(new CompassMarker { Bearing = BearingTo(eye, nearest.transform.position), Kind = CompassMark.Station });
            }

            _compass.Set(cam.transform.eulerAngles.y, _markers);
        }

        static float BearingTo(Vector3 from, Vector3 to)
        {
            var d = to - from;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>The camera drawing the screen: first person, the shoulder camera, or whatever is live.</summary>
        Camera ResolveCamera()
        {
            if (_camera != null && _camera.isActiveAndEnabled && _camera.targetTexture == null)
                return _camera;

            _camera = Camera.main;
            if (_camera != null && _camera.isActiveAndEnabled)
                return _camera;

            _camera = null;
            foreach (var candidate in Camera.allCameras)
            {
                if (candidate != null && candidate.isActiveAndEnabled && candidate.targetTexture == null)
                {
                    _camera = candidate;
                    break;
                }
            }

            return _camera;
        }

        #endregion

        #region Binding and vendor HUDs

        /// <summary>
        /// Finds the controllers this HUD reads - they are built and swapped around after it exists -
        /// and keeps the vendor HUDs switched off, since the bridge re-enables Cowsins' objects on
        /// every switch back to first person.
        /// </summary>
        void Rebind()
        {
            var deps = FindDependencies();
            _cowsins.Bind(deps);
            if (deps != null)
                SuppressCowsinsHud(deps);

            HideMalbersDuplicates();

            if (_spectator == null && _local.IsDead)
                _spectator = FindFirstObjectByType<SpectatorController>();
        }

        PlayerDependencies FindDependencies()
        {
            var body = _local.DualPlayer != null ? _local.DualPlayer.FpsBody : null;
            if (body == null)
                return null;

            var deps = body.GetComponentInParent<PlayerDependencies>(true);
            return deps != null ? deps : body.GetComponentInChildren<PlayerDependencies>(true);
        }

        /// <summary>
        /// Cowsins' HUD stays alive - its scripts drive interaction and inspection - but draws
        /// nothing. Its canvas is switched off rather than its object, because Cowsins' UI code keeps
        /// running either way and starts coroutines that fail on an inactive object.
        /// </summary>
        static void SuppressCowsinsHud(PlayerDependencies deps)
        {
            var root = deps.transform.root;
            foreach (var ui in root.GetComponentsInChildren<UIController>(true))
            {
                var canvas = ui.GetComponent<Canvas>();
                if (canvas != null && canvas.enabled)
                    canvas.enabled = false;
            }

            // Its crosshair is drawn with IMGUI, outside any canvas.
            if (deps.Crosshair != null)
                deps.Crosshair.SetVisibility(false);

            // Its death screen's restart key reloads the scene locally, outside the session.
            foreach (var restart in root.GetComponentsInChildren<DeathRestart>(true))
            {
                if (restart.enabled)
                    restart.enabled = false;
            }
        }

        /// <summary>
        /// The Malbers canvas in the level duplicates two things this HUD now draws - stamina and the
        /// hurt flash. Its interact and ledge prompts stay: they are the third-person climbing hints.
        /// </summary>
        void HideMalbersDuplicates()
        {
            if (_malbersHudHidden)
                return;

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
                return;

            int hidden = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                hidden += HideNamed(root.transform, "Slider Stamina UI v2");
                hidden += HideNamed(root.transform, "Hurt UI");
            }

            _malbersHudHidden = hidden > 0;
        }

        static int HideNamed(Transform root, string name)
        {
            int count = 0;
            if (root.name == name)
            {
                if (root.gameObject.activeSelf)
                    root.gameObject.SetActive(false);
                return 1;
            }

            for (int i = 0; i < root.childCount; i++)
                count += HideNamed(root.GetChild(i), name);
            return count;
        }

        /// <summary>Steve's stamina, 0-1, while third person is live; negative otherwise.</summary>
        float ReadStamina(bool thirdPerson)
        {
            if (!thirdPerson)
                return -1f;

            var steve = _local.DualPlayer != null ? _local.DualPlayer.SteveRoot : null;
            if (steve == null)
                return -1f;

            // Steve is built lazily on the first switch, so the stat is looked up again if he changed.
            if (_stamina == null || _staminaOwner != steve)
            {
                _staminaOwner = steve;
                _stamina = null;
                var stats = steve.GetComponentInChildren<MalbersAnimations.Stats>(true);
                if (stats != null)
                {
                    // The list, not Stat_Get: that one repoints Malbers' pinned stat as a side effect.
                    foreach (var stat in stats.stats)
                    {
                        if (stat != null && stat.Name == "Stamina")
                        {
                            _stamina = stat;
                            break;
                        }
                    }
                }
            }

            return _stamina != null && _stamina.MaxValue > 0f ? Mathf.Clamp01(_stamina.NormalizedValue) : -1f;
        }

        #endregion

        #region Team

        void RefreshPlayers(float now)
        {
            _players.Clear();
            foreach (var bridge in FpsNetworkBridge.All)
            {
                if (bridge != null && bridge.Object != null && bridge.Object.IsValid)
                    _players.Add(bridge);
            }

            // By owner, so every machine lists the team in the same order.
            _players.Sort((a, b) => a.Owner.PlayerId.CompareTo(b.Owner.PlayerId));

            if (now < _nextNames && _names.Count == _players.Count)
                return;

            _nextNames = now + NameRefreshSeconds;
            _names.Clear();
            foreach (var player in _players)
                _names[player] = Shorten(player.DisplayName);
        }

        string NameOf(FpsNetworkBridge player)
        {
            if (player == null)
                return string.Empty;
            return _names.TryGetValue(player, out var name) ? name : Shorten(player.DisplayName);
        }

        static string Shorten(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "?";
            return name.Length <= NameLength ? name : name.Substring(0, NameLength - 1) + "…";
        }

        FpsNetworkBridge FindByOwner(Fusion.PlayerRef owner)
        {
            foreach (var player in _players)
            {
                if (player.Owner == owner)
                    return player;
            }

            return null;
        }

        void FillTeam(float health, float maxHealth, float shield01)
        {
            _rows.Clear();
            var origin = _local.GetNetworkAnchorPosition();
            var watched = _spectator != null && _spectator.IsActive ? _spectator.Target : null;

            foreach (var player in _players)
            {
                bool self = player == _local;
                var row = new TeamRowData
                {
                    Name = NameOf(player),
                    Colour = PlayerColorPalette.Get(player.ColorIndex),
                    IsSelf = self,
                    Status = !player.IsDead ? RowStatus.Alive : player.IsCarried ? RowStatus.Carried : RowStatus.Down,
                    Health = self ? health : player.SyncedHealth,
                    // Max health is not networked; every player is the same prefab.
                    MaxHealth = maxHealth,
                    Shield01 = self ? shield01 : 0f,
                    Metres = self ? 0f : Vector3.Distance(origin, player.GetNetworkAnchorPosition()),
                    Watched = watched == player,
                    Voice = VoiceState.Hidden,
                };

                if (player.IsCarried)
                {
                    var carrier = FindByOwner(player.CarriedBy);
                    row.CarriedBy = carrier == _local ? "YOU" : NameOf(carrier);
                }

                // Carrying: the body that names this player as its carrier.
                foreach (var other in _players)
                {
                    if (other != player && other.IsDead && other.CarriedBy == player.Owner)
                    {
                        row.Carrying = NameOf(other);
                        break;
                    }
                }

                _rows.Add(row);
            }
        }

        /// <summary>"DOT is down" in the killfeed when a teammate dies.</summary>
        void WatchTeammatesGoingDown()
        {
            foreach (var player in _players)
            {
                if (player == _local)
                    continue;

                if (player.IsDead)
                {
                    if (_knownDown.Add(player) && _downsSeeded)
                        _notices.AddLine(NameOf(player) + " is down", ColorRole.Danger);
                }
                else
                {
                    _knownDown.Remove(player);
                }
            }

            // Whoever was already down when this HUD appeared is not news.
            _downsSeeded = true;
        }

        #endregion

        #region Collar and checkpoints

        CollarData ReadCollar(bool dead, float now)
        {
            var team = TeamDistanceManager.Instance;
            if (team != null)
            {
                if (_seenFailureSeq == int.MinValue)
                {
                    _seenFailureSeq = team.FailureSeq;
                }
                else if (team.FailureSeq != _seenFailureSeq)
                {
                    _seenFailureSeq = team.FailureSeq;
                    _failedUntil = now + FailedSeconds;
                }
            }

            if (now < _failedUntil)
                return new CollarData { Stage = CollarStage.Failed };
            if (dead)
                return default;

            // Whoever is furthest from you sets the level.
            FpsNetworkBridge furthest = null;
            float metres = 0f;
            var origin = _local.GetNetworkAnchorPosition();
            foreach (var player in _players)
            {
                if (player == _local)
                    continue;
                float distance = Vector3.Distance(origin, player.GetNetworkAnchorPosition());
                if (distance > metres)
                {
                    metres = distance;
                    furthest = player;
                }
            }

            var data = new CollarData { Who = NameOf(furthest), Metres = metres };
            if (team != null && team.BreachSeconds > 0.05f)
            {
                data.Stage = CollarStage.Breach;
                data.SecondsLeft = Mathf.Max(0f, team.SeparationGraceSeconds - team.BreachSeconds);
                data.Left01 = 1f - team.BreachProgress01;
            }
            else if (metres >= UiTheme.TetherDangerMetres)
            {
                data.Stage = CollarStage.Danger;
            }
            else if (metres >= UiTheme.TetherWarningMetres)
            {
                data.Stage = CollarStage.Warning;
            }

            return data;
        }

        CheckpointData ReadCheckpoint()
        {
            var team = TeamDistanceManager.Instance;
            if (team == null)
                return default;

            var data = new CheckpointData();
            if (_seenSaveSeq == int.MinValue)
            {
                _seenSaveSeq = team.SaveSeq;
            }
            else if (team.SaveSeq != _seenSaveSeq)
            {
                _seenSaveSeq = team.SaveSeq;
                data.JustSaved = true;
                data.Id = team.SavedCheckpointId;
            }

            int pending = team.PendingCheckpointId;
            if (pending <= 0 || pending == team.SavedCheckpointId)
                return data;

            data.Pending = true;
            data.Id = pending;
            data.Here = team.CountPendingVisits();
            data.Needed = team.RequiredVisitCount();

            int alive = 0;
            foreach (var player in _players)
            {
                if (!player.IsDead)
                    alive++;
            }

            if (_padColours.Length != alive)
            {
                _padColours = new Color[alive];
                _padArrived = new bool[alive];
            }

            int i = 0;
            string waiting = null;
            foreach (var player in _players)
            {
                if (player.IsDead)
                    continue;

                bool here = team.HasVisited(player.Owner.PlayerId);
                _padColours[i] = PlayerColorPalette.Get(player.ColorIndex);
                _padArrived[i] = here;
                if (!here)
                    waiting = waiting == null ? NameOf(player) : waiting + " and " + NameOf(player);
                i++;
            }

            data.Colours = _padColours;
            data.Arrived = _padArrived;
            data.WaitingFor = waiting;
            return data;
        }

        #endregion

        #region Feedback

        void UpdateDamageFx(float health, float maxHealth, bool dead)
        {
            if (dead)
            {
                if (!_wasDead)
                    _fx.Clear();
                _wasDead = true;
                _lastHealth = -1f;
                return;
            }

            // Coming back from the dead refills health; that is a revive, not a heal.
            bool revived = _wasDead;
            _wasDead = false;

            if (_lastHealth >= 0f && !revived)
            {
                float change = health - _lastHealth;
                if (change < -0.5f)
                    _fx.Hurt(-change / maxHealth);
                else if (change > 0.5f)
                    _fx.Heal();
            }

            _lastHealth = health;
            _fx.SetCritical(health / maxHealth < 0.3f);
        }

        void OnHit(CombatFeedback.HitInfo hit)
        {
            _crosshair.Flash(hit.Headshot, kill: false);
            // On the shooter's own machine, straight away - not on whichever machine owns the enemy.
            Scene2DamagePopup.Show(hit.Position + Vector3.up * 1.8f, Mathf.Max(1, Mathf.RoundToInt(hit.Damage)), hit.Headshot);
        }

        void OnKill(CombatFeedback.HitInfo hit)
        {
            _crosshair.Flash(hit.Headshot, kill: true);
            _notices.AddKill("You", CombatFeedback.DisplayName(hit.Target));
        }

        #endregion
    }
}
#endif
