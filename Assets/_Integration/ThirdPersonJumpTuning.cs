using System.Collections.Generic;
using MalbersAnimations;
using MalbersAnimations.Controller;
using MalbersAnimations.Scriptables;
using MalbersAnimations.Utilities;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Tunes the third-person (Malbers) jump from PlayerTuning > Third Person Jump, and keeps it tuned.
    /// Lives on the character's MAnimal; DualPlayerController adds it when third person first starts.
    ///
    /// WHAT IT CHANGES
    ///   - Air control is on for every jump and for falling. Malbers wires it to a shared "Global Air
    ///     Control" asset that is saved off and only switched on by a leftover Malbers settings object
    ///     in the Game scene; with it off the flight direction is locked at take-off.
    ///   - Turning and speed changes in the air are quicker (Air Turn Speed, Air Smoothing).
    ///   - Every ground jump - standing, walking or running - follows one arc of a set height and air
    ///     time, and the double jump another, solved for Malbers' own gravity (see Solve). Jumps off a
    ///     wall run or a slide keep Malbers' arcs.
    ///   - Take-off is on the press instead of a third of the way into the jump animation. The
    ///     animation's own "ActivateJump" cue is muted on this character's animator at the same
    ///     moment, or it would launch the jump a second time.
    ///   - Running off an edge still allows a full jump for Coyote Time seconds, keeping the double jump.
    ///   - The arc is solved again whenever the character's time multiplier changes. The carry slowdown
    ///     scales Malbers' gravity but not its push, which made a carrier jump higher.
    ///
    /// Everything is written as constants on this character's runtime copies of the Malbers states
    /// (Malbers clones them per character), never to the shared variable assets they read by default,
    /// so nothing leaks into the project files or into anyone else's character.
    /// </summary>
    [DisallowMultipleComponent]
    public class ThirdPersonJumpTuning : MonoBehaviour
    {
        /// <summary>
        /// How far into Malbers' gravity curve a jump starts, in seconds. That gravity grows with the
        /// square of time, so a jump starting at zero hangs at the top and then drops hard; starting
        /// part-way along gives a quicker take-off and a softer landing for the same height and air
        /// time. Much further and a jump cut short - a head bump - drops abruptly.
        /// </summary>
        const float ArcShapeSeconds = 0.4f;

        /// <summary>Steve's gravity strength, for the moment before it can be read off the controller.</summary>
        const float FallbackGravity = 20f;

        /// <summary>The message the jump animations send to start the jump when it waits for them.</summary>
        const string TakeoffCue = "ActivateJump";

        MAnimal _animal;
        JumpBasic _jump;
        Fall _fall;
        bool[] _shippedWait;
        bool _refusedShared;

        bool _instantTakeoff;
        readonly List<MessageItem> _mutedCues = new List<MessageItem>();
        bool _wasJumping;

        Snapshot _applied;
        bool _hasApplied;

        float _fellAt = -1f;
        bool _coyoteGiven;

        /// <summary>Everything an applied arc depends on, to tell when it has to be solved again.</summary>
        struct Snapshot
        {
            public float JumpHeight, AirTime, DoubleHeight, DoubleAirTime, DoubleDelay, Turn, Smoothing;
            public int Jumps;
            public float Gravity, TimeMultiplier, Scale, Step;

            public bool Matches(in Snapshot other) =>
                Jumps == other.Jumps &&
                Mathf.Approximately(JumpHeight, other.JumpHeight) &&
                Mathf.Approximately(AirTime, other.AirTime) &&
                Mathf.Approximately(DoubleHeight, other.DoubleHeight) &&
                Mathf.Approximately(DoubleAirTime, other.DoubleAirTime) &&
                Mathf.Approximately(DoubleDelay, other.DoubleDelay) &&
                Mathf.Approximately(Turn, other.Turn) &&
                Mathf.Approximately(Smoothing, other.Smoothing) &&
                Mathf.Approximately(Gravity, other.Gravity) &&
                Mathf.Approximately(TimeMultiplier, other.TimeMultiplier) &&
                Mathf.Approximately(Scale, other.Scale) &&
                Mathf.Approximately(Step, other.Step);
        }

        void OnEnable()
        {
            // The animator may have rebuilt its behaviours while this was off, and nothing about the
            // character can be assumed: start from Malbers' own take-off and tune again from scratch.
            _hasApplied = false;
            _wasJumping = false;
            _fellAt = -1f;
            _coyoteGiven = false;
            if (Resolve())
                SetInstantTakeoff(false);
        }

        void OnDisable()
        {
            if (_jump != null)
                SetInstantTakeoff(false);
        }

        void Update()
        {
            if (!Resolve())
                return;

            var settings = PlayerTuning.Active.thirdPersonJump;

            // Retried every frame until the animator is ready to have its cue muted.
            if (settings.instantTakeoff != _instantTakeoff)
                SetInstantTakeoff(settings.instantTakeoff);

            GuardTakeoffCues();
            ApplyIfChanged(settings);
            UpdateCoyote(settings.coyoteTime);
        }

        /// <summary>
        /// Finds this character's states. Only ever its own runtime copies: if Malbers were set not to
        /// clone them, writing to them would rewrite the shared assets every character uses.
        /// </summary>
        bool Resolve()
        {
            if (_refusedShared)
                return false;

            if (_animal == null)
                _animal = GetComponent<MAnimal>();
            if (_animal == null)
                return false;

            if (!_animal.CloneStates)
            {
                _refusedShared = true;
                Debug.LogWarning("[CollarCali] " + _animal.name + " does not clone its Malbers states, so its " +
                                 "jump is left untuned rather than changing the shared state assets.", this);
                return false;
            }

            if (_jump == null)
                _jump = _animal.State_Get<JumpBasic>();
            if (_fall == null)
                _fall = _animal.State_Get<Fall>();

            if (_jump != null && _shippedWait == null)
            {
                _shippedWait = new bool[_jump.profiles.Count];
                for (int i = 0; i < _shippedWait.Length; i++)
                    _shippedWait[i] = _jump.profiles[i].WaitForAnimation;
            }

            return _jump != null || _fall != null;
        }

        #region Take-off

        /// <summary>
        /// Not waiting for the animation and muting its cue always change together: a jump waiting for
        /// a muted cue would never leave the ground, and one that does not wait but still hears the cue
        /// would launch a second time a third of the way through.
        /// </summary>
        void SetInstantTakeoff(bool instant)
        {
            if (_jump == null)
                return;

            // The animator is not ready yet: stay with Malbers' take-off, and try again next frame.
            if (instant && !MuteTakeoffCues())
                instant = false;
            if (!instant)
                UnmuteTakeoffCues();

            for (int i = 0; i < _jump.profiles.Count; i++)
            {
                var profile = _jump.profiles[i];
                profile.WaitForAnimation = !instant && i < _shippedWait.Length && _shippedWait[i];
                _jump.profiles[i] = profile;
            }

            _instantTakeoff = instant;
        }

        /// <summary>
        /// Mutes every take-off cue on this character's animator - its own instances of the animation
        /// behaviours, so nobody else's are touched. False while the animator cannot be read yet.
        /// </summary>
        bool MuteTakeoffCues()
        {
            var anim = _animal.Anim;
            if (anim == null || !anim.isActiveAndEnabled || !anim.isInitialized)
                return false;

            foreach (var behaviour in anim.GetBehaviours<MessagesBehavior>())
            {
                if (behaviour == null)
                    continue;
                MuteIn(behaviour.onEnterMessage);
                MuteIn(behaviour.onTimeMessage);
            }

            return true;
        }

        void MuteIn(MessageItem[] cues)
        {
            if (cues == null)
                return;

            foreach (var cue in cues)
            {
                if (cue == null || !cue.Active || cue.message != TakeoffCue)
                    continue;
                cue.Active = false;
                _mutedCues.Add(cue);
            }
        }

        void UnmuteTakeoffCues()
        {
            foreach (var cue in _mutedCues)
            {
                if (cue != null)
                    cue.Active = true;
            }

            _mutedCues.Clear();
        }

        /// <summary>
        /// Checks again at the start of every jump, in case the animator has rebuilt its behaviours
        /// since: a fresh cue fires a third of the way into the animation, long after this has muted it.
        /// </summary>
        void GuardTakeoffCues()
        {
            bool jumping = ReferenceEquals(_animal.ActiveState, _jump);
            if (jumping && !_wasJumping && _instantTakeoff)
                MuteTakeoffCues();
            _wasJumping = jumping;
        }

        #endregion

        #region Arc and control

        void ApplyIfChanged(PlayerTuning.ThirdPersonJumpSettings settings)
        {
            // Never mid-air: a jump in progress keeps the arc it started with.
            if (!_animal.Grounded)
                return;

            var now = new Snapshot
            {
                JumpHeight = settings.jumpHeight,
                AirTime = settings.airTime,
                DoubleHeight = settings.doubleJumpHeight,
                DoubleAirTime = settings.doubleJumpAirTime,
                DoubleDelay = settings.doubleJumpDelay,
                Turn = settings.airTurnSpeed,
                Smoothing = settings.airSmoothing,
                Jumps = Mathf.Max(1, settings.jumps),
                Gravity = BaseGravity(),
                TimeMultiplier = Mathf.Max(0.01f, _animal.TimeMultiplier),
                Scale = Mathf.Max(0.01f, _animal.ScaleFactor),
                Step = Time.fixedDeltaTime,
            };

            if (_hasApplied && now.Matches(_applied))
                return;

            Apply(now);
            _applied = now;
            _hasApplied = true;
        }

        /// <summary>Malbers' gravity strength on this character, without any state's temporary multiplier.</summary>
        float BaseGravity()
        {
            var state = _animal.ActiveState;
            if (state == null)
                return FallbackGravity;

            float multiplier = _animal.GravityMultiplier * state.GravityMultiplier;
            return multiplier > 0.0001f ? _animal.GravityPower / multiplier : FallbackGravity;
        }

        void Apply(in Snapshot at)
        {
            int startStep = Mathf.Max(0, Mathf.RoundToInt(ArcShapeSeconds / at.Step));

            if (_jump != null)
            {
                _jump.Jumps = Local(_jump.Jumps, at.Jumps);
                _jump.DoubleJumpTime = Local(_jump.DoubleJumpTime, at.DoubleDelay);
                _jump.AirRotation = Local(_jump.AirRotation, at.Turn);
                _jump.AirSmooth = Local(_jump.AirSmooth, at.Smoothing);

                bool ground = Solve(at.JumpHeight, at.AirTime, startStep, at, out var push, out var power, out var rise);
                for (int i = 0; i < _jump.profiles.Count; i++)
                {
                    var profile = _jump.profiles[i];
                    profile.AirControl = Local(profile.AirControl, true);

                    // A profile tied to a last state is a jump off a wall run or a slide: its own arc stays.
                    if (ground && profile.LastState == null)
                    {
                        profile.Height = Local(profile.Height, push);
                        profile.GravityPower = Local(profile.GravityPower, power);
                        profile.StartGravityTime = startStep;
                        profile.JumpTime = rise;
                    }

                    _jump.profiles[i] = profile;
                }

                bool air = Solve(at.DoubleHeight, at.DoubleAirTime, startStep, at, out push, out power, out rise);
                for (int i = 0; i < _jump.multipleJumps.Count; i++)
                {
                    var profile = _jump.multipleJumps[i];
                    profile.AirControl = Local(profile.AirControl, true);
                    if (air)
                    {
                        profile.Height = Local(profile.Height, push);
                        profile.GravityPower = Local(profile.GravityPower, power);
                        profile.StartGravityTime = startStep;
                        profile.JumpTime = rise;
                    }

                    _jump.multipleJumps[i] = profile;
                }
            }

            if (_fall != null)
            {
                _fall.AirControl = Local(_fall.AirControl, true);
                _fall.AirRotation = Local(_fall.AirRotation, at.Turn);
                _fall.AirSmooth = Local(_fall.AirSmooth, at.Smoothing);
            }
        }

        /// <summary>
        /// The push and gravity scale that make one of Malbers' jumps peak <paramref name="height"/>
        /// metres up and come back down to take-off height after <paramref name="airTime"/> seconds.
        ///
        /// Malbers' model, every physics step k from the start of a jump: up by push x dt x scale, down by
        /// power x dt x ½(dt x (start + k))² x gravity x time multiplier x scale. Summed, both conditions
        /// come out in closed form - the apex falls at the step where the two cancel, independent of the
        /// push - so this is exact for that model, within a step. Also returns the time to the apex,
        /// which becomes the jump's own duration before it hands over to falling.
        /// </summary>
        static bool Solve(float height, float airTime, int startStep, in Snapshot at,
            out float push, out float power, out float timeToApex)
        {
            push = power = timeToApex = 0f;

            float dt = at.Step;
            float steps = airTime / dt;
            if (height <= 0f || steps < 4f || dt <= 0f)
                return false;

            float s = startStep;
            float total = SumOfSquares(steps, s);
            float apexStep = Mathf.Sqrt(total / steps) - s;
            float shape = apexStep - steps * SumOfSquares(apexStep, s) / total;
            if (apexStep <= 0f || shape <= 0f)
                return false;

            push = height / (at.Scale * dt * shape);
            float gravityPerStep = dt * 0.5f * dt * dt * at.Gravity * at.TimeMultiplier * at.Scale;
            power = push * at.Scale * dt * steps / (gravityPerStep * total);
            timeToApex = apexStep * dt;
            return true;
        }

        /// <summary>The sum of (s + k)² for k from 0 to n - 1, carried over to a fractional n.</summary>
        static float SumOfSquares(float n, float s) =>
            n * s * s + s * n * (n - 1f) + (n - 1f) * n * (2f * n - 1f) / 6f;

        // Malbers' references read a shared variable asset unless told to use their own constant;
        // writing through one of those would change the asset for every character, and in the Editor
        // it would outlive play mode.
        static FloatReference Local(FloatReference reference, float value)
        {
            reference ??= new FloatReference();
            reference.UseConstant = true;
            reference.ConstantValue = value;
            return reference;
        }

        static BoolReference Local(BoolReference reference, bool value)
        {
            reference ??= new BoolReference();
            reference.UseConstant = true;
            reference.ConstantValue = value;
            return reference;
        }

        static IntReference Local(IntReference reference, int value)
        {
            reference ??= new IntReference();
            reference.UseConstant = true;
            reference.ConstantValue = value;
            return reference;
        }

        #endregion

        #region Coyote time

        /// <summary>
        /// Malbers counts running off an edge as the first jump spent, leaving only the double jump. For
        /// a moment after leaving the ground that jump is given back, so a late press is still a full
        /// jump from the ground; once the moment has passed Malbers' count stands again.
        /// </summary>
        void UpdateCoyote(float window)
        {
            if (_jump == null)
                return;

            bool falling = _fall != null && ReferenceEquals(_animal.ActiveState, _fall);
            if (!falling)
            {
                _fellAt = -1f;
                _coyoteGiven = false;
                return;
            }

            if (_fellAt < 0f)
            {
                // Only a fall that began on the ground - Idle or Locomotion - earns it, not the second
                // half of a jump.
                var last = _animal.LastState;
                _fellAt = last != null && last.ID != null && last.ID.ID <= 1 ? Time.time : float.NegativeInfinity;
            }

            bool inWindow = Time.time - _fellAt <= window;
            if (inWindow && !_coyoteGiven && _jump.JumpsPerformanced == 1)
            {
                _jump.JumpsPerformanced = 0;
                _coyoteGiven = true;
            }
            else if (!inWindow && _coyoteGiven)
            {
                if (_jump.JumpsPerformanced == 0)
                    _jump.JumpsPerformanced = 1;
                _coyoteGiven = false;
            }
        }

        #endregion
    }
}
