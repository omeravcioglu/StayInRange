using System;
using System.Collections;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Smooths what the player sees of a character that physics moves in fixed steps: the body model
    /// and the camera target glide between physics steps instead of jumping once per step.
    ///
    /// WHY. Malbers moves the character's root by its animations inside the physics step, and its
    /// Rigidbody has no interpolation. On a 144 Hz screen the body and the camera target stood still
    /// for two or three frames and then jumped a whole step: the judder.
    ///
    /// HOW. Only the visuals move. Every physics step begins with them put back exactly where the root
    /// holds them, so physics, animation, IK and Malbers' own checks never see the smoothing. Each
    /// frame, before any other script's Update, they are shown where the root was a fraction of a step
    /// ago - the trade Unity's own Rigidbody interpolation makes (at most one physics step behind).
    /// Everything after that sees the smoothed pose: other scripts, Malbers' AnimalTracker (which eases
    /// the camera target relative to Player Core, between Update and LateUpdate) and the camera brain.
    /// Smooth Player Core as a follower and the camera target rides along with it.
    ///
    /// The root moving outside the physics step (a script placing it, a respawn) is taken as it is,
    /// position and rotation each on their own, and so is a move longer than Teleport Distance in one
    /// step. While Suspended, or while the Animator is off (a ragdoll), nothing is smoothed.
    ///
    /// THE POSE. Malbers' Animator runs in the physics step too, so the body's pose changed only 50
    /// times a second: on a 144 Hz screen the limbs held for two or three frames and then jumped, the
    /// "snapping while moving". With Smooth Pose each humanoid bone is shown the same fraction of a
    /// step between the last two poses the Animator made. Every physics step starts from the
    /// Animator's own pose again, and scripts that bend bones after Update (look-at, hand contact)
    /// work on the smoothed pose, as they would on the Animator's.
    ///
    /// Each step's pose is taken once Malbers' IK manager has finished bending it, which it does in an
    /// after-step coroutine whose order against this one's is not fixed (a pose taken in our own
    /// after-step coroutine lost the head's look-at). So it is taken at the next thing that surely
    /// comes after: the first Update after the step, or - when one frame runs two or more steps (any
    /// frame rate under 50, a hitch) - the next step's FixedUpdate, before that step's Animator
    /// overwrites it. The two poses shown between are then always the same two steps the root is
    /// shown between.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    [DisallowMultipleComponent]
    public class CharacterVisualInterpolator : MonoBehaviour
    {
        [Tooltip("The model's parent under the root (Rotator/ModelOffset). Shown smoothed between physics steps.")]
        public Transform visual;

        [Tooltip("Other transforms to smooth the same way, such as Player Core (the camera target's parent).")]
        public Transform[] followers = Array.Empty<Transform>();

        [Tooltip("A move this long in one physics step is shown at once rather than glided (metres).")]
        [Min(0.1f)] public float teleportDistance = 2f;

        [Tooltip("Glide the body's pose between physics steps too. The Animator poses it once per physics step " +
                 "(50 times a second), so on a fast screen the limbs held still and then jumped.")]
        public bool smoothPose = true;

        /// <summary>Set by code that drives the body itself (a ragdoll, a cutscene) to show it unsmoothed.</summary>
        public bool Suspended { get; set; }

        /// <summary>
        /// Where the body is drawn this frame: the root as smoothed between physics steps, which is where
        /// the model's feet stand - up to a physics step behind the root itself. The root's own position
        /// when nothing is offset (during a physics step, or with nothing to smooth).
        /// </summary>
        public Vector3 ShownPosition => _offset && visual != null ? transform.position + transform.rotation * _shownLocalPosition : transform.position;

        /// <summary>The turn the body is drawn with this frame (see <see cref="ShownPosition"/>).</summary>
        public Quaternion ShownRotation => _offset && visual != null ? transform.rotation * _shownLocalRotation : transform.rotation;

        struct Shown
        {
            public Transform Transform;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }

        Shown[] _shown = Array.Empty<Shown>();
        bool _offset;
        Animator _animator;
        Coroutine _afterPhysics;

        // Where Apply drew the root, in the root's own space, so it stays right if the root is placed
        // again later in the same frame (the visuals ride along with it). Meaningful while _offset.
        Vector3 _shownLocalPosition;
        Quaternion _shownLocalRotation = Quaternion.identity;

        bool _hasStep;
        Vector3 _previousPosition, _currentPosition;
        Quaternion _previousRotation, _currentRotation;

        // The humanoid bones, hips first, and the pose the last two physics steps left them in.
        Transform[] _bones = Array.Empty<Transform>();
        Quaternion[] _bonePrevious = Array.Empty<Quaternion>(), _boneCurrent = Array.Empty<Quaternion>();
        Vector3 _hipsPrevious, _hipsCurrent;
        bool _hasPose, _poseShown;

        // A physics step has run since the pose was last taken (and whether the root jumped in it).
        bool _posePending, _poseJumped;

        void OnEnable()
        {
            _animator = GetComponent<Animator>();
            _hasStep = false;
            FindBones();
            _afterPhysics = StartCoroutine(AfterPhysics());
        }

        void OnDisable()
        {
            if (_afterPhysics != null)
                StopCoroutine(_afterPhysics);
            _afterPhysics = null;
            Restore();
            RestorePose();
        }

        // The physics step sees the exact pose.
        void FixedUpdate()
        {
            Restore();
            if (_posePending)
            {
                // A second step in the same frame: no Update has run since the last one, so the bones
                // still hold exactly the pose it left, with Malbers' late IK on it (its after-step
                // coroutine is done by now). Take it before this step's Animator overwrites it;
                // otherwise this frame would show the pose of two steps ago against the root of one
                // step ago, a hitch in the limbs below 50 fps.
                TakePose();
            }
            else
                RestorePose();
        }

        void Update()
        {
            Restore();
            if (_posePending)
            {
                // The last step of the frame. The Animator posed the body in the physics step, and
                // Malbers' IK manager finishes bending it (the head's look-at) after the step, in an
                // after-step coroutine whose order against this one's is not fixed. By the first Update
                // after the step both are done.
                TakePose();
            }

            Apply();
        }

        /// <summary>Takes the pose the last physics step left as the newest one.</summary>
        void TakePose()
        {
            RecordPose(_poseJumped);
            _posePending = false;
            _poseJumped = false;
        }

        IEnumerator AfterPhysics()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                RecordStep();
            }
        }

        /// <summary>Remembers where the physics step left the root, and where it was the step before.</summary>
        void RecordStep()
        {
            var root = transform;
            root.GetPositionAndRotation(out var position, out var rotation);
            bool jumped = (position - _currentPosition).sqrMagnitude > teleportDistance * teleportDistance;
            _previousPosition = _hasStep && !jumped ? _currentPosition : position;
            _previousRotation = _hasStep && !jumped ? _currentRotation : rotation;
            _currentPosition = position;
            _currentRotation = rotation;
            _hasStep = true;
            _posePending = true;
            _poseJumped |= jumped;
        }

        /// <summary>Shows the visuals where the root was a fraction of a physics step ago.</summary>
        void Apply()
        {
            if (!_hasStep || Suspended || (_animator != null && !_animator.enabled) || Time.fixedDeltaTime <= 0f)
            {
                RestorePose();
                return;
            }

            var root = transform;
            root.GetPositionAndRotation(out var position, out var rotation);

            // Moved since the physics step by something else: show that as it is, from here on.
            if ((position - _currentPosition).sqrMagnitude > 1e-8f)
                _previousPosition = _currentPosition = position;
            if (Quaternion.Angle(rotation, _currentRotation) > 0.01f)
                _previousRotation = _currentRotation = rotation;

            float t = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
            ApplyPose(t);
            var shownPosition = Vector3.LerpUnclamped(_previousPosition, _currentPosition, t);
            var shownRotation = Quaternion.SlerpUnclamped(_previousRotation, _currentRotation, t);
            var turn = shownRotation * Quaternion.Inverse(rotation);
            if ((shownPosition - position).sqrMagnitude < 1e-10f && Quaternion.Angle(turn, Quaternion.identity) < 0.001f)
                return;

            var inverse = Quaternion.Inverse(rotation);
            _shownLocalPosition = inverse * (shownPosition - position);
            _shownLocalRotation = inverse * shownRotation;

            Capture();
            for (int i = 0; i < _shown.Length; i++)
            {
                var target = _shown[i].Transform;
                target.GetPositionAndRotation(out var p, out var r);
                target.SetPositionAndRotation(shownPosition + turn * (p - position), turn * r);
            }

            _offset = true;
        }

        /// <summary>Remembers each smoothed transform's own local pose, which Restore puts back.</summary>
        void Capture()
        {
            int count = (visual != null ? 1 : 0) + (followers?.Length ?? 0);
            if (_shown.Length != count)
                _shown = new Shown[count];

            int n = 0;
            if (visual != null)
                _shown[n++] = Snapshot(visual);
            if (followers != null)
            {
                foreach (var follower in followers)
                {
                    if (follower != null && follower != visual)
                        _shown[n++] = Snapshot(follower);
                }
            }

            if (n != _shown.Length)
                Array.Resize(ref _shown, n);
        }

        static Shown Snapshot(Transform t)
        {
            t.GetLocalPositionAndRotation(out var position, out var rotation);
            return new Shown { Transform = t, LocalPosition = position, LocalRotation = rotation };
        }

        // ---- The pose --------------------------------------------------------------------------------

        void FindBones()
        {
            _hasPose = _poseShown = _posePending = _poseJumped = false;
            if (_animator == null || !_animator.isHuman || _animator.avatar == null)
            {
                _bones = Array.Empty<Transform>();
                return;
            }

            var bones = new System.Collections.Generic.List<Transform>();
            for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
            {
                var t = _animator.GetBoneTransform(bone);
                if (t != null && !bones.Contains(t))
                    bones.Add(t);
            }

            _bones = bones.ToArray();
            _bonePrevious = new Quaternion[_bones.Length];
            _boneCurrent = new Quaternion[_bones.Length];
        }

        /// <summary>After a physics step, once all of it is done: the pose it left becomes the newest one.</summary>
        void RecordPose(bool jumped)
        {
            if (!smoothPose || _bones.Length == 0 || _bones[0] == null)
            {
                _hasPose = false;
                return;
            }

            bool keep = _hasPose && !jumped;
            for (int i = 0; i < _bones.Length; i++)
            {
                var rotation = _bones[i] != null ? _bones[i].localRotation : Quaternion.identity;
                _bonePrevious[i] = keep ? _boneCurrent[i] : rotation;
                _boneCurrent[i] = rotation;
            }

            var hips = _bones[0].localPosition;
            _hipsPrevious = keep ? _hipsCurrent : hips;
            _hipsCurrent = hips;
            _hasPose = true;
            _poseShown = false;
        }

        /// <summary>Shows each bone <paramref name="t"/> of the way from the older pose to the newer.</summary>
        void ApplyPose(float t)
        {
            if (!smoothPose || !_hasPose)
                return;

            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] != null)
                    _bones[i].localRotation = Quaternion.SlerpUnclamped(_bonePrevious[i], _boneCurrent[i], t);
            }

            if (_bones[0] != null)
                _bones[0].localPosition = Vector3.LerpUnclamped(_hipsPrevious, _hipsCurrent, t);
            _poseShown = true;
        }

        /// <summary>Puts the Animator's own newest pose back.</summary>
        void RestorePose()
        {
            if (!_poseShown)
                return;

            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] != null)
                    _bones[i].localRotation = _boneCurrent[i];
            }

            if (_bones.Length > 0 && _bones[0] != null)
                _bones[0].localPosition = _hipsCurrent;
            _poseShown = false;
        }

        void Restore()
        {
            if (!_offset)
                return;

            foreach (var shown in _shown)
            {
                if (shown.Transform != null)
                    shown.Transform.SetLocalPositionAndRotation(shown.LocalPosition, shown.LocalRotation);
            }

            _offset = false;
        }
    }
}
