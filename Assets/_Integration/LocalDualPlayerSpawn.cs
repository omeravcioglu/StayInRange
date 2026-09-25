using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Spawns LocalDualPlayer when playing the Game scene offline (no active Fusion session).
    /// </summary>
    public class LocalDualPlayerSpawn : MonoBehaviour
    {
        [SerializeField] DualPlayerController dualPlayerPrefab;
        [SerializeField] Transform spawnPoint;
        [SerializeField] Vector3 fallbackSpawn = new Vector3(0f, 0f, -35f);

        void Start()
        {
            TrySpawn();
        }

        public void TrySpawn()
        {
            if (FindFirstObjectByType<DualPlayerController>() != null)
                return;

            if (HasActiveFusionSession())
                return;

            var prefab = dualPlayerPrefab;
            if (prefab == null)
                prefab = Resources.Load<DualPlayerController>("LocalDualPlayer");

            if (prefab == null)
            {
                Debug.LogError(
                    "[LocalDualPlayerSpawn] Missing LocalDualPlayer prefab. Run Tools/CollarCali/Build Local Dual Player Prefab.");
                return;
            }

            var pos = spawnPoint != null ? spawnPoint.position : fallbackSpawn;
            var rot = spawnPoint != null ? spawnPoint.rotation : Quaternion.identity;
            Instantiate(prefab, pos, rot);
            Debug.Log($"[LocalDualPlayerSpawn] Spawned LocalDualPlayer at {pos}");
        }

        static bool HasActiveFusionSession()
        {
            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning)
                    return true;
            }

            return false;
        }
    }
}
