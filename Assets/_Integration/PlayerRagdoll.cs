using System.Collections.Generic;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Turns the player's visible body into a ragdoll when they die, and puts it back afterwards.
    ///
    /// Built from the Animator's HUMANOID bones rather than by name. These are Meshy exports whose
    /// skeleton is a trap to read by name - the spine is numbered downwards, so "Spine02" is the
    /// bone on the hips and "Spine" is the chest - but the avatar is humanoid, so
    /// GetBoneTransform gives the right bone every time and works across all four character skins.
    ///
    /// THREE THINGS ABOUT THIS RIG THAT DICTATE THE CODE:
    ///
    /// 1. The Animator is NOT on the body. It sits on the network root, two levels above the FBX,
    ///    so the body is searched from the root rather than from "Player Render".
    ///
    /// 2. Every bone has a lossyScale of 100, because the Armature is scaled up by 100. Collider
    ///    sizes are authored in LOCAL units and multiplied by that scale at simulation time, so a
    ///    hand-written radius of 0.1 would be a ten metre capsule. Every radius here is a world size
    ///    divided by the bone's scale. Lengths taken from localPosition are already local and are
    ///    used as they are.
    ///
    /// 3. "Player Render" is never reparented. FpsNetworkBridge.ConfigureProxy documents that
    ///    reparenting it breaks proxy animation, because the Animator on the root resolves its bones
    ///    by path. Instead the body's world pose is captured and restored around the frame's root
    ///    move (see PinWorldPose/RestoreWorldPose), which cancels out the root dragging its children
    ///    and leaves the physics in world space.
    ///
    /// Physics runs on every machine, the way MagicianRagdoll already does it: the tumble differs
    /// slightly per client, which does not matter for a corpse, and drift is pulled back whenever it
    /// gets far enough to matter. While a body is carried it goes kinematic everywhere and is placed
    /// exactly, because that is the part players interact with.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerRagdoll : MonoBehaviour
    {
        static readonly HumanBodyBones[] Bones =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
        };

        static readonly Dictionary<HumanBodyBones, HumanBodyBones> JointParent = new()
        {
            { HumanBodyBones.Spine, HumanBodyBones.Hips },
            { HumanBodyBones.Chest, HumanBodyBones.Spine },
            { HumanBodyBones.Head, HumanBodyBones.Chest },
            { HumanBodyBones.LeftUpperArm, HumanBodyBones.Chest },
            { HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftUpperArm },
            { HumanBodyBones.RightUpperArm, HumanBodyBones.Chest },
            { HumanBodyBones.RightLowerArm, HumanBodyBones.RightUpperArm },
            { HumanBodyBones.LeftUpperLeg, HumanBodyBones.Hips },
            { HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftUpperLeg },
            { HumanBodyBones.RightUpperLeg, HumanBodyBones.Hips },
            { HumanBodyBones.RightLowerLeg, HumanBodyBones.RightUpperLeg },
        };

        static readonly Dictionary<HumanBodyBones, float> Masses = new()
        {
            { HumanBodyBones.Hips, 12f },
            { HumanBodyBones.Spine, 8f },
            { HumanBodyBones.Chest, 8f },
            { HumanBodyBones.Head, 5f },
            { HumanBodyBones.LeftUpperArm, 3f },
            { HumanBodyBones.LeftLowerArm, 2f },
            { HumanBodyBones.RightUpperArm, 3f },
            { HumanBodyBones.RightLowerArm, 2f },
            { HumanBodyBones.LeftUpperLeg, 7f },
            { HumanBodyBones.LeftLowerLeg, 4f },
            { HumanBodyBones.RightUpperLeg, 7f },
            { HumanBodyBones.RightLowerLeg, 4f },
        };

        Transform _body;
        Animator _animator;
        Transform _hips;

        readonly Dictionary<HumanBodyBones, Rigidbody> _bodies = new();
        readonly List<Collider> _colliders = new();

        bool _built;
        bool _buildFailed;
        bool _active;
        bool _carried;

        Vector3 _pinnedPosition;
        Quaternion _pinnedRotation;

        public bool IsActive => _active;
        public Transform Hips => _hips;

        /// <summary>Where the body actually is. What the owner replicates and what carrying reads.</summary>
        public Vector3 BodyPosition => _hips != null ? _hips.position : transform.position;

        /// <summary>
        /// Points this at the visible character mesh and the Animator that drives it.
        ///
        /// The Animator is taken from this object rather than from the body, because on this prefab
        /// it lives on the network root while the skeleton is two levels below it.
        /// </summary>
        public void Bind(Transform body)
        {
            if (body == null)
                return;

            _body = body;
            _animator = GetComponent<Animator>();
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>(true);
        }

        #region Build

        bool Build()
        {
            if (_built)
                return !_buildFailed;

            _built = true;

            if (_body == null || _animator == null || !_animator.isHuman)
            {
                _buildFailed = true;
                Debug.LogWarning("[CollarCali] " + name + " has no humanoid Animator, so its body " +
                                 "cannot ragdoll. Carrying and reviving still work; the body just " +
                                 "will not go limp.", this);
                return false;
            }

            _hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            if (_hips == null)
            {
                _buildFailed = true;
                return false;
            }

            foreach (var bone in Bones)
            {
                var boneTransform = _animator.GetBoneTransform(bone);
                if (boneTransform == null)
                    continue;

                var rigidbody = boneTransform.GetComponent<Rigidbody>();
                if (rigidbody == null)
                    rigidbody = boneTransform.gameObject.AddComponent<Rigidbody>();

                rigidbody.mass = Masses.TryGetValue(bone, out var mass) ? mass : 3f;
                rigidbody.linearDamping = 0.15f;
                rigidbody.angularDamping = 0.25f;
                rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
                // Speculative rather than ContinuousDynamic: Unity warns about continuous modes on
                // kinematic bodies, and these spend most of their life kinematic.
                rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rigidbody.isKinematic = true;
                rigidbody.detectCollisions = false;
                _bodies[bone] = rigidbody;

                var collider = boneTransform.GetComponent<Collider>();
                if (collider == null)
                    collider = AddCollider(boneTransform, bone);
                if (collider != null)
                {
                    collider.enabled = false;
                    _colliders.Add(collider);
                }
            }

            foreach (var pair in JointParent)
            {
                if (!_bodies.TryGetValue(pair.Key, out var child))
                    continue;

                // Falls back to the hips so a rig without an optional bone - Chest is optional in a
                // humanoid avatar - still produces a connected ragdoll instead of loose limbs.
                if (!_bodies.TryGetValue(pair.Value, out var parent))
                    _bodies.TryGetValue(HumanBodyBones.Hips, out parent);
                if (parent == null || parent == child || child.GetComponent<Joint>() != null)
                    continue;

                var joint = child.gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = parent;
                joint.autoConfigureConnectedAnchor = true;
                joint.enableCollision = false;
                joint.enableProjection = true;
                joint.projectionDistance = 0.1f;
                joint.projectionAngle = 15f;
                joint.axis = Vector3.right;
                joint.swingAxis = Vector3.forward;
                joint.lowTwistLimit = new SoftJointLimit { limit = -25f };
                joint.highTwistLimit = new SoftJointLimit { limit = 25f };
                joint.swing1Limit = new SoftJointLimit { limit = 40f };
                joint.swing2Limit = new SoftJointLimit { limit = 25f };
            }

            // Limbs must not shove each other apart, or the ragdoll detonates on its first frame as
            // overlapping colliders resolve.
            for (int i = 0; i < _colliders.Count; i++)
            {
                for (int j = i + 1; j < _colliders.Count; j++)
                    Physics.IgnoreCollision(_colliders[i], _colliders[j], true);
            }

            return true;
        }

        /// <summary>
        /// Sizes are world measurements converted into the bone's local space. On this rig that
        /// division is the difference between a human-sized ragdoll and one the size of a building.
        /// </summary>
        static Collider AddCollider(Transform bone, HumanBodyBones which)
        {
            float scale = Mathf.Max(0.0001f, Mathf.Abs(bone.lossyScale.x));

            Vector3 toChild = bone.childCount > 0 ? bone.GetChild(0).localPosition : Vector3.zero;
            float localLength = toChild.magnitude;

            if (which == HumanBodyBones.Head)
            {
                var sphere = bone.gameObject.AddComponent<SphereCollider>();
                sphere.radius = 0.12f / scale;
                return sphere;
            }

            if (which == HumanBodyBones.Hips)
            {
                var box = bone.gameObject.AddComponent<BoxCollider>();
                box.size = new Vector3(0.3f, 0.24f, 0.24f) / scale;
                return box;
            }

            var capsule = bone.gameObject.AddComponent<CapsuleCollider>();
            if (localLength <= 0.0001f)
            {
                capsule.radius = 0.07f / scale;
                capsule.height = 0.2f / scale;
                return capsule;
            }

            capsule.direction = LongestAxis(toChild);
            capsule.height = localLength;
            capsule.radius = Mathf.Clamp(localLength * scale * 0.24f, 0.05f, 0.12f) / scale;
            capsule.center = toChild * 0.5f;
            return capsule;
        }

        static int LongestAxis(Vector3 local)
        {
            var a = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
            if (a.x >= a.y && a.x >= a.z) return 0;
            if (a.y >= a.z) return 1;
            return 2;
        }

        #endregion

        #region Activation

        /// <summary>Goes limp, carrying the player's last movement into the fall.</summary>
        public void Activate(Vector3 velocity)
        {
            if (!Build() || _active)
                return;

            _active = true;

            if (_animator != null)
                _animator.enabled = false;

            foreach (var collider in _colliders)
            {
                if (collider != null)
                    collider.enabled = true;
            }

            foreach (var rigidbody in _bodies.Values)
            {
                if (rigidbody == null)
                    continue;
                rigidbody.detectCollisions = true;
                rigidbody.isKinematic = false;
                rigidbody.linearVelocity = velocity;
                rigidbody.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>Stands the body back up and hands the skeleton to the Animator again.</summary>
        public void Deactivate()
        {
            if (!_active)
                return;

            _active = false;
            _carried = false;

            foreach (var rigidbody in _bodies.Values)
            {
                if (rigidbody == null)
                    continue;
                rigidbody.linearVelocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
                rigidbody.isKinematic = true;
                rigidbody.detectCollisions = false;
            }

            foreach (var collider in _colliders)
            {
                if (collider != null)
                    collider.enabled = false;
            }

            // Re-enabled last: its first update overwrites every bone, which is what puts the
            // skeleton back into a standing pose once the joints have let go.
            if (_animator != null)
            {
                _animator.enabled = true;
                _animator.Rebind();
                _animator.Update(0f);
            }

            // The body is a child of the root and was never reparented, so putting it back is just
            // clearing whatever offset the ragdoll left behind.
            if (_body != null)
            {
                _body.localPosition = Vector3.zero;
                _body.localRotation = Quaternion.identity;
            }
        }

        /// <summary>
        /// Remembers the body's world pose before the network root is written this frame.
        ///
        /// The root drags its children with it, which would move the ragdoll a second time and send
        /// the body sliding away from itself. Capturing here and restoring afterwards cancels that
        /// out without reparenting anything.
        /// </summary>
        public void PinWorldPose()
        {
            if (_body == null)
                return;
            _pinnedPosition = _body.position;
            _pinnedRotation = _body.rotation;
        }

        public void RestoreWorldPose()
        {
            if (_body == null)
                return;
            _body.SetPositionAndRotation(_pinnedPosition, _pinnedRotation);
        }

        /// <summary>
        /// Moves the whole body so its hips land on <paramref name="position"/>. Used for carrying,
        /// for revives, for drift correction, and when a team failure drags everyone back.
        /// </summary>
        public void MoveTo(Vector3 position)
        {
            if (_hips == null || _body == null)
                return;

            _body.position += position - _hips.position;

            foreach (var rigidbody in _bodies.Values)
            {
                if (rigidbody == null || rigidbody.isKinematic)
                    continue;
                rigidbody.linearVelocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Freezes the body while somebody is carrying it.
        ///
        /// Kinematic on every machine, not just the owner's: a carried body that still simulated
        /// would flail through walls and shove its carrier around, and its position is the one thing
        /// everybody has to agree on while it is being moved.
        /// </summary>
        public void SetCarried(bool carried)
        {
            if (!_built || _carried == carried)
                return;

            _carried = carried;

            foreach (var rigidbody in _bodies.Values)
            {
                if (rigidbody == null)
                    continue;
                rigidbody.isKinematic = carried || !_active;
                if (!carried)
                    continue;
                rigidbody.linearVelocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Puts the bones on a layer the interact raycast ignores.
        ///
        /// The player's interact mask includes Default, and Cowsins only finds an Interactable on the
        /// exact collider its ray hit - so a stray bone in front of the body would silently eat the
        /// prompt. Player is outside that mask, and is where a body belongs anyway.
        /// </summary>
        public void ApplyBoneLayer(int layer)
        {
            if (layer < 0)
                return;

            foreach (var collider in _colliders)
            {
                if (collider != null && collider.transform != _hips)
                    collider.gameObject.layer = layer;
            }
        }

        #endregion
    }
}
