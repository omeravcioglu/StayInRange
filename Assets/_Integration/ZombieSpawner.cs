using System.Collections;
using UnityEngine;
using UnityEngine.AI;

namespace CollarCali
{
    /// <summary>
    /// Drops a group of zombies around itself when the level starts.
    ///
    /// Multiplayer note: this runs on EVERY client, not just the master, and places zombies from
    /// a seed derived from the spawner's own hierarchy path. That is deliberate - NetworkWorldActor
    /// binds an actor to a scene object by hashing that object's path, so the zombies have to exist
    /// with matching names on every machine for the master's brain to drive the remote copies. Only
    /// the master then creates the actors and runs the brains.
    /// </summary>
    public class ZombieSpawner : MonoBehaviour
    {
        [Header("Group")]
        [SerializeField] int count = 6;
        [SerializeField] float radius = 12f;

        /// <summary>Fraction of the group spawned as crawlers, 0 to 1.</summary>
        [Range(0f, 1f)]
        [SerializeField] float crawlerChance = 0.25f;

        [Header("Prefabs (loaded from Resources when empty)")]
        [SerializeField] GameObject walkerPrefab;
        [SerializeField] GameObject crawlerPrefab;

        [Header("Placement")]
        /// <summary>How far from a random point the NavMesh may be sampled before giving up.</summary>
        [SerializeField] float navMeshSampleDistance = 6f;
        [SerializeField] bool spawnOnStart = true;

        bool _spawned;

        void Start()
        {
            if (spawnOnStart)
                Spawn();
        }

        [ContextMenu("Spawn Now")]
        public void Spawn()
        {
            if (_spawned)
                return;
            _spawned = true;

            var walker = ResolvePrefab(walkerPrefab, "Zombie");
            var crawler = ResolvePrefab(crawlerPrefab, "ZombieCrawler");

            if (walker == null)
            {
                Debug.LogError("[CollarCali] ZombieSpawner: no Zombie prefab. Run " +
                               "Tools/CollarCali/Build Zombie Prefabs first.");
                return;
            }

            // Seeded from the path, so every client lays the group out identically. Using
            // UnityEngine.Random here instead would desync the spawn between machines.
            var rng = new System.Random(PlacementSeed());
            var offsets = new Vector2[PlacementAttempts];
            int fellBack = 0;

            for (int i = 0; i < count; i++)
            {
                // Every random for this zombie is drawn up front, always the same number of them.
                // Previously TryFindPoint consumed a variable number of draws depending on how
                // quickly NavMesh sampling succeeded - and sampling is not guaranteed identical
                // across machines. One differing sample shifted every later draw, which flipped
                // walker/crawler for the rest of the group, which changed their names, which broke
                // the path hash the actors bind by. Some zombies then froze for the other player.
                // Drawn unconditionally. `crawler != null && rng.NextDouble() < ...` would
                // short-circuit on a machine that failed to resolve the crawler prefab, consuming
                // one fewer random and shifting every later draw - and with it every later name.
                double roll = rng.NextDouble();
                bool asCrawler = crawler != null && roll < crawlerChance;
                float yaw = (float)(rng.NextDouble() * 360.0);

                for (int k = 0; k < PlacementAttempts; k++)
                    offsets[k] = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());

                var prefab = asCrawler ? crawler : walker;
                var position = ResolvePoint(offsets, i, out bool usedFallback);
                if (usedFallback)
                    fellBack++;

                var zombie = Instantiate(prefab, position, Quaternion.Euler(0f, yaw, 0f), transform);

                // Name, not sibling index, is what the actor key hashes - so it has to be stable
                // and identical on every client.
                zombie.name = (asCrawler ? "ZombieCrawler " : "Zombie ") + i;
            }

            // #region agent log
            AgentDebugLog.Write("Z1", "ZombieSpawner.Spawn", "group_spawned",
                "{\"count\":" + count + ",\"navmeshFallbacks\":" + fellBack +
                ",\"spawner\":\"" + name + "\"}");
            // #endregion

            StartCoroutine(RegisterActors());
        }

        /// <summary>
        /// The session runner is usually not up yet on the frame the level starts, so keep trying
        /// for a few seconds. On a client that never becomes master this simply does nothing.
        /// </summary>
        IEnumerator RegisterActors()
        {
#if CMPSETUP_COMPLETE
            float deadline = Time.realtimeSinceStartup + 12f;

            while (Time.realtimeSinceStartup < deadline)
            {
                var runner = NetworkCombatHooks.FindRunner();
                if (runner != null && runner.IsRunning)
                {
                    if (!runner.IsSharedModeMasterClient)
                        yield break;

                    foreach (var zombie in GetComponentsInChildren<ZombieEnemy>(true))
                    {
                        if (zombie != null)
                            MultiplayerSessionBootstrap.EnsureActorFor(zombie.gameObject, NetworkWorldKind.Zombie);
                    }

                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }
#else
            yield break;
#endif
        }

        const int PlacementAttempts = 12;

        /// <summary>
        /// Walks the pre-drawn offsets until the NavMesh accepts one. Falls back to the spawner's
        /// own position rather than skipping: a missing child would shift nothing else, but that
        /// zombie would simply not exist on that client and its actor could never bind.
        /// </summary>
        Vector3 ResolvePoint(Vector2[] offsets, int index, out bool usedFallback)
        {
            for (int i = 0; i < offsets.Length; i++)
            {
                double angle = offsets[i].x * System.Math.PI * 2.0;
                // Square root keeps the group evenly spread instead of clustering at the centre.
                double distance = System.Math.Sqrt(offsets[i].y) * radius;

                var candidate = transform.position + new Vector3(
                    (float)(System.Math.Cos(angle) * distance),
                    0f,
                    (float)(System.Math.Sin(angle) * distance));

                if (NavMesh.SamplePosition(candidate, out var hit, navMeshSampleDistance, NavMesh.AllAreas))
                {
                    usedFallback = false;
                    return hit.position;
                }
            }

            // Ringed by index rather than all on the spawner: a group that fails sampling would
            // otherwise pile every zombie onto one point, and TryPlaceOnNavMesh would then warp
            // them all to the same place.
            usedFallback = true;
            float ring = index * 0.618f * Mathf.PI * 2f;
            return transform.position + new Vector3(Mathf.Cos(ring), 0f, Mathf.Sin(ring)) * 1.5f;
        }

        int PlacementSeed()
        {
#if CMPSETUP_COMPLETE
            return NetworkWorldActor.MakeKey(transform);
#else
            return name.GetHashCode();
#endif
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
            Gizmos.color = new Color(0.5f, 0.85f, 0.3f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, radius);
        }
    }
}
