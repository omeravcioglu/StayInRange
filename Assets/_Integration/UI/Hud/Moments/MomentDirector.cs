#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using UnityEngine;

namespace CollarCali.UI
{
    /// <summary>
    /// Decides which full-screen moment the local player sees, one at a time, most important first:
    /// the team wipe, being grabbed, dead (YOU DIED, then spectating), revived, a teammate down.
    ///
    /// Most of it is read from the player's own replicated state every frame, so it cannot miss a
    /// change or disagree with the game. The two moments the game announces instead - the wipe and
    /// the grab - arrive through <see cref="ShowTeamWipe"/> and <see cref="SetGrabbed"/>, which the
    /// old TeamWipePanel and CreepGrabbedPanel now forward to.
    ///
    /// A death caused by the collar snapping is left to the HUD's YANKED BACK!: the player is
    /// respawned a moment later, and a YOU DIED card would be a lie.
    /// </summary>
    public class MomentDirector : MonoBehaviour
    {
        const float RevivedSeconds = 2.6f;
        const float TeammateDownSeconds = 4f;
        const float CollarDeathWindow = 1.5f;
        const float NamesRefreshSeconds = 0.5f;

        public static MomentDirector Instance { get; private set; }

        FpsNetworkBridge _local;
        Canvas _backCanvas;
        Canvas _frontCanvas;
        MomentsView _view;
        SpectatorController _spectator;

        bool _wasDead;
        float _diedAt = -100f;
        bool _collarDeath;
        bool _wasCarried;
        string _lastCarrier;
        float _revivedAt = -100f;
        string _revivedBy;

        float _wipeUntil;
        string _wipeLine;
        bool _grabbed;

        float _downUntil;
        string _downName;
        readonly HashSet<FpsNetworkBridge> _knownDown = new HashSet<FpsNetworkBridge>();
        bool _downSeeded;

        int _seenFailureSeq = int.MinValue;
        float _failureAt = -100f;

        readonly Dictionary<FpsNetworkBridge, string> _names = new Dictionary<FpsNetworkBridge, string>();
        float _namesAt;

        public static MomentDirector Create(FpsNetworkBridge local)
        {
            var go = new GameObject("MomentDirector");
            var director = go.AddComponent<MomentDirector>();
            director._local = local;
            return director;
        }

        System.Func<bool> _offlineDead;
        bool _offlineWasDead;
        float _offlineDiedAt;

        /// <summary>
        /// Game.unity played on its own: no team, no revive - only the YOU DIED card, over Cowsins'
        /// own restart key.
        /// </summary>
        public static MomentDirector CreateOffline(System.Func<bool> isDead)
        {
            var go = new GameObject("MomentDirector");
            var director = go.AddComponent<MomentDirector>();
            director._offlineDead = isDead;
            return director;
        }

        void Awake()
        {
            Instance = this;
            // Backdrops sit under the HUD so the team panel stays readable over them; the words
            // go over everything.
            _backCanvas = UiKit.CreateCanvas("Moments Back", UiLayers.HudFx + 10, parent: transform);
            _frontCanvas = UiKit.CreateCanvas("Moments", UiLayers.Moments, parent: transform);
            _view = MomentsView.Create(_backCanvas.transform, _frontCanvas.transform);
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>The whole team is down; shown for <paramref name="seconds"/> over everything.</summary>
        public void ShowTeamWipe(string line, float seconds)
        {
            _wipeLine = line;
            _wipeUntil = Time.unscaledTime + Mathf.Max(0.5f, seconds);
        }

        public void HideTeamWipe() => _wipeUntil = 0f;

        public void SetGrabbed(bool grabbed) => _grabbed = grabbed;

        /// <summary>
        /// A menu is over the game: the cards are not drawn, but their timers keep running, so what
        /// shows when the menu closes is what would be showing anyway.
        /// </summary>
        public bool Covered { get; set; }

        void LateUpdate()
        {
            bool drawn = !Covered;
            if (_backCanvas.enabled != drawn)
                _backCanvas.enabled = drawn;
            if (_frontCanvas.enabled != drawn)
                _frontCanvas.enabled = drawn;

            if (_offlineDead != null)
            {
                bool offlineDead = _offlineDead();
                if (offlineDead && !_offlineWasDead)
                    _offlineDiedAt = Time.unscaledTime;
                _offlineWasDead = offlineDead;
                if (offlineDead)
                    _view.ShowDeathOffline(Time.unscaledTime - _offlineDiedAt);
                else
                    _view.HideAll();
                return;
            }

            if (_local == null || _local.Object == null || !_local.Object.IsValid || !_local.IsLocalOwner)
            {
                _view.HideAll();
                return;
            }

            float now = Time.unscaledTime;
            RefreshNames(now);
            TrackCollarFailure(now);

            bool dead = _local.IsDead;
            if (dead && !_wasDead)
            {
                _diedAt = now;
                _collarDeath = now - _failureAt < CollarDeathWindow;
                _wasCarried = false;
                _lastCarrier = null;
            }
            else if (!dead && _wasDead)
            {
                // A revive at a station, not a respawn after a wipe or a collar snap.
                if (now - _failureAt > CollarDeathWindow && now > _wipeUntil)
                {
                    _revivedAt = now;
                    _revivedBy = _lastCarrier;
                }
            }
            _wasDead = dead;

            if (dead)
                TrackBeingCarried();
            WatchTeammates(now, dead);

            if (now < _wipeUntil)
            {
                _view.ShowTeamWipe(_wipeLine, Mathf.CeilToInt(_wipeUntil - now));
                return;
            }

            if (_grabbed && !dead)
            {
                _view.ShowGrabbed();
                return;
            }

            if (dead)
            {
                if (_collarDeath)
                {
                    _view.HideAll();
                    return;
                }

                float intro = PlayerTuning.Active.death.youDiedCentreSeconds;
                float age = now - _diedAt;
                if (age < intro)
                    _view.ShowDeath(age, Mathf.CeilToInt(intro - age));
                else
                    _view.ShowSpectating(BuildSpectatorData());
                return;
            }

            if (now - _revivedAt < RevivedSeconds)
            {
                _view.ShowRevived(_revivedBy, now - _revivedAt, RevivedSeconds);
                return;
            }

            if (now < _downUntil)
            {
                _view.ShowTeammateDown(_downName);
                return;
            }

            _view.HideAll();
        }

        void TrackCollarFailure(float now)
        {
            var team = TeamDistanceManager.Instance;
            if (team == null)
                return;

            if (_seenFailureSeq == int.MinValue)
            {
                _seenFailureSeq = team.FailureSeq;
            }
            else if (team.FailureSeq != _seenFailureSeq)
            {
                _seenFailureSeq = team.FailureSeq;
                _failureAt = now;
            }
        }

        /// <summary>"SKIP lifted you!" the moment a teammate takes hold of your body.</summary>
        void TrackBeingCarried()
        {
            bool carried = _local.IsCarried;
            if (carried)
            {
                var carrier = FindByOwner(_local.CarriedBy);
                _lastCarrier = NameOf(carrier);
                if (!_wasCarried && carrier != null)
                    _view.Toast(_lastCarrier + " lifted you!", UiTheme.Active.warning);
            }

            _wasCarried = carried;
        }

        /// <summary>A living player is told when a teammate goes down, and what to do about it.</summary>
        void WatchTeammates(float now, bool dead)
        {
            foreach (var player in FpsNetworkBridge.All)
            {
                if (player == null || player == _local || player.Object == null || !player.Object.IsValid)
                    continue;

                if (player.IsDead)
                {
                    if (_knownDown.Add(player) && _downSeeded && !dead && now > _wipeUntil)
                    {
                        _downName = NameOf(player);
                        _downUntil = now + TeammateDownSeconds;
                    }
                }
                else
                {
                    _knownDown.Remove(player);
                }
            }

            _downSeeded = true;
        }

        SpectatorData BuildSpectatorData()
        {
            if (_spectator == null)
                _spectator = FindFirstObjectByType<SpectatorController>();

            var data = new SpectatorData { SelfColour = PlayerColorPalette.Get(_local.ColorIndex) };

            var target = _spectator != null && _spectator.IsActive ? _spectator.Target : null;
            if (target == null)
            {
                data.NobodyStanding = true;
            }
            else
            {
                data.WatchName = NameOf(target);
                data.WatchColour = PlayerColorPalette.Get(target.ColorIndex);
                data.WatchIndex = _spectator.TargetIndex;
                data.WatchCount = _spectator.TargetCount;
            }

            // Where the body is on its way back: Down -> Lifted -> At a revive station -> Revived.
            var body = _local.DownState != null ? _local.DownState.BodyPosition : _local.GetNetworkAnchorPosition();

            FpsNetworkBridge closest = null;
            float closestMetres = float.MaxValue;
            foreach (var player in FpsNetworkBridge.All)
            {
                if (player == null || player == _local || player.IsDead || player.Object == null || !player.Object.IsValid)
                    continue;
                float metres = Vector3.Distance(body, player.GetNetworkAnchorPosition());
                if (metres < closestMetres)
                {
                    closestMetres = metres;
                    closest = player;
                }
            }

            data.DownText = closest != null
                ? "Closest teammate: " + NameOf(closest) + ", " + Mathf.RoundToInt(closestMetres) + " m"
                : "Nobody is left standing";

            bool carried = _local.IsCarried;
            data.LiftedText = carried ? NameOf(FindByOwner(_local.CarriedBy)) + " is holding you up" : "A teammate has to lift you";

            ReviveStation nearest = null;
            float stationMetres = float.MaxValue;
            foreach (var station in ReviveStation.Active)
            {
                if (station == null)
                    continue;
                float metres = Vector3.Distance(body, station.transform.position);
                if (metres < stationMetres)
                {
                    stationMetres = metres;
                    nearest = station;
                }
            }

            bool atStation = nearest != null && nearest.HasBodyInRange && stationMetres <= nearest.BodyRange;
            data.StationText = nearest == null ? "Look for the green light"
                : atStation ? "Almost there!"
                : Mathf.RoundToInt(stationMetres) + " m to go";

            // The step in progress: lifting first, then the trip, then - once the body is in a
            // station's range - the revive itself.
            data.Step = atStation ? BodyStep.Revived : carried ? BodyStep.AtStation : BodyStep.Lifted;
            return data;
        }

        #region Names

        void RefreshNames(float now)
        {
            if (now < _namesAt)
                return;
            _namesAt = now + NamesRefreshSeconds;
            _names.Clear();
            foreach (var player in FpsNetworkBridge.All)
            {
                if (player != null && player.Object != null && player.Object.IsValid)
                    _names[player] = player.DisplayName;
            }
        }

        string NameOf(FpsNetworkBridge player)
        {
            if (player == null)
                return "Someone";
            return _names.TryGetValue(player, out var name) ? name : player.DisplayName;
        }

        static FpsNetworkBridge FindByOwner(Fusion.PlayerRef owner)
        {
            foreach (var player in FpsNetworkBridge.All)
            {
                if (player != null && player.Object != null && player.Object.IsValid && player.Owner == owner)
                    return player;
            }

            return null;
        }

        #endregion
    }
}
#endif
