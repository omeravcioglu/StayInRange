using System.Collections.Generic;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Drop this on any GameObject in the Game scene and drag spawn Transforms into the list.
    /// Team distance failure assigns players to these points by index (wraps if fewer points than players).
    /// </summary>
    public class TeamSpawnPoints : MonoBehaviour
    {
        public static TeamSpawnPoints Instance { get; private set; }

        [Tooltip("Drag empty GameObjects / markers here. Player 0 uses index 0, Player 1 uses index 1, etc.")]
        [SerializeField] List<Transform> spawnPoints = new();

        [Tooltip("Raycast down from each spawn so players land on the floor instead of falling.")]
        [SerializeField] bool snapToGround = true;

        [SerializeField] float groundRayHeight = 8f;
        [SerializeField] float groundRayDistance = 40f;
        [SerializeField] float groundClearance = 0.15f;

        void OnEnable()
        {
            Instance = this;
            // #region agent log
            AgentDebugLog.Write("A", "TeamSpawnPoints.OnEnable", "enabled",
                "{\"scene\":\"" + gameObject.scene.name +
                "\",\"count\":" + Count +
                ",\"active\":" + (gameObject.activeInHierarchy ? "true" : "false") + "}");
            // #endregion
        }

        void Awake()
        {
            Instance = this;
            // #region agent log
            int wired = 0;
            string names = "";
            for (int i = 0; i < spawnPoints.Count; i++)
            {
                if (spawnPoints[i] == null)
                    continue;
                wired++;
                if (names.Length > 0)
                    names += ",";
                names += spawnPoints[i].name;
            }
            AgentDebugLog.Write("B", "TeamSpawnPoints.Awake", "awake",
                "{\"scene\":\"" + gameObject.scene.name +
                "\",\"listSize\":" + spawnPoints.Count +
                ",\"wired\":" + wired +
                ",\"names\":\"" + names +
                "\",\"pos\":\"" + transform.position.ToString() + "\"}");
            // #endregion
        }

        void OnDisable()
        {
            if (Instance == this)
                Instance = null;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public static TeamSpawnPoints Find()
        {
            if (Instance != null)
                return Instance;
            Instance = FindFirstObjectByType<TeamSpawnPoints>(FindObjectsInactive.Include);
            return Instance;
        }

        public int Count
        {
            get
            {
                int n = 0;
                for (int i = 0; i < spawnPoints.Count; i++)
                {
                    if (spawnPoints[i] != null)
                        n++;
                }

                return n;
            }
        }

        /// <summary>
        /// Returns true when a wired spawn exists for this slot (with wrap).
        /// </summary>
        public bool TryGetSpawn(int playerSlot, out Vector3 position, out float yaw)
        {
            position = default;
            yaw = 0f;

            var valid = new List<Transform>(spawnPoints.Count);
            for (int i = 0; i < spawnPoints.Count; i++)
            {
                if (spawnPoints[i] != null)
                    valid.Add(spawnPoints[i]);
            }

            if (valid.Count == 0)
            {
                Debug.LogWarning("[TeamSpawnPoints] Spawn Points list is empty. Drag transforms into the list.", this);
                // #region agent log
                AgentDebugLog.Write("B", "TeamSpawnPoints.TryGetSpawn", "empty_list",
                    "{\"playerSlot\":" + playerSlot + "}");
                // #endregion
                return false;
            }

            var t = valid[Mathf.Abs(playerSlot) % valid.Count];
            position = t.position;
            yaw = t.eulerAngles.y;

            if (snapToGround)
                position = SnapToGround(position);

            Debug.Log($"[TeamSpawnPoints] Slot {playerSlot} → {t.name} @ {position}", t);
            // #region agent log
            AgentDebugLog.Write("D", "TeamSpawnPoints.TryGetSpawn", "resolved",
                "{\"playerSlot\":" + playerSlot +
                ",\"name\":\"" + t.name +
                "\",\"pos\":\"" + position.ToString() +
                "\",\"yaw\":" + yaw + "}");
            // #endregion
            return true;
        }

        public Vector3 SnapToGround(Vector3 position)
        {
            var origin = position + Vector3.up * groundRayHeight;
            if (Physics.Raycast(origin, Vector3.down, out var hit, groundRayDistance, ~0, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * groundClearance;
            return position;
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (spawnPoints == null)
                return;

            Gizmos.color = new Color(0.15f, 0.9f, 0.4f, 0.9f);
            for (int i = 0; i < spawnPoints.Count; i++)
            {
                var t = spawnPoints[i];
                if (t == null)
                    continue;
                Gizmos.DrawWireSphere(t.position + Vector3.up * 0.1f, 0.55f);
                Gizmos.DrawLine(t.position, t.position + t.forward * 1.4f);
            }
        }
#endif
    }
}
