using cowsins;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Moves a laser back and forth on Fusion's shared clock so every client
    /// sees the same sweep. Place this on your laser object, then either assign
    /// Point A / Point B in the scene or set Local Travel.
    /// </summary>
    [AddComponentMenu("CollarCali/Networked Sweeping Laser")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public class NetworkedSweepingLaser : MonoBehaviour
    {
        [Header("Path")]
        [SerializeField, Tooltip("Optional scene marker. If both points are set, the laser sweeps between them.")]
        Transform pointA;

        [SerializeField, Tooltip("Optional scene marker. If both points are set, the laser sweeps between them.")]
        Transform pointB;

        [SerializeField, Tooltip("Used when Point A / Point B are empty. Offset from this object's starting position, in its local space.")]
        Vector3 localTravel = new Vector3(4f, 0f, 0f);

        [SerializeField, Min(0.05f), Tooltip("Seconds to travel from one side to the other. The return trip takes the same time.")]
        float oneWaySeconds = 2f;

        [SerializeField, Tooltip("Leave 0 to start at Point A. Raise this to stagger lasers in the same room.")]
        float phaseOffsetSeconds;

        [Header("Hit")]
        [SerializeField] bool killOnTouch = true;
        [SerializeField, Min(0f)] float damage = 100f;
        [SerializeField, Min(0.05f)] float damageInterval = 0.25f;

        Transform _parent;
        Vector3 _localA;
        Vector3 _localB;
        float _nextHitTime;
        SfxLoop _hum;

        void Reset()
        {
            var col = GetComponent<Collider>();
            col.isTrigger = true;
        }

        void Awake()
        {
            var col = GetComponent<Collider>();
            if (col != null)
                col.isTrigger = true;

            CapturePath();
            ApplyPosition(SharedTime());

            // Local to each client and needs no syncing: the laser already moves off the shared
            // simulation clock, so the hum is in the same place on every machine. It is also the
            // only warning a player gets before walking into one in a dark corridor.
            _hum = GameSfx.PlayLoop(SfxId.LaserHum, transform);
        }

        void OnDestroy()
        {
            _hum.Stop();
        }

        void Update()
        {
            ApplyPosition(SharedTime());
        }

        void OnTriggerEnter(Collider other) => TryHit(other);

        void OnTriggerStay(Collider other) => TryHit(other);

        void CapturePath()
        {
            _parent = transform.parent;

            if (pointA != null && pointB != null)
                return;

            Vector3 worldA = transform.position;
            Vector3 worldB = transform.TransformPoint(localTravel);
            _localA = ToPathSpace(worldA);
            _localB = ToPathSpace(worldB);
        }

        void ApplyPosition(float time)
        {
            transform.position = Vector3.Lerp(CurrentA(), CurrentB(), SweepT(time));
        }

        float SweepT(float time)
        {
            float oneWay = Mathf.Max(0.05f, oneWaySeconds);
            float trip = oneWay * 2f;
            float t = time + phaseOffsetSeconds;
            t %= trip;
            if (t < 0f)
                t += trip;

            return t <= oneWay ? t / oneWay : 1f - (t - oneWay) / oneWay;
        }

        Vector3 CurrentA()
        {
            if (pointA != null)
                return pointA.position;
            return FromPathSpace(_localA);
        }

        Vector3 CurrentB()
        {
            if (pointB != null)
                return pointB.position;
            return FromPathSpace(_localB);
        }

        Vector3 ToPathSpace(Vector3 world)
        {
            return _parent != null ? _parent.InverseTransformPoint(world) : world;
        }

        Vector3 FromPathSpace(Vector3 local)
        {
            return _parent != null ? _parent.TransformPoint(local) : local;
        }

        float SharedTime()
        {
#if CMPSETUP_COMPLETE
            var runner = NetworkCombatHooks.FindRunner();
            if (runner != null && runner.IsRunning)
                return (float)runner.SimulationTime;
#endif
            return Time.time;
        }

        void TryHit(Collider other)
        {
            if (other == null || other.isTrigger)
                return;
            if (Time.time < _nextHitTime)
                return;

#if CMPSETUP_COMPLETE
            var bridge = other.GetComponentInParent<FpsNetworkBridge>();
            if (bridge != null)
            {
                if (!bridge.IsLocalOwner || bridge.IsDead)
                    return;

                _nextHitTime = Time.time + damageInterval;
                GameSfx.Play(SfxId.LaserZap, transform.position);
                bridge.Damage(HitDamage(), false);
                return;
            }
#endif

            var stats = other.GetComponentInParent<PlayerStats>();
            if (stats == null || stats.IsDead)
                return;

            _nextHitTime = Time.time + damageInterval;
            GameSfx.Play(SfxId.LaserZap, transform.position);
            stats.Damage(HitDamage(), false);
        }

        float HitDamage()
        {
            return killOnTouch ? 99999f : damage;
        }

        void OnDrawGizmosSelected()
        {
            Vector3 a;
            Vector3 b;
            if (pointA != null && pointB != null)
            {
                a = pointA.position;
                b = pointB.position;
            }
            else
            {
                a = Application.isPlaying ? CurrentA() : transform.position;
                b = Application.isPlaying ? CurrentB() : transform.TransformPoint(localTravel);
            }

            Gizmos.color = new Color(1f, 0.15f, 0.15f, 0.95f);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawSphere(a, 0.08f);
            Gizmos.DrawSphere(b, 0.08f);
        }
    }
}
