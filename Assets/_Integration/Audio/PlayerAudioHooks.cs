using cowsins;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Gives the local player the feedback Cowsins has no slot for: being hurt, dying, respawning
    /// and healing.
    ///
    /// Attached at runtime rather than authored onto the player prefab, because the prefab is
    /// rebuilt by NetworkedFpsPlayerPrefabBuilder and a hand-added component there would be lost
    /// the next time anyone ran it. Everything it plays is 2D - this is the player's own body, and
    /// feedback about yourself should not fade with distance or pan to one side.
    ///
    /// Only ever installed on the machine that owns the player. A remote teammate's pain is their
    /// own business; hearing it in your ear would read as being hit yourself.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerAudioHooks : MonoBehaviour
    {
        PlayerStats _stats;
        bool _wasDead;
        bool _subscribed;

        public static void AttachTo(PlayerStats stats)
        {
            if (stats == null || stats.GetComponent<PlayerAudioHooks>() != null)
                return;
            stats.gameObject.AddComponent<PlayerAudioHooks>();
        }

        void Awake()
        {
            _stats = GetComponent<PlayerStats>();
            Subscribe();
        }

        void OnDestroy()
        {
            Unsubscribe();
        }

        void Subscribe()
        {
            if (_subscribed || _stats == null || _stats.userEvents == null)
                return;

            _subscribed = true;
            _stats.userEvents.OnDamage.AddListener(OnDamaged);
            _stats.userEvents.OnHeal.AddListener(OnHealed);
            _stats.userEvents.OnDeath.AddListener(OnDied);
        }

        void Unsubscribe()
        {
            if (!_subscribed || _stats == null || _stats.userEvents == null)
                return;

            _subscribed = false;
            _stats.userEvents.OnDamage.RemoveListener(OnDamaged);
            _stats.userEvents.OnHeal.RemoveListener(OnHealed);
            _stats.userEvents.OnDeath.RemoveListener(OnDied);
        }

        void OnDamaged()
        {
            // Skipped on the killing blow: the death cry covers it, and the two together sounded
            // like being hit twice.
            if (_stats != null && _stats.IsDead)
                return;
            GameSfx.Play2D(SfxId.PlayerHurt);
        }

        void OnHealed()
        {
            GameSfx.Play2D(SfxId.PlayerHeal);
        }

        void OnDied()
        {
            GameSfx.Play2D(SfxId.PlayerDeath);
        }

        void Update()
        {
            if (_stats == null)
                return;

            // Respawn has no event of its own in Cowsins - PlayerStats.Respawn just clears the flag -
            // so the edge back to alive is what we watch. It also covers the team-failure respawn,
            // which goes through the same call.
            bool dead = _stats.IsDead;
            if (_wasDead && !dead)
                GameSfx.Play2D(SfxId.PlayerRespawn);
            _wasDead = dead;
        }
    }

    /// <summary>
    /// Finds players as they appear and gives them the hooks above. A watcher rather than a one-off
    /// pass because in a session the local player is spawned by Fusion well after the scene loads,
    /// and switching between FPS and Malbers swaps which body is live.
    /// </summary>
    public class PlayerAudioInstaller : MonoBehaviour
    {
        float _nextSweep;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Ensure();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Ensure();
        }

        static void Ensure()
        {
            if (FindFirstObjectByType<PlayerAudioInstaller>() != null)
                return;

            var go = new GameObject("PlayerAudioInstaller");
            DontDestroyOnLoad(go);
            go.AddComponent<PlayerAudioInstaller>();
        }

        void Update()
        {
            if (Time.unscaledTime < _nextSweep)
                return;
            _nextSweep = Time.unscaledTime + 1f;

            foreach (var stats in FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
            {
                if (stats == null || !OwnedLocally(stats))
                    continue;
                PlayerAudioHooks.AttachTo(stats);
            }
        }

        /// <summary>
        /// True offline, and in a session only for the player this machine controls. Without the
        /// bridge check every client would hear every teammate's grunts in their own ears.
        /// </summary>
        static bool OwnedLocally(PlayerStats stats)
        {
#if CMPSETUP_COMPLETE
            var bridge = stats.GetComponentInParent<FpsNetworkBridge>();
            if (bridge != null && bridge.Object != null && bridge.Object.IsValid)
                return bridge.IsLocalOwner;
#endif
            return true;
        }
    }
}
