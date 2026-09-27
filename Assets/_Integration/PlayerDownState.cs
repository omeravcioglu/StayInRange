#if CMPSETUP_COMPLETE
using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Everything that is true about a player's body while they are dead: a synchronised ragdoll
    /// that teammates can lift, throw and catch, and a spectator camera for the player it belongs to.
    ///
    /// Runs on every machine. FpsNetworkBridge owns the networked facts (who holds the body, who
    /// simulates it, what phase it is in); this component is what those facts look like in the
    /// physics scene.
    ///
    /// ONE SIMULATION AT A TIME. The body is simulated on exactly one machine - whoever is physically
    /// handling it:
    ///
    ///   Loose  (lying, sliding, falling)  the dead player's own machine
    ///   Held   (floating in a grip)       the carrier's machine
    ///   Thrown (in flight)                the thrower's machine, until it lands or is caught
    ///
    /// That machine runs the full ragdoll and publishes the hips and chest pose; every other machine
    /// pins its copy's hips and chest to that pose and lets the limbs swing. The person handling the
    /// body therefore sees it respond with no network delay, and nobody else ever runs a competing
    /// simulation that could disagree about where it is.
    ///
    /// Who simulates is decided by the dead player's machine alone (the bridge's RPCs), which is what
    /// makes simultaneous grabs and catches resolve to exactly one winner.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRagdoll))]
    public class PlayerDownState : MonoBehaviour
    {
        /// <summary>The body this machine is currently the simulation of, if any. Its pose is what
        /// the local player's bridge publishes.</summary>
        public static PlayerDownState LocallySimulated { get; private set; }

        FpsNetworkBridge _bridge;
        PlayerRagdoll _ragdoll;
        SpectatorController _spectator;

        bool _down;
        bool _canRagdoll;
        bool _simulatingHere;
        bool _following;

        // Smoothed pose a following copy is steered along.
        bool _haveFollowPose;
        Vector3 _followHips;
        Quaternion _followHipsRotation;
        Vector3 _followChest;
        Quaternion _followChestRotation;
        Vector3 _lastReplicatedVelocity;

        // The last pose actually received, for coasting through a handover (see TryGetFollowPose).
        const float PoseGraceSeconds = 0.4f;
        bool _haveLastPose;
        float _lastPoseTime;
        Vector3 _lastPoseHips;
        Quaternion _lastPoseHipsRotation;
        Vector3 _lastPoseChest;
        Quaternion _lastPoseChestRotation;

        // Grip bookkeeping, only meaningful where the body is simulated.
        bool _gripped;
        float _stuckSeconds;
        Vector3 _chestLocalForward = Vector3.forward;

        // Flight bookkeeping for a thrown body.
        int _flightEpoch = int.MinValue;
        float _flightSeconds;
        float _settledSeconds;
        bool _restRequested;

        public bool IsDown => _down;
        public bool CanRagdoll => _canRagdoll;
        public PlayerRagdoll Ragdoll => _ragdoll;
        public FpsNetworkBridge Bridge => _bridge;
        public bool IsSimulatedHere => _down && _simulatingHere;

        /// <summary>Where the body is on this machine - its hips, which is also where its collar is measured.</summary>
        public Vector3 BodyPosition => _ragdoll != null ? _ragdoll.BodyPosition : transform.position;

        public void Bind(FpsNetworkBridge bridge, Transform body)
        {
            _bridge = bridge;
            _ragdoll = GetComponent<PlayerRagdoll>();
            _ragdoll.Bind(body);
        }

        void OnDestroy()
        {
            if (LocallySimulated == this)
                LocallySimulated = null;
        }

        #region Down and up

        /// <summary>
        /// Called on every machine the first time this player is seen to be dead.
        ///
        /// <paramref name="owner"/> is true only where the player is actually playing; that is the
        /// machine that loses its controls and gains a spectator view.
        /// </summary>
        public void EnterDown(bool owner, Vector3 velocity)
        {
            if (_down)
                return;
            _down = true;
            // Neither role yet: the first Tick picks one and goes through SwitchRole, which is what
            // registers this body as the one this machine publishes when it is the simulator.
            _simulatingHere = false;
            _following = false;
            _haveFollowPose = false;
            _haveLastPose = false;
            _lastReplicatedVelocity = Vector3.zero;
            _gripped = false;
            _flightEpoch = int.MinValue;

            _canRagdoll = _ragdoll.Activate(velocity);
            if (_canRagdoll)
            {
                _ragdoll.ApplyBoneLayer(LayerMask.NameToLayer("Player"));

                // Which way the character was facing, expressed in the chest bone's own axes. Bone
                // axes on these rigs point anywhere, so this is what lets the grip turn the body to
                // face its carrier.
                var chest = _ragdoll.ChestBody;
                if (chest != null)
                    _chestLocalForward = Quaternion.Inverse(chest.rotation) * _bridge.transform.forward;
            }

            if (!owner)
                return;

            _spectator = SpectatorController.Ensure();
            _spectator.Begin(_bridge);
        }

        /// <summary>Called on every machine when the player is revived.</summary>
        public void ExitDown(bool owner)
        {
            if (!_down)
                return;
            _down = false;
            _gripped = false;

            if (LocallySimulated == this)
                LocallySimulated = null;

            if (_canRagdoll)
            {
                _ragdoll.ResetLimbDamping();
                _ragdoll.Deactivate();
            }

            if (!owner)
                return;

            if (_spectator != null)
                _spectator.End();
            _spectator = null;
        }

        #endregion

        #region Who simulates

        /// <summary>
        /// Per frame, on every machine, from the bridge's LateUpdate: works out whether this machine
        /// is the one simulating the body and switches roles when that changes.
        /// </summary>
        public void Tick()
        {
            if (!_down || !_canRagdoll)
                return;

            bool simulate = ShouldSimulateHere();
            bool follow = !simulate && TryGetFollowPose(out _, out _, out _, out _, out _);

            if (simulate != _simulatingHere || follow != _following)
                SwitchRole(simulate, follow);
        }

        bool ShouldSimulateHere()
        {
            var runner = _bridge.Runner;
            if (runner == null || _bridge.Object == null || !_bridge.Object.IsValid)
                return true;

            return _bridge.BodySimulator == runner.LocalPlayer;
        }

        void SwitchRole(bool simulate, bool follow)
        {
            _simulatingHere = simulate;
            _following = follow;

            if (simulate)
            {
                // Take over from wherever this copy was, moving the way the previous simulation said
                // it was moving - a body caught mid-flight keeps flying into the catcher's grip.
                _ragdoll.SetFollowing(false, _lastReplicatedVelocity);
                LocallySimulated = this;
                _stuckSeconds = 0f;
                return;
            }

            if (LocallySimulated == this)
                LocallySimulated = null;

            _gripped = false;
            _ragdoll.ResetLimbDamping();

            // With no replicated pose to follow - the first moments after a death, before the owner
            // has published anything - the copy keeps tumbling on its own, moving the way the body
            // was last known to move, rather than freezing in place.
            _ragdoll.SetFollowing(follow, _lastReplicatedVelocity);
            _haveFollowPose = false;
        }

        /// <summary>
        /// The pose to follow: the latest one published, or - for a moment after the stream stops -
        /// the last one carried forward ballistically.
        ///
        /// The stream stops briefly on every handover: when a catch moves the simulation from the
        /// thrower to the catcher, the thrower stops publishing a round trip before the catcher
        /// starts. Coasting through that gap on the last known flight keeps the body flying smoothly
        /// on everyone else's screen instead of dropping out of the air and snapping back.
        /// </summary>
        bool TryGetFollowPose(out Vector3 hips, out Quaternion hipsRotation,
            out Vector3 chest, out Quaternion chestRotation, out Vector3 velocity)
        {
            if (TryReadReplicatedPose(out hips, out hipsRotation, out chest, out chestRotation, out velocity))
            {
                _haveLastPose = true;
                _lastPoseTime = Time.time;
                _lastPoseHips = hips;
                _lastPoseHipsRotation = hipsRotation;
                _lastPoseChest = chest;
                _lastPoseChestRotation = chestRotation;
                _lastReplicatedVelocity = velocity;
                return true;
            }

            float age = Time.time - _lastPoseTime;
            if (!_haveLastPose || !_following || age > PoseGraceSeconds)
                return false;

            var drift = _lastReplicatedVelocity * age + 0.5f * age * age * Physics.gravity;
            hips = _lastPoseHips + drift;
            chest = _lastPoseChest + drift;
            hipsRotation = _lastPoseHipsRotation;
            chestRotation = _lastPoseChestRotation;
            velocity = _lastReplicatedVelocity + Physics.gravity * age;
            return true;
        }

        /// <summary>The pose the simulating machine last published for this body, if it has published one.</summary>
        bool TryReadReplicatedPose(out Vector3 hips, out Quaternion hipsRotation,
            out Vector3 chest, out Quaternion chestRotation, out Vector3 velocity)
        {
            hips = chest = velocity = default;
            hipsRotation = chestRotation = Quaternion.identity;

            if (_bridge.Object == null || !_bridge.Object.IsValid)
                return false;

            var simulator = FpsNetworkBridge.FindByPlayer(_bridge.BodySimulator);
            if (simulator == null || simulator.Object == null || !simulator.Object.IsValid)
                return false;
            if (simulator.DrivenBody != _bridge.Object.Id)
                return false;

            hips = simulator.DrivenHipsPosition;
            hipsRotation = simulator.DrivenHipsRotation;
            chest = simulator.DrivenChestPosition;
            chestRotation = simulator.DrivenChestRotation;
            velocity = simulator.DrivenVelocity;
            return true;
        }

        #endregion

        #region Physics

        void FixedUpdate()
        {
            if (!_down || !_canRagdoll || !_ragdoll.IsActive)
                return;

            float dt = Time.fixedDeltaTime;

            if (_simulatingHere)
                SimulateStep(dt);
            else if (_following)
                FollowStep(dt);
        }

        void FollowStep(float dt)
        {
            if (!TryGetFollowPose(out var hips, out var hipsRotation, out var chest,
                    out var chestRotation, out var velocity))
                return;

            var network = PlayerTuning.Active.network;

            // Led by the replicated velocity, which hides most of the network delay on a moving body
            // without inventing motion for a still one.
            var lead = velocity * network.velocityLead;
            var targetHips = hips + lead;
            var targetChest = chest + lead;

            if (!_haveFollowPose ||
                (targetHips - _followHips).sqrMagnitude > network.snapDistance * network.snapDistance)
            {
                _followHips = targetHips;
                _followChest = targetChest;
                _followHipsRotation = hipsRotation;
                _followChestRotation = chestRotation;
                _haveFollowPose = true;
            }
            else
            {
                float t = 1f - Mathf.Exp(-network.followSharpness * dt);
                _followHips = Vector3.Lerp(_followHips, targetHips, t);
                _followChest = Vector3.Lerp(_followChest, targetChest, t);
                _followHipsRotation = Quaternion.Slerp(_followHipsRotation, hipsRotation, t);
                _followChestRotation = Quaternion.Slerp(_followChestRotation, chestRotation, t);
            }

            _ragdoll.DriveFollower(_followHips, _followHipsRotation, _followChest, _followChestRotation);
        }

        void SimulateStep(float dt)
        {
            var tuning = PlayerTuning.Active;
            var phase = (BodyPhase)_bridge.BodyPhaseValue;

            var target = Vector3.zero;
            var facing = Quaternion.identity;
            var carrierVelocity = Vector3.zero;
            bool gripping = phase == BodyPhase.Held &&
                            BodyTelekinesis.Local != null &&
                            BodyTelekinesis.Local.TryGetGripTarget(_bridge, out target, out facing,
                                out carrierVelocity);

            if (gripping != _gripped)
            {
                _gripped = gripping;
                if (gripping)
                    _ragdoll.SetLimbDamping(tuning.carry.limbDampingWhileHeld);
                else
                    _ragdoll.ResetLimbDamping();
                _stuckSeconds = 0f;
            }

            if (gripping)
            {
                ApplyGrip(target, facing, carrierVelocity, tuning.carry, dt);
                return;
            }

            if (phase == BodyPhase.Thrown)
            {
                ApplyLeash(tuning.tether, dt);
                TrackFlight(tuning.catching, dt);
            }
        }

        /// <summary>
        /// The telekinetic grip: a damped spring pulling the CHEST to the float point, so the body
        /// hangs from its collar with its limbs dangling below.
        ///
        /// The spring is measured relative to the carrier's own velocity, so walking with a body does
        /// not leave it trailing behind; the acceleration cap is what gives it weight and keeps a
        /// fast flick of the aim from yanking it violently.
        /// </summary>
        void ApplyGrip(Vector3 target, Quaternion facing, Vector3 carrierVelocity,
            PlayerTuning.CarrySettings carry, float dt)
        {
            var chest = _ragdoll.ChestBody;
            var hips = _ragdoll.HipsBody;
            if (chest == null)
                return;

            float mass = Mathf.Max(1f, _ragdoll.TotalMass);
            float omega = 2f * Mathf.PI * carry.gripFrequency;

            var error = target - chest.position;
            var relativeVelocity = chest.linearVelocity - carrierVelocity;
            var acceleration = omega * omega * error - 2f * carry.gripDamping * omega * relativeVelocity;
            acceleration = Vector3.ClampMagnitude(acceleration, carry.maxGripAcceleration);

            // Speed limit relative to the carrier: brake rather than accelerate past it.
            if (relativeVelocity.magnitude > carry.maxGripSpeed &&
                Vector3.Dot(acceleration, relativeVelocity) > 0f)
            {
                acceleration = Vector3.ProjectOnPlane(acceleration, relativeVelocity.normalized);
            }

            // The whole body's weight and pull go through the chest, like a hook through the collar.
            // The joints carry it to the rest of the skeleton, which is what makes it hang and sway.
            var support = -Physics.gravity * carry.weightSupport;
            chest.AddForce((acceleration + support) * mass, ForceMode.Force);

            // A little damping on the hips takes the pendulum out of the legs without freezing them.
            if (hips != null && hips != chest)
                hips.AddForce(-0.8f * (hips.linearVelocity - carrierVelocity) * hips.mass, ForceMode.Force);

            ApplyUpright(chest, hips, facing, carry.uprightFrequency);

            // Wedged somewhere the spring cannot pull it free: let go, rather than winding the spring
            // up until it drags the body through the wall.
            if (error.magnitude > carry.breakDistance)
            {
                _stuckSeconds += dt;
                if (_stuckSeconds >= carry.breakSeconds)
                {
                    _stuckSeconds = 0f;
                    BodyTelekinesis.Local?.OnGripBroken(_bridge);
                }
            }
            else
            {
                _stuckSeconds = 0f;
            }
        }

        /// <summary>
        /// Keeps the torso hanging upright and turned towards the carrier. Measured geometrically
        /// (hips to chest for "up") rather than from bone axes, which point anywhere on these rigs.
        /// </summary>
        void ApplyUpright(Rigidbody chest, Rigidbody hips, Quaternion facing, float frequency)
        {
            if (frequency <= 0f)
                return;

            float omega = 2f * Mathf.PI * frequency;

            var up = hips != null && hips != chest
                ? (chest.position - hips.position).normalized
                : chest.rotation * Vector3.up;
            var uprightError = Vector3.Cross(up, Vector3.up);

            var forward = Vector3.ProjectOnPlane(chest.rotation * _chestLocalForward, Vector3.up);
            var wantedForward = Vector3.ProjectOnPlane(facing * Vector3.forward, Vector3.up);
            var facingError = forward.sqrMagnitude > 0.0001f && wantedForward.sqrMagnitude > 0.0001f
                ? Vector3.Cross(forward.normalized, wantedForward.normalized)
                : Vector3.zero;

            var angular = omega * omega * (uprightError + 0.6f * facingError) -
                          2f * omega * chest.angularVelocity;
            chest.AddTorque(Vector3.ClampMagnitude(angular, 60f), ForceMode.Acceleration);
        }

        /// <summary>
        /// The collar as a leash on a flying body: once the throw would carry it past the collar's
        /// reach from any teammate, the outward motion is taken off and it is pulled back in - so a
        /// throw can never fling someone far enough to fail the whole team.
        /// </summary>
        void ApplyLeash(PlayerTuning.TetherSettings tether, float dt)
        {
            if (!tether.leashThrownBodies)
                return;

            var manager = TeamDistanceManager.Instance;
            float max = manager != null ? manager.MaxPlayerDistance : 30f;
            float length = Mathf.Max(2f, max - tether.leashSlack);

            var hips = _ragdoll.HipsBody;
            if (hips == null)
                return;

            var correction = Vector3.zero;
            foreach (var other in FpsNetworkBridge.All)
            {
                if (other == null || other == _bridge || other.Object == null || !other.Object.IsValid)
                    continue;

                var offset = hips.position - other.GetNetworkAnchorPosition();
                float distance = offset.magnitude;
                if (distance <= length || distance < 0.001f)
                    continue;

                var outward = offset / distance;
                float excess = distance - length;
                correction -= outward * (excess * tether.leashStiffness);

                float outwardSpeed = Vector3.Dot(hips.linearVelocity, outward);
                if (outwardSpeed > 0f)
                    correction -= outward * (outwardSpeed / Mathf.Max(dt, 0.001f)) * 0.5f;
            }

            if (correction == Vector3.zero)
                return;

            // The whole body together, so the collar stops it rather than stretching it.
            foreach (var body in _ragdoll.Bodies)
            {
                if (body != null && !body.isKinematic)
                    body.AddForce(correction, ForceMode.Acceleration);
            }
        }

        /// <summary>
        /// Watches a thrown body until it lands, then hands it back to its owner's machine. Only the
        /// thrower does this: a body the owner is simulating is already where it belongs.
        /// </summary>
        void TrackFlight(PlayerTuning.CatchSettings catching, float dt)
        {
            if (_bridge.HasStateAuthority)
                return;

            int epoch = _bridge.BodyEpoch;
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
            float speed = _ragdoll.BodyVelocity.magnitude;
            _settledSeconds = speed < catching.settleSpeed ? _settledSeconds + dt : 0f;

            if (_settledSeconds >= catching.settleSeconds || _flightSeconds >= catching.maxFlightSeconds)
            {
                _restRequested = true;
                _bridge.RequestBodyRest(epoch);
            }
        }

        #endregion

        #region Simulating machine API

        /// <summary>Launches the body, on the machine simulating it. Returns false anywhere else.</summary>
        public bool LaunchLocal(Vector3 velocity, Vector3 spin)
        {
            if (!_down || !_canRagdoll || !_simulatingHere)
                return false;

            _gripped = false;
            _ragdoll.ResetLimbDamping();
            _ragdoll.Launch(velocity, spin);
            return true;
        }

        /// <summary>Moves a downed body somewhere - team-failure teleports, and a future revive.</summary>
        public void TeleportBody(Vector3 position)
        {
            if (!_down || !_canRagdoll)
                return;

            _ragdoll.MoveTo(position);
            _haveFollowPose = false;
        }

        /// <summary>The pose this machine publishes while it simulates the body.</summary>
        public void ReadPublishedPose(out Vector3 hips, out Quaternion hipsRotation,
            out Vector3 chest, out Quaternion chestRotation, out Vector3 velocity)
        {
            var hipsBody = _ragdoll.HipsBody;
            var chestBody = _ragdoll.ChestBody;

            hips = hipsBody != null ? hipsBody.position : BodyPosition;
            hipsRotation = hipsBody != null ? hipsBody.rotation : Quaternion.identity;
            chest = chestBody != null ? chestBody.position : hips;
            chestRotation = chestBody != null ? chestBody.rotation : hipsRotation;
            velocity = _ragdoll.BodyVelocity;
        }

        #endregion
    }

    /// <summary>What is happening to a dead body. Replicated as a byte on the body's own bridge.</summary>
    public enum BodyPhase : byte
    {
        /// <summary>Lying, sliding or falling on its own. Simulated by the dead player's machine.</summary>
        Loose = 0,

        /// <summary>Floating in a teammate's telekinetic grip. Simulated by the carrier's machine.</summary>
        Held = 1,

        /// <summary>Flying after a throw or a drop, catchable. Simulated by the thrower's machine.</summary>
        Thrown = 2,
    }
}
#endif
