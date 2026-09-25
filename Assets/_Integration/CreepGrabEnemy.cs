using System.Collections;
using System.Collections.Generic;
using cowsins;
using EmeraldAI;
using MalbersAnimations.Controller;
using UnityEngine;
using UnityEngine.AI;

namespace CollarCali
{
    /// <summary>
    /// Creep1 hunter: idle until a player enters the activate radius, then roar,
    /// sprint, grab, show the grabbed panel, drag, then wait to be activated again.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class CreepGrabEnemy : MonoBehaviour
    {
        const float GrabRange = 2.1f;
        const float GrabHoldSeconds = 7f;
        const float RunSpeed = 21.25f;
        const float GrabDamage = 8f;
        const int ShotsToDie = 20;
        const float CreepMaxHealth = 45f;   // HP model, so weapon damage matters like zombies
        const float CorpseLingerSeconds = 12f;
        const float EscapeMinDistance = 10f;
        const float EscapeMaxDistance = 26f;
        const string MovingParam = "Moving";
        const string RoarParam = "Roar";
        const string GrabParam = "Grab";
        const string DeadParam = "Dead";

        [SerializeField] float activateRadius = 15f;

        [Header("Stalker variant")]
        /// <summary>
        /// Turns this creep into the scripted stalker: no roar, sped-up animation, unkillable, and it
        /// leaves for good once it has dragged somebody. Set by CreepStalkerSpawner rather than by
        /// hand, so the ordinary scene creeps are untouched.
        /// </summary>
        [SerializeField] bool stalker;

        [Tooltip("Animation speed while closing on the victim. The whole point of the variant - a " +
                 "body moving several times too fast is unsettling in a way a fast walk is not.")]
        [SerializeField] float stalkerChaseAnimationSpeed = 4.5f;

        [Tooltip("How long it drags the victim before letting go. Longer than the normal creep on " +
                 "purpose: the drag has to cover enough ground to pull the team apart.")]
        [SerializeField] float stalkerDragSeconds = 12f;

        [Tooltip("Seconds it keeps running after releasing, before it vanishes.")]
        [SerializeField] float stalkerRetreatSeconds = 5f;

        public bool IsStalker => stalker;

        NavMeshAgent _agent;
        Animator[] _animators;
        ZombieHealth _health;
        EnemyVoice _voice;
        Transform _hold;
        SphereCollider _activateTrigger;
        Coroutine _brain;
        GrabVictim _heldVictim;
        bool _dead;
        bool _flopped;
        bool _activated;
        bool _idling;
        Transform _lastVictim;
        Vector3 _escapeDest;
        bool _hasEscape;
        float _stuckTimer;

        public static void Setup(GameObject creep)
        {
            if (creep == null || creep.GetComponent<CreepGrabEnemy>() != null)
                return;

            int enemyLayer = LayerMask.NameToLayer("Enemy");
            if (enemyLayer >= 0)
                creep.layer = enemyLayer;
            creep.tag = "Enemy";

            var agent = creep.GetComponent<NavMeshAgent>();
            if (agent == null)
                agent = creep.AddComponent<NavMeshAgent>();
            ConfigureAgent(agent);

            var box = creep.GetComponent<BoxCollider>();
            if (box == null)
                box = creep.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 1.1f, 0.15f);
            box.size = new Vector3(1.1f, 2.2f, 1.4f);
            box.isTrigger = true;

            var hitbox = creep.transform.Find("DamageHitbox");
            if (hitbox == null)
            {
                var go = new GameObject("DamageHitbox");
                hitbox = go.transform;
                hitbox.SetParent(creep.transform, false);
                hitbox.localPosition = new Vector3(0f, 1.1f, 0.15f);
            }

            hitbox.gameObject.layer = enemyLayer >= 0 ? enemyLayer : 0;
            hitbox.gameObject.tag = "BodyShot";
            var capsule = hitbox.GetComponent<CapsuleCollider>();
            if (capsule == null)
                capsule = hitbox.gameObject.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;
            capsule.height = 2.2f;
            capsule.radius = 0.55f;
            capsule.center = Vector3.zero;

            // ZombieHealth (not the old fixed-hits CreepHitsHealth): real HP so weapon damage
            // counts, and it brings the RVFX hit blood + gut-on-death for free, same as zombies.
            var health = creep.GetComponent<ZombieHealth>();
            if (health == null)
                health = creep.AddComponent<ZombieHealth>();
            health.Configure(CreepMaxHealth, 2f);
            if (hitbox.GetComponent<ZombieHitboxRelay>() == null)
            {
                var relay = hitbox.gameObject.AddComponent<ZombieHitboxRelay>();
                relay.Root = health;
            }

            var grab = creep.AddComponent<CreepGrabEnemy>();
            grab.EnsureActivateRadius();
        }

        /// <summary>
        /// The running sound is decided locally from movement, so it has to be installed on proxies
        /// too - and DisableAsProxy may already have switched this component off before Start.
        /// </summary>
        void Awake()
        {
            _voice = EnemyVoice.Attach(gameObject, EnemyVoice.Kind.Creep);
        }

        void Start()
        {
            _agent = GetComponent<NavMeshAgent>();
            BindAnimators();

            _health = GetComponent<ZombieHealth>();
            if (_health != null)
            {
                _health.OnDied += Die;
                _health.OnHurt += OnHurt;
            }

            _hold = transform.Find("HoldPoint");
            if (_hold == null)
            {
                var go = new GameObject("HoldPoint");
                _hold = go.transform;
                _hold.SetParent(transform, false);
                _hold.localPosition = new Vector3(0f, 1.05f, 0.2f);
            }

            EnsureActivateRadius();

            if (_agent != null)
            {
                ConfigureAgent(_agent);
                TryPlaceOnNavMesh();
            }

            var body = GetComponent<BoxCollider>();
            if (body != null)
                body.isTrigger = true;

            IgnoreOtherEnemyCollisions();
            _brain = StartCoroutine(HuntLoop());
        }

        public void DisableAsProxy()
        {
            if (_brain != null)
            {
                StopCoroutine(_brain);
                _brain = null;
            }

            if (_heldVictim != null)
                ReleaseHeldVictim();

            if (_agent != null)
                _agent.enabled = false;

            enabled = false;
        }

        void OnDestroy()
        {
            if (_health != null)
            {
                _health.OnDied -= Die;
                _health.OnHurt -= OnHurt;
            }
            if (_heldVictim != null)
                ReleaseHeldVictim();
        }

        void OnHurt(float amount)
        {
            GameSfx.PlayShared(SfxId.CreepHurt, transform.position, transform);
        }

        IEnumerator HuntLoop()
        {
            while (!_dead)
            {
                IgnoreOtherEnemyCollisions();
                if (!_activated && FindNextPlayer() != null)
                    _activated = true;
                if (!_activated)
                {
                    IdleInPlace();
                    yield return null;
                    continue;
                }

                _idling = false;
                var victim = FindNextPlayer();
                if (victim == null)
                {
                    _activated = false;
                    continue;
                }

                if (stalker)
                {
                    // No roar and no wind-up at all. The ordinary creep announces itself and gives
                    // you 2.2 seconds to react; this one is meant to be on you before you know it is
                    // there, so it goes straight into the chase from wherever it spawned unseen.
                    SetSpeed(RunSpeed);
                    SetAnimatorSpeed(stalkerChaseAnimationSpeed);
                }
                else
                {
                    FireTrigger(RoarParam);
                    GameSfx.PlayShared(SfxId.CreepRoar, transform.position, transform);
                    SetMoving(false);
                    HaltAgent();             // was sliding: the agent kept its path during the roar
                    SetSpeed(RunSpeed);
                    Face(victim.Position);
                    yield return new WaitForSeconds(2.2f);
                    if (_dead)
                        yield break;
                }

                yield return ChaseAndGrab(victim);
                if (_dead)
                    yield break;

                if (stalker)
                {
                    // Its work is done: it had its victim and dragged them off. Leaving rather than
                    // hunting again is what makes it an event instead of a monster in the room.
                    yield return RetreatAndVanish();
                    yield break;
                }

                SetMoving(false);
                yield return new WaitForSeconds(1.5f);
                _activated = FindNextPlayer() != null;
            }
        }

        void IdleInPlace()
        {
            if (!_idling)
            {
                _idling = true;
                SetMoving(false);
            }

            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                if (_agent.hasPath)
                    _agent.ResetPath();
            }
        }

        public void NotifyPlayerEnteredActivateRadius()
        {
            if (!_dead)
                _activated = true;
        }

        void SetSpeed(float speed)
        {
            if (_agent != null)
                _agent.speed = speed;
        }

        IEnumerator ChaseAndGrab(GrabVictim victim)
        {
            SetSpeed(RunSpeed);
            SetMoving(true);
            while (!_dead && victim.IsValid)
            {
                TryPlaceOnNavMesh();
                if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
                {
                    _agent.updateRotation = true;
                    _agent.isStopped = false;
                    if (NavMesh.SamplePosition(victim.Position, out var dest, 6f, NavMesh.AllAreas))
                        _agent.SetDestination(dest.position);
                }

                float dist = FlatDistance(transform.position, victim.Position);
                if (dist <= GrabRange)
                {
                    yield return GrabAndDrag(victim);
                    yield break;
                }

                yield return null;
            }
        }

        IEnumerator GrabAndDrag(GrabVictim victim)
        {
            HaltAgent();

            FireTrigger(GrabParam);
            GameSfx.PlayShared(SfxId.CreepGrab, transform.position, transform);
            Face(victim.Position);
            yield return new WaitForSeconds(0.35f);
            if (_dead || !victim.IsValid)
                yield break;

            victim.Lock();
            bool attached;
            if (victim.Bridge != null)
            {
                var actor = MultiplayerSessionBootstrap.EnsureActorFor(gameObject, NetworkWorldKind.Creep);
                attached = actor != null && actor.NotifyGrab(victim.Bridge, true);
                // #region agent log
                AgentDebugLog.Write("C1", "CreepGrabEnemy.GrabAndDrag", "network_grab",
                    "{\"actorFound\":" + (actor != null ? "true" : "false") +
                    ",\"attached\":" + (attached ? "true" : "false") +
                    ",\"creep\":\"" + name + "\"" +
                    ",\"wantedKey\":" + NetworkWorldActor.MakeKey(transform) +
                    ",\"actors\":" + NetworkWorldActor.DescribeActors() + "}");
                // #endregion
            }
            else
            {
                victim.Attach(_hold);
                attached = true;
            }

            // Showing the panel when the victim was never actually attached made the grab look
            // like it worked while the player stayed free.
            if (!attached)
                yield break;

            victim.Hurt(GrabDamage);
            _heldVictim = victim;
            _lastVictim = victim.Root;
            // Networked victims get the overlay on their own client via RPC_SetCreepGrab; showing it
            // here would put it on the master (host) even when a remote client was the one grabbed.
            // Only the offline / local-victim path needs the master to show it.
            if (victim.Bridge == null)
                CreepGrabbedPanel.Show();
            IgnoreVictimCollisions(victim, true);
            SetMoving(true);
            BeginNavEscape();

            float held = 0f;
            try
            {
                // The stalker drags for longer: the point of its grab is to haul a player far enough
                // that the team-distance rule starts pressuring the other one to follow.
                float holdLimit = stalker ? stalkerDragSeconds : GrabHoldSeconds;

                // Back to normal speed for the drag. The sped-up animation is for the approach - kept
                // up while hauling somebody it just looks like the clip is broken.
                if (stalker)
                    SetAnimatorSpeed(1f);

                while (!_dead && victim.IsValid && held < holdLimit)
                {
                    held += Time.deltaTime;
                    KeepNavEscape();
                    if (victim.Bridge == null)
                        victim.KeepOnNavMesh(_hold, _agent);
                    yield return null;
                }
            }
            finally
            {
                IgnoreVictimCollisions(victim, false);
                ReleaseHeldVictim();
            }
        }

        void BeginNavEscape()
        {
            _hasEscape = false;
            _stuckTimer = 0f;
            TryPlaceOnNavMesh();
            if (_agent == null || !_agent.enabled)
                return;

            _agent.updateRotation = true;
            _agent.updatePosition = true;
            _agent.autoRepath = true;
            _agent.autoBraking = false;
            _agent.stoppingDistance = 0.6f;
            _agent.isStopped = false;
            SetSpeed(RunSpeed);
            RetargetEscape();
        }

        void KeepNavEscape()
        {
            TryPlaceOnNavMesh();
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return;

            _agent.isStopped = false;

            bool stuck = _agent.velocity.sqrMagnitude < 0.2f;
            _stuckTimer = stuck ? _stuckTimer + Time.deltaTime : 0f;

            bool needNew = !_hasEscape
                || !_agent.hasPath
                || _agent.pathStatus != NavMeshPathStatus.PathComplete
                || _agent.remainingDistance <= 1.25f
                || _stuckTimer > 0.35f;

            if (needNew)
                RetargetEscape();
        }

        void RetargetEscape()
        {
            if (!TryPickEscapeDestination(out var dest))
                return;

            _escapeDest = dest;
            _hasEscape = true;
            _stuckTimer = 0f;
            _agent.ResetPath();
            _agent.SetDestination(dest);
        }

        bool TryPickEscapeDestination(out Vector3 dest)
        {
            dest = transform.position;
            var start = transform.position;
            if (NavMesh.SamplePosition(start, out var startHit, 6f, NavMesh.AllAreas))
                start = startHit.position;

            float best = 0f;
            bool found = false;
            var path = new NavMeshPath();

            for (int i = 0; i < 16; i++)
            {
                var dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                var end = start + dir * EscapeMaxDistance;
                bool blocked = NavMesh.Raycast(start, end, out var rayHit, NavMesh.AllAreas);
                var candidate = blocked ? rayHit.position : end;
                if (NavMesh.SamplePosition(candidate, out var sampled, 3f, NavMesh.AllAreas))
                    candidate = sampled.position;

                float dist = Vector3.Distance(start, candidate);
                if (dist < EscapeMinDistance && i < 15)
                    continue;
                if (!NavMesh.CalculatePath(start, candidate, NavMesh.AllAreas, path))
                    continue;
                if (path.status != NavMeshPathStatus.PathComplete)
                    continue;

                if (dist > best)
                {
                    best = dist;
                    dest = candidate;
                    found = true;
                }
            }

            return found;
        }

        static void ConfigureAgent(NavMeshAgent agent)
        {
            if (agent == null)
                return;
            agent.speed = RunSpeed;
            agent.acceleration = 80f;
            agent.angularSpeed = 360f;
            agent.stoppingDistance = 1.4f;
            agent.radius = 0.55f;
            agent.height = 2.2f;
            agent.baseOffset = 0f;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            agent.avoidancePriority = 30;
            agent.autoRepath = true;
            agent.autoBraking = false;
        }

        void TryPlaceOnNavMesh()
        {
            if (_agent == null || !_agent.enabled || _agent.isOnNavMesh)
                return;
            if (NavMesh.SamplePosition(transform.position, out var hit, 20f, NavMesh.AllAreas))
                _agent.Warp(hit.position);
        }

        void IgnoreVictimCollisions(GrabVictim victim, bool ignore)
        {
            if (victim?.Root == null)
                return;
            var mine = GetComponentsInChildren<Collider>(true);
            var theirs = victim.Root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < mine.Length; i++)
            {
                if (mine[i] == null || mine[i].isTrigger)
                    continue;
                for (int j = 0; j < theirs.Length; j++)
                {
                    if (theirs[j] == null || theirs[j].isTrigger)
                        continue;
                    Physics.IgnoreCollision(mine[i], theirs[j], ignore);
                }
            }
        }

        GrabVictim FindNextPlayer()
        {
            var others = new List<GrabVictim>();
            GrabVictim last = null;

            var bridges = FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None);
            if (bridges != null && bridges.Length > 0)
            {
                foreach (var bridge in bridges)
                {
                    if (bridge == null || bridge.IsDead || bridge.Object == null || !bridge.Object.IsValid)
                        continue;
                    var candidate = GrabVictim.FromBridge(bridge);
                    if (FlatDistance(transform.position, candidate.Position) > activateRadius)
                        continue;
                    if (_lastVictim != null && candidate.Root == _lastVictim)
                        last = candidate;
                    else
                        others.Add(candidate);
                }
            }
            else
            {
                foreach (var stats in FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
                {
                    if (stats == null || stats.IsDead || !stats.gameObject.activeInHierarchy)
                        continue;
                    var candidate = GrabVictim.FromFps(stats);
                    if (FlatDistance(transform.position, candidate.Position) > activateRadius)
                        continue;
                    if (_lastVictim != null && candidate.Root == _lastVictim)
                        last = candidate;
                    else
                        others.Add(candidate);
                }

                foreach (var dual in FindObjectsByType<DualPlayerController>(FindObjectsSortMode.None))
                {
                    if (dual == null || !dual.IsThirdPerson || dual.Animal == null)
                        continue;
                    var root = dual.Animal.transform;
                    if (!root.gameObject.activeInHierarchy)
                        continue;
                    if (FlatDistance(transform.position, root.position) > activateRadius)
                        continue;
                    var candidate = GrabVictim.FromSteve(dual);
                    if (_lastVictim != null && candidate.Root == _lastVictim)
                        last = candidate;
                    else
                        others.Add(candidate);
                }
            }

            if (others.Count > 0)
            {
                others.Sort((a, b) =>
                    FlatDistance(transform.position, a.Position)
                        .CompareTo(FlatDistance(transform.position, b.Position)));
                return others[0];
            }

            return last;
        }

        void IgnoreOtherEnemyCollisions()
        {
            var mine = GetComponentsInChildren<Collider>(true);
            foreach (var ai in FindObjectsByType<EmeraldSystem>(FindObjectsSortMode.None))
            {
                if (ai == null)
                    continue;
                foreach (var other in ai.GetComponentsInChildren<Collider>(true))
                {
                    if (other == null || other.isTrigger)
                        continue;
                    foreach (var col in mine)
                    {
                        if (col == null || col.isTrigger)
                            continue;
                        Physics.IgnoreCollision(col, other, true);
                    }
                }
            }
        }

        void Die()
        {
            if (_dead)
                return;
            _dead = true;
            _activated = false;
            if (_brain != null)
                StopCoroutine(_brain);

            GameSfx.PlayShared(SfxId.CreepDeath, transform.position, transform);
            ReleaseHeldVictim();

            SetMoving(false);
            Flop(physical: true);
        }

        /// <summary>
        /// Simple physics-flop death. Rather than play the creep's death clip (its generic rig
        /// T-posed on that state) the animator is switched off so the mesh freezes in its last
        /// pose, and the whole body is dropped onto one capsule rigidbody so it topples and can
        /// fall off ledges. Reliable on any rig. Runs on the owner (Die) and on clients
        /// (ApplyFlopDeath), so the corpse falls on every screen.
        /// </summary>
        void Flop(bool physical)
        {
            if (_flopped)
                return;
            _flopped = true;

            // Both death paths land here - the owner's Die and the clients' ApplyFlopDeath - so this
            // is where the chase loop is guaranteed to be cut, whichever machine this is.
            if (_voice == null)
                _voice = GetComponent<EnemyVoice>();
            if (_voice != null)
                _voice.Silence();

            if (_brain != null)
            {
                StopCoroutine(_brain);
                _brain = null;
            }
            ReleaseHeldVictim();

            // Animator off so it stops driving the skeleton (and never shows the T-posed death state).
            if (_animators != null)
            {
                foreach (var a in _animators)
                    if (a != null)
                        a.enabled = false;
            }

            if (_agent != null)
            {
                if (_agent.enabled && _agent.isOnNavMesh)
                    _agent.isStopped = true;
                _agent.enabled = false;
            }

            // Trigger hitboxes off so the corpse stops catching shots and driving the grab trigger.
            foreach (var col in GetComponentsInChildren<Collider>(true))
            {
                if (col != null && col.isTrigger)
                    col.enabled = false;
            }

            // Only the owner runs the physics fall. On other clients NetworkWorldActor replicates
            // the falling root transform, so adding a second live rigidbody there would fight it and
            // make the corpse jitter - clients just show it dead (animator + hitboxes off) and follow.
            if (physical)
            {
                var capsule = gameObject.AddComponent<CapsuleCollider>();
                capsule.direction = 1;
                capsule.height = 2.0f;
                capsule.radius = 0.5f;
                capsule.center = new Vector3(0f, 1.0f, 0f);

                var rb = GetComponent<Rigidbody>();
                if (rb == null)
                    rb = gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                // A shove so it topples over instead of sinking straight down in place.
                rb.AddForce(-transform.forward * 1.5f + Vector3.up * 1.0f, ForceMode.VelocityChange);
                rb.AddTorque(transform.right * 4f, ForceMode.VelocityChange);
            }

            if (CorpseLingerSeconds > 0f)
                Destroy(gameObject, CorpseLingerSeconds);
        }

        /// <summary>Called on clients when the master reports this creep dead.</summary>
        public void ApplyFlopDeath()
        {
            if (_dead && _flopped)
                return;
            _dead = true;
            if (_animators == null)
                _animators = GetComponentsInChildren<Animator>(true);
            Flop(physical: false);
        }

        void HaltAgent()
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return;
            _agent.isStopped = true;
            if (_agent.hasPath)
                _agent.ResetPath();
            _agent.velocity = Vector3.zero;
        }

        void ReleaseHeldVictim()
        {
            bool networkedVictim = false;
            if (_heldVictim != null)
            {
                // Inside the null check so the many paths that call this without a victim in hand
                // do not each fire a release grunt.
                GameSfx.PlayShared(SfxId.CreepRelease, transform.position, transform);
                networkedVictim = _heldVictim.Bridge != null;
                if (networkedVictim)
                    NetworkWorldActor.FindFor(gameObject)?.NotifyGrab(_heldVictim.Bridge, false);
                _heldVictim.Release();
                _heldVictim = null;
            }

            // Networked victims hide the overlay on their own client (via RPC_SetCreepGrab false).
            // Only hide here for the offline / local-victim path.
            if (!networkedVictim)
                CreepGrabbedPanel.Hide();
        }

        void EnsureActivateRadius()
        {
            var existing = transform.Find("ActivateRadius");
            Transform slot = existing;
            if (slot == null)
            {
                var go = new GameObject("ActivateRadius");
                slot = go.transform;
                slot.SetParent(transform, false);
                slot.localPosition = new Vector3(0f, 1.1f, 0f);
            }

            _activateTrigger = slot.GetComponent<SphereCollider>();
            if (_activateTrigger == null)
                _activateTrigger = slot.gameObject.AddComponent<SphereCollider>();
            _activateTrigger.isTrigger = true;
            ApplyActivateRadiusToTrigger();

            int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycast >= 0)
                slot.gameObject.layer = ignoreRaycast;

            if (slot.GetComponent<CreepActivateRadius>() == null)
                slot.gameObject.AddComponent<CreepActivateRadius>();
        }

        void ApplyActivateRadiusToTrigger()
        {
            if (_activateTrigger == null)
                return;
            float scale = _activateTrigger.transform.lossyScale.x;
            if (scale < 0.01f)
                scale = 1f;
            _activateTrigger.radius = Mathf.Max(1f, activateRadius) / scale;
        }

        void OnValidate()
        {
            activateRadius = Mathf.Max(1f, activateRadius);
            ApplyActivateRadiusToTrigger();
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.85f, 0.15f, 0.15f, 0.9f);
            var center = transform.position + Vector3.up * 1.1f;
            Gizmos.DrawWireSphere(center, Mathf.Max(1f, activateRadius));
        }

        void BindAnimators()
        {
            _animators = GetComponentsInChildren<Animator>(true);
            var controller = Resources.Load<RuntimeAnimatorController>("CreepAnimator");
            if (_animators == null)
                return;
            for (int i = 0; i < _animators.Length; i++)
            {
                var animator = _animators[i];
                if (animator == null)
                    continue;
                animator.applyRootMotion = false;
                if (controller != null)
                    animator.runtimeAnimatorController = controller;
            }
        }

        void SetMoving(bool moving)
        {
            SetBool(MovingParam, moving);
        }

        /// <summary>
        /// Scales playback on every animator on the body.
        ///
        /// Speed is what sells this creature, not a different clip: the same walk run several times
        /// too fast reads as something moving wrongly, which is more unsettling than any animation
        /// the pack actually ships.
        /// </summary>
        void SetAnimatorSpeed(float speed)
        {
            if (_animators == null)
                return;

            foreach (var animator in _animators)
            {
                if (animator != null)
                    animator.speed = speed;
            }
        }

        /// <summary>
        /// Configures this creep as the scripted stalker. Called by the spawner right after Setup.
        ///
        /// Invulnerability is set on the health component rather than by removing it, because the
        /// grab, the hitboxes and the networked damage routing all go through ZombieHealth - taking
        /// it away would break the grab along with the damage.
        /// </summary>
        public void ConfigureAsStalker(float chaseAnimationSpeed, float dragSeconds, float retreatSeconds)
        {
            stalker = true;
            stalkerChaseAnimationSpeed = Mathf.Max(0.1f, chaseAnimationSpeed);
            stalkerDragSeconds = Mathf.Max(0.5f, dragSeconds);
            stalkerRetreatSeconds = Mathf.Max(0f, retreatSeconds);

            if (_health == null)
                _health = GetComponent<ZombieHealth>();
            if (_health != null)
                _health.SetInvulnerable(true);

            // A health bar over something that cannot be hurt just teaches the player to keep
            // shooting it.
            foreach (var bar in GetComponentsInChildren<ZombieHealthBar>(true))
            {
                if (bar != null)
                    bar.gameObject.SetActive(false);
            }

            _activated = true;
        }

        /// <summary>
        /// Runs away from the players and then removes itself.
        ///
        /// It keeps the NavMesh escape it was already using for the drag, so it leaves the way it
        /// came rather than walking through the level, and is destroyed rather than left standing
        /// somewhere out of sight burning an agent and a skinned mesh.
        /// </summary>
        IEnumerator RetreatAndVanish()
        {
            SetMoving(true);
            SetSpeed(RunSpeed);
            SetAnimatorSpeed(stalkerChaseAnimationSpeed);
            BeginNavEscape();

            float elapsed = 0f;
            while (elapsed < stalkerRetreatSeconds && !_dead)
            {
                elapsed += Time.deltaTime;
                KeepNavEscape();
                yield return null;
            }

            // Handed to whoever spawned it, so the removal can be broadcast - this creature never
            // dies, so the actor despawn that cleans up a killed zombie never fires for it and each
            // client would otherwise keep its copy forever. Destroying locally is the offline path,
            // where there is nobody to tell.
            if (StalkerFinished != null)
                StalkerFinished.Invoke();
            else
                Destroy(gameObject);
        }

        /// <summary>Raised on the owner once the stalker has finished retreating.</summary>
        public event System.Action StalkerFinished;

        void SetBool(string param, bool value)
        {
            if (_animators == null)
                return;
            for (int i = 0; i < _animators.Length; i++)
            {
                var animator = _animators[i];
                if (animator != null && animator.enabled && animator.isActiveAndEnabled)
                    animator.SetBool(param, value);
            }
        }

        void FireTrigger(string param)
        {
            if (_animators == null)
                return;
            for (int i = 0; i < _animators.Length; i++)
            {
                var animator = _animators[i];
                if (animator != null && animator.enabled && animator.isActiveAndEnabled)
                    animator.SetTrigger(param);
            }
        }

        void Face(Vector3 world)
        {
            var look = world - transform.position;
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(look), 0.35f);
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        sealed class GrabVictim
        {
            public Transform Root;
            public FpsNetworkBridge Bridge;
            PlayerControl _control;
            Rigidbody _rb;
            PlayerStats _stats;
            DualPlayerController _dual;
            bool _wasKinematic;
            Transform _oldParent;

            public Vector3 Position =>
                Bridge != null ? Bridge.GetNetworkAnchorPosition() : (Root != null ? Root.position : Vector3.zero);

            public bool IsValid =>
                Bridge != null
                    ? Bridge.Object != null && Bridge.Object.IsValid && !Bridge.IsDead
                    : Root != null && Root.gameObject.activeInHierarchy;

            public static GrabVictim FromFps(PlayerStats stats)
            {
                return new GrabVictim
                {
                    Root = stats.transform,
                    _stats = stats,
                    _control = stats.GetComponent<PlayerControl>(),
                    _rb = stats.GetComponent<Rigidbody>()
                };
            }

            public static GrabVictim FromSteve(DualPlayerController dual)
            {
                return new GrabVictim
                {
                    Root = dual.Animal.transform,
                    _dual = dual
                };
            }

            public static GrabVictim FromBridge(FpsNetworkBridge bridge)
            {
                Transform root = bridge.transform;
                if (bridge.DualPlayer != null && bridge.DualPlayer.IsThirdPerson && bridge.DualPlayer.SteveRoot != null)
                    root = bridge.DualPlayer.SteveRoot.transform;
                else if (bridge.DualPlayer != null && bridge.DualPlayer.FpsBody != null)
                    root = bridge.DualPlayer.FpsBody;

                return new GrabVictim
                {
                    Root = root,
                    Bridge = bridge
                };
            }

            public void Lock()
            {
                if (Bridge != null)
                    return;
                if (Root == null)
                    return;
                _oldParent = Root.parent;
                if (_control != null)
                    _control.LoseControl();
                if (_rb != null)
                {
                    _wasKinematic = _rb.isKinematic;
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;
                    _rb.isKinematic = true;
                }
            }

            public void Attach(Transform hold)
            {
                if (Bridge != null || Root == null)
                    return;
                Root.SetParent(hold, true);
                Root.localPosition = Vector3.zero;
            }

            public void KeepOnNavMesh(Transform hold, NavMeshAgent agent)
            {
                if (Bridge != null || Root == null)
                    return;
                if (Root.parent != hold)
                    Root.SetParent(hold, true);

                if (agent != null && agent.enabled && agent.isOnNavMesh)
                {
                    var pos = agent.nextPosition;
                    pos.y = hold.position.y;
                    Root.position = pos;
                    return;
                }

                Root.localPosition = Vector3.zero;
                if (NavMesh.SamplePosition(Root.position, out var hit, 3f, NavMesh.AllAreas))
                {
                    var snapped = hit.position;
                    snapped.y = Root.position.y;
                    Root.position = snapped;
                }
            }

            public void Hurt(float amount)
            {
                if (Bridge != null)
                {
                    Bridge.TryRouteDamage(amount, false);
                    return;
                }

                if (_stats != null)
                    _stats.Damage(amount, false);
            }

            public void Release()
            {
                if (Bridge != null || Root == null)
                    return;
                Root.SetParent(_oldParent, true);
                if (_control != null)
                    _control.CheckIfCanGrantControl();
                if (_rb != null)
                    _rb.isKinematic = _wasKinematic;

                var pos = Root.position;
                if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out var hit, 8f, ~0, QueryTriggerInteraction.Ignore))
                    pos.y = hit.point.y + 0.1f;
                Root.position = pos;
            }
        }
    }

    public class CreepActivateRadius : MonoBehaviour
    {
        CreepGrabEnemy _creep;

        void Awake()
        {
            _creep = GetComponentInParent<CreepGrabEnemy>();
        }

        void OnTriggerEnter(Collider other)
        {
            if (IsPlayerBody(other))
                _creep?.NotifyPlayerEnteredActivateRadius();
        }

        void OnTriggerStay(Collider other)
        {
            if (IsPlayerBody(other))
                _creep?.NotifyPlayerEnteredActivateRadius();
        }

        static bool IsPlayerBody(Collider other)
        {
            if (other == null)
                return false;

            var bridge = other.GetComponentInParent<FpsNetworkBridge>();
            if (bridge != null && bridge.Object != null && bridge.Object.IsValid && !bridge.IsDead)
                return true;

            if (other.isTrigger)
                return false;

            var dual = other.GetComponentInParent<DualPlayerController>();
            if (dual != null && dual.IsThirdPerson && dual.Animal != null)
            {
                if (dual.Animal.MainCollider != null)
                    return other == dual.Animal.MainCollider;
                return other.GetComponent<MAnimal>() != null;
            }

            var stats = other.GetComponentInParent<PlayerStats>();
            if (stats == null || stats.IsDead)
                return false;

            return other.GetComponent<PlayerStats>() != null || other is CapsuleCollider;
        }
    }

    public class CreepHitsHealthRelay : MonoBehaviour, cowsins.IDamageable
    {
        public CreepHitsHealth Root;

        public void Damage(float damage, bool isHeadshot)
        {
            if (Root == null)
                Root = GetComponentInParent<CreepHitsHealth>();
            Root?.Damage(damage, isHeadshot);
        }
    }
}
