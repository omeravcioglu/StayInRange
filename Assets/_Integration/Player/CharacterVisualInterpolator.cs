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

        /// <summary>Set by code that drives the body itself (a ragdoll, a cutscene) to show it unsmoothed.</summary>
        public bool Suspended { get; set; }

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

        bool _hasStep;
        Vector3 _previousPosition, _currentPosition;
        Quaternion _previousRotation, _currentRotation;

        void OnEnable()
        {
            _animator = GetComponent<Animator>();
            _hasStep = false;
            _afterPhysics = StartCoroutine(AfterPhysics());
        }

        void OnDisable()
        {
            if (_afterPhysics != null)
                StopCoroutine(_afterPhysics);
            _afterPhysics = null;
            Restore();
        }

        // The physics step sees the exact pose.
        void FixedUpdate() => Restore();

        void Update()
        {
            Restore();
            Apply();
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
        }

        /// <summary>Shows the visuals where the root was a fraction of a physics step ago.</summary>
        void Apply()
        {
            if (!_hasStep || Suspended || (_animator != null && !_animator.enabled) || Time.fixedDeltaTime <= 0f)
                return;

            var root = transform;
            root.GetPositionAndRotation(out var position, out var rotation);

            // Moved since the physics step by something else: show that as it is, from here on.
            if ((position - _currentPosition).sqrMagnitude > 1e-8f)
                _previousPosition = _currentPosition = position;
            if (Quaternion.Angle(rotation, _currentRotation) > 0.01f)
                _previousRotation = _currentRotation = rotation;

            float t = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
            var shownPosition = Vector3.LerpUnclamped(_previousPosition, _currentPosition, t);
            var shownRotation = Quaternion.SlerpUnclamped(_previousRotation, _currentRotation, t);
            var turn = shownRotation * Quaternion.Inverse(rotation);
            if ((shownPosition - position).sqrMagnitude < 1e-10f && Quaternion.Angle(turn, Quaternion.identity) < 0.001f)
                return;

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
