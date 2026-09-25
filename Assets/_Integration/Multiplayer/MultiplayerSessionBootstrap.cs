#if CMPSETUP_COMPLETE
using Fusion;
using MalbersAnimations.Utilities;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Master Client spawns TeamLink + world-actor proxies after Fusion loads Game.
    /// </summary>
    public static class MultiplayerSessionBootstrap
    {
        // Fusion loads Game.unity after startup, so RuntimeInitializeOnLoadMethod alone never fires
        // for the Menu -> Game path and no world actors (creeps, AI, platforms, TeamLink) get spawned.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "Game")
                EnsureHost();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            if (SceneManager.GetActiveScene().name == "Game")
                EnsureHost();
        }

        static void EnsureHost()
        {
            if (Object.FindFirstObjectByType<Runner>() != null)
                return;

            // #region agent log
            AgentDebugLog.Write("B3", "MultiplayerSessionBootstrap.EnsureHost", "host_created", "{}");
            // #endregion

            var host = new GameObject("MultiplayerSessionBootstrap");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<Runner>();
        }

        class Runner : MonoBehaviour
        {
            float _deadline;
            bool _spawned;

            void Start()
            {
                _deadline = Time.realtimeSinceStartup + 10f;
            }

            void Update()
            {
                MultiplayerPrefabs.EnsureLoaded();
                var runner = NetworkCombatHooks.FindRunner();
                if (runner == null || !runner.IsRunning)
                {
                    if (Time.realtimeSinceStartup > _deadline)
                        Destroy(gameObject);
                    return;
                }

                if (!runner.IsSharedModeMasterClient)
                {
                    NetworkWorldActor.DisableAllSceneBrains();
                    EnsureCheckpoints();
                    Destroy(gameObject);
                    return;
                }

                if (_spawned)
                {
                    Destroy(gameObject);
                    return;
                }

                if (MultiplayerPrefabs.TeamLink == null || MultiplayerPrefabs.WorldActor == null)
                {
                    if (Time.realtimeSinceStartup > _deadline)
                        Destroy(gameObject);
                    return;
                }

                SpawnTeamLink(runner);
                SpawnWorldActors(runner);
                EnsureCheckpoints();
                _spawned = true;
                Destroy(gameObject);
            }
        }

        static void SpawnTeamLink(NetworkRunner runner)
        {
            if (TeamDistanceManager.Instance != null)
                return;

            runner.Spawn(MultiplayerPrefabs.TeamLink, Vector3.zero, Quaternion.identity);
        }

        static void SpawnWorldActors(NetworkRunner runner)
        {
            var prefab = MultiplayerPrefabs.WorldActor;
            if (prefab == null)
                return;

            int enemies = 0, creeps = 0, platforms = 0, traps = 0, zombies = 0, magicians = 0;

            foreach (var ai in Object.FindObjectsByType<EmeraldAI.EmeraldSystem>(FindObjectsSortMode.None))
            {
                if (ai == null)
                    continue;
                if (SpawnActor(runner, prefab, ai.gameObject, NetworkWorldKind.Enemy))
                    enemies++;
            }

            foreach (var creep in Object.FindObjectsByType<CreepGrabEnemy>(FindObjectsSortMode.None))
            {
                if (creep == null)
                    continue;
                if (SpawnActor(runner, prefab, creep.gameObject, NetworkWorldKind.Creep))
                    creeps++;
            }

            foreach (var zombie in Object.FindObjectsByType<ZombieEnemy>(FindObjectsSortMode.None))
            {
                if (zombie == null)
                    continue;
                if (SpawnActor(runner, prefab, zombie.gameObject, NetworkWorldKind.Zombie))
                    zombies++;
            }

            foreach (var magician in Object.FindObjectsByType<MagicianEnemy>(FindObjectsSortMode.None))
            {
                if (magician == null)
                    continue;
                if (SpawnActor(runner, prefab, magician.gameObject, NetworkWorldKind.Magician))
                    magicians++;
            }

            var motion = new System.Collections.Generic.List<GameObject>();
            NetworkWorldActor.CollectMotionSources(motion);
            for (int i = 0; i < motion.Count; i++)
            {
                var source = motion[i];
                var kind = NetworkWorldActor.ClassifyMotion(source);
                if (!SpawnActor(runner, prefab, source, kind))
                    continue;
                if (kind == NetworkWorldKind.Trap)
                    traps++;
                else
                    platforms++;
            }

            // #region agent log
            AgentDebugLog.Write("B3", "MultiplayerSessionBootstrap.SpawnWorldActors", "actors_spawned",
                "{\"enemies\":" + enemies +
                ",\"creeps\":" + creeps +
                ",\"zombies\":" + zombies +
                ",\"magicians\":" + magicians +
                ",\"platforms\":" + platforms +
                ",\"traps\":" + traps + "}");
            // #endregion
        }

        /// <summary>
        /// Late safety net for callers that cannot work without an actor. The startup sweep runs
        /// once and can miss objects that were inactive or created afterwards, which left creeps
        /// unable to hand a grab to the victim's owner.
        /// </summary>
        public static NetworkWorldActor EnsureActorFor(GameObject source, NetworkWorldKind kind)
        {
            if (source == null)
                return null;

            var existing = NetworkWorldActor.FindFor(source);
            if (existing != null)
                return existing;

            MultiplayerPrefabs.EnsureLoaded();
            var prefab = MultiplayerPrefabs.WorldActor;
            var runner = NetworkCombatHooks.FindRunner();
            if (prefab == null || runner == null || !runner.IsRunning || !runner.IsSharedModeMasterClient)
                return null;

            SpawnActor(runner, prefab, source, kind);
            return NetworkWorldActor.FindFor(source);
        }

        static bool SpawnActor(NetworkRunner runner, NetworkObject prefab, GameObject source, NetworkWorldKind kind)
        {
            int sceneKey = NetworkWorldActor.MakeKey(source.transform);
            bool bound = false;
            NetworkObject obj;
            try
            {
                obj = runner.Spawn(
                    prefab,
                    source.transform.position,
                    source.transform.rotation,
                    inputAuthority: null,
                    onBeforeSpawned: (r, spawned) =>
                    {
                        var pending = spawned.GetComponent<NetworkWorldActor>();
                        if (pending == null)
                            return;
                        pending.InitializeSpawnState(source, sceneKey, kind);
                        bound = true;
                    });
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[CollarCali] Failed to spawn world actor for '{source.name}': {e.Message}");
                return false;
            }

            // Shared mode may queue the spawn and hand back null; the callback has already bound
            // the source in that case, so treating null as a failure would drop a live actor.
            if (obj == null)
                return bound;

            var actor = obj.GetComponent<NetworkWorldActor>();
            if (actor == null)
                return false;

            actor.BindSource(source, sceneKey, kind);
            return true;
        }

        public static void EnsureCheckpoints()
        {
            // Scene-authored TeamSavePoint prefab instances only. Nothing is spawned at runtime.
        }
    }

    public static class MultiplayerPrefabs
    {
        public static NetworkObject TeamLink
        {
            get => NetworkCombatHooks.TeamLinkPrefab;
            set => NetworkCombatHooks.TeamLinkPrefab = value;
        }

        public static NetworkObject WorldActor
        {
            get => NetworkCombatHooks.WorldActorPrefab;
            set => NetworkCombatHooks.WorldActorPrefab = value;
        }

        public static void EnsureLoaded()
        {
            if (TeamLink == null)
            {
                var go = Resources.Load<GameObject>("TeamLink");
                if (go != null)
                    TeamLink = go.GetComponent<NetworkObject>();
            }

            if (WorldActor == null)
            {
                var go = Resources.Load<GameObject>("NetworkWorldActor");
                if (go != null)
                    WorldActor = go.GetComponent<NetworkObject>();
            }

#if UNITY_EDITOR
            if (TeamLink == null)
            {
                var go = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/_Integration/Prefabs/TeamLink.prefab");
                if (go != null)
                    TeamLink = go.GetComponent<NetworkObject>();
            }

            if (WorldActor == null)
            {
                var go = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/_Integration/Prefabs/NetworkWorldActor.prefab");
                if (go != null)
                    WorldActor = go.GetComponent<NetworkObject>();
            }
#endif
        }
    }
}
#endif
