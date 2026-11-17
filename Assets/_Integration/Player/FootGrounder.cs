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
        [Tooltip("The steepest ground a resting shoe tilts to lie flat on (degrees).")]
        [Range(0f, 60f)] public float maxTilt = 35f;

        [Header("Feel")]
        [Tooltip("How quickly the hips follow the ground (higher is quicker).")]
        [Range(1f, 40f)] public float hipsSpeed = 12f;
        [Tooltip("Seconds to switch on or off when the character lands, jumps, climbs.")]
        [Range(0.01f, 1f)] public float blendTime = 0.12f;

        Animator _animator;
        MAnimal _animal;
        Transform _hips, _leftUpper, _leftLower, _leftFoot, _leftToes, _rightUpper, _rightLower, _rightFoot, _rightToes;
        float _weight, _hipsOffset;
        readonly RaycastHit[] _hits = new RaycastHit[8];

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

        void Awake()
        {
            _animator = GetComponent<Animator>();
            _animal = GetComponent<MAnimal>();
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
            _hipsOffset = 0f;
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

        void LateUpdate()
        {
            PutBack();
            if (_hips == null || _leftFoot == null || _rightFoot == null || _leftUpper == null || _rightUpper == null)
                return;

            _weight = Mathf.MoveTowards(_weight, ShouldGround() ? 1f : 0f, Time.deltaTime / blendTime);
            if (_weight <= 0f)
            {
                _hipsOffset = 0f;
                return;
            }

            var up = Vector3.up;
            float feetLevel = transform.position.y;

            float left = Correction(_leftFoot, _leftToes, leftHeel, leftToe, feetLevel, up);
            float right = Correction(_rightFoot, _rightToes, rightHeel, rightToe, feetLevel, up);

            // The hips only ever go down, as far as the lower foot needs, so both legs can reach.
            float hips = Mathf.Max(-maxDrop, Mathf.Min(0f, Mathf.Min(left, right)));
            _hipsOffset = Mathf.Lerp(_hipsOffset, hips, 1f - Mathf.Exp(-hipsSpeed * Time.deltaTime));
            float shown = _hipsOffset * _weight;

            if (Mathf.Abs(shown) > 1e-5f)
            {
                _hipsBefore = _hips.localPosition;
                _hips.position += up * shown;
                _hipsAfter = _hips.localPosition;
                _hipsChanged = true;
            }

            Place(_leftUpper, _leftLower, _leftFoot, (left * _weight) - shown, up);
            Place(_rightUpper, _rightLower, _rightFoot, (right * _weight) - shown, up);
        }

        /// <summary>
        /// How far the foot should go up (+) or down (-) to sit on the ground under it as the animation
        /// has it sit on a flat floor at <paramref name="feetLevel"/>.
        /// </summary>
        float Correction(Transform foot, Transform toes, SolePoint heelPoint, SolePoint toePoint, float feetLevel, Vector3 up)
        {
            var heel = Point(foot, toes, heelPoint);
            var toe = Point(foot, toes, toePoint);

            // How high the animation holds the sole over its own flat floor: about zero when resting on it.
            float lifted = Mathf.Min(heel.y, toe.y) - feetLevel;
            float resting = 1f - Mathf.InverseLerp(restingHeight, steppingHeight, lifted);

            // A resting shoe lies flat on the ground under it: tilted by the ground's slope.
            float heelGround = GroundUnder(heel, feetLevel, out var heelNormal);
            float toeGround = GroundUnder(toe, feetLevel, out var toeNormal);
            float tilt = resting * _weight;
            if (tilt > 0.001f && maxTilt > 0f)
            {
                var normal = (heelNormal + toeNormal).normalized;
                if (Vector3.Angle(up, normal) <= maxTilt && Vector3.Angle(up, normal) > 0.5f)
                {
                    Remember(foot);
                    foot.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(up, normal), tilt) * foot.rotation;
                    Done(foot);
                    heel = Point(foot, toes, heelPoint);
                    toe = Point(foot, toes, toePoint);
                    heelGround = GroundUnder(heel, feetLevel, out _);
                    toeGround = GroundUnder(toe, feetLevel, out _);
                }
            }

            // Then how high it is over the real ground, and so how far it has to go.
            float clear = Mathf.Min(heel.y - heelGround, toe.y - toeGround);
            float want = Mathf.Max(lifted, 0f) - clear;
            if (want < 0f)
                want *= resting;   // lower ground: only a resting shoe follows it down; a stepping one keeps its swing

            return Mathf.Clamp(want, -maxDrop, maxStep);
        }

        static Vector3 Point(Transform foot, Transform toes, SolePoint point) =>
            (point.onToes && toes != null ? toes : foot).TransformPoint(point.local);

        /// <summary>The highest ground under <paramref name="point"/> within reach, not this character itself.</summary>
        float GroundUnder(Vector3 point, float fallback, out Vector3 normal)
        {
            normal = Vector3.up;
            var from = new Vector3(point.x, point.y + maxStep + 0.1f, point.z);
            float length = maxStep + maxDrop + 0.2f;
            int mask = _animal != null ? (int)_animal.GroundLayer : Physics.DefaultRaycastLayers;
            int count = Physics.RaycastNonAlloc(from, Vector3.down, _hits, length, mask, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(transform))
                    continue;
                if (hit.point.y > best)
                {
                    best = hit.point.y;
                    normal = hit.normal;
                }
            }

            return float.IsNegativeInfinity(best) ? fallback : best;
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
