using System.Collections.Generic;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Runtime humanoid ragdoll for the magician.
    ///
    /// The colliders, rigidbodies and joints are built in Awake from the rig's humanoid bones
    /// rather than authored into the prefab - a hand-serialised ragdoll of eleven joints is brittle
    /// and would break the moment the rig changed. They start kinematic and animated; Release()
    /// hands them to physics, so a caster shot off a ledge crumples and falls. Built on every
    /// machine so the corpse falls locally on each screen (the exact tumble differs, which is fine -
    /// it is a dead body, not gameplay state).
    /// </summary>
    public class MagicianRagdoll : MonoBehaviour
    {
        [SerializeField] float totalMass = 20f;

        Animator _animator;
        readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        readonly List<Collider> _ragdollColliders = new List<Collider>();
        bool _built;
        bool _released;

        void Awake()
        {
            Build();
        }

        void Build()
        {
            if (_built)
                return;

            _animator = GetComponentInChildren<Animator>();
            if (_animator == null || !_animator.isHuman)
            {
                Debug.LogWarning("[CollarCali] MagicianRagdoll needs a humanoid Animator; skipping ragdoll.");
                return;
            }
            _built = true;

            Transform B(HumanBodyBones b) => _animator.GetBoneTransform(b);

            var hips = B(HumanBodyBones.Hips);
            var spine = B(HumanBodyBones.Spine);
            var chest = B(HumanBodyBones.Chest) ?? spine;
            var head = B(HumanBodyBones.Head);
            var lUp = B(HumanBodyBones.LeftUpperLeg);
            var lLo = B(HumanBodyBones.LeftLowerLeg);
            var lFo = B(HumanBodyBones.LeftFoot);
            var rUp = B(HumanBodyBones.RightUpperLeg);
            var rLo = B(HumanBodyBones.RightLowerLeg);
            var rFo = B(HumanBodyBones.RightFoot);
            var lArm = B(HumanBodyBones.LeftUpperArm);
            var lFore = B(HumanBodyBones.LeftLowerArm);
            var rArm = B(HumanBodyBones.RightUpperArm);
            var rFore = B(HumanBodyBones.RightLowerArm);

            if (hips == null)
            {
                Debug.LogWarning("[CollarCali] MagicianRagdoll: no hips bone; skipping ragdoll.");
                _built = false;
                return;
            }

            // Pelvis is the joint-less root everything else hangs from.
            var pelvis = MakeBody(hips, 2.5f);
            AddCapsule(hips, lUp != null ? lUp.position : hips.position + Vector3.down * 0.2f, 0.12f);

            var chestBody = MakeLimb(chest, pelvis, 2.4f, 0.14f,
                head != null ? head.position : chest.position + Vector3.up * 0.25f);

            if (head != null)
                MakeLimb(head, chestBody, 1.2f, 0.11f, head.position + Vector3.up * 0.2f, sphere: true);

            // Legs
            var lUpB = MakeLimb(lUp, pelvis, 1.6f, 0.10f, lLo != null ? lLo.position : lUp.position + Vector3.down * 0.4f);
            if (lLo != null) MakeLimb(lLo, lUpB, 1.2f, 0.08f, lFo != null ? lFo.position : lLo.position + Vector3.down * 0.4f);
            var rUpB = MakeLimb(rUp, pelvis, 1.6f, 0.10f, rLo != null ? rLo.position : rUp.position + Vector3.down * 0.4f);
            if (rLo != null) MakeLimb(rLo, rUpB, 1.2f, 0.08f, rFo != null ? rFo.position : rLo.position + Vector3.down * 0.4f);

            // Arms
            var lArmB = MakeLimb(lArm, chestBody, 1.0f, 0.075f, lFore != null ? lFore.position : lArm.position + Vector3.down * 0.25f);
            if (lFore != null) MakeLimb(lFore, lArmB, 0.8f, 0.06f, lFore.position + (lFore.position - lArm.position));
            var rArmB = MakeLimb(rArm, chestBody, 1.0f, 0.075f, rFore != null ? rFore.position : rArm.position + Vector3.down * 0.25f);
            if (rFore != null) MakeLimb(rFore, rArmB, 0.8f, 0.06f, rFore.position + (rFore.position - rArm.position));

            NormaliseMass();
            IgnoreSelfCollisions();
            SetKinematic(true);
        }

        /// <summary>
        /// Enemy-vs-Enemy collision is on in this project, so without this the ragdoll's own bone
        /// capsules shove each other apart the instant physics wakes and the corpse explodes.
        /// </summary>
        void IgnoreSelfCollisions()
        {
            for (int i = 0; i < _ragdollColliders.Count; i++)
                for (int j = i + 1; j < _ragdollColliders.Count; j++)
                    if (_ragdollColliders[i] != null && _ragdollColliders[j] != null)
                        Physics.IgnoreCollision(_ragdollColliders[i], _ragdollColliders[j], true);
        }

        Rigidbody MakeBody(Transform bone, float massShare)
        {
            var rb = bone.gameObject.GetComponent<Rigidbody>();
            if (rb == null)
                rb = bone.gameObject.AddComponent<Rigidbody>();
            rb.mass = massShare;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            // Left Discrete while kinematic - Unity forces-and-warns on continuous modes for
            // kinematic bodies, which would spam ~11 lines per magician at spawn. Release() bumps
            // it to ContinuousDynamic when the body actually starts moving.
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            if (!_bodies.Contains(rb))
                _bodies.Add(rb);
            return rb;
        }

        /// <summary>A limb bone: capsule collider toward its child, rigidbody, and a joint to its parent body.</summary>
        Rigidbody MakeLimb(Transform bone, Rigidbody parent, float massShare, float radius,
            Vector3 childWorld, bool sphere = false)
        {
            if (bone == null)
                return parent;

            var rb = MakeBody(bone, massShare);

            if (sphere)
                AddSphere(bone, radius);
            else
                AddCapsule(bone, childWorld, radius);

            var joint = bone.gameObject.GetComponent<CharacterJoint>();
            if (joint == null)
                joint = bone.gameObject.AddComponent<CharacterJoint>();
            joint.connectedBody = parent;
            joint.enableProjection = true;
            // Gentle cone so joints do not snap into impossible poses when the body lands.
            joint.lowTwistLimit = new SoftJointLimit { limit = -20f };
            joint.highTwistLimit = new SoftJointLimit { limit = 20f };
            joint.swing1Limit = new SoftJointLimit { limit = 40f };
            joint.swing2Limit = new SoftJointLimit { limit = 40f };
            return rb;
        }

        void AddCapsule(Transform bone, Vector3 childWorld, float radius)
        {
            var cap = bone.gameObject.GetComponent<CapsuleCollider>();
            if (cap == null)
                cap = bone.gameObject.AddComponent<CapsuleCollider>();
            if (!_ragdollColliders.Contains(cap))
                _ragdollColliders.Add(cap);

            var localChild = bone.InverseTransformPoint(childWorld);
            float length = localChild.magnitude;
            // Point the capsule down whichever local axis best follows the bone toward its child.
            int axis = 0; float best = Mathf.Abs(localChild.x);
            if (Mathf.Abs(localChild.y) > best) { axis = 1; best = Mathf.Abs(localChild.y); }
            if (Mathf.Abs(localChild.z) > best) { axis = 2; }
            cap.direction = axis;
            cap.radius = radius;
            cap.height = Mathf.Max(length, radius * 2f);
            cap.center = localChild * 0.5f;
        }

        void AddSphere(Transform bone, float radius)
        {
            var sph = bone.gameObject.GetComponent<SphereCollider>();
            if (sph == null)
                sph = bone.gameObject.AddComponent<SphereCollider>();
            if (!_ragdollColliders.Contains(sph))
                _ragdollColliders.Add(sph);
            sph.radius = radius;
            sph.center = Vector3.zero;
        }

        void NormaliseMass()
        {
            float sum = 0f;
            foreach (var rb in _bodies)
                sum += rb.mass;
            if (sum <= 0.001f)
                return;
            float k = totalMass / sum;
            foreach (var rb in _bodies)
                rb.mass *= k;
        }

        void SetKinematic(bool kinematic)
        {
            foreach (var rb in _bodies)
            {
                if (rb == null)
                    continue;
                rb.isKinematic = kinematic;
                // While animated, the ragdoll colliders must not shove the world or each other.
                rb.detectCollisions = !kinematic;
            }
        }

        /// <summary>Hand the corpse to physics. Idempotent; safe to call on every client.</summary>
        public void Release()
        {
            if (_released)
                return;
            if (!_built)
                Build();
            _released = true;

            // The animator must stop or it will keep snapping bones back to the death pose.
            if (_animator != null)
                _animator.enabled = false;

            // Turn off the trigger hitboxes (body + head) so the corpse stops catching bullets.
            // These are the ZombieHitboxRelay colliders; the ragdoll's own bone colliders are left
            // on so the body actually collides with the floor.
            foreach (var relay in GetComponentsInChildren<ZombieHitboxRelay>(true))
            {
                var col = relay.GetComponent<Collider>();
                if (col != null)
                    col.enabled = false;
            }

            SetKinematic(false);

            foreach (var rb in _bodies)
            {
                if (rb == null)
                    continue;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                // Inherit a little downward momentum so it topples rather than dropping straight down.
                rb.linearVelocity = Vector3.down * 0.5f;
            }
        }
    }
}
