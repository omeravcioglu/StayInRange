#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Add this to any object - a ladder, a plank, a crate - and every player in the session can lift
    /// it with telekinesis, carry it, turn it, put it down where they want it, throw it to a friend and
    /// catch it, with the same keys used for a teammate's body: E lifts and places, Q then the left
    /// mouse button throws, the mouse wheel turns it and R with the wheel tilts it.
    ///
    /// NOTHING ELSE TO SET UP. It needs no network component of its own: when a session starts the host
    /// spawns a NetworkWorldActor for it (as for enemies and platforms), and that actor carries its
    /// networked state. A Rigidbody is added if there is none, and concave mesh colliders are made
    /// convex, because a moving physics object cannot use them. It must not be Static - static batching
    /// freezes the mesh where it is however the object moves - so adding the component clears that.
    /// Two props need different names when they share a parent: the network tells them apart by
    /// their place in the hierarchy.
    ///
    /// WHO DOES WHAT. Exactly as for bodies, one machine decides and one machine simulates:
    ///   - the HOST decides who holds it - every lift, catch, release and landing is a request to the
    ///     host carrying the epoch the sender saw, so two players catching the same throw cannot both
    ///     succeed;
    ///   - whoever is HANDLING it simulates it: the carrier while it is held, the thrower while it
    ///     flies, the host while it lies loose. The handler publishes its pose through their own player
    ///     bridge (FpsNetworkBridge.Driven*), the host through the actor's transform, and every other
    ///     machine follows the published pose kinematically.
    ///
    /// PLACING. With "Freeze When Placed" (on by default) letting go with the interact key puts it down
    /// exactly where it is - leaning on a wall, bridging a gap - and it stays there for everyone until
    /// someone lifts it again. With "Place Needs Support" it only stays if it is touching something;
    /// let go in mid-air and it falls, so nobody can build a platform out of thin air.
    ///
    /// LADDERS keep working wherever they are put: Cowsins climbs anything on the Ladder layer and
    /// Malbers ladders work out their ends from their own transform. While it is held or flying nobody
    /// can climb it: its colliders move to the Ignore Raycast layer (out of Cowsins' ladder ray and out
    /// of the telekinesis' own wall checks) and its trigger zones - Malbers' ladder entry - switch off,
    /// until it is put down again. Anyone on it when it is lifted is let go.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("CollarCali/Telekinetic Prop")]
    public class TelekineticProp : MonoBehaviour, ITelekineticTarget, ITelekineticSimulation
    {
        /// <summary>Every prop in the loaded scenes.</summary>
        public static readonly List<TelekineticProp> All = new List<TelekineticProp>();

        [Tooltip("Name shown in the prompt, as in \"Lift Ladder\". Empty uses the object's name.")]
        [SerializeField] string displayName = "";

        [Header("Placing")]
        [Tooltip("Letting go with the interact key puts it down exactly where it is held - leaning on a " +
                 "wall, bridging a gap - and it stays there for everyone. Off: it falls like anything else.")]
        [SerializeField] bool freezeWhenPlaced = true;

        [Tooltip("Only stays put when it is touching something. Let go in mid-air and it falls instead, " +
                 "so nobody can build floating platforms.")]
        [SerializeField] bool placeNeedsSupport = true;

        [Tooltip("Starts the level frozen exactly where it was placed in the scene, instead of settling " +
                 "under physics when the level loads.")]
        [SerializeField] bool startFrozen = true;

        [Header("Handling")]
        [Tooltip("Mass while simulated. 0 keeps the Rigidbody's own, or 20 if one had to be added.")]
        [SerializeField, Min(0f)] float mass = 0f;

        [Tooltip("Can be thrown (throw key, charge, release). Off: it can only be carried and placed.")]
        [SerializeField] bool throwable = true;

        [Tooltip("Degrees it turns or tilts per step of the mouse wheel while held.")]
        [SerializeField, Range(5f, 90f)] float turnStepDegrees = 15f;

        [Header("Climbing")]
        [Tooltip("Nobody can climb it while it is being carried or is in flight: its trigger zones " +
                 "(Malbers' ladder entry) switch off until it is put down.")]
        [SerializeField] bool blockClimbingWhileMoving = true;

        const int IgnoreRaycastLayer = 2;

        /// <summary>How recently it must have touched something to count as supported when placed.</summary>
        const float ContactMemorySeconds = 0.2f;

        /// <summary>How long a following copy coasts on the last pose when the stream pauses at a handover.</summary>
        const float PoseGraceSeconds = 0.4f;

        /// <summary>How quickly a held prop turns to the aimed orientation, in hertz.</summary>
        const float TurnFrequency = 2.5f;

        /// <summary>
        /// How long the host waits for the startup sweep before spawning a missing actor itself - for
        /// a prop that was inactive when the session started, or one in a level the sweep skips.
        /// </summary>
        const float ActorWaitSeconds = 3f;

        enum Role
        {
            /// <summary>No session, or its actor is not spawned yet: left as the level put it.</summary>
            Unbound,

            /// <summary>Placed and frozen: kinematic everywhere, at the networked pose.</summary>
            Frozen,

            /// <summary>Lying loose, simulated by this machine because it is the host.</summary>
            HostSimulates,

            /// <summary>Held or flying, simulated by this machine because the local player is handling it.</summary>
            SimulatesHere,

            /// <summary>Somebody else simulates it; this copy follows their published pose.</summary>
            Follows,
        }

        Rigidbody _rb;
        readonly List<Collider> _solid = new List<Collider>();
        readonly List<Collider> _triggers = new List<Collider>();
        readonly Dictionary<GameObject, int> _savedLayers = new Dictionary<GameObject, int>();
        readonly List<bool> _savedTriggers = new List<bool>();
        bool _movingLook;
        float _holdReach;

        NetworkWorldActor _actor;
        float _nextActorLookup;
        float _hostWaitingSince = -1f;
        Role _role = Role.Unbound;

        // Following another machine's simulation.
        bool _haveFollow;
        Vector3 _followPosition;
        Quaternion _followRotation;
        bool _haveLastPose;
        float _lastPoseTime;
        Vector3 _lastPosePosition;
        Quaternion _lastPoseRotation;
        Vector3 _lastVelocity;

        // Simulating here.
        bool _gripped;
        float _stuckSeconds;
        int _flightEpoch = int.MinValue;
        float _flightSeconds;
        float _settledSeconds;
        bool _restRequested;
        float _lastContactTime = -999f;

        // A placement made on this machine: frozen here at once, at exactly the pose the host is told,
        // and held there while the host confirms it - so it never drops for the round trip, nor
        // glides through the host's slightly older copy of the pose afterwards.
        bool _placedLocally;
        int _placeEpoch;
        Vector3 _placedPosition;
        Quaternion _placedRotation;

        public bool StartsFrozen => startFrozen;
        public float TurnStepDegrees => turnStepDegrees;

        /// <summary>True once its actor is spawned and readable on this machine.</summary>
        bool Live => _actor != null && _actor.Object != null && _actor.Object.IsValid;

        public bool IsFrozen => Live && _actor.PropFrozen;

        #region Setup

        void Reset()
        {
            displayName = name.IndexOf("ladder", System.StringComparison.OrdinalIgnoreCase) >= 0
                ? "Ladder"
                : Prettify(name);

#if UNITY_EDITOR
            // A moving object cannot be static: batching would leave its mesh behind.
            int cleared = 0;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (UnityEditor.GameObjectUtility.GetStaticEditorFlags(t.gameObject) == 0)
                    continue;
                UnityEditor.GameObjectUtility.SetStaticEditorFlags(t.gameObject, 0);
                cleared++;
            }

            if (cleared > 0)
                Debug.Log("[CollarCali] " + name + ": Static switched off on " + cleared +
                          " object(s) so it can be moved.", this);
#endif
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                var flags = UnityEditor.GameObjectUtility.GetStaticEditorFlags(t.gameObject);
                if ((flags & (UnityEditor.StaticEditorFlags.BatchingStatic |
                              UnityEditor.StaticEditorFlags.OccluderStatic)) == 0)
                    continue;

                Debug.LogWarning("[CollarCali] '" + t.name + "' is part of the telekinetic prop '" + name +
                                 "' but is marked Static. Untick Static on it: a static mesh stays where it " +
                                 "is when the object moves.", t.gameObject);
                break;
            }
        }
#endif

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            bool added = false;
            if (_rb == null)
            {
                _rb = gameObject.AddComponent<Rigidbody>();
                added = true;
            }

            if (mass > 0f)
                _rb.mass = mass;
            else if (added)
                _rb.mass = 20f;

            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _rb.maxDepenetrationVelocity = 3f;
            _rb.useGravity = true;

            int convexed = 0;
            foreach (var collider in GetComponentsInChildren<Collider>(true))
            {
                if (collider == null)
                    continue;
                if (collider.isTrigger)
                {
                    _triggers.Add(collider);
                    continue;
                }

                // A moving physics object cannot use a concave mesh collider - Unity would refuse to
                // simulate it - so it becomes its convex hull. For a ladder that is a solid slab, which
                // is also what both climbing systems want to touch.
                if (collider is MeshCollider mesh && !mesh.convex)
                {
                    mesh.convex = true;
                    convexed++;
                }

                _solid.Add(collider);
            }

            if (convexed > 0)
                Debug.Log("[CollarCali] " + name + ": " + convexed + " mesh collider(s) made convex so it can move.", this);
            if (_solid.Count == 0)
                Debug.LogWarning("[CollarCali] TelekineticProp '" + name + "' has no solid collider, so it " +
                                 "cannot be aimed at, held or placed. Add a collider to it.", this);

            _holdReach = ComputeReach();
            SetKinematic(startFrozen);
        }

        void OnEnable()
        {
            if (!All.Contains(this))
                All.Add(this);
        }

        void OnDisable()
        {
            All.Remove(this);
            if (ReferenceEquals(TelekineticSimulation.Local, this))
                TelekineticSimulation.Local = null;
            SetMovingLook(false);
        }

        /// <summary>
        /// How far it reaches out from its centre in the worst case: half the diagonal of its bounds,
        /// which holds whichever way it was standing when the level loaded.
        /// </summary>
        float ComputeReach()
        {
            bool any = false;
            var bounds = new Bounds(transform.position, Vector3.zero);
            foreach (var collider in _solid)
            {
                if (collider == null)
                    continue;
                if (!any)
                {
                    bounds = collider.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return bounds.extents.magnitude;
        }

        static string Prettify(string raw)
        {
            int paren = raw.LastIndexOf(" (", System.StringComparison.Ordinal);
            if (paren > 0 && raw.EndsWith(")"))
                raw = raw.Substring(0, paren);
            return raw.Replace('_', ' ').Trim();
        }

        #endregion

        #region Role

        void Update()
        {
            var role = DecideRole();
            if (role != _role)
                SwitchRole(role);

            // Something else this machine simulated may have had the publishing slot and let it go.
            if (_role == Role.SimulatesHere && TelekineticSimulation.Local == null)
                TelekineticSimulation.Local = this;

            var phase = Phase;
            SetMovingLook(Live && !_placedLocally && !_actor.PropFrozen &&
                          (phase == BodyPhase.Held || phase == BodyPhase.Thrown));
        }

        Role DecideRole()
        {
            if (!ResolveActor())
            {
                EnsureActorAsHost();
                return Role.Unbound;
            }

            _hostWaitingSince = -1f;

            if (_placedLocally)
            {
                int epoch = _actor.PropEpoch;
                bool waiting = epoch == _placeEpoch && !_actor.PropFrozen;
                bool confirmed = epoch == _placeEpoch + 1 && _actor.PropFrozen;
                if (waiting || confirmed)
                    return Role.Frozen;

                // Refused, or somebody has lifted it since: back to what the network says.
                _placedLocally = false;
            }

            if (_actor.PropFrozen)
                return Role.Frozen;

            var driver = _actor.PropDriver;
            if (driver == PlayerRef.None)
                return _actor.HasStateAuthority ? Role.HostSimulates : Role.Follows;

            var runner = _actor.Runner;
            return runner != null && driver == runner.LocalPlayer ? Role.SimulatesHere : Role.Follows;
        }

        void SwitchRole(Role role)
        {
            var previous = _role;
            _role = role;

            if (role != Role.SimulatesHere && ReferenceEquals(TelekineticSimulation.Local, this))
                TelekineticSimulation.Local = null;

            switch (role)
            {
                case Role.Unbound:
                    SetKinematic(startFrozen);
                    break;

                case Role.Frozen:
                    SetKinematic(true);
                    _haveFollow = false;
                    break;

                case Role.HostSimulates:
                    SetKinematic(false);
                    _rb.useGravity = true;
                    _rb.angularDamping = 0.05f;
                    // Carries on the way the last simulation said it was moving.
                    if (previous == Role.Follows)
                        _rb.linearVelocity = _lastVelocity;
                    break;

                case Role.SimulatesHere:
                    SetKinematic(false);
                    _rb.useGravity = true;
                    if (previous == Role.Follows)
                        _rb.linearVelocity = _lastVelocity;
                    _stuckSeconds = 0f;
                    _gripped = false;
                    TelekineticSimulation.Local = this;
                    break;

                case Role.Follows:
                    SetKinematic(true);
                    _haveFollow = false;
                    break;
            }
        }

        bool ResolveActor()
        {
            if (Live)
                return true;

            _actor = null;
            if (Time.unscaledTime < _nextActorLookup)
                return false;
            _nextActorLookup = Time.unscaledTime + 0.5f;

            var actor = NetworkWorldActor.FindExact(gameObject);
            if (actor != null && actor.Object != null && actor.Object.IsValid && actor.IsProp)
                _actor = actor;

            return _actor != null;
        }

        /// <summary>
        /// The host spawns this prop's actor itself if the startup sweep has not after a few seconds -
        /// it was inactive then, or this level is not the one the sweep runs in.
        /// </summary>
        void EnsureActorAsHost()
        {
            var runner = NetworkCombatHooks.FindRunner();
            if (runner == null || !runner.IsRunning || !runner.IsSharedModeMasterClient)
            {
                _hostWaitingSince = -1f;
                return;
            }

            if (_hostWaitingSince < 0f)
            {
                _hostWaitingSince = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _hostWaitingSince < ActorWaitSeconds)
                return;

            _hostWaitingSince = Time.unscaledTime;
            MultiplayerSessionBootstrap.EnsurePropActor(gameObject);
            _nextActorLookup = 0f;
        }

        void SetKinematic(bool kinematic)
        {
            if (_rb == null || _rb.isKinematic == kinematic)
                return;

            if (kinematic)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }

            _rb.isKinematic = kinematic;
        }

        /// <summary>
        /// While held or flying: out of every ray that looks for ladders or walls, and (optionally) its
        /// trigger zones off so nobody can climb it. Put back exactly as it was when it comes to rest.
        /// </summary>
        void SetMovingLook(bool moving)
        {
            if (moving == _movingLook)
                return;
            _movingLook = moving;

            if (moving)
            {
                LetGoOfClimbers();

                _savedLayers.Clear();
                foreach (var collider in _solid)
                {
                    if (collider == null)
                        continue;
                    var go = collider.gameObject;
                    if (_savedLayers.ContainsKey(go))
                        continue;
                    _savedLayers[go] = go.layer;
                    go.layer = IgnoreRaycastLayer;
                }

                _savedTriggers.Clear();
                foreach (var trigger in _triggers)
                {
                    _savedTriggers.Add(trigger != null && trigger.enabled);
                    if (trigger != null && blockClimbingWhileMoving)
                        trigger.enabled = false;
                }

                return;
            }

            foreach (var pair in _savedLayers)
            {
                if (pair.Key != null)
                    pair.Key.layer = pair.Value;
            }
            _savedLayers.Clear();

            for (int i = 0; i < _triggers.Count && i < _savedTriggers.Count; i++)
            {
                if (_triggers[i] != null)
                    _triggers[i].enabled = _savedTriggers[i];
            }
            _savedTriggers.Clear();
        }

        /// <summary>
        /// A third-person character climbing this ladder would ride it away - Malbers' ladder state
        /// moves the climber with the ladder - so whoever is on it on this machine drops off as it is
        /// lifted. First person needs nothing: Cowsins lets go by itself once its ladder ray stops
        /// hitting the Ladder layer.
        /// </summary>
        void LetGoOfClimbers()
        {
            var animals = MalbersAnimations.Controller.MAnimal.Animals;
            if (animals == null)
                return;

            foreach (var animal in animals)
            {
                if (animal == null || !(animal.ActiveState is MalbersAnimations.Ladder ladder))
                    continue;
                if (ladder.MLadder == null || !ladder.MLadder.transform.IsChildOf(transform))
                    continue;
                animal.State_Force(MalbersAnimations.StateEnum.Fall);
            }
        }

        #endregion

        #region Physics

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            switch (_role)
            {
                case Role.SimulatesHere:
                    SimulateStep(dt);
                    break;
                case Role.Follows:
                    FollowStep(dt);
                    break;
                case Role.Frozen:
                    HoldFrozenPose(dt);
                    break;
            }
        }

        void LateUpdate()
        {
            // The host keeps the actor - the prop's networked pose - on this copy whenever it is moving,
            // whoever simulates it: late joiners and the next simulator start from there.
            if (!Live || !_actor.HasStateAuthority || _role == Role.Frozen || _role == Role.Unbound)
                return;

            _actor.transform.SetPositionAndRotation(transform.position, transform.rotation);
        }

        void SimulateStep(float dt)
        {
            var phase = Phase;

            var target = Vector3.zero;
            var facing = Quaternion.identity;
            var carrierVelocity = Vector3.zero;
            bool gripping = phase == BodyPhase.Held && !_placedLocally &&
                            BodyTelekinesis.Local != null &&
                            BodyTelekinesis.Local.TryGetGripTarget(this, out target, out facing, out carrierVelocity);

            if (gripping != _gripped)
            {
                _gripped = gripping;
                // Held up by the grip, not by gravity - and steadier while it is.
                _rb.useGravity = !gripping;
                _rb.angularDamping = gripping ? 2f : 0.05f;
                _stuckSeconds = 0f;
            }

            if (gripping)
            {
                ApplyGrip(target, facing, carrierVelocity, dt);
                return;
            }

            if (phase == BodyPhase.Thrown)
                TrackFlight(dt);
        }

        /// <summary>
        /// The same damped spring that carries a body, on the prop's centre of mass, plus a second one
        /// turning it to the orientation the carrier is aiming it at.
        /// </summary>
        void ApplyGrip(Vector3 target, Quaternion facing, Vector3 carrierVelocity, float dt)
        {
            var carry = PlayerTuning.Active.carry;
            float omega = 2f * Mathf.PI * carry.gripFrequency;

            var error = target - _rb.worldCenterOfMass;
            var relativeVelocity = _rb.linearVelocity - carrierVelocity;
            var acceleration = omega * omega * error - 2f * carry.gripDamping * omega * relativeVelocity;
            acceleration = Vector3.ClampMagnitude(acceleration, carry.maxGripAcceleration);
            if (relativeVelocity.magnitude > carry.maxGripSpeed && Vector3.Dot(acceleration, relativeVelocity) > 0f)
                acceleration = Vector3.ProjectOnPlane(acceleration, relativeVelocity.normalized);
            _rb.AddForce(acceleration, ForceMode.Acceleration);

            var delta = facing * Quaternion.Inverse(_rb.rotation);
            delta.ToAngleAxis(out float angle, out var axis);
            if (angle > 180f)
                angle -= 360f;
            if (Mathf.Abs(angle) > 0.01f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
            {
                float turn = 2f * Mathf.PI * TurnFrequency;
                var angular = axis.normalized * (angle * Mathf.Deg2Rad * turn * turn) - 2f * turn * _rb.angularVelocity;
                _rb.AddTorque(Vector3.ClampMagnitude(angular, 80f), ForceMode.Acceleration);
            }

            // Wedged where the spring cannot pull it free: let go rather than drag it through a wall.
            if (error.magnitude > carry.breakDistance)
            {
                _stuckSeconds += dt;
                if (_stuckSeconds >= carry.breakSeconds)
                {
                    _stuckSeconds = 0f;
                    BodyTelekinesis.Local?.OnGripBroken(this);
                }
            }
            else
            {
                _stuckSeconds = 0f;
            }
        }

        /// <summary>Watches a thrown or dropped prop until it lands, then hands it back to the host.</summary>
        void TrackFlight(float dt)
        {
            var catching = PlayerTuning.Active.catching;
            int epoch = Epoch;
            if (epoch != _flightEpoch)
            {
                _flightEpoch = epoch;
                _flightSeconds = 0f;
                _settledSeconds = 0f;
                _restRequested = false;
            }

            if (_restRequested)
                return;

            _flightSeconds += dt;
            _settledSeconds = _rb.linearVelocity.magnitude < catching.settleSpeed ? _settledSeconds + dt : 0f;

            if (_settledSeconds >= catching.settleSeconds || _flightSeconds >= catching.maxFlightSeconds)
            {
                _restRequested = true;
                RequestRest(epoch);
            }
        }

        void FollowStep(float dt)
        {
            if (!TryGetFollowPose(out var position, out var rotation, out var velocity))
                return;

            var network = PlayerTuning.Active.network;
            var target = position + velocity * network.velocityLead;

            if (!_haveFollow || (target - _followPosition).sqrMagnitude > network.snapDistance * network.snapDistance)
            {
                _followPosition = target;
                _followRotation = rotation;
                _haveFollow = true;
            }
            else
            {
                float t = 1f - Mathf.Exp(-network.followSharpness * dt);
                _followPosition = Vector3.Lerp(_followPosition, target, t);
                _followRotation = Quaternion.Slerp(_followRotation, rotation, t);
            }

            _rb.MovePosition(_followPosition);
            _rb.MoveRotation(_followRotation);
        }

        /// <summary>
        /// The pose to follow: what the simulating player last published, coasting on it briefly
        /// through a handover; otherwise the actor's own networked pose, which the host keeps current.
        /// </summary>
        bool TryGetFollowPose(out Vector3 position, out Quaternion rotation, out Vector3 velocity)
        {
            position = default;
            rotation = Quaternion.identity;
            velocity = default;
            if (!Live)
                return false;

            var driver = _actor.PropDriver;
            if (driver != PlayerRef.None)
            {
                var bridge = FpsNetworkBridge.FindByPlayer(driver);
                if (bridge != null && bridge.DrivenBody == _actor.Object.Id)
                {
                    position = bridge.DrivenHipsPosition;
                    rotation = bridge.DrivenHipsRotation;
                    velocity = bridge.DrivenVelocity;

                    _haveLastPose = true;
                    _lastPoseTime = Time.time;
                    _lastPosePosition = position;
                    _lastPoseRotation = rotation;
                    _lastVelocity = velocity;
                    return true;
                }

                float age = Time.time - _lastPoseTime;
                if (_haveLastPose && age < PoseGraceSeconds)
                {
                    position = _lastPosePosition + _lastVelocity * age + 0.5f * age * age * Physics.gravity;
                    rotation = _lastPoseRotation;
                    velocity = _lastVelocity + Physics.gravity * age;
                    return true;
                }
            }

            position = _actor.transform.position;
            rotation = _actor.transform.rotation;
            velocity = Vector3.zero;
            _lastVelocity = Vector3.zero;
            return true;
        }

        void HoldFrozenPose(float dt)
        {
            Vector3 position;
            Quaternion rotation;
            if (_placedLocally)
            {
                position = _placedPosition;
                rotation = _placedRotation;
            }
            else if (Live)
            {
                position = _actor.transform.position;
                rotation = _actor.transform.rotation;
            }
            else
            {
                return;
            }

            if ((_rb.position - position).sqrMagnitude < 0.000001f &&
                Quaternion.Angle(_rb.rotation, rotation) < 0.05f)
                return;

            var network = PlayerTuning.Active.network;
            if (!_haveFollow || (position - _rb.position).sqrMagnitude > network.snapDistance * network.snapDistance)
            {
                _rb.position = position;
                _rb.rotation = rotation;
                _haveFollow = true;
                return;
            }

            float t = 1f - Mathf.Exp(-network.followSharpness * dt);
            _rb.MovePosition(Vector3.Lerp(_rb.position, position, t));
            _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, rotation, t));
        }

        /// <summary>Puts this copy straight onto a pose the host has just been given.</summary>
        public void SnapTo(Vector3 position, Quaternion rotation)
        {
            if (_rb == null)
                return;
            _rb.position = position;
            _rb.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            if (!_rb.isKinematic)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
        }

        void OnCollisionStay(Collision collision) => NoteContact(collision);
        void OnCollisionEnter(Collision collision) => NoteContact(collision);

        /// <summary>Remembers touching the world - not a player - for deciding whether a placement has support.</summary>
        void NoteContact(Collision collision)
        {
            if (_role != Role.SimulatesHere || collision.collider == null)
                return;

            int layer = collision.collider.gameObject.layer;
            if (layer == PlayerLayer || layer == AnimalLayer)
                return;

            _lastContactTime = Time.time;
        }

        static int _playerLayer = -2;
        static int _animalLayer = -2;
        static int PlayerLayer => _playerLayer == -2 ? (_playerLayer = LayerMask.NameToLayer("Player")) : _playerLayer;
        static int AnimalLayer => _animalLayer == -2 ? (_animalLayer = LayerMask.NameToLayer("Animal")) : _animalLayer;

        bool IsSupported() => Time.time - _lastContactTime <= ContactMemorySeconds;

        #endregion

        #region ITelekineticTarget

        public bool IsValidTarget => isActiveAndEnabled && ResolveActor();

        public string DisplayName => !string.IsNullOrWhiteSpace(displayName) ? displayName : Prettify(name);

        public BodyPhase Phase => Live ? (BodyPhase)_actor.PropPhase : BodyPhase.Loose;

        public int Epoch => Live ? _actor.PropEpoch : 0;

        public PlayerRef Holder => Live ? _actor.PropHolder : PlayerRef.None;

        public Vector3 Position => _rb != null ? _rb.worldCenterOfMass : transform.position;

        public Quaternion Rotation => _rb != null ? _rb.rotation : transform.rotation;

        public IReadOnlyList<Collider> Colliders => _solid;

        public bool SupportsRotation => true;

        public bool Throwable => throwable;

        public string DropVerb => freezeWhenPlaced ? "Place" : "Drop";

        public float HoldReach => _holdReach;

        public bool IsLocallySimulated => _role == Role.SimulatesHere;

        public void RequestHold(int epoch, bool catching)
        {
            if (ResolveActor())
                _actor.RPC_PropHold(epoch, catching);
        }

        /// <summary>
        /// Let go. Thrown: in flight. Otherwise placed, frozen exactly where it is - when placing is on
        /// and it is touching something (or support is not required) - or simply dropped.
        /// </summary>
        public void RequestRelease(int epoch, bool thrown)
        {
            if (!ResolveActor())
                return;

            // A resend of a placement already frozen here sends the same placement again.
            bool place = !thrown && (_placedLocally ||
                                     (freezeWhenPlaced && (!placeNeedsSupport || IsSupported())));

            if (place)
            {
                if (!_placedLocally)
                {
                    // Frozen here and now, where the player let go; the host is told this exact pose
                    // so every machine stops it in the same place.
                    _placedLocally = true;
                    _placedPosition = _rb.position;
                    _placedRotation = _rb.rotation;
                    SwitchRole(Role.Frozen);
                }

                _placeEpoch = epoch;
            }

            _actor.RPC_PropRelease(epoch, place,
                place ? _placedPosition : _rb.position,
                place ? _placedRotation : _rb.rotation);
        }

        public void RequestRest(int epoch)
        {
            if (ResolveActor())
                _actor.RPC_PropRest(epoch, _rb.position, _rb.rotation);
        }

        public bool LaunchLocal(Vector3 velocity, Vector3 spin)
        {
            if (_role != Role.SimulatesHere)
                return false;

            _gripped = false;
            _rb.useGravity = true;
            _rb.angularDamping = 0.05f;
            _rb.linearVelocity = velocity;
            _rb.angularVelocity = spin;
            return true;
        }

        public void IgnoreCollisionsWith(IReadOnlyList<Collider> others, bool ignore)
        {
            if (others == null)
                return;

            foreach (var mine in _solid)
            {
                if (mine == null)
                    continue;
                foreach (var other in others)
                {
                    if (other != null && other != mine)
                        Physics.IgnoreCollision(mine, other, ignore);
                }
            }
        }

        #endregion

        #region ITelekineticSimulation

        NetworkId ITelekineticSimulation.PublishedId => Live ? _actor.Object.Id : default;

        bool ITelekineticSimulation.IsSimulatedHere => _role == Role.SimulatesHere;

        ITelekineticTarget ITelekineticSimulation.Target => this;

        public void ReadPublishedPose(out Vector3 position, out Quaternion rotation,
            out Vector3 secondaryPosition, out Quaternion secondaryRotation, out Vector3 velocity)
        {
            position = _rb.position;
            rotation = _rb.rotation;
            secondaryPosition = position;
            secondaryRotation = rotation;
            velocity = _rb.linearVelocity;
        }

        #endregion
    }
}
#endif
