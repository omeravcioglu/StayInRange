using System.Collections;
using System.Collections.Generic;
using cowsins;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Stationary ranged caster. Perches on high ground, turns to face the nearest player it can
    /// see, and lobs a magic projectile on a cooldown. It never moves - the threat is the angle,
    /// not the chase - so it needs no NavMeshAgent.
    ///
    /// Reuses the whole zombie enemy stack: ZombieHealth for HP and damage routing, ZombieHealthBar
    /// for the toony bar, ZombieBloodFx for hit blood and the gut splash on the killing blow. The
    /// only magician-specific pieces are this brain, the projectile it fires, and the ragdoll it
    /// drops when it dies.
    ///
    /// In a session only the master runs this; every other client sleeps its brain
    /// (DisableAsProxy) and is driven by replicated transform and animator state.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class MagicianEnemy : MonoBehaviour
    {
        // Animator parameters, matched by MagicianPrefabBuilder.
        const string CastParam = "Cast";
        const string DeadParam = "Dead";
        const string AimParam = "Aiming";

        [Header("Senses")]
        [SerializeField] float detectRadius = 40f;

        /// <summary>Vertical reach is generous: the whole point is shooting down from a height.</summary>
        [SerializeField] float verticalReach = 30f;

        [SerializeField] float turnSpeedDegrees = 220f;

        [Header("Casting")]
        [SerializeField] float castCooldown = 3f;

        /// <summary>Wind-up from the cast animation starting to the bolt leaving the hand.</summary>
        [SerializeField] float castWindup = 0.6f;

        [SerializeField] float projectileDamage = 10f;
        [SerializeField] float projectileSpeed = 16f;

        /// <summary>
        /// Local offset the bolt spawns from - roughly the caster's raised hand. Serialised so it
        /// can be nudged per model without touching code.
        /// </summary>
        [SerializeField] Vector3 castHandOffset = new Vector3(0.35f, 1.5f, 0.5f);

        [Header("Death")]
        [SerializeField] float corpseLingerSeconds = 12f;

        [Header("Line of sight")]
        [SerializeField] LayerMask sightBlockers = 0;
        [SerializeField] float eyeHeight = 1.6f;

        Animator[] _animators;
        ZombieHealth _health;
        MagicianRagdoll _ragdoll;
        Coroutine _brain;
        EnemyVoice _voice;

        bool _dead;
        float _nextCastTime;
        float _nextOwnershipCheck;

        public bool IsDead => _dead;

        public void Configure(float newMaxDetect, float newCastCooldown, float newProjectileDamage,
            float newProjectileSpeed)
        {
            detectRadius = newMaxDetect;
            castCooldown = newCastCooldown;
            projectileDamage = newProjectileDamage;
            projectileSpeed = newProjectileSpeed;
        }

        /// <summary>
        /// Same reasoning as the zombie: Start never runs on a client whose brain was put to sleep
        /// before it, and the muttering has to keep working there.
        /// </summary>
        void Awake()
        {
            _voice = EnemyVoice.Attach(gameObject, EnemyVoice.Kind.Magician);
        }

        void Start()
        {
            if (sightBlockers == 0)
                sightBlockers = DefaultSightBlockers();

            _animators = GetComponentsInChildren<Animator>(true);
            _ragdoll = GetComponent<MagicianRagdoll>();

#if CMPSETUP_COMPLETE
            var runner = NetworkCombatHooks.FindRunner();
            if (runner != null && runner.IsRunning && !runner.IsSharedModeMasterClient)
            {
                DisableAsProxy();
                return;
            }
#endif

            _health = GetComponent<ZombieHealth>();
            if (_health != null)
            {
                _health.OnDied += Die;
                _health.OnHurt += OnHurt;
            }

            _brain = StartCoroutine(CastLoop());
        }

        void OnDestroy()
        {
            if (_health == null)
                return;
            _health.OnDied -= Die;
            _health.OnHurt -= OnHurt;
        }

        void OnHurt(float amount)
        {
            GameSfx.PlayShared(SfxId.MagicianHurt, transform.position, transform);
        }

        /// <summary>Called on clients that do not own this magician; replicated state takes over.</summary>
        public void DisableAsProxy()
        {
            if (_brain != null)
            {
                StopCoroutine(_brain);
                _brain = null;
            }
            enabled = false;
        }

        #region Brain

        IEnumerator CastLoop()
        {
            // Staggered so a cluster of casters does not fire in perfect unison.
            yield return new WaitForSeconds(Random.Range(0f, 0.6f));
            SetBool(AimParam, false);

            while (!_dead)
            {
                var target = FindTarget();

                if (!target.IsValid)
                {
                    SetBool(AimParam, false);
                    yield return new WaitForSeconds(0.3f);
                    continue;
                }

                SetBool(AimParam, true);
                FaceTarget(target.Position);

                if (Time.time >= _nextCastTime && FacingTarget(target.Position))
                {
                    yield return Cast(target);
                    continue;
                }

                yield return null;
            }
        }

        IEnumerator Cast(Target target)
        {
            _nextCastTime = Time.time + castCooldown;
            SetTrigger(CastParam);
            // The caster's own effort, at the start of the wind-up. The bolt leaving the hand has
            // its own sound, fired from MagicProjectileFx once the wind-up finishes.
            GameSfx.PlayShared(SfxId.MagicianCast, transform.position, transform);

            // Keep tracking through the wind-up so the shot leads a moving player a little.
            float windup = Mathf.Max(0.05f, castWindup);
            float elapsed = 0f;
            while (elapsed < windup && !_dead)
            {
                if (target.IsValid)
                    FaceTarget(target.Position);
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (_dead || !target.IsValid)
                yield break;

            var origin = CastOrigin();
            var dir = (target.Position + Vector3.up * 0.9f - origin).normalized;
            MagicProjectileFx.Fire(origin, dir, projectileSpeed, projectileDamage, gameObject);
        }

        Vector3 CastOrigin()
        {
            return transform.TransformPoint(castHandOffset);
        }

        void Die()
        {
            if (_dead)
                return;
            _dead = true;

            if (_brain != null)
            {
                StopCoroutine(_brain);
                _brain = null;
            }

            GameSfx.PlayShared(SfxId.MagicianDeath, transform.position, transform);
            if (_voice != null)
                _voice.Silence();

            // Ragdoll is the whole point of the death: a caster shot off a ledge should crumple and
            // fall. Disable the animator first so it stops fighting the physics.
            SetBool(DeadParam, true);
            ReleaseRagdoll(fromFront: true);

            enabled = false;
            if (corpseLingerSeconds > 0f)
                Destroy(gameObject, corpseLingerSeconds);
        }

        /// <summary>Applied on clients when the master reports this magician dead.</summary>
        public void ApplyRemoteDeath()
        {
            if (_dead)
                return;
            _dead = true;

            // The death cry itself comes from the master through the relay; only the ambient
            // muttering has to be stopped locally.
            if (_voice == null)
                _voice = GetComponent<EnemyVoice>();
            if (_voice != null)
                _voice.Silence();

            if (_animators == null)
                _animators = GetComponentsInChildren<Animator>(true);
            SetBool(DeadParam, true);
            ReleaseRagdoll(fromFront: true);

            if (corpseLingerSeconds > 0f)
                Destroy(gameObject, corpseLingerSeconds);
        }

        void ReleaseRagdoll(bool fromFront)
        {
            if (_ragdoll == null)
                _ragdoll = GetComponent<MagicianRagdoll>();
            if (_ragdoll != null)
                _ragdoll.Release();
        }

        bool OwnsBrain()
        {
#if CMPSETUP_COMPLETE
            if (Time.realtimeSinceStartup < _nextOwnershipCheck)
                return true;
            _nextOwnershipCheck = Time.realtimeSinceStartup + 1f;

            var runner = NetworkCombatHooks.FindRunner();
            if (runner == null || !runner.IsRunning)
                return true;
            return runner.IsSharedModeMasterClient;
#else
            return true;
#endif
        }

        #endregion

        #region Targeting

        readonly struct Target
        {
            readonly FpsNetworkBridge _bridge;
            readonly PlayerStats _stats;
            readonly Transform _root;

            public Target(FpsNetworkBridge bridge, PlayerStats stats, Transform root)
            {
                _bridge = bridge;
                _stats = stats;
                _root = root;
            }

            public bool IsValid => _root != null;

            public Vector3 Position
            {
                get
                {
                    if (_bridge != null && _bridge.Object != null && _bridge.Object.IsValid)
                        return _bridge.GetNetworkAnchorPosition();
                    return _root != null ? _root.position : Vector3.zero;
                }
            }
        }

        Target FindTarget()
        {
            Target best = default;
            float bestDistance = float.MaxValue;

            var bridges = FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None);
            if (bridges != null && bridges.Length > 0)
            {
                foreach (var bridge in bridges)
                {
                    if (bridge == null || bridge.IsDead || bridge.Object == null || !bridge.Object.IsValid)
                        continue;
                    var anchor = bridge.GetNetworkAnchorPosition();
                    if (!InRange(anchor, out float d) || d >= bestDistance)
                        continue;
                    bestDistance = d;
                    best = new Target(bridge, null, bridge.transform);
                }
                return best;
            }

            foreach (var stats in FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
            {
                if (stats == null || stats.IsDead || !stats.gameObject.activeInHierarchy)
                    continue;
                if (!InRange(stats.transform.position, out float d) || d >= bestDistance)
                    continue;
                bestDistance = d;
                best = new Target(null, stats, stats.transform);
            }

            return best;
        }

        bool InRange(Vector3 position, out float distance)
        {
            var flat = position - transform.position;
            flat.y = 0f;
            distance = flat.magnitude;

            if (distance > detectRadius)
                return false;
            if (Mathf.Abs(position.y - transform.position.y) > verticalReach)
                return false;
            return HasLineOfSight(position);
        }

        bool HasLineOfSight(Vector3 position)
        {
            var from = transform.position + Vector3.up * eyeHeight;
            var to = position + Vector3.up * 1.1f;
            return !Physics.Linecast(from, to, sightBlockers, QueryTriggerInteraction.Ignore);
        }

        static int DefaultSightBlockers()
        {
            int transparent = LayerMask.GetMask(
                "Ignore Raycast", "TransparentFX", "UI", "UITop", "Water",
                "Weapons", "Player", "Enemy", "Animal", "Item", "PostProcessing", "Effects");
            return ~transparent;
        }

        void FaceTarget(Vector3 position)
        {
            var flat = position - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f)
                return;
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, Quaternion.LookRotation(flat), turnSpeedDegrees * Time.deltaTime);
        }

        bool FacingTarget(Vector3 position)
        {
            var flat = position - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f)
                return true;
            return Vector3.Angle(transform.forward, flat) < 25f;
        }

        #endregion

        #region Animator plumbing

        void SetBool(string param, bool value)
        {
            if (_animators == null)
                return;
            foreach (var a in _animators)
                if (a != null && a.isActiveAndEnabled)
                    a.SetBool(param, value);
        }

        void SetTrigger(string param)
        {
            if (_animators == null)
                return;
            foreach (var a in _animators)
                if (a != null && a.isActiveAndEnabled)
                    a.SetTrigger(param);
        }

        void Update()
        {
            // Cheap continuous ownership gate, same reasoning as the zombie: a magician that spawned
            // before the session came up must hand its brain to the master once it learns it is not
            // the master, or two machines cast at cross purposes.
            if (_dead || _brain == null)
                return;
            if (!OwnsBrain())
                DisableAsProxy();
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.6f, 0.3f, 0.9f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, detectRadius);
            Gizmos.color = new Color(0.9f, 0.7f, 0.2f, 0.8f);
            var o = Application.isPlaying ? CastOrigin() : transform.TransformPoint(castHandOffset);
            Gizmos.DrawWireSphere(o, 0.15f);
        }

        #endregion
    }
}
