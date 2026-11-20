using System;
using MalbersAnimations;
using MalbersAnimations.Controller;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Keeps the player's shoes on the ground they stand on.
    ///
    /// WHY. Malbers stands the character on the ground under its middle and plays the animation as it
    /// was made, for a flat floor. On a bump, a step or a slope one shoe went into the ground and the
    /// other hung over it, and the sprint's own foot plants push the chunky shoes' toes up to 7 cm into
    /// even a flat floor.
    ///
    /// HOW. After the animation has posed the body each frame (Late Update), each shoe's heel and toe -
    /// points under the sole, measured from the model by PlayerCharacterBuilder - are tested against
    /// the ground right under them:
    ///   - a shoe in the ground is lifted out of it, by as much as the deeper of the two points;
    ///   - a shoe resting on the floor in the animation (its sole within a few centimetres of the feet)
    ///     is tilted to lie flat on the ground under it (a slope, a ramp), and lowered onto lower ground,
    ///     the hips going down with it so the leg can reach;
    ///   - a shoe in the air keeps the animation's own lift, over the real ground.
    /// Each leg then bends to put its foot there, keeping the foot's own turn, the way ClimbHandContact
    /// keeps the hands on a wall. Only on the ground, standing or moving (Malbers' Idle and
    /// Locomotion): jumping, climbing and the rest play as made. Whatever it changes is put back before
    /// the next frame, so the correction never piles up on frames the Animator does not run.
    ///
    /// DETAILS.
    ///   - The feet are measured from where the body is drawn (CharacterVisualInterpolator's smoothed
    ///     root), not from the physics root, which runs up to a physics step ahead of it. Going down a
    ///     ramp or stairs that difference is centimetres and changes every frame: the shoes hovered.
    ///   - A point's ground is the surface it stands on or is in. A surface over it with open space
    ///     between - a table top, a bench, a beam, the next tread of open stairs - is not: the point is
    ///     under it, and a shoe is not lifted onto it. The ground the body itself stands on always is,
    ///     even where a toe has sunk right through it (a thin floor tile), and so is anything lying on the
    ///     ground (a tile, a plank, a mat): there is no open space under it.
    ///   - Slopes, and ground that moves, are followed exactly. A height the ground changes all at once
    ///     under a moving shoe - a step's edge, a kerb - is eased in instead of jumped: quicker the faster
    ///     the shoe moves, so a sprint up stairs keeps up, and quicker for a shoe standing on the ground;
    ///     but never faster than Edge Max Speed, so a foot swinging over a riser takes it over several
    ///     frames, unless the shoe is sweeping up a flight of stairs: then as fast as they climb under it.
    ///   - How high a shoe is over the ground is judged with its sole as a line through its lowest point,
    ///     along the slope: as it lies once planted. The walk's own pitch of the shoe - landing on the heel,
    ///     pushing off the toe - does not lift a shoe whose toe is over the next step, nor drop one whose heel
    ///     is over the step behind, whatever the slopes of their treads. Where the slope itself changes under
    ///     the shoe (a ramp's foot or top) the sole is not taken flat; it eases between the two, never at once.
    ///   - The slope a shoe lies on is eased too. On ground steeper than Max Tilt the tilt fades out, and
    ///     is gone 15 degrees steeper.
    ///   - Leaving the ground (a jump, a fall, a climb), nothing new is measured: the last corrections
    ///     fade out as they were, instead of pulling the feet back toward the floor the body is leaving.
    ///   - Only the owner's character has one (it needs MAnimal). Other players see a remote body whose
    ///     feet are as animated.
    /// </summary>
    [DefaultExecutionOrder(50)]
    [DisallowMultipleComponent]
    public class FootGrounder : MonoBehaviour
    {
        /// <summary>A point under the sole, in the foot's (or the toes') own space.</summary>
        [Serializable]
        public struct SolePoint
        {
            public bool onToes;
            public Vector3 local;
        }

        [Header("Sole (measured by PlayerCharacterBuilder)")]
        public SolePoint leftHeel, leftToe, rightHeel, rightToe;

        [Tooltip("Set by the builder once the sole points are measured. Off: nothing is changed.")]
        public bool measured;

        [Header("Reach")]
        [Tooltip("How far a shoe may be lifted onto higher ground or out of the floor (m).")]
        [Range(0f, 0.8f)] public float maxStep = 0.45f;
        [Tooltip("How far a resting shoe may be lowered onto lower ground, and the hips with it (m).")]
        [Range(0f, 0.8f)] public float maxDrop = 0.35f;
        [Tooltip("A shoe whose sole is this close to the feet in the animation is resting on the floor (m).")]
        [Range(0f, 0.2f)] public float restingHeight = 0.03f;
        [Tooltip("Above this, a shoe is lifted (stepping) and is no longer lowered onto lower ground (m).")]
        [Range(0f, 0.4f)] public float steppingHeight = 0.12f;
        [Tooltip("The steepest ground a resting shoe lies flat on (degrees). On steeper ground the tilt fades out, and is gone 15 degrees steeper.")]
        [Range(0f, 60f)] public float maxTilt = 35f;

        [Header("Feel")]
        [Tooltip("How quickly the hips follow the ground (higher is quicker).")]
        [Range(1f, 40f)] public float hipsSpeed = 12f;
        [Tooltip("Seconds to switch on or off when the character lands, jumps, climbs.")]
        [Range(0.01f, 1f)] public float blendTime = 0.12f;
        [Tooltip("About how long (s) a shoe takes to follow a height the ground changes all at once under it - a " +
                 "step's edge, a kerb - instead of jumping it. A fast shoe, or one standing on the ground, takes less. " +
                 "Slopes are followed exactly.")]
        [Range(0.01f, 0.3f)] public float edgeTime = 0.06f;
        [Tooltip("The fastest (m/s) a shoe follows a height the ground changes all at once under it, so a foot swinging " +
                 "over a riser takes it over several frames instead of most of it in one. A shoe sweeping up a flight of " +
                 "stairs may go as fast as they climb under it.")]
        [Range(0.5f, 10f)] public float edgeMaxSpeed = 3f;
        [Tooltip("How quickly a resting shoe turns to a change of slope under it (higher is quicker).")]
        [Range(1f, 60f)] public float tiltSpeed = 20f;

        // A shoe eases an edge over about this much of its own travel (m) when that is sooner than Edge
        // Time: a running shoe crosses tread after tread, and a time alone would fall behind the stairs.
        const float EdgeTravel = 0.15f;

        // How steeply the ground under a shoe has been climbing: what the edges it crossed over about this much
        // of its own travel (m) rose, net, per metre. A shoe sweeping up stairs may follow them faster than Edge
        // Max Speed: that steeply for its speed, times Edge Keep Up (more than 1, to catch up). With Edge Max
        // Speed alone a running shoe would fall further behind at every tread and land in them. Going down, or
        // up and down again - a toe rocking on a nose, a ray finding the floor through the crack between two
        // treads for a frame - is no climb: counted as one, it let the next edge come in up to 10 cm a frame.
        // (Worked through for 18 cm risers on 40 cm treads: a walking shoe rises at most about 3.5 cm in a
        // 144 Hz frame and 6 in a 72 Hz one, where it rose 5.5 and 9 without the limit. A running or
        // sprinting shoe lands within about 1 cm of the tread on average, on 20 cm risers on 22 cm treads
        // as well, and swings 6 to 10 cm under the stairs' own profile on average.)
        const float EdgeSpan = 0.5f, EdgeKeepUp = 1.5f;

        // A shoe standing on the ground (resting, and slower than Still Speed) settles onto it this much
        // quicker: it should stand on it, not near it or in it. A shoe just lifting off is low too, but it
        // is swinging, and its toe clipping the next riser should rise smoothly.
        const float RestingEase = 0.35f;
        const float StillSpeed = 1f;

        // A surface less than this over a point is taken as under it (m); above that, the space between is tested.
        const float OpenMargin = 0.005f;

        // Air under a surface thinner than this is no open space: nothing a shoe fits in (m). A tile, plank or
        // mat lying on the floor, or laid a hair over it, is solid down to the floor.
        const float SolidGap = 0.03f;

        // Ground under the heel and under the toe this close in slope (degrees) is of one slope: the same surface, or two a
        // step apart. From there to SlopeApart the slope changes under the shoe more and more (a ramp's foot or top, rough
        // ground), unless the two are a step apart: by StepApart (m) one over the other, whatever their slopes (see SoleClear).
        const float SameSlope = 2f, SlopeApart = 7f, StepApart = 0.02f;

        // How long (s) a shoe's sole takes to go from taken flat to not, or back, as the ground under it changes between the
        // two (see SoleClear): a tenth of a second, not one frame.
        const float FlatTime = 0.1f;

        // A point's ground eased in from the feet level, after it had none in reach, counts as found again once it is
        // this close to the real ground (m).
        const float BlindDone = 0.005f;

        // On ground steeper than Max Tilt the tilt fades out over this many degrees more: gone at 50 for the
        // default 35, the steepest ground Malbers lets the human walk on. No shoe snaps flat at one angle.
        const float TiltFade = 15f;

        Animator _animator;
        MAnimal _animal;
        CharacterVisualInterpolator _smoother;
        Transform _hips, _leftUpper, _leftLower, _leftFoot, _leftToes, _rightUpper, _rightLower, _rightFoot, _rightToes;
        float _weight, _hipsOffset;
        int _mask;
        readonly RaycastHit[] _hits = new RaycastHit[8];
        readonly RaycastHit[] _probe = new RaycastHit[8];

        // The ground under one sole point as it is shown: the real ground, plus an offset that eases out
        // each height the ground changes all at once, so a shoe crossing an edge does not jump it.
        struct Footing
        {
            public bool known, found;               // measured before; and whether there was ground in reach
            public Vector3 at;                      // where the point was
            public Collider collider;               // the ground it was over
            public Vector3 point, normal;           // that ground, in the world
            public Vector3 localPoint, localNormal; // the same in the collider's own space, to follow it if it moves
            public float offset, rate;              // shown height minus real height (m), and how fast that eases (m/s)
            public float climbed;                   // what the edges crossed over about the last Edge Span of travel rose, net (m)
            public bool blind;                      // the offset eases in the feet level that stood in for no ground, not an edge
        }

        // One leg: where its sole points stand, the slope its shoe lies on (eased), and the correction it
        // was last given at full weight - the lift asked of the foot (m) and the turn laying it on the slope -
        // with what that was worked out from (kept only to be read, see LegReading).
        struct Leg
        {
            public Footing heel, toe;
            public Vector3 slope;
            public bool hasSlope;
            public float lift;
            public Quaternion tilt;
            public float lifted, resting, clear, want, heelAt, toeAt;
            public float flat;      // how much the sole is taken flat (0 to 1, eased; see SoleClear)
            public float flatLift;  // and the lift that added last frame (m)
            public bool hasFlat;

            public static Leg None => new Leg { slope = Vector3.up, tilt = Quaternion.identity };
        }

        /// <summary>
        /// What one leg was last measured as: for the live checks, and to find out why a foot moved. Reading it
        /// changes nothing. It holds the last frame that measured the ground (see <see cref="Grounding"/>); while
        /// the grounding fades out it keeps that frame's values.
        /// </summary>
        public struct LegReading
        {
            /// <summary>How high the animation holds the sole over the drawn feet level (m).</summary>
            public float lifted;
            /// <summary>How much the shoe is resting on the floor in the animation: 1 resting, 0 stepping.</summary>
            public float resting;
            /// <summary>How high the sole is over the ground under it as shown (m).</summary>
            public float clear;
            /// <summary>How far the foot should go up (+) or down (-) to sit on that ground as animated (m).</summary>
            public float want;
            /// <summary>The correction at full weight: Want within reach (m).</summary>
            public float lift;
            /// <summary>The height of the heel and of the toe as animated, laid on the slope if resting (m, world).</summary>
            public float heelHeight, toeHeight;
            /// <summary>Whether there was ground within reach under the heel, and under the toe.</summary>
            public bool heelFound, toeFound;
            /// <summary>The height of the ground shown under the heel and the toe (m, world).</summary>
            public float heelGround, toeGround;
            /// <summary>How much of a change of height (an edge) is still being eased in under the heel and the toe: shown minus real (m).</summary>
            public float heelOffset, toeOffset;
        }

        Leg _left = Leg.None, _right = Leg.None;
        Vector3 _drawnWas;
        bool _hasDrawn, _grounding;

        // The ground the body itself stands on this frame, as a plane: through where the body is drawn, facing
        // the ground's slope under it. Set before each frame's measuring.
        Vector3 _standAt, _standNormal;

        // What this frame changed, to put back next frame unless the Animator (or the smoothing) already did.
        struct Changed
        {
            public Transform transform;
            public Quaternion before, after;
        }

        readonly Changed[] _changed = new Changed[6];
        int _changedCount;
        Vector3 _hipsBefore, _hipsAfter;
        bool _hipsChanged;

        /// <summary>How much the grounding is on (0 to 1).</summary>
        public float Weight => _weight;

        /// <summary>How far the left foot is moved up (+) or down (-) this frame from where the animation has it (m).</summary>
        public float LeftCorrection => _left.lift * _weight;

        /// <summary>How far the right foot is moved up (+) or down (-) this frame from where the animation has it (m).</summary>
        public float RightCorrection => _right.lift * _weight;

        /// <summary>How far the hips are moved down this frame from where the animation has them (m, zero or less).</summary>
        public float HipsCorrection => _hipsOffset * _weight;

        /// <summary>Whether this frame measured the ground: on it, standing or moving (Malbers' Idle or Locomotion).</summary>
        public bool Grounding => _grounding;

        /// <summary>What the left leg was last measured as (see <see cref="LegReading"/>).</summary>
        public LegReading LeftReading => Reading(_left);

        /// <summary>What the right leg was last measured as (see <see cref="LegReading"/>).</summary>
        public LegReading RightReading => Reading(_right);

        static LegReading Reading(in Leg leg) => new LegReading
        {
            lifted = leg.lifted,
            resting = leg.resting,
            clear = leg.clear,
            want = leg.want,
            lift = leg.lift,
            heelHeight = leg.heelAt,
            toeHeight = leg.toeAt,
            heelFound = leg.heel.found,
            toeFound = leg.toe.found,
            heelGround = leg.heel.point.y + leg.heel.offset,
            toeGround = leg.toe.point.y + leg.toe.offset,
            heelOffset = leg.heel.offset,
            toeOffset = leg.toe.offset,
        };

        void Awake()
        {
            _animator = GetComponent<Animator>();
            _animal = GetComponent<MAnimal>();
            _smoother = GetComponent<CharacterVisualInterpolator>();
            if (_animator == null || !_animator.isHuman)
                return;

            _hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            _leftUpper = _animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            _leftLower = _animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            _leftFoot = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            _leftToes = _animator.GetBoneTransform(HumanBodyBones.LeftToes);
            _rightUpper = _animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            _rightLower = _animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            _rightFoot = _animator.GetBoneTransform(HumanBodyBones.RightFoot);
            _rightToes = _animator.GetBoneTransform(HumanBodyBones.RightToes);
        }

        void OnDisable()
        {
            PutBack();
            _weight = 0f;
            _grounding = false;
            Forget();
        }

        bool ShouldGround()
        {
            if (!measured || _animal == null || !_animal.enabled || _animator == null || !_animator.enabled)
                return false;
            if (!_animal.Grounded || _animal.ActiveStateID == null)
                return false;
            int state = _animal.ActiveStateID.ID;
            return state == StateEnum.Idle || state == StateEnum.Locomotion;
        }

        /// <summary>Drops everything measured: the next grounding starts from the ground as it is then.</summary>
        void Forget()
        {
            _hipsOffset = 0f;
            _left = Leg.None;
            _right = Leg.None;
            _hasDrawn = false;
        }

        void LateUpdate()
        {
            PutBack();
            if (_hips == null || _leftFoot == null || _rightFoot == null || _leftUpper == null || _rightUpper == null)
                return;

            bool grounding = ShouldGround();
            _grounding = grounding;
            _weight = Mathf.MoveTowards(_weight, grounding ? 1f : 0f, Time.deltaTime / blendTime);
            if (_weight <= 0f)
            {
                Forget();
                return;
            }

            var up = Vector3.up;
            if (grounding)
            {
                // The feet stand where the body is drawn: the smoothed root, not the physics root.
                var drawn = _smoother != null ? _smoother.ShownPosition : transform.position;
                float teleport = _smoother != null ? _smoother.teleportDistance : 2f;
                if (_hasDrawn && (drawn - _drawnWas).sqrMagnitude > teleport * teleport)
                {
                    // Put somewhere else (a respawn, a teleport): the old ground says nothing about the new.
                    _left = Leg.None;
                    _right = Leg.None;
                }

                _drawnWas = drawn;
                _hasDrawn = true;

                // The ground the body stands on, where it is drawn. Its slope is Malbers' ground normal under the
                // body (SlopeNormal), which it tilts this human's root motion along: its Surface Normal stays
                // straight up for a body with only a chest pivot, which is how it keeps the body upright on slopes.
                _standAt = drawn;
                _standNormal = _animal.SlopeNormal;

                _mask = _animal != null ? (int)_animal.GroundLayer : Physics.DefaultRaycastLayers;
                Sample(ref _left, _leftFoot, _leftToes, leftHeel, leftToe, drawn.y, up);
                Sample(ref _right, _rightFoot, _rightToes, rightHeel, rightToe, drawn.y, up);

                // The hips only ever go down, as far as the lower foot needs, so both legs can reach.
                float hips = Mathf.Max(-maxDrop, Mathf.Min(0f, Mathf.Min(_left.lift, _right.lift)));
                _hipsOffset = Mathf.Lerp(_hipsOffset, hips, 1f - Mathf.Exp(-hipsSpeed * Time.deltaTime));
            }
            else
            {
                // Leaving the ground: nothing new is measured. The floor is still under the soles for a
                // moment, and measuring on would stretch the legs back down to it as the body rises; the
                // last corrections fade out as they were instead.
                Tilt(_leftFoot, _left.tilt);
                Tilt(_rightFoot, _right.tilt);
            }

            float shown = _hipsOffset * _weight;
            if (Mathf.Abs(shown) > 1e-5f)
            {
                _hipsBefore = _hips.localPosition;
                _hips.position += up * shown;
                _hipsAfter = _hips.localPosition;
                _hipsChanged = true;
            }

            Place(_leftUpper, _leftLower, _leftFoot, (_left.lift * _weight) - shown, up);
            Place(_rightUpper, _rightLower, _rightFoot, (_right.lift * _weight) - shown, up);
        }

        /// <summary>
        /// Measures one shoe against the ground under it: lays it on the slope there (turning the foot
        /// now, by the weight) and works out how far the foot should go up (+) or down (-) to sit on that
        /// ground as the animation has it sit on a flat floor at <paramref name="feetLevel"/>.
        /// </summary>
        void Sample(ref Leg leg, Transform foot, Transform toes, SolePoint heelPoint, SolePoint toePoint, float feetLevel, Vector3 up)
        {
            var heel = Point(foot, toes, heelPoint);
            var toe = Point(foot, toes, toePoint);

            // How high the animation holds the sole over its own flat floor: about zero when resting on it.
            float lifted = Mathf.Min(heel.y, toe.y) - feetLevel;
            float resting = 1f - Mathf.InverseLerp(restingHeight, steppingHeight, lifted);

            float heelGround = Follow(ref leg.heel, heel, feetLevel, Standing(heel), resting, out var heelNormal);
            float toeGround = Follow(ref leg.toe, toe, feetLevel, Standing(toe), resting, out var toeNormal);

            // A resting shoe lies flat on the ground under it, tilted by its slope. The slope is eased, so an
            // edge under the heel or toe does not flick the shoe. Up to Max Tilt the shoe lies flat on it;
            // steeper, the tilt fades out rather than stopping at once.
            var slope = (heelNormal + toeNormal).normalized;
            leg.slope = leg.hasSlope ? Vector3.Slerp(leg.slope, slope, 1f - Mathf.Exp(-tiltSpeed * Time.deltaTime)).normalized : slope;
            leg.hasSlope = true;

            leg.tilt = Quaternion.identity;
            float angle = Vector3.Angle(up, leg.slope);
            if (maxTilt > 0f && angle > 0.5f)
            {
                float amount = resting * Mathf.InverseLerp(maxTilt + TiltFade, maxTilt, angle);
                if (amount > 0.001f)
                    leg.tilt = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(up, leg.slope), amount);
            }

            if (Tilt(foot, leg.tilt))
            {
                // The turn moved the heel and toe a little: the same ground, a little further along it.
                var turnedHeel = Point(foot, toes, heelPoint);
                var turnedToe = Point(foot, toes, toePoint);
                heelGround = HeightAlong(new Vector3(heel.x, heelGround, heel.z), heelNormal, turnedHeel);
                toeGround = HeightAlong(new Vector3(toe.x, toeGround, toe.z), toeNormal, turnedToe);
                heel = turnedHeel;
                toe = turnedToe;
            }

            // Then how high it is over the ground, and so how far it has to go: the sole as it lies once it is
            // planted, not as the animation pitches it (see SoleClear).
            float clear = SoleClear(ref leg, heel, heelGround, heelNormal, toe, toeGround, toeNormal);
            float want = Mathf.Max(lifted, 0f) - clear;
            if (want < 0f)
                want *= resting;   // lower ground: only a resting shoe follows it down; a stepping one keeps its swing

            leg.lift = Mathf.Clamp(want, -maxDrop, maxStep);
            leg.lifted = lifted;
            leg.resting = resting;
            leg.clear = clear;
            leg.want = want;
            leg.heelAt = heel.y;
            leg.toeAt = toe.y;
        }

        /// <summary>Turns the foot by <paramref name="tilt"/> (in the world), as much as the weight; false if that is no turn.</summary>
        bool Tilt(Transform foot, Quaternion tilt)
        {
            var turn = Quaternion.Slerp(Quaternion.identity, tilt, _weight);
            if (Quaternion.Angle(turn, Quaternion.identity) < 0.01f)
                return false;

            Remember(foot);
            foot.rotation = turn * foot.rotation;
            Done(foot);
            return true;
        }

        static Vector3 Point(Transform foot, Transform toes, SolePoint point) =>
            (point.onToes && toes != null ? toes : foot).TransformPoint(point.local);

        /// <summary>
        /// The ground under <paramref name="point"/> as it is shown this frame (its height), and the real
        /// ground's normal. A slope, or ground that moves, is followed exactly. Any other change of height
        /// - the point crossing a step's edge onto other ground - is eased in. <paramref name="standing"/>:
        /// up to this height a surface is the ground the body stands on (see <see cref="Standing"/>).
        /// </summary>
        float Follow(ref Footing f, Vector3 point, float fallback, float standing, float resting, out Vector3 normal)
        {
            bool found = GroundUnder(point, standing, out var hit);
            float height = found ? hit.point.y : fallback;
            normal = found ? hit.normal : Vector3.up;

            float dt = Time.deltaTime;
            if (!f.known)
            {
                f.offset = 0f;
                f.rate = 0f;
                f.climbed = 0f;
                f.blind = false;
            }
            else if (dt > 0f)
            {
                // Where last frame's ground lies under the point now, moved along with its object if that
                // moved. With nothing in reach then, the feet level as it is now.
                float expected = fallback;
                if (f.found)
                {
                    Vector3 was = f.point, wasNormal = f.normal;
                    if (f.collider != null)
                    {
                        var t = f.collider.transform;
                        was = t.TransformPoint(f.localPoint);
                        wasNormal = t.TransformDirection(f.localNormal);
                    }

                    expected = HeightAlong(was, wasNormal, point);
                }
                else
                    f.blind = true;   // what this eases in is the feet level standing in for no ground, not an edge (see SoleClear)

                // What the ground under the point did all at once since last frame: an edge, not its slope.
                float jump = expected - height;
                float reach = maxStep + maxDrop;
                f.offset = Mathf.Clamp(f.offset + jump, -reach, reach);

                // Eased over Edge Time, or over Edge Travel of the point's own travel when that is sooner
                // (a shoe that moved far since it last measured takes the new ground almost at once), and
                // quicker for a shoe standing on the ground.
                var travel = point - f.at;
                travel.y = 0f;
                float moved = travel.magnitude;
                float speed = moved / dt;
                float time = 1f / (1f / edgeTime + speed / EdgeTravel);
                time *= Mathf.Lerp(1f, RestingEase, resting * (1f - Mathf.Clamp01(speed / StillSpeed)));

                // But never faster than Edge Max Speed: a fast swing over a riser, or a long frame, took most of
                // it in one frame. A shoe sweeping up a flight of stairs has to rise as fast as they climb under
                // it, though, or it falls behind tread after tread and lands in them: as fast as the edges it
                // crossed over about the last Edge Span of its travel rose, with room to catch up. (Net, and no
                // more than the reach either way: a toe standing on an edge crosses it back and forth without
                // climbing. Only ground found both frames counts: the feet level standing in for none is no climb.)
                f.climbed = Mathf.Clamp((f.climbed * Mathf.Exp(-moved / EdgeSpan)) - (found && f.found ? jump : 0f), -reach, reach);
                float fastest = Mathf.Max(edgeMaxSpeed, speed * EdgeKeepUp * f.climbed / EdgeSpan);
                f.offset = Mathf.SmoothDamp(f.offset, 0f, ref f.rate, time, fastest, dt);
                if (f.blind && Mathf.Abs(f.offset) < BlindDone)
                    f.blind = false;
            }

            f.known = true;
            f.found = found;
            f.at = point;
            f.point = new Vector3(point.x, height, point.z);
            f.normal = normal;
            f.collider = found ? hit.collider : null;
            if (f.collider != null)
            {
                var t = f.collider.transform;
                f.localPoint = t.InverseTransformPoint(f.point);
                f.localNormal = t.InverseTransformDirection(f.normal);
            }

            return height + f.offset;
        }

        /// <summary>
        /// How high the sole is over the ground under it (m, below zero in it): the lower of the heel's and the toe's
        /// height over their ground, the higher of the two points taken as low as the other one along the slope - the
        /// sole as a line through its lowest point, as it lies once it is planted, whatever its pitch.
        ///
        /// Over one ground, a floor or a slope, that is the same thing. Over an edge it is not: the walk lands on the
        /// heel with the toe up and then lowers the toe, and with the heel on one step and the toe over the next, each
        /// centimetre the toe came down lifted the whole shoe - heel and leg with it - by as much: up to 4.5 cm a frame
        /// at 144 Hz, 23 cm in six frames, a pop of the foot every few steps going up stairs (and going down, a shoe on a
        /// step with its toe out over the next one down would drop as its heel rose to push off). Taken flat, the shoe
        /// is on the higher step from when the toe gets over it, eased in as any edge is, and its pitch - the heel
        /// landing, the toe pushing off - no longer moves it up or down. The sole is laid flat through the heel along the
        /// slope of the heel's ground, and through the toe along the toe's, and the lower of the two goes: on treads tilted
        /// unlike each other that changes nothing at once as the shoe rolls from heel to toe.
        ///
        /// Over a step it is taken flat whatever the slopes of its treads: worn or tilted treads and slabs, a tread's two
        /// triangles a few degrees apart. A step is told by heights, not slopes: across one, the toe's ground lies over the
        /// slope of the heel's and the heel's under the toe's (or the other way round, going down); where the slope changes
        /// under the shoe - a ramp's foot or top, rough ground - both lie on the same side, and the sole is not taken flat
        /// there (from SameSlope to SlopeApart apart less and less): it cannot lie flat on both. Between the two the shoe
        /// eases over Flat Time, so neither a tilted tread nor a triangle's edge under the heel or toe moves it all at once
        /// (as a switch at 2 degrees did: by as much as its pitch, up to 15 cm, in one frame).
        ///
        /// The sole is laid on the ground as it is shown, which never changes at once: the step is taken eased in, not as it
        /// really is (when the toe gets over the next riser while the heel's ground is still easing onto the one before, the
        /// real one comes at once). With no ground in reach under a point, or while the ground it found again is still eased
        /// in from the feet level that stood in for none, the sole is not taken flat - that is no step, and taken as one it held
        /// shoes landing on a ramp that falls away ahead up over it (running down a 20 degree ramp, the soles' 95th percentile
        /// over it went from 3.6 to 7 cm) - and it adds no lift it did not add the frame before: what it added fades out with it.
        /// The flat lines go along the slopes of the ground under the heel and toe as far as those are of one slope, level as
        /// they part: what a ray finds right at an edge or an inner corner of a stair mesh is no slope a shoe lies along.
        /// </summary>
        float SoleClear(ref Leg leg, Vector3 heel, float heelGround, Vector3 heelNormal, Vector3 toe, float toeGround, Vector3 toeNormal)
        {
            float clear = Mathf.Min(heel.y - heelGround, toe.y - toeGround);

            // How much the sole is taken flat: over ground of one slope, or across a step (the toe's ground over the heel's
            // slope and the heel's under the toe's, or the other way round); not where the slope changes under the shoe, nor
            // with no ground in reach under a point or while it is eased in.
            bool unsure = !leg.heel.found || !leg.toe.found || leg.heel.blind || leg.toe.blind;
            float oneSlope = 1f - Mathf.InverseLerp(SameSlope, SlopeApart, Vector3.Angle(heelNormal, toeNormal));
            float target = 0f;
            if (!unsure)
            {
                float toeOver = toeGround - HeightAlong(new Vector3(heel.x, heelGround, heel.z), heelNormal, toe);
                float heelOver = heelGround - HeightAlong(new Vector3(toe.x, toeGround, toe.z), toeNormal, heel);
                float step = Mathf.Max(Mathf.Min(toeOver, -heelOver), Mathf.Min(-toeOver, heelOver));
                target = Mathf.Max(oneSlope, Mathf.Clamp01(step / StepApart));
            }

            // The sole flat through the heel, over the toe's ground; or through the toe, over the heel's.
            var heelLine = Vector3.Slerp(Vector3.up, heelNormal, oneSlope);
            var toeLine = Vector3.Slerp(Vector3.up, toeNormal, oneSlope);
            float flat = Mathf.Min(HeightAlong(heel, heelLine, toe) - toeGround, HeightAlong(toe, toeLine, heel) - heelGround);

            leg.flat = leg.hasFlat ? Mathf.MoveTowards(leg.flat, target, Time.deltaTime / FlatTime) : target;
            float lift = leg.flat * Mathf.Max(0f, clear - flat);
            if (unsure && leg.hasFlat)
                lift = Mathf.Min(lift, leg.flatLift);
            leg.flatLift = lift;
            leg.hasFlat = true;
            return clear - lift;
        }

        /// <summary>The height at <paramref name="at"/> of the ground through <paramref name="point"/> facing <paramref name="normal"/>.</summary>
        static float HeightAlong(Vector3 point, Vector3 normal, Vector3 at)
        {
            if (normal.y < 0.5f)
                return point.y;   // steeper than 60 degrees: no slope to go by
            return point.y - ((normal.x * (at.x - point.x)) + (normal.z * (at.z - point.z))) / normal.y;
        }

        /// <summary>
        /// The height at <paramref name="point"/>'s x and z up to which a surface is the ground the body itself
        /// stands on: the feet level, or the body's slope through it where that is higher (a ramp ahead), and
        /// Resting Height over that. A sprinting toe goes several centimetres into the floor, deeper than many
        /// floor tiles are thick: under the bottom of the tile the body stands on, it is still on that tile.
        /// </summary>
        float Standing(Vector3 point) => Mathf.Max(_standAt.y, HeightAlong(_standAt, _standNormal, point)) + restingHeight;

        /// <summary>
        /// The ground under <paramref name="point"/> within reach, not this character itself: the highest
        /// surface the point is on or in. A surface over the point with open space between - a table top, a
        /// bench, a beam, the next tread of open stairs - is not its ground: the point is under it. Only a
        /// surface higher than <paramref name="standing"/>, clearly over the ground the body stands on, can be.
        /// </summary>
        bool GroundUnder(Vector3 point, float standing, out RaycastHit ground)
        {
            var from = new Vector3(point.x, point.y + maxStep + 0.1f, point.z);
            int count = Physics.RaycastNonAlloc(from, Vector3.down, _hits, maxStep + maxDrop + 0.2f, _mask, QueryTriggerInteraction.Ignore);
            SortHighestFirst(_hits, count);

            bool under = false;
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider == null || Own(hit.collider))
                    continue;

                float above = hit.point.y - point.y;
                if (above > OpenMargin && hit.point.y > standing && Overhead(point, above - OpenMargin, i, count))
                {
                    under = true;
                    continue;
                }

                ground = hit;
                return true;
            }

            // Under something and nothing else found: what holds the point up may be the same object (a
            // ray reports each collider once, so one stair mesh hides its own lower tread), so look again
            // from the point itself.
            if (under)
                return Below(point, out ground);

            ground = default;
            return false;
        }

        /// <summary>
        /// Whether <paramref name="point"/> is in open space under the surface the ray down through it met
        /// <paramref name="index"/>th (of <paramref name="count"/>, highest first): that surface's underside is
        /// straight over the point within <paramref name="distance"/>, with room under it. A ray that starts
        /// inside a collider does not see it (and back faces are not hit), so for a point inside a step,
        /// nothing is over it. A tile, plank or mat lying on the floor has the floor's top right under its
        /// underside: a point sunk into the floor there is in the ground, not under the tile.
        /// </summary>
        bool Overhead(Vector3 point, float distance, int index, int count)
        {
            var surface = _hits[index].collider;
            int found = Physics.RaycastNonAlloc(point, Vector3.up, _probe, distance, _mask, QueryTriggerInteraction.Ignore);
            float underside = float.NaN;
            for (int i = 0; i < found; i++)
            {
                if (_probe[i].collider == surface)
                {
                    underside = _probe[i].point.y;
                    break;
                }
            }

            if (float.IsNaN(underside))
                return false;

            // The next surface down the same line: with its top at that underside (or over it), the surface
            // rests on it, and there is no room under it.
            for (int i = index + 1; i < count; i++)
            {
                var below = _hits[i];
                if (below.collider == null || below.collider == surface || Own(below.collider))
                    continue;
                return below.point.y < underside - SolidGap;
            }

            return true;
        }

        /// <summary>The first ground straight under <paramref name="point"/>, which is in open space, within reach.</summary>
        bool Below(Vector3 point, out RaycastHit ground)
        {
            var from = new Vector3(point.x, point.y + OpenMargin, point.z);
            int count = Physics.RaycastNonAlloc(from, Vector3.down, _probe, maxDrop + 0.1f + OpenMargin, _mask, QueryTriggerInteraction.Ignore);
            ground = default;
            float best = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var hit = _probe[i];
                if (hit.collider == null || Own(hit.collider) || hit.distance >= best)
                    continue;
                best = hit.distance;
                ground = hit;
            }

            return !float.IsPositiveInfinity(best);
        }

        bool Own(Collider collider) => collider.transform.IsChildOf(transform);

        static void SortHighestFirst(RaycastHit[] hits, int count)
        {
            for (int i = 1; i < count; i++)
            {
                var hit = hits[i];
                int j = i - 1;
                while (j >= 0 && hits[j].point.y < hit.point.y)
                {
                    hits[j + 1] = hits[j];
                    j--;
                }

                hits[j + 1] = hit;
            }
        }

        /// <summary>Bends the leg so the foot moves <paramref name="lift"/> along <paramref name="up"/>, keeping its turn.</summary>
        void Place(Transform upper, Transform lower, Transform foot, float lift, Vector3 up)
        {
            if (Mathf.Abs(lift) < 1e-4f || lower == null)
                return;

            Remember(upper);
            Remember(lower);
            Remember(foot);
            Reach(upper, lower, foot, foot.position + up * lift);
            Done(upper);
            Done(lower);
            Done(foot);
        }

        void Remember(Transform t)
        {
            for (int i = 0; i < _changedCount; i++)
            {
                if (_changed[i].transform == t)
                    return;
            }

            if (_changedCount < _changed.Length)
                _changed[_changedCount++] = new Changed { transform = t, before = t.localRotation, after = t.localRotation };
        }

        void Done(Transform t)
        {
            for (int i = 0; i < _changedCount; i++)
            {
                if (_changed[i].transform == t)
                    _changed[i].after = t.localRotation;
            }
        }

        /// <summary>Undoes last frame's change wherever nothing else has posed the bone since.</summary>
        void PutBack()
        {
            for (int i = 0; i < _changedCount; i++)
            {
                var c = _changed[i];
                if (c.transform != null && c.transform.localRotation == c.after)
                    c.transform.localRotation = c.before;
            }

            _changedCount = 0;
            if (_hipsChanged && _hips != null && _hips.localPosition == _hipsAfter)
                _hips.localPosition = _hipsBefore;
            _hipsChanged = false;
        }

        /// <summary>Two-bone reach: the end of upper-lower-end goes to <paramref name="target"/>, keeping the end's turn.</summary>
        static void Reach(Transform upper, Transform lower, Transform end, Vector3 target)
        {
            var endTurn = end.rotation;
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float ab = Vector3.Distance(a, b), bc = Vector3.Distance(b, c);
            if (ab < 1e-4f || bc < 1e-4f)
                return;

            float want = Mathf.Clamp(Vector3.Distance(a, target), Mathf.Abs(ab - bc) + 1e-4f, ab + bc - 1e-4f);
            var bend = Vector3.Cross(a - b, c - b);
            if (bend.sqrMagnitude > 1e-10f)
            {
                float knee = Vector3.Angle(a - b, c - b);
                float wanted = Mathf.Acos(Mathf.Clamp((ab * ab + bc * bc - want * want) / (2f * ab * bc), -1f, 1f)) * Mathf.Rad2Deg;
                lower.rotation = Quaternion.AngleAxis(wanted - knee, bend.normalized) * lower.rotation;
            }

            upper.rotation = Quaternion.FromToRotation(end.position - a, target - a) * upper.rotation;
            end.rotation = endTurn;
        }
    }
}
