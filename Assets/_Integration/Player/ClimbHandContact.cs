using MalbersAnimations;
using MalbersAnimations.Controller;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Keeps the hands on the wall while the character free-climbs. Malbers climbs with no hand IK: the
    /// hands go where the animation puts them, which on Steve's body lands them on the wall and keeps
    /// them there. This body's arms are shorter against its trunk, so over a climbing cycle its hands
    /// swing from about 6 cm inside the wall to 9 cm off it, even with the body held at the right
    /// distance (CharacterStateCalibration). After the animation each frame of a climb, this finds the
    /// wall in front of each hand and moves the wrist along the wall's normal - only along it - until
    /// the hand's nearest finger joint touches, as Steve's do, bending the elbow to suit (a two-bone
    /// reach on the real bones, so it agrees with what is drawn). The hand keeps the path up the wall
    /// and the turn the animation gives it. Lives on the character's root, beside its Animator
    /// (PlayerCharacterBuilder adds it).
    /// </summary>
    [DisallowMultipleComponent]
    public class ClimbHandContact : MonoBehaviour
    {
        [Tooltip("How far the nearest finger joint stays from the wall (m): the joints sit inside the fingers.")]
        [Range(0f, 0.05f)] public float contactGap = 0.01f;

        [Tooltip("The furthest a hand is moved to meet the wall (m). A hand further off than this is left where the animation has it.")]
        [Range(0f, 0.3f)] public float maxShift = 0.15f;

        [Tooltip("How long the hands take to settle onto the wall as a climb starts, and to come off it as the climb ends (s).")]
        [Range(0.01f, 1f)] public float blendTime = 0.15f;

        static readonly HumanBodyBones[] LeftFingers = Fingers(true);
        static readonly HumanBodyBones[] RightFingers = Fingers(false);

        Animator _animator;
        MAnimal _animal;
        CharacterVisualInterpolator _smoother;
        Climb _climb;
        float _weight;

        /// <summary>How much the hands are held to the wall now, 0 to 1.</summary>
        public float Weight => _weight;

        static HumanBodyBones[] Fingers(bool left)
        {
            var names = new[] { "Thumb", "Index", "Middle", "Ring", "Little" };
            var joints = new[] { "Proximal", "Intermediate", "Distal" };
            var bones = new HumanBodyBones[names.Length * joints.Length];
            int i = 0;
            foreach (var finger in names)
            {
                foreach (var joint in joints)
                    bones[i++] = (HumanBodyBones)System.Enum.Parse(typeof(HumanBodyBones), (left ? "Left" : "Right") + finger + joint);
            }

            return bones;
        }

        void Awake()
        {
            _animator = GetComponent<Animator>();
            _animal = GetComponent<MAnimal>();
            _smoother = GetComponent<CharacterVisualInterpolator>();
        }

        void LateUpdate()
        {
            bool climbing = _animal != null && _animal.ActiveStateID != null && _animal.ActiveStateID.ID == StateEnum.Climb;
            _weight = Mathf.MoveTowards(_weight, climbing ? 1f : 0f, Time.deltaTime / blendTime);
            if (_weight <= 0f || _animator == null || !_animator.isHuman)
                return;

            // The character's own copy of the climb, which sees the levels' surface layers.
            if (_climb == null && _animal != null)
                _climb = _animal.State_Get<Climb>();
            LayerMask walls = _climb != null && _climb.ClimbLayer != null ? _climb.ClimbLayer.Value : (LayerMask)Physics.DefaultRaycastLayers;

            Touch(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, LeftFingers, walls);
            Touch(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, RightFingers, walls);
        }

        void Touch(HumanBodyBones upperBone, HumanBodyBones lowerBone, HumanBodyBones handBone, HumanBodyBones[] fingers, LayerMask walls)
        {
            var upper = _animator.GetBoneTransform(upperBone);
            var lower = _animator.GetBoneTransform(lowerBone);
            var hand = _animator.GetBoneTransform(handBone);
            if (upper == null || lower == null || hand == null)
                return;

            // The wall in front of the hand: a climber faces its wall. The body is drawn turned as the
            // smoothed root is, which can lag the root itself by up to a physics step.
            var forward = (_smoother != null ? _smoother.ShownRotation : transform.rotation) * Vector3.forward;
            var wrist = hand.position;
            if (!Physics.Raycast(wrist - forward * 0.4f, forward, out var hit, 0.8f, walls, QueryTriggerInteraction.Ignore))
                return;

            var normal = hit.normal;
            float nearest = float.MaxValue;
            foreach (var finger in fingers)
            {
                var joint = _animator.GetBoneTransform(finger);
                if (joint != null)
                    nearest = Mathf.Min(nearest, Vector3.Dot(joint.position - hit.point, normal));
            }

            if (nearest == float.MaxValue)
                return;
            float needed = contactGap - nearest;
            if (Mathf.Abs(needed) > maxShift || Mathf.Abs(needed) < 1e-4f)
                return;

            Reach(upper, lower, hand, wrist + normal * (needed * _weight));
        }

        /// <summary>
        /// Bends the elbow, then turns the shoulder, so the wrist lands on the target; the hand keeps its
        /// turn in the world. For the few centimetres asked of it here the arm stays in its own plane.
        /// </summary>
        static void Reach(Transform upper, Transform lower, Transform hand, Vector3 target)
        {
            var handTurn = hand.rotation;
            Vector3 a = upper.position, b = lower.position, c = hand.position;
            float ab = Vector3.Distance(a, b), bc = Vector3.Distance(b, c);
            if (ab < 1e-4f || bc < 1e-4f)
                return;

            float want = Mathf.Clamp(Vector3.Distance(a, target), Mathf.Abs(ab - bc) + 1e-4f, ab + bc - 1e-4f);
            var bend = Vector3.Cross(a - b, c - b);
            if (bend.sqrMagnitude > 1e-10f)
            {
                float elbow = Vector3.Angle(a - b, c - b);
                float wanted = Mathf.Acos(Mathf.Clamp((ab * ab + bc * bc - want * want) / (2f * ab * bc), -1f, 1f)) * Mathf.Rad2Deg;
                lower.rotation = Quaternion.AngleAxis(wanted - elbow, bend.normalized) * lower.rotation;
            }

            upper.rotation = Quaternion.FromToRotation(hand.position - a, target - a) * upper.rotation;
            hand.rotation = handTurn;
        }
    }
}
