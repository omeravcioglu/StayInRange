#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali
{
    /// <summary>
    /// The local player's telekinesis: lifting a dead teammate's body, carrying it, throwing it with
    /// a charged throw, and catching it out of the air.
    ///
    /// Exists only on the machine of the player it belongs to (the bridge adds it for the local
    /// player), and works identically in first and third person because it reads the view through
    /// DualPlayerController rather than from either controller directly.
    ///
    /// AUTHORITY. Nothing here decides who holds a body. Every pickup, catch, drop and throw is a
    /// REQUEST to the dead player's own machine (FpsNetworkBridge RPCs), which grants it to exactly one
    /// player; this component reacts when the grant replicates back. What it owns is the feel: the
    /// grip target the simulation pulls the body towards, the aim and charge, and the prompt.
    ///
    /// INPUT. It has its own actions, built from the bindings in PlayerTuning, rather than borrowing
    /// Cowsins' or Malbers', because it has to work whichever of the two is active. While a body is
    /// held, DualPlayerController mutes the actions of both controllers that share these keys
    /// (firing, aiming, melee, interact and so on), which is what stops a throw from also firing the
    /// gun or punching.
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

        FpsNetworkBridge _self;
        DualPlayerController _dual;

        InputAction _interact;
        InputAction _throwReady;
        InputAction _charge;
        InputAction _cancel;
        string _bindingsKey;

        Mode _mode;
        FpsNetworkBridge _held;

        // A body this player let go of, until its owner confirms. Keeps the grip from pulling on a
        // body that is already on its way out of our hands.
        FpsNetworkBridge _releasedLocally;
        float _releaseResendAt;
        bool _releaseWasThrow;

        FpsNetworkBridge _pending;
        bool _pendingCatch;
        float _pendingUntil;

        FpsNetworkBridge _target;
        bool _targetIsCatch;

        bool _charging;
        float _chargeStartedAt;
        float _charge01;

        readonly List<Collider> _ownColliders = new List<Collider>();
        FpsNetworkBridge _collisionBody;
        float _restoreCollisionsAt = -1f;
        float _nextCollisionRefresh;

        BodyCarryHud _hud;
        ThrowTrajectory _trajectory;

        public bool IsHolding => _held != null && _held != _releasedLocally;
        public FpsNetworkBridge HeldBody => IsHolding ? _held : null;
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
            _interact?.Dispose();
            _throwReady?.Dispose();
            _charge?.Dispose();
            _cancel?.Dispose();

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
                // Dead, or between lives. The body's owner releases whatever this player was holding
                // once it sees the death, so there is nothing to send - only local state to clear.
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

            // A menu over the game takes the keys: E would drop the body and letting go of a charge
            // would throw it. The body stays held; a charge in progress is called off.
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

            // While a body is in reach, E is ours alone - see DualPlayerController.SetInteractClaimed.
            _dual.SetInteractClaimed(_mode == Mode.Idle && (_target != null || _pending != null));

            RefreshCollisionIgnore();
            UpdateFeedback();
        }

        void UpdateIdle()
        {
            _charging = false;
            FindTarget(out _target, out _targetIsCatch);

            if (_target != null && _pending == null && Pressed(_interact) && InputAllowed())
            {
                _pending = _target;
                _pendingCatch = _targetIsCatch;
                _pendingUntil = Time.time + PlayerTuning.Active.catching.requestTimeout;
                _target.RequestHold(_target.BodyEpoch, _targetIsCatch);
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

            if (Pressed(_throwReady) && InputAllowed())
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
        /// Picks up grants, losses and handovers from the replicated state: whichever body names
        /// this player as its carrier is the one being held.
        /// </summary>
        void RefreshHeldFromNetwork()
        {
            var me = _self.Owner;
            FpsNetworkBridge granted = null;

            foreach (var body in FpsNetworkBridge.All)
            {
                if (body == null || body == _self || body.Object == null || !body.Object.IsValid)
                    continue;
                if (!body.IsDead || body.CarriedBy != me)
                    continue;
                granted = body;
                break;
            }

            // Our release has been processed: the body no longer names us.
            if (_releasedLocally != null && (_releasedLocally.CarriedBy != me || !_releasedLocally.IsDead))
                _releasedLocally = null;

            if (granted == _releasedLocally)
                granted = null;

            if (granted == _held)
                return;

            if (_held != null)
                LoseHeld(silent: _held == _releasedLocally);

            if (granted != null)
                GainHeld(granted);
        }

        void GainHeld(FpsNetworkBridge body)
        {
            bool wasCatch = _pending == body && _pendingCatch;
            _pending = null;
            _held = body;
            _mode = Mode.Holding;
            _charging = false;
            _charge01 = 0f;

            // A body this player threw a moment ago and is still simulating in flight is handed back
            // to its owner: one player simulates one body at a time.
            var simulated = PlayerDownState.LocallySimulated;
            if (simulated != null && simulated.Bridge != body && simulated.Bridge != _self)
                simulated.Bridge.RequestBodyRest(simulated.Bridge.BodyEpoch);

            if (_dual != null)
                _dual.SetCarrying(true, PlayerTuning.Active.carry.carrySpeedMultiplier);

            IgnoreCollisions(body);

            var down = body.DownState;
            GameSfx.PlayShared(SfxId.MagicCast, down != null ? down.BodyPosition : body.transform.position);
            if (wasCatch)
                _hud?.Toast("Caught!", new Color(0.55f, 1f, 0.6f));
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

            // The owner gave it to somebody else, or the body landed and moved on before the
            // request got there. Only a catch is worth telling the player about.
            if (_pendingCatch)
                _hud?.Toast("Missed!", new Color(1f, 0.45f, 0.35f));
            _pending = null;
        }

        /// <summary>
        /// A release the owner never processed - lost to a stale epoch, most likely - is sent again
        /// with the current one, so a body can never stay stuck to a player who let go of it.
        /// </summary>
        void ResendReleaseIfIgnored()
        {
            if (_releasedLocally == null || Time.time < _releaseResendAt)
                return;

            _releaseResendAt = Time.time + PlayerTuning.Active.catching.requestTimeout;
            _releasedLocally.RequestRelease(_releasedLocally.BodyEpoch, _releaseWasThrow);
        }

        #endregion

        #region Release and throw

        void Release(bool throwIt, Vector3 velocity = default, Vector3 spin = default)
        {
            if (_held == null)
                return;

            var body = _held;
            if (throwIt)
            {
                var down = body.DownState;
                // Launched on this machine, which is simulating the body while it is held - so the
                // throw starts on this frame, not a round trip later. The owner only hears that it is
                // now in flight, and this machine keeps simulating it until it lands or is caught.
                down?.LaunchLocal(velocity, spin);
                GameSfx.PlayShared(SfxId.MagicCast, down != null ? down.BodyPosition : body.transform.position);
            }

            _releasedLocally = body;
            _releaseWasThrow = throwIt;
            _releaseResendAt = Time.time + PlayerTuning.Active.catching.requestTimeout;
            body.RequestRelease(body.BodyEpoch, throwIt);

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
            var body = _held;
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
            if (_held == null || _dual == null || !_dual.TryGetAim(out _, out var forward))
                return;

            var throwing = PlayerTuning.Active.throwing;
            var velocity = ComputeThrowVelocity(forward, charge01, throwing);
            var spin = Random.insideUnitSphere * throwing.launchSpin;
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

        /// <summary>Called by the simulation when the body has been wedged too long to pull free.</summary>
        public void OnGripBroken(FpsNetworkBridge body)
        {
            if (body == null || body != _held)
                return;

            Release(throwIt: false);
            _hud?.Toast("The grip slipped", new Color(1f, 0.6f, 0.4f));
        }

        #endregion

        #region Grip target

        /// <summary>
        /// Where the held body's collar should float, for the simulation on this machine.
        ///
        /// In front of the eyes in first person and in front of the chest in third person, along the
        /// camera's aim either way, pulled back from any wall in the way so the body is never pressed
        /// into geometry.
        /// </summary>
        public bool TryGetGripTarget(FpsNetworkBridge body, out Vector3 target, out Quaternion facing,
            out Vector3 carrierVelocity)
        {
            target = default;
            facing = Quaternion.identity;
            carrierVelocity = default;

            if (body == null || body != _held || body == _releasedLocally || _dual == null)
                return false;
            if (!_dual.TryGetAim(out var origin, out var forward))
                return false;

            var carry = PlayerTuning.Active.carry;

            var flat = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flat.sqrMagnitude < 0.0001f)
                flat = Vector3.ProjectOnPlane(_dual.transform.forward, Vector3.up);
            flat.Normalize();
            var right = Vector3.Cross(Vector3.up, flat);

            // Pitch limited, so looking at your feet does not drive the body into the floor.
            var aim = forward;
            aim.y = Mathf.Clamp(aim.y, -0.5f, 0.6f);
            aim.Normalize();

            float distance = carry.holdDistance * (_mode == Mode.Aiming ? 0.85f : 1f);
            var desired = origin + aim * distance + right * carry.holdOffset.x + Vector3.up * carry.holdOffset.y;

            var toDesired = desired - origin;
            float length = toDesired.magnitude;
            if (length > 0.01f &&
                Physics.SphereCast(origin, 0.25f, toDesired / length, out var hit, length,
                    HiddenSpawnUtility.DefaultSightBlockers(), QueryTriggerInteraction.Ignore))
            {
                desired = origin + toDesired / length * Mathf.Max(0.35f, hit.distance - carry.wallPadding);
            }

            target = desired;
            facing = Quaternion.LookRotation(-flat, Vector3.up);
            carrierVelocity = _dual.GetBodyVelocity();
            return true;
        }

        #endregion

        #region Targeting

        /// <summary>
        /// The body a press of the interact key would act on: a flying one inside catch range takes
        /// priority, otherwise the loose body closest to the middle of the view.
        /// </summary>
        void FindTarget(out FpsNetworkBridge best, out bool isCatch)
        {
            best = null;
            isCatch = false;

            if (_dual == null || !_dual.TryGetAim(out var origin, out var forward))
                return;

            var tuning = PlayerTuning.Active;
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

                bool flying = body.Phase == BodyPhase.Thrown;
                float maxDistance = flying ? tuning.catching.maxCatchDistance : tuning.carry.pickupDistance;
                float cone = flying ? tuning.catching.catchViewAngle : tuning.carry.pickupViewAngle;

                var position = down.BodyPosition;
                var offset = position - origin;
                float distance = offset.magnitude;
                if (distance > maxDistance)
                    continue;

                // Right at your feet the angle means nothing - looking straight ahead still counts.
                float angle = distance > 0.9f ? Vector3.Angle(forward, offset) : 0f;
                if (angle > cone)
                    continue;

                if (Physics.Linecast(origin, position, out var hit, HiddenSpawnUtility.DefaultSightBlockers(),
                        QueryTriggerInteraction.Ignore) && hit.distance < distance - 0.3f)
                    continue;

                float score = angle / cone + distance / maxDistance - (flying ? 1f : 0f);
                if (score >= bestScore)
                    continue;

                bestScore = score;
                best = body;
                isCatch = flying;
            }
        }

        #endregion

        #region Collisions

        /// <summary>
        /// The carrier's own colliders pass through the body they hold. Without this the floating
        /// body shoves its carrier around - and in first person the carrier's capsule is right
        /// behind the grip point.
        /// </summary>
        void IgnoreCollisions(FpsNetworkBridge body)
        {
            if (_collisionBody != null && _collisionBody != body)
                RestoreCollisions();

            _ownColliders.Clear();
            _dual?.CollectOwnColliders(_ownColliders);

            _collisionBody = body;
            _restoreCollisionsAt = -1f;
            _nextCollisionRefresh = Time.time + CollisionRefreshSeconds;
            body.DownState?.Ragdoll?.IgnoreCollisionsWith(_ownColliders, true);
        }

        /// <summary>
        /// Re-applied every so often while holding. Switching between first and third person enables
        /// a different set of colliders, and Unity forgets ignored pairs when a collider is toggled.
        /// </summary>
        void RefreshCollisionIgnore()
        {
            if (!IsHolding || _collisionBody != _held || Time.time < _nextCollisionRefresh)
                return;

            _nextCollisionRefresh = Time.time + CollisionRefreshSeconds;
            _ownColliders.Clear();
            _dual?.CollectOwnColliders(_ownColliders);
            _held.DownState?.Ragdoll?.IgnoreCollisionsWith(_ownColliders, true);
        }

        void ScheduleCollisionRestore()
        {
            if (_collisionBody == null)
                return;
            _restoreCollisionsAt = Time.time + PlayerTuning.Active.throwing.ignoreThrowerSeconds;
        }

        void RestoreCollisionsIfDue()
        {
            if (_collisionBody == null || _restoreCollisionsAt < 0f || Time.time < _restoreCollisionsAt)
                return;
            RestoreCollisions();
        }

        void RestoreCollisions()
        {
            if (_collisionBody != null)
                _collisionBody.DownState?.Ragdoll?.IgnoreCollisionsWith(_ownColliders, false);
            _collisionBody = null;
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
                        string who = _target.DisplayName;
                        if (_targetIsCatch)
                            _hud.SetPrompt("[" + interact + "]  CATCH " + who.ToUpperInvariant() + "!",
                                BodyCarryHud.Style.Urgent);
                        else
                            _hud.SetPrompt("[" + interact + "]  Lift " + who, BodyCarryHud.Style.Normal);
                    }
                    else
                    {
                        _hud.SetPrompt(null, BodyCarryHud.Style.Normal);
                    }
                    break;

                case Mode.Holding:
                    _trajectory.Hide();
                    _hud.SetCharge(false, 0f, 0f);

                    // Beside a station the same key revives instead - see TryReviveHeld.
                    string letGo = _held != null && ReviveStation.FindFor(_held) != null
                        ? "Revive " + _held.DisplayName
                        : "Drop";
                    _hud.SetPrompt("[" + interact + "]  " + letGo + "      [" + KeyName(_throwReady) + "]  Throw",
                        BodyCarryHud.Style.Normal);
                    break;

                case Mode.Aiming:
                    _hud.SetPrompt(_charging
                            ? "Release [" + KeyName(_charge) + "] to throw"
                            : "Hold [" + KeyName(_charge) + "] to charge      [" + KeyName(_throwReady) + "]  Cancel",
                        BodyCarryHud.Style.Aiming);

                    float shaped = Mathf.Pow(_charge01, tuning.throwing.chargeExponent);
                    float speed = Mathf.Lerp(tuning.throwing.minThrowForce, tuning.throwing.maxThrowForce, shaped);
                    _hud.SetCharge(true, _charge01, speed);

                    if (tuning.throwing.showTrajectory && _held != null && _held.DownState != null &&
                        _dual.TryGetAim(out _, out var forward))
                    {
                        var velocity = ComputeThrowVelocity(forward, _charge01, tuning.throwing);
                        _trajectory.Show(_held.DownState.BodyPosition, velocity, _charge01, tuning.throwing);
                    }
                    else
                    {
                        _trajectory.Hide();
                    }
                    break;
            }
        }

        #endregion

        #region Input

        void EnsureInput()
        {
            var bindings = PlayerTuning.Active.input;
            // Interact is whatever key interacts everywhere else, rebound in settings or not.
            string interactBinding = GameSettings.KeyPath("Interacting") ?? bindings.interactBinding;
            string key = interactBinding + "|" + bindings.throwReadyBinding + "|" +
                         bindings.chargeBinding + "|" + bindings.cancelBinding;
            if (key == _bindingsKey && _interact != null)
            {
                SetInputEnabled(isActiveAndEnabled);
                return;
            }

            // Rebuilt when the bindings change, so they can be edited in the tuning asset mid-session.
            _bindingsKey = key;
            _interact?.Dispose();
            _throwReady?.Dispose();
            _charge?.Dispose();
            _cancel?.Dispose();

            _interact = new InputAction("Telekinesis Interact", InputActionType.Button, interactBinding);
            _throwReady = new InputAction("Telekinesis Throw Ready", InputActionType.Button, bindings.throwReadyBinding);
            _charge = new InputAction("Telekinesis Charge", InputActionType.Button, bindings.chargeBinding);
            _cancel = new InputAction("Telekinesis Cancel", InputActionType.Button, bindings.cancelBinding);
            SetInputEnabled(isActiveAndEnabled);
        }

        void SetInputEnabled(bool enabled)
        {
            foreach (var action in new[] { _interact, _throwReady, _charge, _cancel })
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
            }

            return InputControlPath.ToHumanReadableString(path,
                InputControlPath.HumanReadableStringOptions.OmitDevice).ToUpperInvariant();
        }

        #endregion
    }
}
#endif
