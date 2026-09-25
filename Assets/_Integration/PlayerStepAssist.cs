using cowsins;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Step-up assist for the Cowsins rigidbody-capsule controller.
    ///
    /// The controller has no step offset - a capsule with a rounded bottom simply jams against any
    /// obstacle taller than its edge can roll over, so small rocks and ledges stop the player dead,
    /// and standing wedged on uneven ground bleeds jump impulse into depenetration. This gently
    /// lifts the body over anything shorter than stepHeight when it is grounded and walking into it,
    /// which fixes both.
    ///
    /// It self-installs at runtime (see PlayerStepAssistInstaller) so there is no prefab to edit,
    /// and it only ever nudges the rigidbody UP while grounded and moving - never mid-jump, never
    /// in the air - so it cannot fight the controller's own movement or its jumps.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PlayerStepAssist : MonoBehaviour
    {
        [Tooltip("Obstacles up to this tall are climbed. Keep below the player's jump-worthy height.")]
        [SerializeField] float stepHeight = 0.4f;

        [Tooltip("How far ahead an obstacle is felt for, beyond the capsule radius.")]
        [SerializeField] float probeDistance = 0.15f;

        [Tooltip("Climb speed in metres per second while a step is detected.")]
        [SerializeField] float climbSpeed = 4f;

        [Tooltip("Do not assist while rising faster than this - that is a jump, not a step.")]
        [SerializeField] float risingCutoff = 1.5f;

        [Tooltip("Minimum horizontal speed before the assist engages.")]
        [SerializeField] float minMoveSpeed = 0.4f;

        Rigidbody _rb;
        CapsuleCollider _capsule;
        PlayerMovement _movement;
        LayerMask _groundMask;
        bool _ready;

        public void Bind(PlayerMovement movement)
        {
            _movement = movement;
            _rb = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();
            if (_capsule == null)
                _capsule = GetComponentInChildren<CapsuleCollider>();

            // Reuse the controller's own ground definition so the assist never lifts over a wall
            // it should not, and never onto something the controller would not call ground.
            _groundMask = movement != null && movement.playerSettings != null
                ? movement.playerSettings.whatIsGround
                : ~0;

            _ready = _rb != null && _capsule != null;
        }

        void FixedUpdate()
        {
            if (!_ready || _movement == null)
                return;

            // Only the locally-simulated player: a networked proxy's body is kinematic and driven
            // by replication, so nudging it here would fight the incoming transform.
            if (_rb.isKinematic)
                return;

            // Grounded only: lifting the body while airborne would let the player float up walls.
            if (!_movement.Grounded)
                return;

            var vel = _rb.linearVelocity;
            if (vel.y > risingCutoff)
                return;   // mid-jump

            var horizontal = new Vector3(vel.x, 0f, vel.z);
            if (horizontal.sqrMagnitude < minMoveSpeed * minMoveSpeed)
                return;   // not walking into anything

            var moveDir = horizontal.normalized;

            // Foot point at the very base of the capsule.
            Vector3 worldCenter = _capsule.transform.TransformPoint(_capsule.center);
            float half = _capsule.height * 0.5f * _capsule.transform.lossyScale.y;
            Vector3 foot = new Vector3(worldCenter.x, worldCenter.y - half, worldCenter.z);
            float radius = _capsule.radius * _capsule.transform.lossyScale.x;
            float reach = radius + probeDistance;

            // Fan of three low feelers so a rock met off-centre still counts.
            if (!BlockedLow(foot, moveDir, reach))
                return;

            // If the same direction is blocked at step height, it is a wall, not a step - leave it.
            Vector3 stepEye = foot + Vector3.up * (stepHeight + 0.05f);
            if (Physics.Raycast(stepEye, moveDir, reach, _groundMask, QueryTriggerInteraction.Ignore))
                return;

            // Confirm there is ground to land on just past the obstacle, within step height.
            Vector3 ahead = foot + moveDir * reach + Vector3.up * (stepHeight + 0.1f);
            if (!Physics.Raycast(ahead, Vector3.down, out var top, stepHeight + 0.15f,
                    _groundMask, QueryTriggerInteraction.Ignore))
                return;

            // The top surface must be genuinely walkable (not a near-vertical face read as a step).
            if (Vector3.Angle(Vector3.up, top.normal) > 50f)
                return;

            // Glide up. Moving the rigidbody position (not adding velocity) keeps the controller's
            // own horizontal velocity untouched, so the player continues forward onto the step.
            float lift = climbSpeed * Time.fixedDeltaTime;
            float remaining = top.point.y - foot.y;
            if (remaining <= 0.001f)
                return;
            lift = Mathf.Min(lift, remaining + 0.02f);
            _rb.position += Vector3.up * lift;
        }

        bool BlockedLow(Vector3 foot, Vector3 moveDir, float reach)
        {
            // A hair above the very bottom, so the ray is not born inside the ground.
            Vector3 origin = foot + Vector3.up * 0.05f;
            Quaternion left = Quaternion.AngleAxis(-25f, Vector3.up);
            Quaternion right = Quaternion.AngleAxis(25f, Vector3.up);

            return Cast(origin, moveDir, reach)
                || Cast(origin, left * moveDir, reach)
                || Cast(origin, right * moveDir, reach);
        }

        bool Cast(Vector3 origin, Vector3 dir, float reach) =>
            Physics.Raycast(origin, dir, reach, _groundMask, QueryTriggerInteraction.Ignore);
    }
}

namespace CollarCali
{
    /// <summary>
    /// Watches for Cowsins players and gives each one a PlayerStepAssist. Runs as a hidden
    /// persistent object so it also catches players spawned later over the network, without any
    /// prefab having to be edited.
    /// </summary>
    public static class PlayerStepAssistInstaller
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var go = new GameObject("PlayerStepAssistInstaller");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideInHierarchy;
            go.AddComponent<Watcher>();
        }

        class Watcher : MonoBehaviour
        {
            float _next;

            void Update()
            {
                // Cheap poll; new players are rare events.
                if (Time.unscaledTime < _next)
                    return;
                _next = Time.unscaledTime + 1f;

                foreach (var movement in FindObjectsByType<cowsins.PlayerMovement>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (movement == null)
                        continue;
                    if (movement.GetComponent<PlayerStepAssist>() != null)
                        continue;

                    var assist = movement.gameObject.AddComponent<PlayerStepAssist>();
                    assist.Bind(movement);
                }
            }
        }
    }
}
