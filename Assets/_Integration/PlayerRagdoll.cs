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
    /// GetBoneTransform gives the right bone every time and works across every character skin.
    ///
    /// THREE THINGS ABOUT THIS RIG THAT DICTATE THE CODE:
    ///
    /// 1. The Animator is NOT on the body. It sits on the network root, two levels above the FBX,
    ///    so the body is searched from the root rather than from "Player Render".
    ///
    /// 2. Every bone has a lossyScale of 100, because the Armature is scaled up by 100. Collider
    ///    sizes are authored in LOCAL units and multiplied by that scale at simulation time, so a
    ///    hand-written radius of 0.1 would be a ten metre capsule. Every radius here is a world size
    ///    divided by the bone's scale.
    ///
    /// 3. While dead, "Player Render" is DETACHED from the network root and put back on revive. The
    ///    root is moved every frame - by the network on other machines, and by the owner to follow
    ///    the hips - and a body parented under it was dragged along a second time on top of its own
    ///    physics. Reparenting is only unsafe while the root's Animator is running (it resolves bones
    ///    by path), and it is switched off for the whole time the body is detached; it is re-enabled
    ///    and rebound only after the body is back in place.
    ///
    /// ONE SIMULATION, MANY COPIES. Exactly one machine simulates a body at any moment (see
    /// PlayerDownState): that copy is fully dynamic. Every other machine holds the hips and chest on
    /// the pose that machine replicates and lets only the limbs swing, so all players see the body
    /// in the same place while the arms and legs still move naturally.
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

        const float BaseLinearDamping = 0.15f;
        const float BaseAngularDamping = 0.25f;

        Transform _body;
        Transform _bodyParent;
        Animator _animator;
        Transform _hips;

        readonly Dictionary<HumanBodyBones, Rigidbody> _bodies = new();
        readonly List<Rigidbody> _bodyList = new();
        readonly List<Collider> _colliders = new();

        Rigidbody _hipsBody;
        Rigidbody _chestBody;
        float _totalMass;

        bool _built;
        bool _buildFailed;
        bool _active;
        bool _detached;
        bool _following;

        public bool IsActive => _active;
        public bool IsFollowing => _following;
        public Transform Hips => _hips;
        public Rigidbody HipsBody => _hipsBody;

        /// <summary>The upper-body bone the telekinetic grip holds, so the body hangs from its collar.</summary>
        public Rigidbody ChestBody => _chestBody != null ? _chestBody : _hipsBody;

        public IReadOnlyList<Rigidbody> Bodies => _bodyList;
        public IReadOnlyList<Collider> Colliders => _colliders;
        public float TotalMass => _totalMass;

        /// <summary>Where the body actually is. What gets replicated and what everything aims at.</summary>
        public Vector3 BodyPosition => _hips != null ? _hips.position : transform.position;

        /// <summary>Velocity of the body as a whole, taken from the hips.</summary>
        public Vector3 BodyVelocity =>
            _hipsBody != null && !_hipsBody.isKinematic ? _hipsBody.linearVelocity : Vector3.zero;

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
            _bodyParent = body.parent;
            _animator = GetComponent<Animator>();
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>(true);
        }

        void OnDestroy()
        {
            // The network root is going away - the player left, or the object despawned. A body
            // parked at the scene root would otherwise outlive its owner forever.
            if (_detached && _body != null)
                Destroy(_body.gameObject);
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
                                 "cannot ragdoll. It will stay where it fell and cannot be carried.", this);
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
                rigidbody.linearDamping = BaseLinearDamping;
                rigidbody.angularDamping = BaseAngularDamping;
                rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
                // Speculative rather than ContinuousDynamic: it is still continuous collision, so a
                // hard throw cannot tunnel through a wall, but Unity does not complain about it on
                // the bodies that spend their life kinematic.
                rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                // Extra solver passes keep a chain of joints from stretching and jittering when the
                // grip pulls on one end of it.
                rigidbody.solverIterations = 12;
                rigidbody.solverVelocityIterations = 4;
                // Overlapping colliders at the moment of death resolve gently instead of launching
                // the body across the room.
                rigidbody.maxDepenetrationVelocity = 3f;
                rigidbody.isKinematic = true;
                rigidbody.detectCollisions = false;
                _bodies[bone] = rigidbody;
                _bodyList.Add(rigidbody);
                _totalMass += rigidbody.mass;

                var collider = boneTransform.GetComponent<Collider>();
                if (collider == null)
                    collider = AddCollider(boneTransform, bone);
                if (collider != null)
                {
                    collider.enabled = false;
                    _colliders.Add(collider);
                }
            }

            _bodies.TryGetValue(HumanBodyBones.Hips, out _hipsBody);
            if (!_bodies.TryGetValue(HumanBodyBones.Chest, out _chestBody))
                _bodies.TryGetValue(HumanBodyBones.Spine, out _chestBody);

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

        /// <summary>
        /// Goes limp, carrying the player's last movement into the fall. Returns false when this rig
        /// cannot ragdoll at all.
        /// </summary>
        public bool Activate(Vector3 velocity)
        {
            if (!Build())
                return false;
            if (_active)
                return true;

            _active = true;
            _following = false;

            if (_animator != null)
                _animator.enabled = false;

            Detach();

            foreach (var collider in _colliders)
            {
                if (collider != null)
                    collider.enabled = true;
            }

            foreach (var rigidbody in _bodyList)
            {
                if (rigidbody == null)
                    continue;
                rigidbody.detectCollisions = true;
                rigidbody.isKinematic = false;
                rigidbody.linearVelocity = velocity;
                rigidbody.angularVelocity = Vector3.zero;
            }

            return true;
        }

        /// <summary>Stands the body back up and hands the skeleton to the Animator again.</summary>
        public void Deactivate()
        {
            if (!_active)
                return;

            _active = false;
            _following = false;

            foreach (var rigidbody in _bodyList)
            {
                if (rigidbody == null)
                    continue;
                if (!rigidbody.isKinematic)
                {
                    rigidbody.linearVelocity = Vector3.zero;
                    rigidbody.angularVelocity = Vector3.zero;
                }
                rigidbody.isKinematic = true;
                rigidbody.detectCollisions = false;
                rigidbody.linearDamping = BaseLinearDamping;
                rigidbody.angularDamping = BaseAngularDamping;
            }

            foreach (var collider in _colliders)
            {
                if (collider != null)
                    collider.enabled = false;
            }

            // Back under the root BEFORE the Animator wakes up: it binds bones by path from the root,
            // and Rebind below is what makes it pick the skeleton up again.
            Reattach();

            if (_animator != null)
            {
                _animator.enabled = true;
                _animator.Rebind();
                _animator.Update(0f);
            }
        }

        void Detach()
        {
            if (_detached || _body == null)
                return;

            _bodyParent = _body.parent;
            _body.SetParent(null, true);
            _detached = true;
        }

        void Reattach()
        {
            if (!_detached || _body == null)
                return;

            _body.SetParent(_bodyParent != null ? _bodyParent : transform, false);
            _body.localPosition = Vector3.zero;
            _body.localRotation = Quaternion.identity;
            _detached = false;
        }

        #endregion

        #region Simulating and following

        /// <summary>
        /// Switches between being THE simulation of this body (every bone dynamic) and following the
        /// machine that is (hips and chest pinned to its replicated pose, limbs left to swing).
        ///
        /// <paramref name="inheritedVelocity"/> is applied when taking over the simulation, so a body
        /// caught mid-flight keeps flying instead of stopping dead on the new machine.
        /// </summary>
        public void SetFollowing(bool following, Vector3 inheritedVelocity)
        {
            if (!_active || _following == following)
                return;

            _following = following;

            if (following)
            {
                SetPinned(_hipsBody, true);
                SetPinned(_chestBody, true);
                return;
            }

            SetPinned(_hipsBody, false);
            SetPinned(_chestBody, false);

            foreach (var rigidbody in _bodyList)
            {
                if (rigidbody == null || rigidbody.isKinematic)
                    continue;
                rigidbody.linearVelocity = inheritedVelocity;
            }
        }

        static void SetPinned(Rigidbody body, bool pinned)
        {
            if (body == null)
                return;

            if (pinned && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            body.isKinematic = pinned;
        }

        /// <summary>
        /// Moves the pinned hips and chest to a replicated pose. Called from FixedUpdate so the
        /// kinematic bodies sweep there and push the swinging limbs along with them.
        /// </summary>
        public void DriveFollower(Vector3 hipsPosition, Quaternion hipsRotation,
            Vector3 chestPosition, Quaternion chestRotation)
        {
            if (!_following || _hipsBody == null)
                return;

            _hipsBody.MovePosition(hipsPosition);
            _hipsBody.MoveRotation(hipsRotation);

            if (_chestBody != null && _chestBody != _hipsBody)
            {
                _chestBody.MovePosition(chestPosition);
                _chestBody.MoveRotation(chestRotation);
            }
        }

        /// <summary>Loosens or tightens the limbs. Held bodies swing; loose bodies tumble freely.</summary>
        public void SetLimbDamping(float angularDamping)
        {
            foreach (var rigidbody in _bodyList)
            {
                if (rigidbody == null || rigidbody == _hipsBody || rigidbody == _chestBody)
                    continue;
                rigidbody.angularDamping = angularDamping;
            }
        }

        public void ResetLimbDamping() => SetLimbDamping(BaseAngularDamping);

        /// <summary>Launches every bone together, so the body flies as one piece rather than stretching.</summary>
        public void Launch(Vector3 velocity, Vector3 spin)
        {
            foreach (var rigidbody in _bodyList)
            {
                if (rigidbody == null || rigidbody.isKinematic)
                    continue;
                rigidbody.linearVelocity = velocity;
                rigidbody.angularVelocity = spin;
            }
        }

        /// <summary>
        /// Moves the whole body so its hips land on <paramref name="position"/>. Used for revives,
        /// team-failure teleports and large network corrections.
        /// </summary>
        public void MoveTo(Vector3 position)
        {
            if (_hips == null || _body == null)
                return;

            // The transform only: the physics bodies pick the new pose up when transforms sync before
            // the next step. Writing Rigidbody.position as well would move them twice whenever
            // auto-sync is on.
            _body.position += position - _hips.position;

            foreach (var rigidbody in _bodyList)
            {
                if (rigidbody == null || rigidbody.isKinematic)
                    continue;
                rigidbody.linearVelocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>Makes a set of colliders - a carrier's own body, say - pass through this one, or stop doing so.</summary>
        public void IgnoreCollisionsWith(IReadOnlyList<Collider> others, bool ignore)
        {
            if (others == null)
                return;

            foreach (var mine in _colliders)
            {
                if (mine == null)
                    continue;
                foreach (var other in others)
                {
                    if (other != null && other != mine)
                        Physics.IgnoreCollision(mine, other, ignore);
                }
            }
        }

        /// <summary>
        /// Puts the bones on a layer the interact raycast ignores. Player is outside the Cowsins
        /// interact mask - a bone in front of a door would otherwise eat its prompt - and is where a
        /// body belongs anyway.
        /// </summary>
        public void ApplyBoneLayer(int layer)
        {
            if (layer < 0)
                return;

            foreach (var collider in _colliders)
            {
                if (collider != null)
                    collider.gameObject.layer = layer;
            }
        }

        #endregion
    }
}
