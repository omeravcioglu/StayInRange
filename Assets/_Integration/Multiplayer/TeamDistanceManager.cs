using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Master Client team link: unique colors, 30 m rule with grace, checkpoint / spawn reset.
    /// </summary>
    public class TeamDistanceManager : NetworkBehaviour
    {
        public const float WarningStart = 20f;
        public const float DangerStart = 25f;

        static readonly Vector3 DefaultFallbackSpawn = new Vector3(3.3f, 0f, -36.35f);

        [SerializeField] float maxPlayerDistance = 30f;
        [SerializeField] float separationGraceSeconds = 5f;
        [SerializeField] float failureCooldownSeconds = 4f;

        [Header("Team wipe")]
        [Tooltip("How long the YOU DIED card stays up before everyone is put back at the checkpoint.")]
        [SerializeField] float wipePanelSeconds = 3.5f;

        [Tooltip("Guards against a second wipe firing while the first one is still putting people back.")]
        [SerializeField] float wipeCooldownSeconds = 8f;

        [Networked] public int CheckpointId { get; set; }
        [Networked] public Vector3 CheckpointPosition { get; set; }
        [Networked] public int VisitCheckpointId { get; set; }
        [Networked] public int VisitMask { get; set; }
        [Networked] public int SaveSeq { get; set; }
        [Networked] public Vector3 FallbackSpawn { get; set; }
        [Networked] public int FailureSeq { get; set; }
        [Networked] public TickTimer FailureCooldown { get; set; }
        [Networked] public float BreachSeconds { get; set; }

        /// <summary>Bumped once per team wipe, so every client runs the sequence exactly once.</summary>
        [Networked] public int WipeSeq { get; set; }

        [Networked] public TickTimer WipeCooldown { get; set; }

        public static TeamDistanceManager Instance { get; private set; }

        public float MaxPlayerDistance => maxPlayerDistance;
        public float SeparationGraceSeconds => separationGraceSeconds;
        public float BreachProgress01 =>
            separationGraceSeconds <= 0.01f
                ? 0f
                : Mathf.Clamp01(BreachSeconds / separationGraceSeconds);

        ChangeDetector _changes;
        int _seenFailureSeq;
        int _seenSaveSeq;

        public override void Spawned()
        {
            Instance = this;
            _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);
            _seenFailureSeq = FailureSeq;
            // Seeded, not zeroed: a client joining a session that has already banked checkpoints
            // would otherwise hear the save chime on its first frame.
            _seenSaveSeq = SaveSeq;

            if (Runner != null && Runner.IsSharedModeMasterClient && FallbackSpawn.sqrMagnitude < 0.01f)
                FallbackSpawn = DefaultFallbackSpawn;
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this)
                Instance = null;
        }

        public override void FixedUpdateNetwork()
        {
            if (Runner == null || !Runner.IsSharedModeMasterClient)
                return;

            EnsureUniqueColors();
            CheckTeamDistance();
            CheckTeamWipe();
        }

        /// <summary>
        /// Fires when there is nobody left standing.
        ///
        /// A single death is not a failure in this game - it is a problem the other player solves by
        /// carrying the body to a station. This is what happens when that is no longer possible
        /// because everybody is down, and there is nobody left to do the carrying.
        /// </summary>
        void CheckTeamWipe()
        {
            if (!WipeCooldown.ExpiredOrNotRunning(Runner))
                return;

            var players = ListConnectedPlayers();
            if (players.Count == 0)
                return;

            foreach (var player in players)
            {
                if (player.IsDead)
                    continue;

                // Somebody is up, so the team is not wiped. Arming here is what stops a wipe from
                // firing twice off the same corpses, or firing at all before anyone has lived.
                _sawSomeoneAlive = true;
                return;
            }

            if (!_sawSomeoneAlive)
                return;

            _sawSomeoneAlive = false;
            WipeSeq++;
            WipeCooldown = TickTimer.CreateFromSeconds(Runner, wipeCooldownSeconds);
            RPC_TeamWipe(CheckpointId, CheckpointPosition);
        }

        bool _sawSomeoneAlive;

        /// <summary>
        /// Everybody shows the card, waits, and then puts THEIR OWN player back.
        ///
        /// Each client revives its own character rather than the master reviving everyone, because
        /// respawning runs through Cowsins on the machine that owns that character - the same reason
        /// the separation failure is structured this way.
        /// </summary>
        [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = true)]
        void RPC_TeamWipe(int checkpointId, Vector3 checkpointPosition)
        {
            StartCoroutine(TeamWipeRoutine(checkpointId, checkpointPosition));
        }

        IEnumerator TeamWipeRoutine(int checkpointId, Vector3 checkpointPosition)
        {
            TeamWipePanel.Show("The whole team is down. Restarting from the last checkpoint.",
                wipePanelSeconds);

            // Realtime, so it still reads correctly if anything has touched the time scale.
            yield return new WaitForSecondsRealtime(wipePanelSeconds);

            var local = NetworkCombatHooks.FindLocalBridge();
            if (local == null || !local.HasStateAuthority)
                yield break;

            // Spread out by slot, the same way a separation failure does it, so a two-player team
            // does not come back inside one another.
            var players = ListConnectedPlayers();
            int slot = players.IndexOf(local);
            if (slot < 0)
                slot = 0;

            ResolveSpawnPose(checkpointId, checkpointPosition, slot, out var position, out var yaw);
            local.ReviveAt(position, yaw);
        }

        public override void Render()
        {
            if (_changes == null)
                return;

            foreach (var change in _changes.DetectChanges(this))
            {
                if (change == nameof(FailureSeq) && FailureSeq != _seenFailureSeq)
                {
                    _seenFailureSeq = FailureSeq;
                    ApplyLocalTeamFailure();
                }
                else if (change == nameof(SaveSeq) && SaveSeq != _seenSaveSeq)
                {
                    // The whole team banked a checkpoint. Heard by everyone, because it is a shared
                    // achievement rather than something that happened to one player.
                    _seenSaveSeq = SaveSeq;
                    GameSfx.Play2D(SfxId.CheckpointSaved);
                }
            }

            TickSeparationWarning();
        }

        /// <summary>
        /// Rising unease while the team is too far apart and the grace timer is running down.
        ///
        /// Read from the networked breach timer rather than measured locally, so both players hear
        /// it start at the same moment - and it is the only warning that the yank is coming.
        /// </summary>
        void TickSeparationWarning()
        {
            float progress = BreachProgress01;
            if (progress < 0.25f)
                return;

            // Interval tightens as the deadline approaches: a steady beep reads as a status light,
            // an accelerating one reads as a countdown.
            float interval = Mathf.Lerp(1.4f, 0.4f, progress);
            if (GameSfx.Ready(SfxId.TeamSeparationWarning, this, interval))
                GameSfx.Play2D(SfxId.TeamSeparationWarning);
        }

        /// <summary>
        /// Records that one player walked a save pad. The shared checkpoint only advances
        /// once every living teammate has entered that same pad.
        /// </summary>
        public void NotifyPlayerReached(int checkpointId, int playerId, Vector3 position)
        {
            if (Object == null || !Object.IsValid || playerId < 0)
                return;

            if (Runner != null && Runner.IsSharedModeMasterClient)
            {
                RecordVisit(checkpointId, playerId, position);
                return;
            }

            RPC_RequestVisit(checkpointId, playerId, position);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        void RPC_RequestVisit(int checkpointId, int playerId, Vector3 position)
        {
            RecordVisit(checkpointId, playerId, position);
        }

        void RecordVisit(int checkpointId, int playerId, Vector3 position)
        {
            if (checkpointId <= CheckpointId)
                return;

            if (checkpointId != VisitCheckpointId)
            {
                VisitCheckpointId = checkpointId;
                VisitMask = 0;
            }

            VisitMask |= PlayerBit(playerId);
            if (!AllAliveHaveVisited())
                return;

            CheckpointId = checkpointId;
            CheckpointPosition = position;
            VisitCheckpointId = 0;
            VisitMask = 0;
            SaveSeq++;
        }

        public int PendingCheckpointId => VisitCheckpointId;
        public int SavedCheckpointId => CheckpointId;

        public int CountPendingVisits()
        {
            int mask = VisitMask;
            if (mask == 0)
                return 0;

            int count = 0;
            var players = ListAlivePlayers();
            for (int i = 0; i < players.Count; i++)
            {
                int id = players[i].Object.InputAuthority.PlayerId;
                if (id >= 0 && (mask & PlayerBit(id)) != 0)
                    count++;
            }

            return count;
        }

        public int RequiredVisitCount() => ListAlivePlayers().Count;

        public bool HasVisited(int playerId)
        {
            return playerId >= 0 && (VisitMask & PlayerBit(playerId)) != 0;
        }

        bool AllAliveHaveVisited()
        {
            var players = ListAlivePlayers();
            if (players.Count == 0)
                return false;

            int mask = VisitMask;
            for (int i = 0; i < players.Count; i++)
            {
                int id = players[i].Object.InputAuthority.PlayerId;
                if (id < 0 || (mask & PlayerBit(id)) == 0)
                    return false;
            }

            return true;
        }

        static int PlayerBit(int playerId) => 1 << (playerId & 31);

        void EnsureUniqueColors()
        {
            var players = ListConnectedPlayers();
            var used = new HashSet<int>();
            for (int i = 0; i < players.Count; i++)
            {
                var bridge = players[i];
                int want = bridge.ColorIndex;
                if (want < 0 || want >= PlayerColorPalette.Count || used.Contains(want))
                {
                    want = FirstFreeColor(used);
                    if (want != bridge.ColorIndex)
                        bridge.RPC_AssignColor(want);
                }

                used.Add(want);
            }
        }

        static int FirstFreeColor(HashSet<int> used)
        {
            for (int i = 0; i < PlayerColorPalette.Count; i++)
            {
                if (!used.Contains(i))
                    return i;
            }

            return used.Count % Mathf.Max(1, PlayerColorPalette.Count);
        }

        void CheckTeamDistance()
        {
            if (!FailureCooldown.ExpiredOrNotRunning(Runner))
            {
                BreachSeconds = 0f;
                return;
            }

            var players = ListCollarPlayers();
            if (players.Count < 2)
            {
                BreachSeconds = 0f;
                return;
            }

            if (!AnyPairTooFar(players, maxPlayerDistance))
            {
                BreachSeconds = 0f;
                return;
            }

            BreachSeconds += Runner.DeltaTime;
            if (BreachSeconds < separationGraceSeconds)
                return;

            BreachSeconds = 0f;
            FailureSeq++;
            FailureCooldown = TickTimer.CreateFromSeconds(Runner, failureCooldownSeconds);
            RPC_TeamFailure(CheckpointId, CheckpointPosition);
        }

        // Uses the replicated anchor rather than each player's local gameplay transform so the rule
        // measures the same points the clients' HUDs do.
        static bool AnyPairTooFar(List<FpsNetworkBridge> players, float max)
        {
            for (int i = 0; i < players.Count; i++)
            {
                var a = players[i].GetNetworkAnchorPosition();
                for (int j = i + 1; j < players.Count; j++)
                {
                    var b = players[j].GetNetworkAnchorPosition();
                    if (Vector3.Distance(a, b) > max)
                        return true;
                }
            }

            return false;
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = true)]
        void RPC_TeamFailure(int checkpointId, Vector3 checkpointPosition)
        {
            _seenFailureSeq = FailureSeq;
            // Every client, including the master: the team failed together and both players are
            // being pulled back, so both need to hear why.
            GameSfx.Play2D(SfxId.TeamFail);
            ApplyLocalTeamFailure(checkpointId, checkpointPosition);
        }

        void ApplyLocalTeamFailure()
        {
            ApplyLocalTeamFailure(CheckpointId, CheckpointPosition);
        }

        void ApplyLocalTeamFailure(int checkpointId, Vector3 checkpointPosition)
        {
            var local = NetworkCombatHooks.FindLocalBridge();
            if (local == null || !local.HasStateAuthority)
                return;

            var players = ListConnectedPlayers();
            int slot = 0;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] == local)
                {
                    slot = i;
                    break;
                }
            }

            ResolveSpawnPose(checkpointId, checkpointPosition, slot, out var pos, out var yaw);
            local.AuthorityTeamFailAndRespawn(pos, yaw);
        }

        void ResolveSpawnPose(int checkpointId, Vector3 checkpointPosition, int playerSlot, out Vector3 position, out float yaw)
        {
            yaw = 0f;

            var registry = TeamSpawnPoints.Find();
            // #region agent log
            AgentDebugLog.Write("A", "TeamDistanceManager.ResolveSpawnPose", "lookup",
                "{\"registryNull\":" + (registry == null ? "true" : "false") +
                ",\"count\":" + (registry != null ? registry.Count : -1) +
                ",\"playerSlot\":" + playerSlot +
                ",\"checkpointId\":" + checkpointId + "}");
            // #endregion
            if (registry != null && registry.TryGetSpawn(playerSlot, out position, out yaw))
            {
                // #region agent log
                AgentDebugLog.Write("D", "TeamDistanceManager.ResolveSpawnPose", "used_team_spawns",
                    "{\"pos\":\"" + position.ToString() + "\",\"yaw\":" + yaw + "}");
                // #endregion
                return;
            }

            Debug.LogWarning(
                "[TeamDistanceManager] No TeamSpawnPoints wired. Falling back to checkpoint/default spawn.");

            var points = CollectSpawnPoints();
            if (points.Count > 0)
            {
                var point = points[playerSlot % points.Count];
                position = Snap(point.Position);
                yaw = point.Yaw;
                // #region agent log
                AgentDebugLog.Write("D", "TeamDistanceManager.ResolveSpawnPose", "used_network_team_spawn_point",
                    "{\"pos\":\"" + position.ToString() + "\"}");
                // #endregion
                return;
            }

            if (checkpointId > 0 && checkpointPosition.sqrMagnitude > 0.01f)
            {
                position = Snap(checkpointPosition + LateralOffset(playerSlot));
                // #region agent log
                AgentDebugLog.Write("D", "TeamDistanceManager.ResolveSpawnPose", "used_checkpoint",
                    "{\"pos\":\"" + position.ToString() + "\"}");
                // #endregion
                return;
            }

            var fallback = FallbackSpawn.sqrMagnitude > 0.01f ? FallbackSpawn : DefaultFallbackSpawn;
            position = Snap(fallback + LateralOffset(playerSlot));
            // #region agent log
            AgentDebugLog.Write("D", "TeamDistanceManager.ResolveSpawnPose", "used_fallback",
                "{\"pos\":\"" + position.ToString() + "\"}");
            // #endregion
        }

        static Vector3 Snap(Vector3 position)
        {
            var registry = TeamSpawnPoints.Find();
            return registry != null ? registry.SnapToGround(position) : position;
        }

        static Vector3 LateralOffset(int slot)
        {
            float x = (slot % 2 == 0 ? -1f : 1f) * (1.25f + (slot / 2) * 1.5f);
            float z = (slot / 2) * 1.25f;
            return new Vector3(x, 0f, z);
        }

        static List<NetworkTeamSpawnPoint> CollectSpawnPoints()
        {
            var list = new List<NetworkTeamSpawnPoint>(
                FindObjectsByType<NetworkTeamSpawnPoint>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            list.Sort((a, b) =>
            {
                int order = a.Order.CompareTo(b.Order);
                if (order != 0)
                    return order;
                return string.CompareOrdinal(a.name, b.name);
            });
            return list;
        }

        /// <summary>
        /// Everyone the collar binds, which is everyone connected - alive or dead.
        ///
        /// A corpse still wears its collar, so it still counts against the distance rule and a body
        /// left behind will drag the team back. That is the whole point of the carrying mechanic:
        /// you cannot solve a dead teammate by walking away from them.
        ///
        /// Deliberately NOT used for the checkpoint logic, which stays on living players only - a
        /// dead player cannot stand on a save pad, and counting them there would mean no checkpoint
        /// could ever complete while somebody was down.
        /// </summary>
        static List<FpsNetworkBridge> ListCollarPlayers()
        {
            return ListConnectedPlayers();
        }

        static List<FpsNetworkBridge> ListAlivePlayers()
        {
            var list = ListConnectedPlayers();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].IsDead)
                    list.RemoveAt(i);
            }

            return list;
        }

        static List<FpsNetworkBridge> ListConnectedPlayers()
        {
            var list = new List<FpsNetworkBridge>();
            foreach (var bridge in FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None))
            {
                if (bridge == null || bridge.Object == null || !bridge.Object.IsValid)
                    continue;
                list.Add(bridge);
            }

            list.Sort((a, b) =>
                a.Object.StateAuthority.PlayerId.CompareTo(b.Object.StateAuthority.PlayerId));
            return list;
        }
    }
}
