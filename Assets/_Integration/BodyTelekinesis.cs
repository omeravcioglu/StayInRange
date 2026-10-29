#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali
{
    /// <summary>
    /// The local player's telekinesis: lifting a dead teammate's body - or any TelekineticProp, such
    /// as a ladder - carrying it, throwing it with a charged throw, and catching it out of the air.
    /// A prop can also be turned and tilted while held, and placed where it is.
    ///
    /// Exists only on the machine of the player it belongs to (the bridge adds it for the local
    /// player), and works identically in first and third person because it reads the view through
    /// DualPlayerController rather than from either controller directly.
    ///
    /// AUTHORITY. Nothing here decides who holds anything. Every pickup, catch, drop and throw is a
    /// REQUEST - to the dead player's own machine for a body (FpsNetworkBridge RPCs), to the host for
    /// a prop (NetworkWorldActor RPCs) - which grants it to exactly one player; this component reacts
    /// when the grant replicates back. What it owns is the feel: the grip target the simulation pulls
    /// the held thing towards, the aim and charge, and the prompt.
    ///
    /// INPUT. It has its own actions, built from the bindings in PlayerTuning, rather than borrowing
    /// Cowsins' or Malbers', because it has to work whichever of the two is active. While something is
    /// held, DualPlayerController mutes the actions of both controllers that share these keys
    /// (firing, aiming, melee, interact, reload, the mouse wheel and so on), which is what stops a
    /// throw from also firing the gun or punching.
    /// </summary>
    [DisallowMultipleComponent]
    public class BodyTelekinesis : MonoBehaviour
    {
        public static BodyTelekinesis Local { get; private set; }

        enum Mode
        {
            Idle,
            Holding,
            Aiming,
        }

        const float CollisionRefreshSeconds = 0.5f;

        /// <summary>Extra room in front of a held prop's furthest reach, so turning it never sweeps the camera.</summary>
        const float PropHoldMargin = 0.5f;

        FpsNetworkBridge _self;
        DualPlayerController _dual;

        InputAction _interact;
        InputAction _throwReady;
        InputAction _charge;
        InputAction _cancel;
        InputAction _turn;
        InputAction _tilt;
        string _bindingsKey;

        Mode _mode;
        ITelekineticTarget _held;

        // Something this player let go of, until its authority confirms. Keeps the grip from pulling on
        // a body that is already on its way out of our hands.
        ITelekineticTarget _releasedLocally;
        float _releaseResendAt;
        bool _releaseWasThrow;

        ITelekineticTarget _pending;
        bool _pendingCatch;
        float _pendingUntil;

        ITelekineticTarget _target;
        bool _targetIsCatch;

        bool _charging;
        float _chargeStartedAt;
        float _charge01;

        // How a held prop is turned, relative to the way the carrier faces: it turns with them, and
        // the wheel adds to it.
        Quaternion _holdRotation = Quaternion.identity;

        readonly List<Collider> _ownColliders = new List<Collider>();
        ITelekineticTarget _collisionTarget;
        float _restoreCollisionsAt = -1f;
        float _nextCollisionRefresh;

        BodyCarryHud _hud;
        ThrowTrajectory _trajectory;

        public bool IsHolding => _held != null && !ReferenceEquals(_held, _releasedLocally);
        public ITelekineticTarget HeldTarget => IsHolding ? _held : null;
        public FpsNetworkBridge HeldBody => IsHolding ? _held as FpsNetworkBridge : null;
        public bool IsAiming => IsHolding && _mode == Mode.Aiming;

        public void Bind(FpsNetworkBridge self)
        {
            _self = self;
            Local = this;
        }

        void OnDestroy()
        {
            if (Local == this)
                Local = null;

            SetInputEnabled(false);
            foreach (var action in Actions())
                action?.Dispose();

            if (_dual != null)
            {
                _dual.SetCarrying(false, 1f);
                _dual.SetInteractClaimed(false);
            }

            RestoreCollisions();
            _trajectory?.Destroy();
            if (_hud != null)
                Destroy(_hud.gameObject);
        }

        void OnDisable()
        {
            SetInputEnabled(false);
        }

        /// <summary>
        /// Interface references skip Unity's destroyed-object check: a body whose player left or a prop
        /// that was destroyed still compares unequal to null.
        /// </summary>
        static bool Exists(ITelekineticTarget target) =>
            target is UnityEngine.Object unityObject ? unityObject != null : target != null;

        #region Frame

        void Update()
        {
            if (_self == null || _self.Object == null || !_self.Object.IsValid)
                return;

            EnsureInput();
            _dual = _self.DualPlayer;
            if (_hud == null)
                _hud = BodyCarryHud.Create();
            if (_trajectory == null)
                _trajectory = new ThrowTrajectory();

            RefreshHeldFromNetwork();
            RestoreCollisionsIfDue();

            bool usable = !_self.IsDead && _dual != null && !_dual.IsSuspended;
            if (!usable)
            {
                // Dead, or between lives. Whatever decides over the held thing releases it once it sees
                // the death, so there is nothing to send - only local state to clear.
                if (_held != null)
                    LoseHeld(silent: true);
                _pending = null;
                _target = null;
                _dual?.SetInteractClaimed(false);
                _hud.Hide();
                _trajectory.Hide();
                return;
            }

            ExpirePending();
            ResendReleaseIfIgnored();

            // A menu over the game takes the keys: E would drop what is held and letting go of a charge
            // would throw it. It stays held; a charge in progress is called off.
            if (UI.UiInput.MenuOpen)
            {
                _charging = false;
            }
            else
            {
                switch (_mode)
                {
                    case Mode.Idle:
                        UpdateIdle();
                        break;
                    case Mode.Holding:
                        UpdateHolding();
                        break;
                    case Mode.Aiming:
                        UpdateAiming();
                        break;
                }
            }

            // While something is in reach, E is ours alone - see DualPlayerController.SetInteractClaimed.
            _dual.SetInteractClaimed(_mode == Mode.Idle && (_target != null || _pending != null));

            RefreshCollisionIgnore();
            UpdateFeedback();
        }

        void UpdateIdle()
        {
            _charging = false;

            // On a ladder the crosshair is on the ladder itself, and E must not lift it from under you.
            if (_dual.IsClimbing)
            {
                _target = null;
                return;
            }

            FindTarget(out _target, out _targetIsCatch);

            if (_target != null && _pending == null && Pressed(_interact) && InputAllowed())
            {
                _pending = _target;
                _pendingCatch = _targetIsCatch;
                _pendingUntil = Time.time + PlayerTuning.Active.catching.requestTimeout;
                _target.RequestHold(_target.Epoch, _targetIsCatch);
            }
        }

        void UpdateHolding()
        {
            _target = null;
            _charging = false;

            if (Pressed(_interact))
            {
                if (!TryReviveHeld())
                    Release(throwIt: false);
                return;
            }

            UpdateHoldRotation();

            if (Pressed(_throwReady) && InputAllowed() && _held.Throwable)
                _mode = Mode.Aiming;
        }

        void UpdateAiming()
        {
            _target = null;

            if (Pressed(_interact))
            {
                if (!TryReviveHeld())
                    Release(throwIt: false);
                return;
            }

            if (Pressed(_throwReady) || Pressed(_cancel))
            {
                _charging = false;
                _mode = Mode.Holding;
                _dual.SetThirdPersonAimView(false);
                return;
            }

            UpdateHoldRotation();

            // Third person aims from the eyes. Requested every frame rather than once, so it also
            // engages when a switch into third person lands mid-aim; repeated requests do nothing.
            if (_dual.IsThirdPerson)
                _dual.SetThirdPersonAimView(true);

            var throwing = PlayerTuning.Active.throwing;

            if (!_charging && Pressed(_charge) && InputAllowed())
            {
                _charging = true;
                _chargeStartedAt = Time.time;
            }

            if (_charging)
            {
                _charge01 = Mathf.Clamp01((Time.time - _chargeStartedAt) / throwing.chargeDuration);
                if (Released(_charge) || !Held(_charge))
                {
                    _charging = false;
                    Throw(_charge01);
                }
            }
            else
            {
                _charge01 = 0f;
            }
        }

        #endregion

        #region Grants from the network

        /// <summary>
        /// Picks up grants, losses and handovers from the replicated state: whichever body or prop
        /// names this player as its holder is the one being held.
        /// </summary>
        void RefreshHeldFromNetwork()
        {
            var me = _self.Owner;
            var granted = FindGranted(me);

            // Our release has been processed: it no longer names us.
            if (_releasedLocally != null &&
                (!Exists(_releasedLocally) || !_releasedLocally.IsValidTarget || _releasedLocally.Holder != me))
                _releasedLocally = null;

            if (ReferenceEquals(granted, _releasedLocally))
                granted = null;

            if (ReferenceEquals(granted, _held))
                return;

            if (_held != null)
                LoseHeld(silent: ReferenceEquals(_held, _releasedLocally));

            if (granted != null)
                GainHeld(granted);
        }

        ITelekineticTarget FindGranted(PlayerRef me)
        {
            foreach (var body in FpsNetworkBridge.All)
            {
                if (body == null || body == _self || body.Object == null || !body.Object.IsValid)
                    continue;
                if (!body.IsDead || body.CarriedBy != me)
                    continue;
                return body;
            }

            foreach (var prop in TelekineticProp.All)
            {
                if (prop != null && prop.IsValidTarget && prop.Holder == me)
                    return prop;
            }

            return null;
        }

        void GainHeld(ITelekineticTarget target)
        {
            bool wasCatch = ReferenceEquals(_pending, target) && _pendingCatch;
            _pending = null;
            _held = target;
            _mode = Mode.Holding;
            _charging = false;
            _charge01 = 0f;
            ResetHoldRotation(target);

            HandBackOtherSimulations(target);

            if (_dual != null)
                _dual.SetCarrying(true, PlayerTuning.Active.carry.carrySpeedMultiplier);

            IgnoreCollisions(target);

            GameSfx.PlayShared(SfxId.MagicCast, target.Position);
            if (wasCatch)
                _hud?.Toast("Caught!", new Color(0.55f, 1f, 0.6f));
        }

        /// <summary>
        /// Anything this player threw a moment ago and is still simulating in flight is handed back -
        /// a body to its owner, a prop to the host: one machine simulates one thing at a time, and
        /// only one of them can be published.
        /// </summary>
        void HandBackOtherSimulations(ITelekineticTarget keep)
        {
            foreach (var body in FpsNetworkBridge.All)
            {
                if (body == null || body == _self || ReferenceEquals(body, keep) ||
                    body.Object == null || !body.Object.IsValid)
                    continue;

                ITelekineticTarget asTarget = body;
                if (asTarget.IsLocallySimulated)
                    body.RequestBodyRest(body.BodyEpoch);
            }

            foreach (var prop in TelekineticProp.All)
            {
                if (prop != null && !ReferenceEquals(prop, keep) && prop.IsLocallySimulated)
                    prop.RequestRest(prop.Epoch);
            }
        }

        void LoseHeld(bool silent)
        {
            _held = null;
            _mode = Mode.Idle;
            _charging = false;
            _charge01 = 0f;

            if (_dual != null)
            {
                _dual.SetCarrying(false, 1f);
                // Nothing left to aim. A no-op if a throw has already asked for a delayed return.
                _dual.SetThirdPersonAimView(false);
            }

            ScheduleCollisionRestore();

            if (!silent)
                _hud?.Toast("Lost your grip", new Color(1f, 0.6f, 0.4f));
        }

        void ExpirePending()
        {
            if (_pending == null || Time.time < _pendingUntil)
                return;

            // Given to somebody else, or it landed and moved on before the request got there. Only a
            // catch is worth telling the player about.
            if (_pendingCatch)
                _hud?.Toast("Missed!", new Color(1f, 0.45f, 0.35f));
            _pending = null;
        }

        /// <summary>
        /// A release that was never processed - lost to a stale epoch, most likely - is sent again with
        /// the current one, so nothing can stay stuck to a player who let go of it.
        /// </summary>
        void ResendReleaseIfIgnored()
        {
            if (_releasedLocally == null || Time.time < _releaseResendAt)
                return;

            if (!Exists(_releasedLocally))
            {
                _releasedLocally = null;
                return;
            }

            _releaseResendAt = Time.time + PlayerTuning.Active.catching.requestTimeout;
            _releasedLocally.RequestRelease(_releasedLocally.Epoch, _releaseWasThrow);
        }

        #endregion

        #region Release and throw

        void Release(bool throwIt, Vector3 velocity = default, Vector3 spin = default)
        {
            if (_held == null)
                return;

            var held = _held;
            if (throwIt)
            {
                // Launched on this machine, which is simulating it while it is held - so the throw
                // starts on this frame, not a round trip later. The authority only hears that it is
                // now in flight, and this machine keeps simulating it until it lands or is caught.
                held.LaunchLocal(velocity, spin);
                GameSfx.PlayShared(SfxId.MagicCast, held.Position);
            }

            _releasedLocally = held;
            _releaseWasThrow = throwIt;
            _releaseResendAt = Time.time + PlayerTuning.Active.catching.requestTimeout;
            held.RequestRelease(held.Epoch, throwIt);

            // Back over the shoulder: after a moment when it was a throw, so it can be watched
            // leaving; at once for a drop.
            _dual?.SetThirdPersonAimView(false,
                throwIt ? PlayerTuning.Active.cameraTransition.aimViewReturnDelay : 0f);

            LoseHeld(silent: true);
        }

        /// <summary>
        /// E beside a revive station revives the held body instead of dropping it, so a teammate
        /// never has to be put down first.
        ///
        /// The body is let go here as a drop would be, but no release is sent: the revive clears its
        /// carrier on the owner's side, and marking it released keeps that from reading as a lost
        /// grip. Should the revive never land, the usual resend drops the body instead.
        /// </summary>
        bool TryReviveHeld()
        {
            if (!(_held is FpsNetworkBridge body))
                return false;

            var station = ReviveStation.FindFor(body);
            if (station == null || !station.TryRevive(body, _self.transform))
                return false;

            _releasedLocally = body;
            _releaseWasThrow = false;
            _releaseResendAt = Time.time + PlayerTuning.Active.catching.requestTimeout;

            LoseHeld(silent: true);
            return true;
        }

        void Throw(float charge01)
        {
            if (_held == null || !_held.Throwable || _dual == null || !_dual.TryGetAim(out _, out var forward))
                return;

            var throwing = PlayerTuning.Active.throwing;
            var velocity = ComputeThrowVelocity(forward, charge01, throwing);
            // A body tumbles; a prop flies the way it was aimed, so a ladder thrown to a friend
            // arrives the right way round to catch.
            var spin = _held.SupportsRotation ? Vector3.zero : UnityEngine.Random.insideUnitSphere * throwing.launchSpin;
            Release(throwIt: true, velocity, spin);
        }

        Vector3 ComputeThrowVelocity(Vector3 forward, float charge01, PlayerTuning.ThrowSettings throwing)
        {
            float shaped = Mathf.Pow(Mathf.Clamp01(charge01), throwing.chargeExponent);
            float speed = Mathf.Lerp(throwing.minThrowForce, throwing.maxThrowForce, shaped);

            var direction = (forward + Vector3.up * throwing.upwardBias).normalized;
            var carrier = _dual != null ? _dual.GetBodyVelocity() : Vector3.zero;
            return direction * speed + carrier * throwing.inheritCarrierVelocity;
        }

        /// <summary>Called by the simulation when what is held has been wedged too long to pull free.</summary>
        public void OnGripBroken(ITelekineticTarget target)
        {
            if (target == null || !ReferenceEquals(target, _held))
                return;

            Release(throwIt: false);
            _hud?.Toast("The grip slipped", new Color(1f, 0.6f, 0.4f));
        }

        #endregion

        #region Grip target

        /// <summary>
        /// Where the held thing should float, for the simulation on this machine - a body's collar, a
        /// prop's centre - and which way it should face.
        ///
        /// In front of the eyes in first person and in front of the chest in third person, along the
        /// camera's aim either way, pulled back from any wall in the way so it is never pressed into
        /// geometry. A prop floats further out the bigger it is, so turning it never sweeps through
        /// the carrier's view, and keeps the orientation it was given relative to the carrier.
        /// </summary>
        public bool TryGetGripTarget(ITelekineticTarget held, out Vector3 target, out Quaternion facing,
            out Vector3 carrierVelocity)
        {
            target = default;
            facing = Quaternion.identity;
            carrierVelocity = default;

            if (held == null || !ReferenceEquals(held, _held) || ReferenceEquals(held, _releasedLocally) ||
                _dual == null)
                return false;
            if (!_dual.TryGetAim(out var origin, out var forward))
                return false;

            var carry = PlayerTuning.Active.carry;

            var flat = FlatForward(forward);
            var right = Vector3.Cross(Vector3.up, flat);

            // Pitch limited, so looking at your feet does not drive it into the floor.
            var aim = forward;
            aim.y = Mathf.Clamp(aim.y, -0.5f, 0.6f);
            aim.Normalize();

            float hold = carry.holdDistance;
            if (held.SupportsRotation)
                hold = Mathf.Max(hold, held.HoldReach + PropHoldMargin);
            float distance = hold * (_mode == Mode.Aiming ? 0.85f : 1f);
            var desired = origin + aim * distance + right * carry.holdOffset.x + Vector3.up * carry.holdOffset.y;

            var toDesired = desired - origin;
            float length = toDesired.magnitude;
            if (length > 0.01f &&
                Physics.SphereCast(origin, 0.25f, toDesired / length, out var hit, length,
                    HiddenSpawnUtility.DefaultSightBlockers(), QueryTriggerInteraction.Ignore) &&
                !IsPartOf(held, hit.collider))
            {
                desired = origin + toDesired / length * Mathf.Max(0.35f, hit.distance - carry.wallPadding);
            }

            target = desired;
            facing = held.SupportsRotation
                ? Quaternion.LookRotation(flat, Vector3.up) * _holdRotation
                : Quaternion.LookRotation(-flat, Vector3.up);
            carrierVelocity = _dual.GetBodyVelocity();
            return true;
        }

        Vector3 FlatForward(Vector3 forward)
        {
            var flat = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flat.sqrMagnitude < 0.0001f && _dual != null)
                flat = Vector3.ProjectOnPlane(_dual.transform.forward, Vector3.up);
            if (flat.sqrMagnitude < 0.0001f)
                flat = Vector3.forward;
            return flat.normalized;
        }

        /// <summary>
        /// A prop is held the way it was lying relative to the carrier - a ladder leaning on a wall
        /// comes away upright - squared up to the nearest turn step, so it can be put back square.
        /// </summary>
        void ResetHoldRotation(ITelekineticTarget target)
        {
            _holdRotation = Quaternion.identity;
            if (target == null || !target.SupportsRotation)
                return;

            var forward = _dual != null && _dual.TryGetAim(out _, out var aim) ? aim : transform.forward;
            var facing = Quaternion.LookRotation(FlatForward(forward), Vector3.up);
            _holdRotation = SnapToStep(Quaternion.Inverse(facing) * target.Rotation, TurnStep(target));
        }

        /// <summary>The wheel turns a held prop about the vertical; with the tilt key held it tips it towards or away from the carrier.</summary>
        void UpdateHoldRotation()
        {
            if (_held == null || !_held.SupportsRotation || !InputAllowed())
                return;

            int steps = WheelSteps(_turn);
            if (steps == 0)
                return;

            var axis = Held(_tilt) ? Vector3.right : Vector3.up;
            _holdRotation = Quaternion.AngleAxis(steps * TurnStep(_held), axis) * _holdRotation;
        }

        static float TurnStep(ITelekineticTarget target) =>
            target is TelekineticProp prop ? prop.TurnStepDegrees : 15f;

        static Quaternion SnapToStep(Quaternion rotation, float step)
        {
            if (step <= 0f)
                return rotation;

            var euler = rotation.eulerAngles;
            euler.x = Mathf.Round(euler.x / step) * step;
            euler.y = Mathf.Round(euler.y / step) * step;
            euler.z = Mathf.Round(euler.z / step) * step;
            return Quaternion.Euler(euler);
        }

        /// <summary>Whole wheel notches this frame. Some platforms report 120 per notch and some 1.</summary>
        static int WheelSteps(InputAction action)
        {
            if (action == null)
                return 0;

            float value = action.ReadValue<float>();
            if (Mathf.Abs(value) < 0.01f)
                return 0;

            int steps = Mathf.Abs(value) >= 60f ? Mathf.RoundToInt(value / 120f) : 0;
            return steps != 0 ? steps : (value > 0f ? 1 : -1);
        }

        #endregion

        #region Targeting

        /// <summary>
        /// What a press of the interact key would act on: whatever the crosshair is on first, then a
        /// flying body or prop inside catch range, then the loose one closest to the middle of the view.
        /// </summary>
        void FindTarget(out ITelekineticTarget best, out bool isCatch)
        {
            best = null;
            isCatch = false;

            // Whatever the crosshair is on comes first: pointing at something should lift it from a
            // normal distance, not only from standing on top of it.
            if (TryCrosshairTarget(out best, out isCatch))
                return;

            if (_dual == null || !_dual.TryGetAim(out var origin, out var forward))
                return;

            var tuning = PlayerTuning.Active;
            float inReach = Mathf.Max(tuning.carry.pickupDistance, tuning.catching.maxCatchDistance);
            float bestScore = float.MaxValue;

            foreach (var body in FpsNetworkBridge.All)
            {
                if (body == null || body == _self || body.Object == null || !body.Object.IsValid)
                    continue;
                if (!body.IsDead || body.IsCarried)
                    continue;

                var down = body.DownState;
                if (down == null || !down.IsDown || !down.CanRagdoll)
                    continue;

                Consider(body, down.BodyPosition, origin, forward, ref best, ref isCatch, ref bestScore);
            }

            foreach (var prop in TelekineticProp.All)
            {
                if (prop == null || !prop.IsValidTarget || prop.Holder != PlayerRef.None)
                    continue;

                // Out of reach even at its furthest end: not worth measuring.
                float limit = inReach + prop.HoldReach;
                if ((prop.Position - origin).sqrMagnitude > limit * limit)
                    continue;

                // A long prop is in reach when its nearest end is, not only its middle.
                Consider(prop, NearestPoint(prop, origin), origin, forward, ref best, ref isCatch, ref bestScore);
            }
        }

        void Consider(ITelekineticTarget candidate, Vector3 position, Vector3 origin, Vector3 forward,
            ref ITelekineticTarget best, ref bool isCatch, ref float bestScore)
        {
            var tuning = PlayerTuning.Active;
            bool flying = candidate.Phase == BodyPhase.Thrown;
            float maxDistance = flying ? tuning.catching.maxCatchDistance : tuning.carry.pickupDistance;
            float cone = flying ? tuning.catching.catchViewAngle : tuning.carry.pickupViewAngle;

            var offset = position - origin;
            float distance = offset.magnitude;
            if (distance > maxDistance)
                return;

            // Right at your feet the angle means nothing - looking straight ahead still counts.
            float angle = distance > 0.9f ? Vector3.Angle(forward, offset) : 0f;
            if (angle > cone)
                return;

            if (Physics.Linecast(origin, position, out var hit, HiddenSpawnUtility.DefaultSightBlockers(),
                    QueryTriggerInteraction.Ignore) && hit.distance < distance - 0.3f &&
                !IsPartOf(candidate, hit.collider))
                return;

            float score = angle / cone + distance / maxDistance - (flying ? 1f : 0f);
            if (score >= bestScore)
                return;

            bestScore = score;
            best = candidate;
            isCatch = flying;
        }

        /// <summary>The point of a prop nearest to a position, for judging reach.</summary>
        static Vector3 NearestPoint(ITelekineticTarget target, Vector3 from)
        {
            var best = target.Position;
            float bestSq = (best - from).sqrMagnitude;

            var colliders = target.Colliders;
            for (int i = 0; i < colliders.Count; i++)
            {
                var collider = colliders[i];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                    continue;
                if (collider is MeshCollider mesh && !mesh.convex)
                    continue;

                var point = collider.ClosestPoint(from);
                float sq = (point - from).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = point;
                }
            }

            return best;
        }

        static bool IsPartOf(ITelekineticTarget target, Collider collider)
        {
            if (target == null || collider == null)
                return false;

            var colliders = target.Colliders;
            for (int i = 0; i < colliders.Count; i++)
            {
                if (colliders[i] == collider)
                    return true;
            }

            return false;
        }

        const float CrosshairRadius = 0.2f;
        const float OwnColliderRefreshSeconds = 2f;

        static readonly RaycastHit[] CrosshairHits = new RaycastHit[24];
        static readonly IComparer<RaycastHit> NearestFirst =
            Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));

        readonly HashSet<Collider> _ownColliderSet = new HashSet<Collider>();
        readonly List<Collider> _ownColliderScratch = new List<Collider>();
        float _ownCollidersRefreshedAt = -999f;

        /// <summary>
        /// The dead body or prop the crosshair is on, if it is in reach and nothing solid is in front of it.
        ///
        /// A slightly fat ray (limbs are thin) from the camera actually rendering the view, through
        /// every hit in order: this player's own colliders are skipped - in third person the ray
        /// passes through the character first - and the first thing that is neither a body nor a prop
        /// ends the search, so a wall between you and it blocks it. Range is measured from the player,
        /// so the third-person camera sitting metres behind them does not eat into it.
        /// </summary>
        bool TryCrosshairTarget(out ITelekineticTarget target, out bool isCatch)
        {
            target = null;
            isCatch = false;

            if (_dual == null || !_dual.TryGetCrosshairRay(out var ray))
                return false;

            var tuning = PlayerTuning.Active;
            float reach = Mathf.Max(tuning.carry.aimPickupDistance, tuning.catching.maxCatchDistance);

            // How far along the ray the player stands: zero in first person, the camera's distance
            // behind the character in third person.
            float behind = 0f;
            if (_dual.TryGetAim(out var origin, out _))
                behind = Mathf.Max(0f, Vector3.Dot(origin - ray.origin, ray.direction));

            RefreshOwnColliders();

            int count = Physics.SphereCastNonAlloc(ray, CrosshairRadius, CrosshairHits, reach + behind,
                ~0, QueryTriggerInteraction.Ignore);
            if (count <= 0)
                return false;
            System.Array.Sort(CrosshairHits, 0, count, NearestFirst);

            for (int i = 0; i < count; i++)
            {
                var collider = CrosshairHits[i].collider;
                if (collider == null || _ownColliderSet.Contains(collider))
                    continue;

                var owner = TargetOwning(collider);
                if (owner == null)
                    return false; // a wall, a crate, an enemy - the crosshair is on that, not on something to lift

                if (!owner.IsValidTarget || owner.Holder != PlayerRef.None)
                    return false;

                bool flying = owner.Phase == BodyPhase.Thrown;
                float fromPlayer = CrosshairHits[i].distance - behind;
                float limit = flying ? tuning.catching.maxCatchDistance : tuning.carry.aimPickupDistance;
                if (fromPlayer > limit)
                    return false;

                target = owner;
                isCatch = flying;
                return true;
            }

            return false;
        }

        /// <summary>The dead player whose ragdoll this collider is a bone of, or the prop it belongs to, or null.</summary>
        ITelekineticTarget TargetOwning(Collider collider)
        {
            foreach (var bridge in FpsNetworkBridge.All)
            {
                if (bridge == null || bridge == _self || !bridge.IsDead)
                    continue;

                var ragdoll = bridge.DownState != null ? bridge.DownState.Ragdoll : null;
                if (ragdoll == null)
                    continue;

                var colliders = ragdoll.Colliders;
                for (int i = 0; i < colliders.Count; i++)
                {
                    if (colliders[i] == collider)
                        return bridge;
                }
            }

            var prop = collider.GetComponentInParent<TelekineticProp>();
            return prop != null && IsPartOf(prop, collider) ? prop : null;
        }

        void RefreshOwnColliders()
        {
            if (Time.time - _ownCollidersRefreshedAt < OwnColliderRefreshSeconds)
                return;

            _ownCollidersRefreshedAt = Time.time;
            _ownColliderScratch.Clear();
            _dual?.CollectOwnColliders(_ownColliderScratch);
            _ownColliderSet.Clear();
            foreach (var collider in _ownColliderScratch)
            {
                if (collider != null)
                    _ownColliderSet.Add(collider);
            }
        }

        #endregion

        #region Collisions

        /// <summary>
        /// The carrier's own colliders pass through what they hold. Without this the floating body or
        /// prop shoves its carrier around - and in first person the carrier's capsule is right behind
        /// the grip point.
        /// </summary>
        void IgnoreCollisions(ITelekineticTarget target)
        {
            if (_collisionTarget != null && !ReferenceEquals(_collisionTarget, target))
                RestoreCollisions();

            _ownColliders.Clear();
            _dual?.CollectOwnColliders(_ownColliders);

            _collisionTarget = target;
            _restoreCollisionsAt = -1f;
            _nextCollisionRefresh = Time.time + CollisionRefreshSeconds;
            target.IgnoreCollisionsWith(_ownColliders, true);
        }

        /// <summary>
        /// Re-applied every so often while holding. Switching between first and third person enables
        /// a different set of colliders, and Unity forgets ignored pairs when a collider is toggled.
        /// </summary>
        void RefreshCollisionIgnore()
        {
            if (!IsHolding || !ReferenceEquals(_collisionTarget, _held) || Time.time < _nextCollisionRefresh)
                return;

            _nextCollisionRefresh = Time.time + CollisionRefreshSeconds;
            _ownColliders.Clear();
            _dual?.CollectOwnColliders(_ownColliders);
            _held.IgnoreCollisionsWith(_ownColliders, true);
        }

        void ScheduleCollisionRestore()
        {
            if (_collisionTarget == null)
                return;
            _restoreCollisionsAt = Time.time + PlayerTuning.Active.throwing.ignoreThrowerSeconds;
        }

        void RestoreCollisionsIfDue()
        {
            if (_collisionTarget == null || _restoreCollisionsAt < 0f || Time.time < _restoreCollisionsAt)
                return;
            RestoreCollisions();
        }

        void RestoreCollisions()
        {
            if (Exists(_collisionTarget))
                _collisionTarget.IgnoreCollisionsWith(_ownColliders, false);
            _collisionTarget = null;
            _restoreCollisionsAt = -1f;
        }

        #endregion

        #region Feedback

        void UpdateFeedback()
        {
            var tuning = PlayerTuning.Active;
            string interact = KeyName(_interact);

            // A reticle only for the third-person aim view: first person already has the Cowsins
            // crosshair, and the shoulder view has nothing in the middle of the screen to aim with.
            _hud.SetReticle(_mode == Mode.Aiming && _dual != null && _dual.IsAimViewLive);

            switch (_mode)
            {
                case Mode.Idle:
                    _trajectory.Hide();
                    _hud.SetCharge(false, 0f, 0f);

                    if (_pending != null)
                    {
                        _hud.SetPrompt(_pendingCatch ? "Catching..." : "Lifting...", BodyCarryHud.Style.Waiting);
                    }
                    else if (_target != null)
                    {
                        string what = _target.DisplayName;
                        if (_targetIsCatch)
                            _hud.SetPrompt("[" + interact + "]  CATCH " + what.ToUpperInvariant() + "!",
                                BodyCarryHud.Style.Urgent);
                        else
                            _hud.SetPrompt("[" + interact + "]  Lift " + what, BodyCarryHud.Style.Normal);
                    }
                    else
                    {
                        _hud.SetPrompt(null, BodyCarryHud.Style.Normal);
                    }
                    break;

                case Mode.Holding:
                    _trajectory.Hide();
                    _hud.SetCharge(false, 0f, 0f);
                    _hud.SetPrompt(HoldingPrompt(interact), BodyCarryHud.Style.Normal);
                    break;

                case Mode.Aiming:
                    _hud.SetPrompt(_charging
                            ? "Release [" + KeyName(_charge) + "] to throw"
                            : "Hold [" + KeyName(_charge) + "] to charge      [" + KeyName(_throwReady) + "]  Cancel",
                        BodyCarryHud.Style.Aiming);

                    float shaped = Mathf.Pow(_charge01, tuning.throwing.chargeExponent);
                    float speed = Mathf.Lerp(tuning.throwing.minThrowForce, tuning.throwing.maxThrowForce, shaped);
                    _hud.SetCharge(true, _charge01, speed);

                    if (tuning.throwing.showTrajectory && _held != null && _dual.TryGetAim(out _, out var forward))
                    {
                        var velocity = ComputeThrowVelocity(forward, _charge01, tuning.throwing);
                        _trajectory.Show(_held.Position, velocity, _charge01, tuning.throwing);
                    }
                    else
                    {
                        _trajectory.Hide();
                    }
                    break;
            }
        }

        string HoldingPrompt(string interact)
        {
            if (_held == null)
                return null;

            // Beside a station the same key revives instead - see TryReviveHeld.
            var body = _held as FpsNetworkBridge;
            string letGo = body != null && ReviveStation.FindFor(body) != null
                ? "Revive " + body.DisplayName
                : _held.DropVerb;

            string prompt = "[" + interact + "]  " + letGo;
            if (_held.Throwable)
                prompt += "      [" + KeyName(_throwReady) + "]  Throw";
            // Two key caps joined by a plus: PromptLine makes every [..] one cap.
            if (_held.SupportsRotation)
                prompt += "      [" + KeyName(_turn) + "]  Turn      [" + KeyName(_tilt) + "] + [" +
                          KeyName(_turn) + "]  Tilt";
            return prompt;
        }

        #endregion

        #region Input

        IEnumerable<InputAction> Actions()
        {
            yield return _interact;
            yield return _throwReady;
            yield return _charge;
            yield return _cancel;
            yield return _turn;
            yield return _tilt;
        }

        void EnsureInput()
        {
            var bindings = PlayerTuning.Active.input;
            // Interact is whatever key interacts everywhere else, rebound in settings or not.
            string interactBinding = GameSettings.KeyPath("Interacting") ?? bindings.interactBinding;
            string key = interactBinding + "|" + bindings.throwReadyBinding + "|" +
                         bindings.chargeBinding + "|" + bindings.cancelBinding + "|" +
                         bindings.turnBinding + "|" + bindings.tiltBinding;
            if (key == _bindingsKey && _interact != null)
            {
                SetInputEnabled(isActiveAndEnabled);
                return;
            }

            // Rebuilt when the bindings change, so they can be edited in the tuning asset mid-session.
            _bindingsKey = key;
            foreach (var action in Actions())
                action?.Dispose();

            _interact = new InputAction("Telekinesis Interact", InputActionType.Button, interactBinding);
            _throwReady = new InputAction("Telekinesis Throw Ready", InputActionType.Button, bindings.throwReadyBinding);
            _charge = new InputAction("Telekinesis Charge", InputActionType.Button, bindings.chargeBinding);
            _cancel = new InputAction("Telekinesis Cancel", InputActionType.Button, bindings.cancelBinding);
            // Pass-through: the wheel is a per-frame delta, read as it comes.
            _turn = new InputAction("Telekinesis Turn", InputActionType.PassThrough, bindings.turnBinding);
            _tilt = new InputAction("Telekinesis Tilt", InputActionType.Button, bindings.tiltBinding);
            SetInputEnabled(isActiveAndEnabled);
        }

        void SetInputEnabled(bool enabled)
        {
            foreach (var action in Actions())
            {
                if (action == null)
                    continue;
                if (enabled && !action.enabled)
                    action.Enable();
                else if (!enabled && action.enabled)
                    action.Disable();
            }
        }

        static bool Pressed(InputAction action) => action != null && action.WasPressedThisFrame();
        static bool Released(InputAction action) => action != null && action.WasReleasedThisFrame();
        static bool Held(InputAction action) => action != null && action.IsPressed();

        /// <summary>No new actions while a menu has the cursor. Letting go is always allowed.</summary>
        static bool InputAllowed() => Cursor.lockState == CursorLockMode.Locked;

        static string KeyName(InputAction action)
        {
            if (action == null || action.bindings.Count == 0)
                return "?";

            var path = action.bindings[0].effectivePath;
            switch (path)
            {
                case "<Mouse>/leftButton": return "LMB";
                case "<Mouse>/rightButton": return "RMB";
                case "<Mouse>/middleButton": return "MMB";
                case "<Mouse>/scroll":
                case "<Mouse>/scroll/y":
                    return "WHEEL";
            }

            return InputControlPath.ToHumanReadableString(path,
                InputControlPath.HumanReadableStringOptions.OmitDevice).ToUpperInvariant();
        }

        #endregion
    }
}
#endif
