using System.Collections;
using System.Collections.Generic;
using cowsins;
using UnityEngine;
using UnityEngine.AI;

namespace CollarCali
{
    /// <summary>
    /// Melee zombie. Shambles toward the nearest living player, breaks into a short run only once
    /// it is close, and swings when in reach. Deliberately slower than a sprinting player so the
    /// pressure comes from being cornered rather than from being outrun.
    ///
    /// In a session only the master client runs this - remote copies are put to sleep by
    /// NetworkWorldActor.DisableLocalBrain and are driven entirely by replicated transform and
    /// animator state.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class ZombieEnemy : MonoBehaviour
    {
        // Animator parameters, matched by ZombiePrefabBuilder.
        const string SpeedParam = "Speed";
        const string AttackParam = "Attack";
        const string ScreamParam = "Scream";
        const string DeadParam = "Dead";

        [Header("Senses")]
        /// <summary>Range and line of sight are only needed to NOTICE a player.</summary>
        [SerializeField] float detectRadius = 22f;

        /// <summary>
        /// Once a zombie has noticed you it keeps coming: no leash, no line-of-sight requirement.
        /// Outrunning one only buys distance, never safety. Turn this off if you want them to give
        /// up and go back to idle when you break away.
        /// </summary>
        [SerializeField] bool relentless = true;

        /// <summary>Only applies while acquiring, so it will not notice players on other floors.</summary>
        [SerializeField] float verticalTolerance = 4f;

        [Header("Movement")]
        /// <summary>The only movement speed - zombies idle or run, they never walk.</summary>
        [SerializeField] float runSpeed = 3.6f;
        [SerializeField] float turnSpeedDegrees = 260f;

        [Header("Attack")]
        [SerializeField] float attackRange = 2.1f;
        [SerializeField] float attackDamage = 12f;
        [SerializeField] float attackCooldown = 0.85f;

        /// <summary>Delay from the swing starting to the damage landing, so the hit reads.</summary>
        [SerializeField] float attackWindup = 0.28f;

        /// <summary>
        /// Extra reach at the moment of impact. The swing keeps closing during the wind-up, but a
        /// player strafing around the zombie can still drift a little past the base range; this is
        /// what stops a moving target from taking zero damage.
        /// </summary>
        [SerializeField] float attackReachBonus = 0.8f;

        [Header("Death")]
        [SerializeField] float corpseLingerSeconds = 10f;

        [Header("Line of sight")]
        /// <summary>Geometry that blocks sight. Without this a zombie hunts through walls.</summary>
        [SerializeField] LayerMask sightBlockers = 0;
        [SerializeField] float eyeHeight = 1.6f;

        NavMeshAgent _agent;
        Animator[] _animators;
        ZombieHealth _health;
        Coroutine _brain;
        EnemyVoice _voice;

        /// <summary>
        /// Crawlers share this brain but not its voice - a legless one drags rather than shambles.
        /// Resolved from the name because that is what ZombieSpawner and ZombiePrefabBuilder set,
        /// and it avoids adding a serialised flag the builders would both have to learn to write.
        /// </summary>
        bool _crawler;

        bool _dead;
        bool _hunting;
        float _nextAttackTime;
        float _currentSpeed01;
        float _lastSpeedWrite;

        public bool IsDead => _dead;

        /// <summary>Applied by ZombiePrefabBuilder and ZombieSpawner to make variants.</summary>
        public void Configure(float newRunSpeed, float newAttackRange, float newAttackDamage,
            float newDetectRadius)
        {
            runSpeed = newRunSpeed;
            attackRange = newAttackRange;
            attackDamage = newAttackDamage;
            detectRadius = newDetectRadius;
        }

        /// <summary>
        /// The voice is attached here rather than in Start because a proxy's brain is often already
        /// disabled by the network sweep before Start would run - and the ambient groans have to
        /// keep working on exactly those copies, since they are the ones the other player hears.
        /// </summary>
        void Awake()
        {
            _crawler = name.IndexOf("crawler", System.StringComparison.OrdinalIgnoreCase) >= 0;
            _voice = EnemyVoice.Attach(gameObject,
                _crawler ? EnemyVoice.Kind.ZombieCrawler : EnemyVoice.Kind.ZombieWalker);
        }

        void Start()
        {
            if (sightBlockers == 0)
                sightBlockers = DefaultSightBlockers();

            // Cached before the proxy gate below. Returning early without these left the agent
            // enabled on proxies (fighting the replicated transform) and made ApplyRemoteDeath
            // silently do nothing, because it had no animators to write the Dead flag to.
            _agent = GetComponent<NavMeshAgent>();
            _animators = GetComponentsInChildren<Animator>(true);

#if CMPSETUP_COMPLETE
            // The scene-wide sweep that sleeps remote brains runs once at startup, so a zombie
            // spawned afterwards can miss it and briefly run its brain on every client - which
            // means several clients each firing the same melee damage RPC. Gate on arrival.
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

            if (_agent != null)
            {
                ConfigureAgent(_agent, runSpeed, attackRange);
                TryPlaceOnNavMesh();
            }

            _brain = StartCoroutine(HuntLoop());
        }

        void OnDestroy()
        {
            if (_health == null)
                return;
            _health.OnDied -= Die;
            _health.OnHurt -= OnHurt;
        }

        /// <summary>
        /// Applied on clients when the master reports this zombie dead. The local Die() only ever
        /// runs on the owner, so without this the corpse stays upright and bullet-absorbing on
        /// every other machine.
        /// </summary>
        public void ApplyRemoteDeath()
        {
            if (_dead)
                return;
            _dead = true;

            // Only the groaning is stopped here. The death cry itself arrives from the master
            // through the relay, so playing one locally as well would double it.
            if (_voice == null)
                _voice = GetComponent<EnemyVoice>();
            if (_voice != null)
                _voice.Silence();

            if (_animators == null)
                _animators = GetComponentsInChildren<Animator>(true);
            SetBool(DeadParam, true);

            foreach (var col in GetComponentsInChildren<Collider>(true))
            {
                if (col != null)
                    col.enabled = false;
            }

            if (corpseLingerSeconds > 0f)
                Destroy(gameObject, corpseLingerSeconds);
        }

        public void DisableAsProxy()
        {
            if (_brain != null)
            {
                StopCoroutine(_brain);
                _brain = null;
            }

            // Resolved here rather than trusting the cached field: this can be called by the
            // network sweep before Start has run at all.
            if (_agent == null)
                _agent = GetComponent<NavMeshAgent>();
            if (_agent != null)
                _agent.enabled = false;

            enabled = false;
        }

        #region Brain

        IEnumerator HuntLoop()
        {
            // Staggered so a crowd spawned on the same frame does not re-path in lockstep.
            yield return new WaitForSeconds(Random.Range(0f, 0.4f));

            while (!_dead)
            {
                // Re-checked continuously, not just at Start. If the session was not up yet when
                // this zombie spawned, the Start gate could not fire - and until its actor bound,
                // this client would run its own brain, pick its own target and even send its own
                // melee damage. That is the one way two machines could see the same zombie chasing
                // different people, so it is closed here rather than left to bind latency.
                if (!OwnsBrain())
                {
                    DisableAsProxy();
                    yield break;
                }

                var target = FindTarget();

                if (!target.IsValid)
                {
                    // Reached only when no living player exists at all. Losing sight or being
                    // outrun no longer lands here - that is what made zombies stop dead the
                    // moment you opened up a gap.
                    _hunting = false;
                    Halt();
                    SetSpeed01(0f);
                    yield return new WaitForSeconds(0.25f);
                    continue;
                }

                // No detection pause any more - the moment a player is seen the zombie is already
                // running at them. The scream stalled them for a second (and slid them around while
                // the clip played), which read as sluggish.
                //
                // The alert shriek still fires on that first sighting, but only as audio: the
                // zombie keeps closing while it plays, so it warns the player without giving back
                // the free second of escape the old scream animation handed them.
                if (!_hunting)
                    GameSfx.PlayShared(_crawler ? SfxId.CrawlerAlert : SfxId.ZombieAlert,
                        transform.position, transform);
                _hunting = true;

                float distance = FlatDistance(transform.position, target.Position);

                if (distance <= attackRange && Time.time >= _nextAttackTime)
                {
                    yield return Swing(target);
                    continue;
                }

                Chase(target);
                yield return null;
            }
        }

        float _nextOwnershipCheck;

        /// <summary>
        /// True offline, and true in a session only on the master client. Throttled because
        /// FindRunner walks the runner list; a second of latency here is harmless since
        /// NetworkWorldActor also disables remote brains the moment an actor binds.
        /// </summary>
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

        void Chase(Target target)
        {
            if (_agent == null || !_agent.enabled)
                return;

            if (!_agent.isOnNavMesh)
            {
                // Off the mesh the agent silently refuses every command, which used to leave the
                // zombie spinning in a tight loop still playing its run animation.
                TryPlaceOnNavMesh();
                SetSpeed01(0f);
                return;
            }

            _agent.isStopped = false;
            _agent.speed = runSpeed;
            _agent.SetDestination(target.Position);

            SetSpeed01(1f);
            FaceTarget(target.Position);
        }

        IEnumerator Swing(Target target)
        {
            _nextAttackTime = Time.time + attackCooldown;
            SetTrigger(AttackParam);
            GameSfx.PlayShared(SfxId.ZombieAttackSwing, transform.position, transform);

            // Keep pressing into the target through the wind-up instead of freezing. A stationary
            // swing let a strafing player simply walk out of the short attack range before impact,
            // which is why moving players took no damage at all.
            float windup = Mathf.Max(0.05f, attackWindup);
            float elapsed = 0f;
            while (elapsed < windup && !_dead)
            {
                if (target.IsValid)
                {
                    FaceTarget(target.Position);
                    ChaseKeepingContact(target);
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (_dead)
                yield break;

            // Generous reach at impact so a circling player is still clipped, but a player who
            // genuinely sprinted clear is not - backing off far enough still dodges.
            if (target.IsValid &&
                FlatDistance(transform.position, target.Position) <= attackRange + attackReachBonus &&
                HasLineOfSight(target.Position))
            {
                target.Hurt(attackDamage);
                // Only on a connecting swing, so a missed swipe is audibly a miss. Placed at the
                // victim rather than the zombie - it is the sound of the hit landing on them.
                GameSfx.PlayShared(SfxId.ZombieAttackHit, target.Position);
            }

            Halt();
            SetSpeed01(0f);
            float recover = Mathf.Max(0f, attackCooldown - windup);
            yield return new WaitForSeconds(recover);
        }

        /// <summary>Light re-path during a swing so the zombie stays glued to a moving target.</summary>
        void ChaseKeepingContact(Target target)
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return;
            _agent.isStopped = false;
            _agent.speed = runSpeed;
            _agent.SetDestination(target.Position);
            SetSpeed01(1f);
        }

        void OnHurt(float amount)
        {
            // No stagger by design: a zombie that flinches on every pellet stops being a threat.
            // Being shot does pull it into hunting the shooter, though.
            _hunting = true;

            // The grunt is throttled in the library, not here: a shotgun applies its damage one
            // pellet at a time, and without that gate a single blast stacked eight of these.
            GameSfx.PlayShared(SfxId.ZombieHurt, transform.position, transform);
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

            GameSfx.PlayShared(SfxId.ZombieDeath, transform.position, transform);
            if (_voice != null)
                _voice.Silence();

            SetBool(DeadParam, true);
            SetSpeed01(0f);

            if (_agent != null)
            {
                if (_agent.enabled && _agent.isOnNavMesh)
                    _agent.isStopped = true;
                _agent.enabled = false;
            }

            // Hitboxes off so the corpse stops soaking shots. They are triggers, so this is about
            // damage, not about unblocking the player.
            foreach (var col in GetComponentsInChildren<Collider>(true))
            {
                if (col != null)
                    col.enabled = false;
            }

            enabled = false;
            if (corpseLingerSeconds > 0f)
                Destroy(gameObject, corpseLingerSeconds);
        }

        void Halt()
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return;
            _agent.isStopped = true;
            _agent.velocity = Vector3.zero;
        }

        #endregion

        #region Targeting

        /// <summary>
        /// Wraps whichever player representation exists: FpsNetworkBridge in a session, Cowsins
        /// PlayerStats when testing the scene on its own.
        /// </summary>
        readonly struct Target
        {
            readonly FpsNetworkBridge _bridge;
            readonly PlayerStats _stats;
            readonly Transform _root;
            readonly Vector3 _aim;

            public Target(FpsNetworkBridge bridge, PlayerStats stats, Transform root, Vector3 aim)
            {
                _bridge = bridge;
                _stats = stats;
                _root = root;
                _aim = aim;
            }

            public bool IsValid => _root != null;

            /// <summary>
            /// Live position where possible; the anchor captured at selection is the fallback for
            /// the frame the transform goes away mid-swing.
            /// </summary>
            public Vector3 Position
            {
                get
                {
                    if (_bridge != null && _bridge.Object != null && _bridge.Object.IsValid)
                        return _bridge.GetNetworkAnchorPosition();
                    return _root != null ? _root.position : _aim;
                }
            }

            public void Hurt(float amount)
            {
                if (_bridge != null)
                {
                    // Routes to the owning client so health stays authoritative there.
                    _bridge.TryRouteDamage(amount, false);
                    return;
                }

                if (_stats != null)
                    _stats.Damage(amount, false);
            }
        }

        Target FindTarget()
        {
            // Acquiring needs range, height and line of sight. Once hunting, a relentless zombie
            // drops all three and simply comes for the nearest living player - being outrun or
            // ducking behind cover no longer resets it to idle.
            bool acquiring = !(_hunting && relentless);
            Target best = default;
            float bestDistance = float.MaxValue;

            var bridges = FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None);
            if (bridges != null && bridges.Length > 0)
            {
                foreach (var bridge in bridges)
                {
                    if (bridge == null || bridge.IsDead || bridge.Object == null || !bridge.Object.IsValid)
                        continue;

                    // The visible character can sit offset from the network root, which is why
                    // the creep aims at the anchor rather than the transform.
                    var anchor = bridge.GetNetworkAnchorPosition();
                    if (!Reachable(anchor, acquiring, out float distance) || distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    best = new Target(bridge, null, bridge.transform, anchor);
                }

                return best;
            }

            foreach (var stats in FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
            {
                if (stats == null || stats.IsDead || !stats.gameObject.activeInHierarchy)
                    continue;

                var root = stats.transform;
                if (!Reachable(root.position, acquiring, out float distance) || distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = new Target(null, stats, root, root.position);
            }

            return best;
        }

        /// <summary>
        /// While acquiring, all three gates apply. While hunting relentlessly, every living player
        /// qualifies and only proximity decides which one.
        /// </summary>
        bool Reachable(Vector3 position, bool acquiring, out float distance)
        {
            distance = FlatDistance(transform.position, position);

            if (!acquiring)
                return true;

            if (distance > detectRadius)
                return false;
            if (Mathf.Abs(position.y - transform.position.y) > verticalTolerance)
                return false;
            return HasLineOfSight(position);
        }

        /// <summary>
        /// Keeps zombies from noticing - and swinging at - players through walls. Triggers are
        /// ignored so the world's own hitboxes and volumes do not read as cover.
        /// </summary>
        bool HasLineOfSight(Vector3 position)
        {
            var from = transform.position + Vector3.up * eyeHeight;
            var to = position + Vector3.up * 1.1f;
            return !Physics.Linecast(from, to, sightBlockers, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Everything except the things that must never count as cover. An allowlist of Default
        /// and Ground missed walls built on Object, Metal, Wood and the rest, which let zombies
        /// see straight through most real level geometry.
        /// </summary>
        static int DefaultSightBlockers()
        {
            int transparent = LayerMask.GetMask(
                "Ignore Raycast", "TransparentFX", "UI", "UITop", "Water",
                "Weapons", "Player", "Enemy", "Animal", "Item", "PostProcessing", "Effects");
            return ~transparent;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        void FaceTarget(Vector3 position)
        {
            var flat = position - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f)
                return;

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                Quaternion.LookRotation(flat),
                turnSpeedDegrees * Time.deltaTime);
        }

        void FaceTargetInstant(Vector3 position)
        {
            var flat = position - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f)
                return;
            transform.rotation = Quaternion.LookRotation(flat);
        }

        #endregion

        #region Plumbing

        void SetSpeed01(float value)
        {
            // Elapsed time, not Time.deltaTime: this is called from a coroutine that sometimes
            // yields for a whole second, and damping by one frame's worth per call left zombies
            // standing still while still playing the run animation for seconds afterwards.
            float now = Time.time;
            float delta = _lastSpeedWrite > 0f ? Mathf.Max(0f, now - _lastSpeedWrite) : Time.deltaTime;
            _lastSpeedWrite = now;

            _currentSpeed01 = Mathf.MoveTowards(_currentSpeed01, value, delta * 4f);
            SetFloat(SpeedParam, _currentSpeed01);
        }

        void SetFloat(string param, float value)
        {
            if (_animators == null)
                return;
            foreach (var anim in _animators)
            {
                if (anim != null && anim.isActiveAndEnabled)
                    anim.SetFloat(param, value);
            }
        }

        void SetBool(string param, bool value)
        {
            if (_animators == null)
                return;
            foreach (var anim in _animators)
            {
                if (anim != null && anim.isActiveAndEnabled)
                    anim.SetBool(param, value);
            }
        }

        void SetTrigger(string param)
        {
            if (_animators == null)
                return;
            foreach (var anim in _animators)
            {
                if (anim != null && anim.isActiveAndEnabled)
                    anim.SetTrigger(param);
            }
        }

        public static void ConfigureAgent(NavMeshAgent agent, float speed, float stoppingDistance)
        {
            if (agent == null)
                return;
            agent.speed = speed;
            agent.acceleration = 20f;
            agent.angularSpeed = 260f;
            agent.stoppingDistance = Mathf.Max(0.5f, stoppingDistance - 0.4f);
            // radius and height are deliberately left alone: they are authored per prefab, and
            // overwriting them here gave the crawler a 1.9m tall agent that could not fit under
            // the low geometry the variant exists for.
            agent.baseOffset = 0f;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
            // Low priority so a crowd of zombies shoves itself apart rather than pushing players.
            agent.avoidancePriority = 60;
            agent.autoRepath = true;
            agent.autoBraking = true;
        }

        void TryPlaceOnNavMesh()
        {
            if (_agent == null || !_agent.enabled || _agent.isOnNavMesh)
                return;
            if (NavMesh.SamplePosition(transform.position, out var hit, 20f, NavMesh.AllAreas))
                _agent.Warp(hit.position);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.6f, 0.9f, 0.3f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, detectRadius);
            Gizmos.color = new Color(0.95f, 0.2f, 0.2f, 0.8f);
            Gizmos.DrawWireSphere(transform.position, attackRange);
        }

        #endregion
    }
}
