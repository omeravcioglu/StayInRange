using Unity.Cinemachine;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Every number the unified player systems are tuned by - camera transitions, death and
    /// spectating, telekinetic carrying, throwing and catching - in one asset.
    ///
    /// Lives at Resources/PlayerTuning so every player loads the same values without a prefab
    /// reference. Values are read live, so the asset can be edited during Play Mode and the next
    /// pickup or throw uses the new numbers. Each machine reads its own copy: keep builds in step
    /// when testing across machines, because nothing here is synchronised.
    /// </summary>
    [CreateAssetMenu(menuName = "CollarCali/Player Tuning", fileName = "PlayerTuning")]
    public class PlayerTuning : ScriptableObject
    {
        [System.Serializable]
        public class CameraSettings
        {
            [Tooltip("Seconds the camera takes to pull back from the eyes to over the shoulder when " +
                     "entering third person. 0 is an instant cut.")]
            [Min(0f)] public float toThirdPersonSeconds = 0.55f;

            [Tooltip("Seconds the camera takes to travel from over the shoulder back into the eyes when " +
                     "returning to first person. 0 is an instant cut.")]
            [Min(0f)] public float toFirstPersonSeconds = 0.45f;

            [Tooltip("Easing used by both transitions. Position, rotation and field of view all follow it.")]
            public CinemachineBlendDefinition.Styles blendStyle = CinemachineBlendDefinition.Styles.EaseInOut;

            [Tooltip("Carry the body's velocity across the switch into first person, so a switch " +
                     "mid-jump or mid-run keeps its momentum.")]
            public bool keepMomentumIntoFirstPerson = true;

            [Header("Third-person throw aiming")]
            [Tooltip("In third person, aiming a throw (Q while carrying) drops the view into the " +
                     "character's eyes, and it returns over the shoulder after the throw. Only the " +
                     "camera changes: the third-person controller stays in charge throughout.")]
            public bool firstPersonAimInThirdPerson = true;

            [Tooltip("Seconds for the view to move from over the shoulder into the eyes.")]
            [Min(0f)] public float aimViewInSeconds = 0.3f;

            [Tooltip("Seconds for the view to move from the eyes back over the shoulder.")]
            [Min(0f)] public float aimViewOutSeconds = 0.45f;

            [Tooltip("Seconds the view stays in the eyes after a throw, so the throw can be watched " +
                     "leaving before the camera pulls back. Cancelling or dropping returns at once.")]
            [Min(0f)] public float aimViewReturnDelay = 0.35f;

            [Tooltip("Eye height above the feet, used when the character's head bone cannot be found.")]
            [Min(0.5f)] public float aimViewEyeHeight = 1.62f;

            [Tooltip("Field of view while aiming from the eyes. 0 matches the first-person camera.")]
            [Range(0f, 120f)] public float aimViewFieldOfView = 0f;

            [Tooltip("How far the aim view can look up (x, negative) and down (y), in degrees.")]
            public Vector2 aimViewPitchLimits = new Vector2(-80f, 80f);
        }

        [System.Serializable]
        public class DeathSettings
        {
            [Tooltip("How long the large YOU DIED title stays in the middle of the screen before " +
                     "shrinking to the top so the spectator view is unobstructed.")]
            [Min(0f)] public float youDiedCentreSeconds = 2.2f;

            [Tooltip("Where the spectator camera sits relative to the watched teammate: x right, y up, " +
                     "z forward (negative is behind them).")]
            public Vector3 spectatorOffset = new Vector3(0f, 2.1f, -3.4f);

            [Tooltip("How quickly the spectator camera follows. Higher is tighter, lower is floatier.")]
            [Min(0.1f)] public float spectatorFollowSharpness = 9f;

            [Tooltip("How quickly the spectator camera turns to face the watched teammate.")]
            [Min(0.1f)] public float spectatorTurnSharpness = 11f;
        }

        [System.Serializable]
        public class CarrySettings
        {
            [Header("Picking up")]
            [Tooltip("How close a body has to be to lift it, in metres, measured from the carrier's " +
                     "eyes in first person (chest in third person) to the body's hips.")]
            [Min(0.5f)] public float pickupDistance = 2.6f;

            [Tooltip("Half-angle of the cone in front of the view a body has to be inside to be " +
                     "targeted. Larger is more forgiving.")]
            [Range(5f, 90f)] public float pickupViewAngle = 40f;

            [Header("Holding")]
            [Tooltip("How far in front of the carrier the body floats, in metres.")]
            [Min(0.8f)] public float holdDistance = 2.0f;

            [Tooltip("Offset of the float point from the aim line: x to the right, y up. Keeps the body " +
                     "out of the middle of the screen in first person.")]
            public Vector2 holdOffset = new Vector2(0.35f, -0.35f);

            [Tooltip("Movement speed while carrying, as a multiplier. 1 is no slowdown.")]
            [Range(0.2f, 1f)] public float carrySpeedMultiplier = 0.8f;

            [Tooltip("Spring frequency of the telekinetic grip, in hertz. Higher follows the aim more " +
                     "tightly; lower drifts behind it and feels heavier.")]
            [Range(0.3f, 8f)] public float gripFrequency = 2.2f;

            [Tooltip("Damping of the grip. 1 settles without overshoot; below 1 the body sways; above 1 " +
                     "it feels sluggish.")]
            [Range(0.1f, 2f)] public float gripDamping = 0.8f;

            [Tooltip("Upper limit on the grip's pull, in m/s². This is what stops the body snapping " +
                     "violently when the aim swings fast, and what gives it weight.")]
            [Min(1f)] public float maxGripAcceleration = 40f;

            [Tooltip("Upper limit on how fast the grip moves the body, in m/s.")]
            [Min(1f)] public float maxGripSpeed = 12f;

            [Tooltip("How much of the body's weight the grip holds up. 1 floats it; slightly less lets " +
                     "it sag under its own weight.")]
            [Range(0f, 1.2f)] public float weightSupport = 1f;

            [Tooltip("Strength of the grip keeping the body hanging upright and facing the carrier, in hertz.")]
            [Range(0f, 8f)] public float uprightFrequency = 1.6f;

            [Tooltip("Extra angular damping on the limbs while held, so they swing rather than flail.")]
            [Min(0f)] public float limbDampingWhileHeld = 2.5f;

            [Tooltip("Gap kept between the float point and any wall in front of the carrier, in metres, " +
                     "so the body is never pushed into geometry.")]
            [Min(0f)] public float wallPadding = 0.35f;

            [Tooltip("If the body is held back further than this from the float point - wedged behind " +
                     "a corner, say - for Break Seconds, the grip lets go instead of dragging it through.")]
            [Min(0.5f)] public float breakDistance = 3.2f;

            [Min(0.1f)] public float breakSeconds = 0.8f;
        }

        [System.Serializable]
        public class ThrowSettings
        {
            [Tooltip("Launch speed of the weakest throw (a tap), in m/s. Applied as a velocity change, " +
                     "so it is independent of the body's mass.")]
            [Min(0f)] public float minThrowForce = 5f;

            [Tooltip("Launch speed of a fully charged throw, in m/s.")]
            [Min(0f)] public float maxThrowForce = 16f;

            [Tooltip("Seconds of holding the charge button to reach the maximum throw.")]
            [Min(0.05f)] public float chargeDuration = 1.1f;

            [Tooltip("Shape of the charge. 1 is linear; above 1 the top end takes longer to reach, " +
                     "which makes gentle throws easier to judge.")]
            [Range(0.3f, 4f)] public float chargeExponent = 1.5f;

            [Tooltip("Lifts the aim slightly so a throw straight ahead still arcs. 0 throws exactly " +
                     "along the crosshair.")]
            [Range(0f, 0.6f)] public float upwardBias = 0.12f;

            [Tooltip("Fraction of the carrier's own velocity added to the throw, so throwing while " +
                     "running throws further.")]
            [Range(0f, 1f)] public float inheritCarrierVelocity = 1f;

            [Tooltip("Random tumble added at launch, in radians per second.")]
            [Min(0f)] public float launchSpin = 2.5f;

            [Tooltip("Seconds the thrower's own colliders ignore the body after launch, so it cannot " +
                     "clip the thrower on the way out.")]
            [Min(0f)] public float ignoreThrowerSeconds = 0.35f;

            [Header("Aiming feedback")]
            public bool showTrajectory = true;

            [Range(8, 120)] public int trajectorySteps = 48;

            [Tooltip("Seconds of flight each trajectory step covers.")]
            [Range(0.01f, 0.2f)] public float trajectoryStepSeconds = 0.05f;
        }

        [System.Serializable]
        public class CatchSettings
        {
            [Tooltip("Maximum distance at which a flying body can be caught, in metres.")]
            [Min(0.5f)] public float maxCatchDistance = 3.8f;

            [Tooltip("Half-angle of the cone a flying body has to be inside to be caught. Wide on " +
                     "purpose - a body flying at you is often at the edge of the screen.")]
            [Range(10f, 180f)] public float catchViewAngle = 75f;

            [Tooltip("Seconds to wait for the body's owner to confirm a pickup or catch before " +
                     "treating it as missed.")]
            [Min(0.1f)] public float requestTimeout = 0.75f;

            [Tooltip("A thrown body counts as landed once it moves slower than this, in m/s.")]
            [Min(0.05f)] public float settleSpeed = 0.7f;

            [Tooltip("...for this many seconds. Landing hands the body back to its owner's machine.")]
            [Min(0.05f)] public float settleSeconds = 0.5f;

            [Tooltip("A flight is ended after this long even if the body is still moving.")]
            [Min(1f)] public float maxFlightSeconds = 7f;
        }

        [System.Serializable]
        public class TetherSettings
        {
            [Tooltip("Treat the collar as a leash on a flying body, so a throw cannot fling a teammate " +
                     "past the collar's limit and fail the whole team.")]
            public bool leashThrownBodies = true;

            [Tooltip("How far inside the collar's maximum distance the leash engages, in metres.")]
            [Min(0f)] public float leashSlack = 3f;

            [Tooltip("How hard the leash pulls a body back once it is taut, in m/s² per metre.")]
            [Min(0f)] public float leashStiffness = 30f;
        }

        [System.Serializable]
        public class InputSettings
        {
            [Tooltip("Pick up, drop and catch. Input System binding path.")]
            public string interactBinding = "<Keyboard>/e";

            [Tooltip("Enter and leave throw-ready mode while carrying.")]
            public string throwReadyBinding = "<Keyboard>/q";

            [Tooltip("Hold to charge, release to throw.")]
            public string chargeBinding = "<Mouse>/leftButton";

            [Tooltip("Leaves throw-ready mode without throwing.")]
            public string cancelBinding = "<Mouse>/rightButton";
        }

        [System.Serializable]
        public class NetworkSettings
        {
            [Tooltip("How quickly other machines' copies of a body catch up with where its simulating " +
                     "machine says it is. Higher is tighter but shows more network jitter.")]
            [Min(1f)] public float followSharpness = 16f;

            [Tooltip("Seconds of velocity extrapolation used to hide network delay on moving bodies.")]
            [Range(0f, 0.2f)] public float velocityLead = 0.06f;

            [Tooltip("Copies further than this from the replicated pose jump straight to it rather " +
                     "than gliding through the level.")]
            [Min(0.5f)] public float snapDistance = 4f;
        }

        public CameraSettings cameraTransition = new CameraSettings();
        public DeathSettings death = new DeathSettings();
        public CarrySettings carry = new CarrySettings();
        public ThrowSettings throwing = new ThrowSettings();
        public CatchSettings catching = new CatchSettings();
        public TetherSettings tether = new TetherSettings();
        public InputSettings input = new InputSettings();
        public NetworkSettings network = new NetworkSettings();

        static PlayerTuning _active;

        /// <summary>
        /// The asset in Resources, or built-in defaults if it is missing. Never null, so callers can
        /// read settings without guarding every access.
        /// </summary>
        public static PlayerTuning Active
        {
            get
            {
                if (_active != null)
                    return _active;

                _active = Resources.Load<PlayerTuning>("PlayerTuning");
                if (_active == null)
                {
                    _active = CreateInstance<PlayerTuning>();
                    _active.name = "PlayerTuning (defaults)";
                    Debug.LogWarning("[CollarCali] Resources/PlayerTuning.asset not found - using built-in " +
                                     "defaults. Create one via Create > CollarCali > Player Tuning to tune them.");
                }

                return _active;
            }
        }
    }
}
