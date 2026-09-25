using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// An area that releases the unkillable creep stalker once, unseen, when players come near.
    ///
    /// Place it where the stalker should come FROM - behind the players' route rather than ahead of
    /// it, since it spawns out of sight and then closes the distance itself.
    ///
    /// The creature it produces is the ordinary creep put into stalker mode (see
    /// CreepGrabEnemy.ConfigureAsStalker): no roar, animation running several times too fast, immune
    /// to damage, and gone for good once it has dragged somebody off. The intended shape of the
    /// encounter is that it takes one player and hauls them away, and the team-distance rule does the
    /// rest - the other player has to chase it or both get pulled back to the checkpoint.
    ///
    /// Like the zombie ambush spawner, only the master client decides, and the spawn is broadcast so
    /// every machine builds the same creature under the same name. Without that the grab cannot work,
    /// because the grab is routed through NetworkWorldActor, which binds by hierarchy path.
    /// </summary>
    [AddComponentMenu("CollarCali/Creep Stalker Spawner")]
    public class CreepStalkerSpawner : MonoBehaviour, IHiddenSpawnReceiver
    {
        [Header("Creep")]
        [Tooltip("The creep prefab to use. Filled in automatically from the Creep Horror Creature " +
                 "pack when the component is added.")]
        [SerializeField] GameObject creepPrefab;

        [Header("Behaviour")]
        [Tooltip("Animation speed while it closes on its victim. 4 to 5 is the unsettling range.")]
        [SerializeField] float chaseAnimationSpeed = 4.5f;

        [Tooltip("Seconds it drags the victim. Long enough to pull the team apart.")]
        [SerializeField] float dragSeconds = 12f;

        [Tooltip("Seconds it runs away for after releasing, before vanishing.")]
        [SerializeField] float retreatSeconds = 5f;

        [Header("When to strike")]
        [Tooltip("A player must be within this range of the area before it will release anything.")]
        [SerializeField] float activationRange = 30f;

        [Tooltip("It appears within this radius of this object, on the NavMesh and out of sight.")]
        [SerializeField] float spawnRadius = 5f;

        [Tooltip("Horizontal field of view counted as 'looking at it', in degrees.")]
        [SerializeField] float playerFieldOfView = 100f;

        [Tooltip("Never appears closer than this to a player, even behind their back.")]
        [SerializeField] float minPlayerDistance = 12f;

        [Tooltip("How many times this area may ever fire. 1 makes it a one-off scripted scare.")]
        [SerializeField] int maxUses = 1;

        [Tooltip("Seconds before it may fire again, when Max Uses is above 1.")]
        [SerializeField] float cooldownSeconds = 90f;

        [Tooltip("Geometry that blocks sight. Left empty it uses the project's standard set.")]
        [SerializeField] LayerMask sightBlockers = 0;

        int _uses;
        int _sequence;
        float _nextCheck;
        float _readyAt;
        CreepGrabEnemy _active;

        void Reset()
        {
#if UNITY_EDITOR
            if (creepPrefab == null)
            {
                creepPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Creep Horror Creature/Prefabs/Creep1.prefab");
            }
#endif
        }

        void Awake()
        {
            if (sightBlockers == 0)
                sightBlockers = HiddenSpawnUtility.DefaultSightBlockers();
        }

        void Update()
        {
            if (Time.time < _nextCheck)
                return;
            _nextCheck = Time.time + 0.4f;

            if (!HiddenSpawnUtility.OwnsSpawning())
                return;
            if (maxUses > 0 && _uses >= maxUses)
                return;
            if (Time.time < _readyAt)
                return;

            // One at a time, always. Two of these at once would be a fight rather than a scare, and
            // neither of them can be killed.
            if (_active != null)
                return;

            var players = HiddenSpawnUtility.CollectPlayers();
            if (players.Count == 0)
                return;
            if (!HiddenSpawnUtility.AnyPlayerWithin(transform.position, players, activationRange))
                return;
            if (!HiddenSpawnUtility.TryFindHiddenNavPoint(transform.position, spawnRadius, players,
                    playerFieldOfView, minPlayerDistance, sightBlockers, out var position))
                return;

            _sequence++;
            _uses++;
            _readyAt = Time.time + cooldownSeconds;

            // Faces roughly towards whoever triggered it, so its first step is already the right way.
            float yaw = Quaternion.LookRotation(
                Flat(players[0].Position - position, Vector3.forward)).eulerAngles.y;

#if CMPSETUP_COMPLETE
            var relay = NetworkVfxRelay.Instance;
            if (relay != null)
            {
                relay.BroadcastHiddenSpawn(NetworkWorldActor.MakeKey(transform), _sequence,
                    position, yaw, 0);
                return;
            }
#endif

            SpawnFromNetwork(_sequence, position, yaw, 0);
        }

        static Vector3 Flat(Vector3 value, Vector3 fallback)
        {
            value.y = 0f;
            return value.sqrMagnitude < 0.01f ? fallback : value.normalized;
        }

        /// <summary>
        /// Builds the stalker on this machine. Runs on every client via the relay, so the name has to
        /// come from the master's sequence number - that name is what the networked grab binds by.
        /// </summary>
        public void SpawnFromNetwork(int sequence, Vector3 position, float yaw, int variant)
        {
            if (creepPrefab == null)
            {
                Debug.LogError("[CollarCali] CreepStalkerSpawner has no creep prefab assigned.", this);
                return;
            }

            var creep = Instantiate(creepPrefab, position, Quaternion.Euler(0f, yaw, 0f), transform);
            creep.name = "Stalker " + sequence;

            // Setup does the collider, hitbox, health and brain wiring the scene creeps get from
            // Scene2CombatBootstrap. Reused rather than repeated so a runtime stalker and a placed
            // creep are built the same way.
            CreepGrabEnemy.Setup(creep);

            var brain = creep.GetComponent<CreepGrabEnemy>();
            if (brain == null)
            {
                Debug.LogError("[CollarCali] CreepGrabEnemy.Setup produced no brain on " + creep.name,
                    this);
                return;
            }

            brain.ConfigureAsStalker(chaseAnimationSpeed, dragSeconds, retreatSeconds);
            _active = brain;

#if CMPSETUP_COMPLETE
            var runner = NetworkCombatHooks.FindRunner();
            bool owner = runner == null || !runner.IsRunning || runner.IsSharedModeMasterClient;
            if (runner != null && runner.IsRunning && runner.IsSharedModeMasterClient)
                MultiplayerSessionBootstrap.EnsureActorFor(creep, NetworkWorldKind.Creep);
#else
            const bool owner = true;
#endif

            // Only the machine running the brain listens: the retreat only ever completes there, and
            // the removal is then broadcast to everyone from one place.
            if (owner)
            {
                int retiring = sequence;
                brain.StalkerFinished += () => RetireStalker(retiring);
            }
        }

        void RetireStalker(int sequence)
        {
#if CMPSETUP_COMPLETE
            var relay = NetworkVfxRelay.Instance;
            if (relay != null)
            {
                relay.BroadcastHiddenDespawn(NetworkWorldActor.MakeKey(transform), sequence);
                return;
            }
#endif
            DespawnFromNetwork(sequence);
        }

        /// <summary>Removes this machine's copy of the stalker. Runs on every client via the relay.</summary>
        public void DespawnFromNetwork(int sequence)
        {
            var child = transform.Find("Stalker " + sequence);
            if (child != null)
                Destroy(child.gameObject);

            if (_active != null && _active.name == "Stalker " + sequence)
                _active = null;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.75f, 0.1f, 0.8f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, spawnRadius);
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.25f);
            Gizmos.DrawWireSphere(transform.position, activationRange);
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, minPlayerDistance);
        }
    }
}
