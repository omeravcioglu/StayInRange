using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Drop this on an empty GameObject anywhere up high to spawn a magician there when the level
    /// starts. A lightweight alternative to hand-placing the prefab - mark the ledges, and every
    /// marker fills itself.
    ///
    /// Like ZombieSpawner it runs on every client and instantiates from a deterministic name based
    /// on the marker's own hierarchy path, so NetworkWorldActor can bind the master's brain to the
    /// matching object on each machine. Only the master then creates the actor and runs the brain.
    /// </summary>
    public class MagicianPerch : MonoBehaviour
    {
        [SerializeField] GameObject magicianPrefab;
        [SerializeField] bool spawnOnStart = true;

        /// <summary>Face this way if set; otherwise the magician keeps the marker's rotation.</summary>
        [SerializeField] Transform faceTarget;

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

            var prefab = ResolvePrefab();
            if (prefab == null)
            {
                Debug.LogError("[CollarCali] MagicianPerch: no Magician prefab. " +
                               "Run Tools/CollarCali/Build Magician first.");
                return;
            }

            var rot = faceTarget != null
                ? Quaternion.LookRotation(Flatten(faceTarget.position - transform.position))
                : transform.rotation;

            var magician = Instantiate(prefab, transform.position, rot, transform);
            // Stable name so the actor key matches on every client.
            magician.name = "Magician (" + name + ")";

            StartCoroutine(RegisterActor(magician));
        }

        System.Collections.IEnumerator RegisterActor(GameObject magician)
        {
#if CMPSETUP_COMPLETE
            float deadline = Time.realtimeSinceStartup + 12f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var runner = NetworkCombatHooks.FindRunner();
                if (runner != null && runner.IsRunning)
                {
                    if (runner.IsSharedModeMasterClient && magician != null)
                        MultiplayerSessionBootstrap.EnsureActorFor(magician, NetworkWorldKind.Magician);
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
#else
            yield break;
#endif
        }

        static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 0.001f ? Vector3.forward : v.normalized;
        }

        GameObject ResolvePrefab()
        {
            if (magicianPrefab != null)
                return magicianPrefab;
            var loaded = Resources.Load<GameObject>("Magician");
#if UNITY_EDITOR
            if (loaded == null)
                loaded = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/_Integration/Resources/Magician.prefab");
#endif
            return loaded;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.6f, 0.3f, 0.9f, 0.9f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.9f, 0.4f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.5f);
        }
    }
}
