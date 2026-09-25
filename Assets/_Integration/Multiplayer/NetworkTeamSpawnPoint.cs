using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Scene marker for team failure respawns. Place several in Game; players are assigned by index.
    /// </summary>
    public class NetworkTeamSpawnPoint : MonoBehaviour
    {
        [SerializeField] int order;

        public int Order => order;
        public Vector3 Position => transform.position;
        public float Yaw => transform.eulerAngles.y;

        public void SetOrder(int value)
        {
            order = value;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.85f, 0.45f, 0.85f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.1f, 0.6f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.5f);
        }
    }
}
