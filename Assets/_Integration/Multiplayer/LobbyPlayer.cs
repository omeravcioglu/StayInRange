#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// One networked record per connected client while the session sits in the pre-game lobby.
    /// Shared mode gives every client state authority over the object it spawned, so a player
    /// writes its own ready flag locally and the change replicates without any RPC round trip.
    /// </summary>
    public class LobbyPlayer : NetworkBehaviour
    {
        /// <summary>Live roster. Maintained by Spawned/Despawned; prune before reading.</summary>
        public static readonly List<LobbyPlayer> All = new List<LobbyPlayer>();

        [Networked] public NetworkString<_32> PlayerName { get; set; }
        [Networked] public NetworkBool IsReady { get; set; }
        [Networked] public NetworkBool IsHost { get; set; }

        /// <summary>
        /// The character skin this player picked in the lobby. Replicated so every client can show
        /// each other's choice in the roster, and read straight from the local PlayerPrefs selection
        /// so it matches what FpsNetworkBridge will spawn with in Game.
        /// </summary>
        [Networked] public int CharacterIndex { get; set; }

        /// <summary>
        /// Set by the host the instant it commits to starting. Clients that see it in time lock
        /// their lobby UI; the scene sync is what actually guarantees the transition, so treat
        /// this as a UI hint rather than the mechanism.
        /// </summary>
        [Networked] public NetworkBool MatchStarting { get; set; }

        bool _hooked;

        public bool IsLocal => Object != null && Object.IsValid && Object.HasStateAuthority;
        public PlayerRef Owner => Object != null && Object.IsValid ? Object.StateAuthority : PlayerRef.None;

        public string DisplayName
        {
            get
            {
                var raw = PlayerName.ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                    return raw;
                var id = Owner.IsRealPlayer ? Owner.PlayerId : 0;
                return "Player " + id;
            }
        }

        public override void Spawned()
        {
            if (!All.Contains(this))
                All.Add(this);

            if (Object.HasStateAuthority && string.IsNullOrWhiteSpace(PlayerName.ToString()))
                PlayerName = ResolveLocalName();

            // Seed the networked pick from whatever this client last chose so the roster is right
            // before the player touches the selector.
            if (Object.HasStateAuthority)
                CharacterIndex = CharacterSelection.SelectedIndex;

            if (!_hooked)
            {
                SceneManager.activeSceneChanged += OnActiveSceneChanged;
                _hooked = true;
            }

            // #region agent log
            AgentDebugLog.Write("L1", "LobbyPlayer.Spawned", "lobby_player_spawned",
                "{\"name\":\"" + DisplayName.Replace("\"", "'") +
                "\",\"local\":" + (IsLocal ? "true" : "false") +
                ",\"roster\":" + All.Count + "}");
            // #endregion
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            All.Remove(this);
            Unhook();
        }

        void OnDestroy()
        {
            All.Remove(this);
            Unhook();
        }

        void Unhook()
        {
            if (!_hooked)
                return;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            _hooked = false;
        }

        public override void FixedUpdateNetwork()
        {
            if (Object == null || !Object.HasStateAuthority || Runner == null)
                return;

            // Mirroring master-client status onto networked state is what lets every client draw
            // the host badge, and it follows automatically if the host drops and Fusion promotes
            // someone else. The Start button itself keys off the local IsSharedModeMasterClient,
            // not this flag, so a room is never left with nobody able to start.
            bool master = Runner.IsSharedModeMasterClient;
            if (IsHost != master)
                IsHost = master;

            // The host never toggles ready - pressing Start is the equivalent action - so keeping
            // its flag latched true stops it from blocking its own all-ready gate.
            if (master && !IsReady)
                IsReady = true;
        }

        public void SetReady(bool ready)
        {
            if (Object == null || !Object.HasStateAuthority || IsHost)
                return;
            IsReady = ready;
        }

        public void ToggleReady() => SetReady(!IsReady);

        /// <summary>
        /// Local player commits a character pick: writes it to the networked record (so others see it)
        /// and to PlayerPrefs (so it carries into Game when the match starts).
        /// </summary>
        public void SetCharacter(int index)
        {
            CharacterSelection.SelectedIndex = index;
            if (Object != null && Object.HasStateAuthority)
                CharacterIndex = CharacterSelection.SelectedIndex;
        }

        public void FlagMatchStarting()
        {
            if (Object != null && Object.HasStateAuthority)
                MatchStarting = true;
        }

        /// <summary>
        /// Released when a start attempt fails. The flag gates ready toggles, the Start button and
        /// record respawns on every peer, so leaving it latched after a failure locks the room.
        /// </summary>
        public void ClearMatchStarting()
        {
            if (Object != null && Object.IsValid && Object.HasStateAuthority)
                MatchStarting = false;
        }

        /// <summary>
        /// Belt-and-braces cleanup. Fusion normally destroys these along with the lobby scene, so
        /// this only bites if a queued spawn resolves on the far side of the transition.
        /// </summary>
        void OnActiveSceneChanged(Scene from, Scene to)
        {
            if (to.name == LobbyController.LobbySceneName)
                return;
            if (Object == null || !Object.IsValid || !Object.HasStateAuthority || Runner == null)
                return;

            try
            {
                Runner.Despawn(Object);
            }
            catch (System.Exception)
            {
                // Fusion may already have despawned this as part of the same scene transition.
            }
        }

        static string ResolveLocalName()
        {
            // Read from the static, not FusionConnection.Instance: that component lives on a
            // Menu.unity object with no DontDestroyOnLoad, so by the time a lobby record spawns
            // the instance has already been destroyed with the menu.
            var captured = AvocadoShark.FusionConnection.LastPlayerName;
            if (!string.IsNullOrWhiteSpace(captured))
                return Trim(captured);

            var connection = AvocadoShark.FusionConnection.Instance;
            if (connection != null && !string.IsNullOrWhiteSpace(connection._playerName))
                return Trim(connection._playerName);

            return string.Empty;
        }

        // NetworkString<_32> holds 32 characters; anything longer is cut mid-name, so trim where
        // the shortening is deliberate instead of letting the tail be mangled.
        static string Trim(string value) => value.Length <= 24 ? value : value.Substring(0, 24);

        public static void PruneRoster()
        {
            for (int i = All.Count - 1; i >= 0; i--)
            {
                var entry = All[i];
                if (entry == null || entry.Object == null || !entry.Object.IsValid)
                    All.RemoveAt(i);
            }
        }

        /// <summary>
        /// A spawn that Fusion queues can resolve after a retry has already been issued, leaving
        /// one client owning two records and showing up twice in everyone's list. Keep the first
        /// and drop the rest.
        /// </summary>
        public static void DespawnDuplicateLocals()
        {
            PruneRoster();

            var locals = new List<LobbyPlayer>();
            foreach (var entry in All)
            {
                if (entry.IsLocal)
                    locals.Add(entry);
            }

            if (locals.Count < 2)
                return;

            // Snapshot before despawning: Despawn invokes Despawned() synchronously, which mutates
            // All, so walking All directly would skip an entry per removal.
            for (int i = 1; i < locals.Count; i++)
            {
                var extra = locals[i];
                if (extra == null || extra.Object == null || !extra.Object.IsValid || extra.Runner == null)
                    continue;

                try
                {
                    extra.Runner.Despawn(extra.Object);
                }
                catch (System.Exception)
                {
                    // Already gone; PruneRoster will drop it on the next pass.
                }
            }
        }

        public static LobbyPlayer Local()
        {
            PruneRoster();
            foreach (var entry in All)
                if (entry.IsLocal)
                    return entry;
            return null;
        }

        public static bool AnyMatchStarting()
        {
            PruneRoster();
            foreach (var entry in All)
                if (entry.MatchStarting)
                    return true;
            return false;
        }
    }
}
#endif
