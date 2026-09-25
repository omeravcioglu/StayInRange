using System.Collections.Generic;
using cowsins;
using EmeraldAI;
using Fusion;
using MalbersAnimations.Utilities;
using UnityEngine;
using UnityEngine.AI;

namespace CollarCali
{
    /// <summary>
    /// Master-simulated world object. Other clients disable brains and follow this NetworkTransform.
    /// </summary>
    [RequireComponent(typeof(NetworkTransform))]
    public class NetworkWorldActor : NetworkBehaviour
    {
        [Networked] public int SceneKey { get; set; }
        [Networked] public int Kind { get; set; }
        [Networked] public NetworkBool IsDead { get; set; }
        [Networked] public PlayerRef GrabbedPlayer { get; set; }
        [Networked] public int HitsLeft { get; set; }
        [Networked] public int AnimState { get; set; }
        [Networked] public float AnimTime { get; set; }

        static readonly Dictionary<GameObject, NetworkWorldActor> _bySource =
            new Dictionary<GameObject, NetworkWorldActor>();

        static Dictionary<int, GameObject> _sourcesByKey;
        static float _sourceCacheTime;

        const float BindRetrySeconds = 0.25f;

        GameObject _source;
        bool _brainsDisabled;
        bool _appliedDead;
        float _nextBindAttempt;

        public GameObject Source => _source;

        public override void Spawned()
        {
            if (_source == null)
                TryBindSource();
            if (!HasStateAuthority && _source != null)
                DisableLocalBrain(_source, (NetworkWorldKind)Kind);
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            SetSource(null);
        }

        public override void Render()
        {
            if (_source == null)
                TryBindSource();

            if (_source == null)
                return;

            if (HasStateAuthority)
                SyncDeathFromSource();
            else if (!_brainsDisabled)
            {
                DisableLocalBrain(_source, (NetworkWorldKind)Kind);
                _brainsDisabled = true;
            }

            if (!HasStateAuthority && IsDead && !_appliedDead)
            {
                _appliedDead = true;

                // Zombies run their own death instead of the Emerald ragdoll drop: the local Die()
                // only ever executes on the owner, so without this the corpse stays upright and
                // still absorbing shots on every other client.
                var kindNow = (NetworkWorldKind)Kind;
                if (kindNow == NetworkWorldKind.Zombie)
                {
                    var zombie = _source.GetComponent<ZombieEnemy>();
                    if (zombie != null)
                        zombie.ApplyRemoteDeath();
                }
                else if (kindNow == NetworkWorldKind.Magician)
                {
                    var magician = _source.GetComponent<MagicianEnemy>();
                    if (magician != null)
                        magician.ApplyRemoteDeath();
                }
                else if (kindNow == NetworkWorldKind.Creep)
                {
                    var creep = _source.GetComponent<CreepGrabEnemy>();
                    if (creep != null)
                        creep.ApplyFlopDeath();
                }
                else
                {
                    var fall = _source.GetComponent<Scene2EnemyDeathFall>();
                    fall?.Drop();
                }
            }
        }

        public override void FixedUpdateNetwork()
        {
            // A dead zombie destroys its own GameObject once the corpse has lingered. Retiring the
            // actor with it stops TryBindSource re-scanning the scene for it forever. Done here
            // rather than in Render() because Fusion is mid-dispatch over its behaviour list
            // during Render and does not support despawning from inside it.
            if (HasStateAuthority && _source == null && IsDead &&
                IsZombieHealthKind((NetworkWorldKind)Kind) &&
                Object != null && Object.IsValid && Runner != null)
            {
                Runner.Despawn(Object);
                return;
            }

            if (!HasStateAuthority || _source == null)
                return;
            CaptureAnimator();
        }

        void LateUpdate()
        {
            if (Object == null || !Object.IsValid || _source == null)
                return;

            var target = ResolveSyncTransform();
            if (target == null)
                return;

            if (HasStateAuthority)
            {
                transform.SetPositionAndRotation(target.position, target.rotation);
                return;
            }

            target.SetPositionAndRotation(transform.position, transform.rotation);
            ApplySyncedAnimator();
        }

        Transform ResolveSyncTransform()
        {
            var transformer = _source.GetComponent<MSimpleTransformer>()
                              ?? _source.GetComponentInChildren<MSimpleTransformer>(true);
            if (transformer != null && transformer.Object != null)
                return transformer.Object;
            return _source.transform;
        }

        void CaptureAnimator()
        {
            var anim = FirstHazardAnimator(_source);
            if (anim == null || !anim.isActiveAndEnabled)
                return;

            var info = anim.GetCurrentAnimatorStateInfo(0);
            AnimState = info.fullPathHash;
            AnimTime = info.normalizedTime;
        }

        void ApplySyncedAnimator()
        {
            if (AnimState == 0)
                return;

            foreach (var anim in _source.GetComponentsInChildren<Animator>(true))
            {
                if (anim == null || IsCharacterAnimator(anim))
                    continue;
                anim.speed = 0f;
                var info = anim.GetCurrentAnimatorStateInfo(0);
                if (info.fullPathHash != AnimState || Mathf.Abs(info.normalizedTime - AnimTime) > 0.02f)
                    anim.Play(AnimState, 0, AnimTime);
            }
        }

        static Animator FirstHazardAnimator(GameObject source)
        {
            foreach (var anim in source.GetComponentsInChildren<Animator>(true))
            {
                if (anim != null && !IsCharacterAnimator(anim))
                    return anim;
            }

            return null;
        }

        /// <summary>
        /// Must run from the spawn callback: Spawned() has already executed by the time
        /// Runner.Spawn returns, so a later SceneKey write never reaches other readers and
        /// FindFor can no longer match this actor to its scene object.
        /// </summary>
        public void InitializeSpawnState(GameObject source, int sceneKey, NetworkWorldKind kind)
        {
            SetSource(source);
            SceneKey = sceneKey;
            Kind = (int)kind;
        }

        public void BindSource(GameObject source, int sceneKey, NetworkWorldKind kind)
        {
            SetSource(source);
            if (HasStateAuthority)
            {
                SceneKey = sceneKey;
                Kind = (int)kind;
            }
        }

        void SetSource(GameObject source)
        {
            if (_source == source)
                return;

            if (_source != null)
                _bySource.Remove(_source);

            _source = source;

            if (_source != null)
                _bySource[_source] = this;
        }

        // #region agent log
        public static string DescribeActors()
        {
            var actors = FindObjectsByType<NetworkWorldActor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (int i = 0; i < actors.Length; i++)
            {
                var a = actors[i];
                if (a == null)
                    continue;
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"key\":").Append(a.SceneKey)
                  .Append(",\"kind\":").Append(a.Kind)
                  .Append(",\"src\":\"").Append(a._source != null ? a._source.name : "null")
                  .Append("\"}");
            }

            sb.Append(']');
            return sb.ToString();
        }
        // #endregion

        public Transform GetLocalHoldPoint()
        {
            if (_source == null)
                TryBindSource();
            if (_source == null)
                return null;

            var hold = _source.transform.Find("HoldPoint");
            if (hold != null)
                return hold;

            var go = new GameObject("HoldPoint");
            hold = go.transform;
            hold.SetParent(_source.transform, false);
            hold.localPosition = new Vector3(0f, 1.05f, 0.2f);
            return hold;
        }

        public void RequestDamage(float damage, bool isHeadshot)
        {
            if (!Object || !Object.IsValid)
                return;
            if (HasStateAuthority)
                ApplyDamage(damage, isHeadshot);
            else
                RPC_Damage(damage, isHeadshot);
        }

        /// <summary>Returns false when the grab could not be handed to the victim's owner.</summary>
        public bool NotifyGrab(FpsNetworkBridge victim, bool grabbed)
        {
            if (!HasStateAuthority || victim == null || victim.Object == null)
                return false;

            if (grabbed && GetLocalHoldPoint() == null)
                return false;

            GrabbedPlayer = grabbed ? victim.Object.InputAuthority : PlayerRef.None;
            victim.RPC_SetCreepGrab(Object.Id, grabbed);
            return true;
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        void RPC_Damage(float damage, bool isHeadshot)
        {
            ApplyDamage(damage, isHeadshot);
        }

        void ApplyDamage(float damage, bool isHeadshot)
        {
            if (_source == null)
                TryBindSource();
            if (_source == null || IsDead)
                return;

            var kind = (NetworkWorldKind)Kind;
            if (kind == NetworkWorldKind.Enemy)
            {
                foreach (var hits in _source.GetComponentsInChildren<CowsinsHitsEmeraldHealth>(true))
                {
                    hits.ApplyNetworkedDamage(damage, isHeadshot);
                    break;
                }

                var health = _source.GetComponent<EmeraldHealth>();
                IsDead = health != null && health.Health <= 0;
            }
            else if (IsZombieHealthKind(kind))
            {
                var zombieHealth = _source.GetComponent<ZombieHealth>();
                zombieHealth?.ApplyNetworkedDamage(damage, isHeadshot);
                // HitsLeft carries rounded HP for zombies and magicians alike; reusing it avoids
                // adding a networked field to a prefab every other actor kind already shares.
                HitsLeft = zombieHealth != null ? Mathf.CeilToInt(zombieHealth.Health) : HitsLeft;
                IsDead = zombieHealth != null && zombieHealth.IsDead;
            }
        }

        /// <summary>Kinds whose HP lives on a ZombieHealth component (zombies and magicians).</summary>
        static bool IsZombieHealthKind(NetworkWorldKind kind) =>
            kind == NetworkWorldKind.Zombie || kind == NetworkWorldKind.Magician ||
            kind == NetworkWorldKind.Creep;

        void SyncDeathFromSource()
        {
            var kind = (NetworkWorldKind)Kind;
            if (kind == NetworkWorldKind.Enemy)
            {
                var health = _source.GetComponent<EmeraldHealth>();
                var anim = _source.GetComponent<EmeraldSystem>();
                bool dead = (health != null && health.Health <= 0) ||
                            (anim != null && anim.AnimationComponent != null && anim.AnimationComponent.IsDead);
                IsDead = dead;
            }
            else if (IsZombieHealthKind(kind))
            {
                var zombieHealth = _source.GetComponent<ZombieHealth>();
                if (zombieHealth != null)
                {
                    HitsLeft = Mathf.CeilToInt(zombieHealth.Health);
                    IsDead = zombieHealth.IsDead;
                }
            }
        }

        void TryBindSource()
        {
            if (SceneKey == 0 || Time.unscaledTime < _nextBindAttempt)
                return;

            _nextBindAttempt = Time.unscaledTime + BindRetrySeconds;

            var found = FindSourceByKey(SceneKey);
            if (found != null)
                SetSource(found);
        }

        public static NetworkWorldActor FindFor(GameObject go)
        {
            if (go == null)
                return null;

            if (_bySource.TryGetValue(go, out var cached) && IsLive(cached))
                return cached;

            int key = MakeKey(go.transform);
            NetworkWorldActor byKey = null;

            foreach (var actor in FindObjectsByType<NetworkWorldActor>(FindObjectsSortMode.None))
            {
                if (!IsLive(actor))
                    continue;

                if (actor._source == null)
                    actor.TryBindSource();

                if (actor._source != null &&
                    (actor._source == go || go.transform.IsChildOf(actor._source.transform)))
                    return actor;

                if (actor.SceneKey == key)
                    byKey = actor;
            }

            if (byKey != null)
                byKey.SetSource(go);

            return byKey;
        }

        static bool IsLive(NetworkWorldActor actor)
        {
            return actor != null && actor.Object != null && actor.Object.IsValid;
        }

        public static int MakeKey(Transform t)
        {
            return t == null ? 0 : StableHash(MakePath(t));
        }

        public static string MakePath(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent)
                sb.Insert(0, '/').Insert(0, p.name);
            return sb.ToString();
        }

        /// <summary>
        /// FNV-1a over the hierarchy path. Sibling indices are deliberately excluded: runtime
        /// spawns and destroys at the scene root shift them, which made keys computed at lookup
        /// time disagree with the ones written at spawn time. string.GetHashCode is also avoided
        /// because it is not guaranteed to agree between two processes in a session.
        /// </summary>
        static int StableHash(string s)
        {
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < s.Length; i++)
                {
                    hash ^= s[i];
                    hash *= 16777619u;
                }

                int result = (int)hash;
                return result == 0 ? 1 : result;
            }
        }

        public static GameObject FindSourceByKey(int key)
        {
            if (_sourcesByKey == null || Time.unscaledTime - _sourceCacheTime > BindRetrySeconds)
                RebuildSourceCache();

            return _sourcesByKey.TryGetValue(key, out var go) && go != null ? go : null;
        }

        static void RebuildSourceCache()
        {
            _sourceCacheTime = Time.unscaledTime;

            if (_sourcesByKey == null)
                _sourcesByKey = new Dictionary<int, GameObject>();
            else
                _sourcesByKey.Clear();

            foreach (var ai in FindObjectsByType<EmeraldSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                CacheSource(ai);

            foreach (var creep in FindObjectsByType<CreepGrabEnemy>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                CacheSource(creep);

            var motion = new List<GameObject>();
            CollectMotionSources(motion);
            for (int i = 0; i < motion.Count; i++)
                CacheSource(motion[i].transform);

            // Last on purpose. Without this a zombie is never findable by key, so no actor can
            // bind to it on a client and remote zombies never follow the master - and caching it
            // after the motion sweep means that even if a zombie did leak into that sweep, the
            // Zombie entry still wins the key instead of a Trap entry that eats all damage.
            foreach (var zombie in FindObjectsByType<ZombieEnemy>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                CacheSource(zombie);

            foreach (var magician in FindObjectsByType<MagicianEnemy>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                CacheSource(magician);
        }

        static void CacheSource(Component c)
        {
            if (c != null)
                _sourcesByKey[MakeKey(c.transform)] = c.gameObject;
        }

        public static void DisableAllSceneBrains()
        {
            foreach (var ai in FindObjectsByType<EmeraldSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (ai != null)
                    DisableLocalBrain(ai.gameObject, NetworkWorldKind.Enemy);
            }

            foreach (var creep in FindObjectsByType<CreepGrabEnemy>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (creep != null)
                    DisableLocalBrain(creep.gameObject, NetworkWorldKind.Creep);
            }

            foreach (var zombie in FindObjectsByType<ZombieEnemy>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (zombie != null)
                    DisableLocalBrain(zombie.gameObject, NetworkWorldKind.Zombie);
            }

            foreach (var magician in FindObjectsByType<MagicianEnemy>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (magician != null)
                    DisableLocalBrain(magician.gameObject, NetworkWorldKind.Magician);
            }

            var motion = new List<GameObject>();
            CollectMotionSources(motion);
            for (int i = 0; i < motion.Count; i++)
            {
                var kind = ClassifyMotion(motion[i]);
                DisableLocalBrain(motion[i], kind);
            }
        }

        public static void DisableLocalBrain(GameObject source, NetworkWorldKind kind)
        {
            if (source == null)
                return;

            switch (kind)
            {
                case NetworkWorldKind.Enemy:
                    SetEnabled<EmeraldSystem>(source, false);
                    SetEnabled<EmeraldDetection>(source, false);
                    SetEnabled<EmeraldCombat>(source, false);
                    var agent = source.GetComponent<NavMeshAgent>();
                    if (agent != null)
                        agent.enabled = false;
                    break;
                case NetworkWorldKind.Creep:
                    var creep = source.GetComponent<CreepGrabEnemy>();
                    creep?.DisableAsProxy();
                    var creepAgent = source.GetComponent<NavMeshAgent>();
                    if (creepAgent != null)
                        creepAgent.enabled = false;
                    break;
                case NetworkWorldKind.Zombie:
                    var zombie = source.GetComponent<ZombieEnemy>();
                    zombie?.DisableAsProxy();
                    var zombieAgent = source.GetComponent<NavMeshAgent>();
                    if (zombieAgent != null)
                        zombieAgent.enabled = false;
                    break;
                case NetworkWorldKind.Magician:
                    var magician = source.GetComponent<MagicianEnemy>();
                    magician?.DisableAsProxy();
                    break;
                case NetworkWorldKind.Platform:
                case NetworkWorldKind.Trap:
                    DisableLocalMotion(source);
                    break;
            }
        }

        static void DisableLocalMotion(GameObject source)
        {
            foreach (var transformer in source.GetComponentsInChildren<MSimpleTransformer>(true))
            {
                if (transformer != null)
                    transformer.enabled = false;
            }

            foreach (var anim in source.GetComponentsInChildren<Animator>(true))
            {
                if (anim == null || IsCharacterAnimator(anim))
                    continue;
                anim.speed = 0f;
            }

            foreach (var body in source.GetComponentsInChildren<Rigidbody>(true))
            {
                if (body == null)
                    continue;
                body.isKinematic = true;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }

        public static void CollectMotionSources(List<GameObject> into)
        {
            if (into == null)
                return;

            var seen = new HashSet<GameObject>();
            foreach (var transformer in FindObjectsByType<MSimpleTransformer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transformer == null || IsCharacterObject(transformer.gameObject) ||
                    IsZombieObject(transformer.gameObject))
                    continue;
                AddMotionSource(into, seen, transformer.gameObject);
            }

            foreach (var anim in FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (anim == null || IsCharacterAnimator(anim) || IsZombieObject(anim.gameObject))
                    continue;
                AddMotionSource(into, seen, MotionRoot(anim.gameObject));
            }
        }

        public static NetworkWorldKind ClassifyMotion(GameObject source)
        {
            if (source != null && source.GetComponentInChildren<MSimpleTransformer>(true) != null)
                return NetworkWorldKind.Platform;
            return NetworkWorldKind.Trap;
        }

        static void AddMotionSource(List<GameObject> into, HashSet<GameObject> seen, GameObject go)
        {
            if (go == null || !seen.Add(go))
                return;
            into.Add(go);
        }

        static GameObject MotionRoot(GameObject go)
        {
            var transformer = go.GetComponentInParent<MSimpleTransformer>();
            if (transformer != null)
                return transformer.gameObject;
            return go;
        }

        /// <summary>
        /// Zombies are deliberately NOT in IsCharacterObject - that exclusion is what lets their
        /// animator state replicate through CaptureAnimator/ApplySyncedAnimator. But they must
        /// still stay out of the generic motion sweep, or each zombie ends up with a second actor
        /// of kind Trap that wins the source cache and silently eats all incoming damage.
        /// </summary>
        static bool IsZombieObject(GameObject go)
        {
            // includeInactive: the motion sweep enumerates inactive objects, and the default
            // overload returns null through an inactive ancestor - which let inactive zombies
            // slip through and pick up the duplicate actor this exists to prevent.
            return go != null &&
                   (go.GetComponentInParent<ZombieEnemy>(true) != null ||
                    go.GetComponentInParent<MagicianEnemy>(true) != null);
        }

        static bool IsCharacterAnimator(Animator anim)
        {
            return anim != null && IsCharacterObject(anim.gameObject);
        }

        static bool IsCharacterObject(GameObject go)
        {
            if (go == null)
                return false;
            return go.GetComponentInParent<FpsNetworkBridge>() != null
                   || go.GetComponentInParent<PlayerMovement>() != null
                   || go.GetComponentInParent<DualPlayerController>() != null
                   || go.GetComponentInParent<MalbersAnimations.Controller.MAnimal>() != null
                   || go.GetComponentInParent<EmeraldSystem>() != null
                   || go.GetComponentInParent<CreepGrabEnemy>() != null;
        }

        static void SetEnabled<T>(GameObject go, bool enabled) where T : UnityEngine.Behaviour
        {
            var c = go.GetComponent<T>();
            if (c != null)
                c.enabled = enabled;
        }
    }
}
