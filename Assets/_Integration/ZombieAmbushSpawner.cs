using System.Collections.Generic;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// A spawn point that only produces zombies while nobody is looking at it.
    ///
    /// The problem with the plain ZombieSpawner is that it drops its whole group at level start, so
    /// every zombie is skinned, animated and drawn from the first frame - including the ones standing
    /// in a corridor the player reaches ten minutes later. This spawns on demand instead, caps how
    /// many exist at once, and only ever appears out of sight.
    ///
    /// MULTIPLAYER, and this dictates the design: the plain spawner gets away with running on every
    /// client because it is deterministic - a seed from its hierarchy path means all machines lay out
    /// the same group with the same names, which is what lets NetworkWorldActor bind the master's
    /// brains. Spawning on sight cannot work that way, because "nobody is looking" is a different
    /// answer on each machine at a different moment. So the master decides alone and broadcasts, and
    /// every client builds an identically named zombie from that one decision.
    /// </summary>
    [AddComponentMenu("CollarCali/Zombie Ambush Spawner")]
    public class ZombieAmbushSpawner : MonoBehaviour, IHiddenSpawnReceiver
    {
        [Header("Budget")]
        [Tooltip("How many zombies from THIS spawner may be alive at once.")]
        [SerializeField] int maxAlive = 4;

        [Tooltip("Total this spawner will ever produce. 0 means unlimited.")]
        [SerializeField] int totalBudget;

        [Tooltip("Seconds between spawns, once the other conditions are met.")]
        [SerializeField] float spawnInterval = 4f;

        [Header("Placement")]
        [Tooltip("Zombies appear within this radius of the spawn point, on the NavMesh.")]
        [SerializeField] float spawnRadius = 4f;

        [Range(0f, 1f)]
        [SerializeField] float crawlerChance = 0.25f;

        [Header("Visibility")]
        [Tooltip("Horizontal field of view treated as 'looking at it', in degrees. Wider is safer.")]
        [SerializeField] float playerFieldOfView = 100f;

        [Tooltip("Never spawn closer than this to any player, even unseen - materialising at arm's " +
                 "length reads as a glitch rather than an ambush.")]
        [SerializeField] float minPlayerDistance = 8f;

        [Tooltip("At least one player must be within this range, or the spawner stays idle. This is " +
                 "what keeps far-off parts of the level from filling up before anyone goes there.")]
        [SerializeField] float activationRange = 45f;

        [Tooltip("Geometry that blocks sight. Left empty it uses the project's standard set.")]
        [SerializeField] LayerMask sightBlockers = 0;

        [Header("Prefabs (loaded from Resources when empty)")]
        [SerializeField] GameObject walkerPrefab;
        [SerializeField] GameObject crawlerPrefab;

        const int VariantWalker = 0;
        const int VariantCrawler = 1;

        float _nextCheck;
        float _nextSpawnTime;
        int _spawned;
        int _sequence;
        readonly List<ZombieEnemy> _mine = new();

        void Awake()
        {
            if (sightBlockers == 0)
                sightBlockers = HiddenSpawnUtility.DefaultSightBlockers();
        }

        void Update()
        {
            // Cheap gate first: this runs on every client, and only the master gets past it.
            if (Time.time < _nextCheck)
                return;
            _nextCheck = Time.time + 0.35f;

            if (!HiddenSpawnUtility.OwnsSpawning())
                return;

            TrySpawn();
        }

        void TrySpawn()
        {
            if (Time.time < _nextSpawnTime)
                return;
            if (totalBudget > 0 && _spawned >= totalBudget)
                return;

            PruneDead();
            if (_mine.Count >= maxAlive)
                return;

            var players = HiddenSpawnUtility.CollectPlayers();
            if (players.Count == 0)
                return;
            if (!HiddenSpawnUtility.AnyPlayerWithin(transform.position, players, activationRange))
                return;
            if (!HiddenSpawnUtility.IsHidden(transform.position + Vector3.up, players,
                    playerFieldOfView, minPlayerDistance, sightBlockers))
                return;
            if (!HiddenSpawnUtility.TryFindHiddenNavPoint(transform.position, spawnRadius, players,
                    playerFieldOfView, minPlayerDistance, sightBlockers, out var position))
                return;

            int variant = Random.value < crawlerChance ? VariantCrawler : VariantWalker;
            float yaw = Random.Range(0f, 360f);

            _sequence++;
            _spawned++;
            _nextSpawnTime = Time.time + spawnInterval;

#if CMPSETUP_COMPLETE
            var relay = NetworkVfxRelay.Instance;
            if (relay != null)
            {
                // Broadcast, including back to this machine, so the master and the clients run the
                // exact same spawn path and cannot drift apart.
                relay.BroadcastHiddenSpawn(NetworkWorldActor.MakeKey(transform), _sequence,
                    position, yaw, variant);
                return;
            }
#endif

            SpawnFromNetwork(_sequence, position, yaw, variant);
        }

        /// <summary>
        /// Called on every client from the relay, and directly when offline.
        ///
        /// The name is built from the sequence number the master chose, because that name is what
        /// NetworkWorldActor hashes to bind this zombie across machines. Two clients disagreeing
        /// about it is the one thing that would leave a zombie frozen for somebody.
        /// </summary>
        public void SpawnFromNetwork(int sequence, Vector3 position, float yaw, int variant)
        {
            bool crawler = variant == VariantCrawler;
            var prefab = crawler
                ? ResolvePrefab(crawlerPrefab, "ZombieCrawler") ?? ResolvePrefab(walkerPrefab, "Zombie")
                : ResolvePrefab(walkerPrefab, "Zombie");

            if (prefab == null)
            {
                Debug.LogError("[CollarCali] ZombieAmbushSpawner: no Zombie prefab. Run " +
                               "Tools/CollarCali/Build Zombie Prefabs first.", this);
                return;
            }

            var zombie = Instantiate(prefab, position, Quaternion.Euler(0f, yaw, 0f), transform);
            zombie.name = (crawler ? "AmbushCrawler " : "Ambush ") + sequence;

            var brain = zombie.GetComponent<ZombieEnemy>();
            if (brain != null)
                _mine.Add(brain);

#if CMPSETUP_COMPLETE
            var runner = NetworkCombatHooks.FindRunner();
            if (runner != null && runner.IsRunning && runner.IsSharedModeMasterClient && brain != null)
                MultiplayerSessionBootstrap.EnsureActorFor(zombie, NetworkWorldKind.Zombie);
#endif
        }

        /// <summary>
        /// Not used by this spawner - its zombies die normally, and a dead one is cleaned up by its
        /// own corpse timer and the actor despawn. Implemented so one spawn RPC can serve every kind
        /// of hidden spawner.
        /// </summary>
        public void DespawnFromNetwork(int sequence)
        {
            var child = transform.Find("Ambush " + sequence) ??
                        transform.Find("AmbushCrawler " + sequence);
            if (child != null)
                Destroy(child.gameObject);
        }

        void PruneDead()
        {
            for (int i = _mine.Count - 1; i >= 0; i--)
            {
                var brain = _mine[i];
                if (brain == null || brain.IsDead)
                    _mine.RemoveAt(i);
            }
        }

        static GameObject ResolvePrefab(GameObject assigned, string resourceName)
        {
            if (assigned != null)
                return assigned;

            var loaded = Resources.Load<GameObject>(resourceName);
            if (loaded != null)
                return loaded;

#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Integration/Resources/" + resourceName + ".prefab");
#else
            return null;
#endif
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.9f, 0.35f, 0.2f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, spawnRadius);
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.25f);
            Gizmos.DrawWireSphere(transform.position, activationRange);
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, minPlayerDistance);
        }
    }
}
