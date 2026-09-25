using System.Collections.Generic;
using EmeraldAI;
using UnityEngine;
using UnityEngine.AI;

namespace CollarCali
{
    /// <summary>
    /// Scene 2 RPG enemies have no ragdoll components. On death, build one on
    /// the existing bones so the skinned mesh goes limp.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public class Scene2EnemyDeathFall : MonoBehaviour
    {
        static readonly string[] BoneOrder =
        {
            "B_Pelvis",
            "B_Spine",
            "B_Spine2",
            "B_Head",
            "B_L_Thigh",
            "B_L_Calf",
            "B_L_Foot",
            "B_R_Thigh",
            "B_R_Calf",
            "B_R_Foot",
            "B_L_UpperArm",
            "B_L_Forearm",
            "B_L_Hand",
            "B_R_UpperArm",
            "B_R_Forearm",
            "B_R_Hand"
        };

        static readonly Dictionary<string, string> JointParent = new()
        {
            { "B_Spine", "B_Pelvis" },
            { "B_Spine2", "B_Spine" },
            { "B_Head", "B_Spine2" },
            { "B_L_Thigh", "B_Pelvis" },
            { "B_L_Calf", "B_L_Thigh" },
            { "B_L_Foot", "B_L_Calf" },
            { "B_R_Thigh", "B_Pelvis" },
            { "B_R_Calf", "B_R_Thigh" },
            { "B_R_Foot", "B_R_Calf" },
            { "B_L_UpperArm", "B_Spine2" },
            { "B_L_Forearm", "B_L_UpperArm" },
            { "B_L_Hand", "B_L_Forearm" },
            { "B_R_UpperArm", "B_Spine2" },
            { "B_R_Forearm", "B_R_UpperArm" },
            { "B_R_Hand", "B_R_Forearm" }
        };

        static readonly Dictionary<string, float> Masses = new()
        {
            { "B_Pelvis", 12.7f },
            { "B_Spine", 8.5f },
            { "B_Spine2", 7.5f },
            { "B_Head", 5.5f },
            { "B_L_Thigh", 7.4f },
            { "B_L_Calf", 3.5f },
            { "B_R_Thigh", 7.4f },
            { "B_R_Calf", 3.5f },
            { "B_L_UpperArm", 3.5f },
            { "B_L_Forearm", 2.5f },
            { "B_L_Hand", 1.5f },
            { "B_R_UpperArm", 3.5f },
            { "B_R_Forearm", 2.5f },
            { "B_R_Hand", 1.5f },
            { "B_L_Foot", 2f },
            { "B_R_Foot", 2f }
        };

        bool _ragdolled;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void WatchDead()
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "2")
                return;
            if (Object.FindFirstObjectByType<Scene2DeathWatcher>() != null)
                return;
            var go = new GameObject("Scene2DeathWatcher");
            go.AddComponent<Scene2DeathWatcher>();
        }

        public static void Force(GameObject enemy)
        {
            if (enemy == null)
                return;
            var fall = enemy.GetComponent<Scene2EnemyDeathFall>();
            if (fall == null)
                fall = enemy.AddComponent<Scene2EnemyDeathFall>();
            fall.ActivateRagdoll();
        }

        public void Drop()
        {
            ActivateRagdoll();
        }

        void ActivateRagdoll()
        {
            if (_ragdolled)
                return;
            _ragdolled = true;

            StopControl();

            foreach (var skin in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                skin.enabled = true;
                skin.updateWhenOffscreen = true;
                skin.forceMatrixRecalculationPerRender = true;
            }

            var bodies = new Dictionary<string, Rigidbody>();
            var colliders = new List<Collider>();

            foreach (var boneName in BoneOrder)
            {
                var bone = FindBone(transform, boneName);
                if (bone == null)
                    continue;

                var rb = bone.GetComponent<Rigidbody>();
                if (rb == null)
                    rb = bone.gameObject.AddComponent<Rigidbody>();
                rb.mass = Masses.TryGetValue(boneName, out var mass) ? mass : 3f;
                rb.linearDamping = 0.1f;
                rb.angularDamping = 0.15f;
                rb.useGravity = true;
                rb.isKinematic = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                bodies[boneName] = rb;

                var col = bone.GetComponent<Collider>();
                if (col == null)
                    col = AddBoneCollider(bone, boneName);
                if (col != null)
                {
                    col.enabled = true;
                    col.isTrigger = false;
                    colliders.Add(col);
                }
            }

            foreach (var pair in JointParent)
            {
                if (!bodies.TryGetValue(pair.Key, out var rb))
                    continue;
                if (!bodies.TryGetValue(pair.Value, out var parent))
                    continue;
                if (rb.GetComponent<Joint>() != null)
                    continue;

                var joint = rb.gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = parent;
                joint.autoConfigureConnectedAnchor = true;
                joint.enableCollision = false;
                joint.enableProjection = true;
                joint.projectionDistance = 0.1f;
                joint.projectionAngle = 10f;
                joint.axis = Vector3.right;
                joint.swingAxis = Vector3.forward;
                joint.lowTwistLimit = new SoftJointLimit { limit = -45f };
                joint.highTwistLimit = new SoftJointLimit { limit = 45f };
                joint.swing1Limit = new SoftJointLimit { limit = 45f };
                joint.swing2Limit = new SoftJointLimit { limit = 25f };
            }

            for (int i = 0; i < colliders.Count; i++)
            {
                for (int j = i + 1; j < colliders.Count; j++)
                    Physics.IgnoreCollision(colliders[i], colliders[j], true);
            }

            if (bodies.TryGetValue("B_Pelvis", out var hips))
            {
                hips.AddForce(Vector3.down * 6f + transform.forward * -2f, ForceMode.VelocityChange);
                hips.AddTorque(transform.right * 4f, ForceMode.VelocityChange);
            }
        }

        void StopControl()
        {
            var hitbox = transform.Find("DamageHitbox");
            if (hitbox != null)
                hitbox.gameObject.SetActive(false);

            var box = GetComponent<BoxCollider>();
            if (box != null)
                box.enabled = false;

            foreach (var animator in GetComponentsInChildren<Animator>(true))
            {
                if (animator == null)
                    continue;
                animator.applyRootMotion = false;
                animator.enabled = false;
            }

            var ik = GetComponent<EmeraldInverseKinematics>();
            if (ik != null)
                ik.DisableInverseKinematics();

            var agent = GetComponent<NavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.isStopped = true;
                agent.updatePosition = false;
                agent.updateRotation = false;
                agent.enabled = false;
            }

            var movement = GetComponent<EmeraldMovement>();
            if (movement != null)
                movement.enabled = false;
            var behaviours = GetComponent<EmeraldBehaviors>();
            if (behaviours != null)
                behaviours.enabled = false;
            var combat = GetComponent<EmeraldCombat>();
            if (combat != null)
                combat.enabled = false;
        }

        static Collider AddBoneCollider(Transform bone, string boneName)
        {
            Vector3 offset = Vector3.zero;
            float length = 0.28f;
            if (bone.childCount > 0)
            {
                offset = bone.GetChild(0).localPosition;
                length = offset.magnitude;
            }

            if (boneName.Contains("Foot") || boneName.Contains("Hand") || boneName == "B_Head")
            {
                var sphere = bone.gameObject.AddComponent<SphereCollider>();
                sphere.radius = boneName == "B_Head" ? 0.14f : 0.08f;
                sphere.center = offset.sqrMagnitude > 0.0001f ? offset * 0.35f : Vector3.zero;
                return sphere;
            }

            if (boneName == "B_Pelvis")
            {
                var box = bone.gameObject.AddComponent<BoxCollider>();
                box.size = new Vector3(0.2f, 0.28f, 0.22f);
                box.center = Vector3.zero;
                return box;
            }

            var capsule = bone.gameObject.AddComponent<CapsuleCollider>();
            capsule.direction = LongestAxis(offset);
            capsule.height = Mathf.Max(0.18f, length * 0.95f);
            capsule.radius = Mathf.Clamp(length * 0.18f, 0.05f, 0.14f);
            capsule.center = offset * 0.5f;
            return capsule;
        }

        static int LongestAxis(Vector3 local)
        {
            var a = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
            if (a.x >= a.y && a.x >= a.z) return 0;
            if (a.y >= a.z) return 1;
            return 2;
        }

        static Transform FindBone(Transform root, string name)
        {
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindBone(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }

    public class Scene2DeathWatcher : MonoBehaviour
    {
        void LateUpdate()
        {
            var healths = FindObjectsByType<EmeraldHealth>(FindObjectsSortMode.None);
            for (int i = 0; i < healths.Length; i++)
            {
                var health = healths[i];
                if (health == null)
                    continue;
                var system = health.GetComponent<EmeraldSystem>();
                if (system == null || system.AnimationComponent == null || !system.AnimationComponent.IsDead)
                    continue;
                Scene2EnemyDeathFall.Force(health.gameObject);
            }
        }
    }
}
