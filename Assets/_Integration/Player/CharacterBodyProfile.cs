using System;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Every number that fits the player rig to the player's body, in one asset
    /// (Assets/_Integration/Characters/Player/CharacterBodyProfile.asset):
    ///   - PlayerCharacterBuilder builds PlayerCharacter.prefab from it: Malbers' pivots, capsules,
    ///     Rotator and ground layers, the sockets and the hitboxes;
    ///   - PlayerCameraRigBuilder builds the camera rig from its Camera section;
    ///   - CharacterStateCalibration reads the climbing section at runtime.
    /// Change a number, run Tools > CollarCali > Character > Build Player Character, and the prefabs
    /// follow. The defaults fit the omerfeozs model at import scale 0.75 (1.84 m, hips 0.69 m, a big
    /// head from 1.15 m up), measured with the Character Fit Check.
    /// </summary>
    [CreateAssetMenu(menuName = "CollarCali/Character Body Profile", fileName = "CharacterBodyProfile")]
    public class CharacterBodyProfile : ScriptableObject
    {
        /// <summary>An upright capsule, measured from the feet.</summary>
        [Serializable]
        public struct Capsule
        {
            [Min(0.01f)] public float radius;
            [Tooltip("Height of the centre above the feet (m).")]
            public float centre;
            [Tooltip("Full height, ends included (m).")]
            [Min(0.02f)] public float height;

            public Capsule(float radius, float centre, float height)
            {
                this.radius = radius;
                this.centre = centre;
                this.height = height;
            }

            public float Bottom => centre - height * 0.5f;
            public float Top => centre + height * 0.5f;
        }

        /// <summary>
        /// A named point on a bone: weapon grips, strike points, effect roots. Placed on the bone,
        /// or between the bone and Toward, then moved by Offset in the character's own axes (x right,
        /// y up, z forward) as it stands in its model's pose, and turned to face the character's
        /// forward there - so the numbers mean the same whatever way the model's bones point.
        /// </summary>
        [Serializable]
        public class Socket
        {
            public string name;
            public HumanBodyBones bone;
            [Tooltip("Another bone to move toward (Last Bone = none).")]
            public HumanBodyBones toward = HumanBodyBones.LastBone;
            [Tooltip("How far toward it: 0 = on the bone, 1 = on the other bone.")]
            [Range(0f, 1f)] public float along;
            [Tooltip("Then moved by this, in the character's axes (m).")]
            public Vector3 offset;

            public Socket() { }

            public Socket(string name, HumanBodyBones bone, HumanBodyBones toward = HumanBodyBones.LastBone,
                float along = 0f, Vector3 offset = default)
            {
                this.name = name;
                this.bone = bone;
                this.toward = toward;
                this.along = along;
                this.offset = offset;
            }
        }

        /// <summary>
        /// A hitbox the enemies and the melee hit: a capsule from one bone to another (a sphere when
        /// both are the same), carried by the first, on the Animal layer like Malbers' own.
        /// </summary>
        [Serializable]
        public class Hitbox
        {
            public string name;
            public HumanBodyBones from;
            public HumanBodyBones to;
            [Min(0.01f)] public float radius;
            [Tooltip("How far the capsule reaches past both bones (m).")]
            public float extend;
            [Tooltip("Moves it, in the character's axes (m).")]
            public Vector3 offset;

            public Hitbox() { }

            public Hitbox(string name, HumanBodyBones from, HumanBodyBones to, float radius, float extend = 0f,
                Vector3 offset = default)
            {
                this.name = name;
                this.from = from;
                this.to = to;
                this.radius = radius;
                this.extend = extend;
                this.offset = offset;
            }
        }

        [Serializable]
        public class CameraNumbers
        {
            [Tooltip("Height of the camera target above the feet (m).")]
            public float targetHeight = 1.30f;
            [Tooltip("Distance of the third-person camera behind the target (m).")]
            public float distance = 4.5f;
            [Tooltip("Sideways shoulder offset (m).")]
            public float shoulder = 0.45f;
            [Tooltip("How softly the camera follows on its x, y and z axes (seconds-ish).")]
            public Vector3 damping = new Vector3(0.1f, 0.25f, 0.2f);
            [Tooltip("Camera shake from Cinemachine noise. Off by default: the follow is smooth on its own.")]
            public bool noise;
            [Tooltip("How close the camera may get to a wall before it is pushed in front of it (m).")]
            [Range(0.01f, 1f)] public float collisionRadius = 0.15f;
        }

        /// <summary>
        /// How the player's body compares with Malbers' Steve, whose climbing, ledge and wall-run
        /// numbers CharacterStateCalibration scales by these. The model is as tall as Steve, but its
        /// hips are 0.69 m up against his 0.92 m and its head is a third of its height, so its chest,
        /// hips and reach all sit lower. 0 = measure: PlayerCharacterBuilder fills in each one from the
        /// two skeletons, and keeps any value set by hand.
        /// </summary>
        [Serializable]
        public class ClimbNumbers
        {
            [Tooltip("Chest height against Steve's: where the climb casts from its chest (0 = measure).")]
            [Min(0f)] public float chestScale;
            [Tooltip("Hip height against Steve's: the climb's hip cast and the wall-run checks (0 = measure).")]
            [Min(0f)] public float hipScale;
            [Tooltip("Reach of the raised arms against Steve's: how far below a ledge the body hangs, so " +
                     "the hands land on it (0 = measure). Check on Celil's levels.")]
            [Min(0f)] public float reachScale;
        }

        [Header("Model")]
        [Tooltip("The model nested into the prefab (omerfeozs - 3D Low Poly).")]
        public GameObject model;

        [Header("Controller (Malbers)")]
        [Tooltip("MAnimal Height: how high the hip pivot casts down to find the ground (m).")]
        public float height = 1.05f;
        [Tooltip("Height of the chest pivot (m).")]
        public float chestPivot = 1.05f;
        [Tooltip("Height of the Rotator, the point the body turns about on slopes and in the air (m).")]
        public float rotatorHeight = 0.80f;
        [Tooltip("The main capsule. It starts above the feet, as Malbers' do: the ground is found by rays.")]
        public Capsule capsule = new Capsule(0.27f, 1.125f, 1.35f);
        [Tooltip("The capsule while crouching.")]
        public Capsule crouchCapsule = new Capsule(0.27f, 0.80f, 0.70f);
        [Tooltip("The capsule while standing with the back against a wall.")]
        public Capsule againstWallCapsule = new Capsule(0.20f, 1.125f, 1.35f);
        [Tooltip("What counts as ground, wall and ledge: Default, Ground, Grass, Metal, Mud and Wood.")]
        public LayerMask surfaces = (1 << 0) | (1 << 3) | (1 << 10) | (1 << 11) | (1 << 12) | (1 << 13);

        [Header("Sockets")]
        public Socket[] sockets =
        {
            new Socket("Socket Hand R", HumanBodyBones.RightHand),
            new Socket("Socket Hand L", HumanBodyBones.LeftHand),
            new Socket("Socket Knuckles R", HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, 1f),
            new Socket("Socket Knuckles L", HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, 1f),
            new Socket("Socket Foot R", HumanBodyBones.RightFoot, HumanBodyBones.RightToes, 1f),
            new Socket("Socket Foot L", HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, 1f),
            new Socket("Socket Chest", HumanBodyBones.UpperChest),
            new Socket("Socket Hips", HumanBodyBones.Hips),
            new Socket("Socket Head", HumanBodyBones.Head, offset: new Vector3(0f, 0.30f, 0f)),
            new Socket("Socket Eyes", HumanBodyBones.LeftEye, HumanBodyBones.RightEye, 0.5f),
        };

        [Header("Hitboxes")]
        public Hitbox[] hitboxes =
        {
            new Hitbox("HB_Pelvis", HumanBodyBones.Hips, HumanBodyBones.Spine, 0.20f, 0.10f),
            new Hitbox("HB_Torso", HumanBodyBones.Spine, HumanBodyBones.Neck, 0.21f, 0.02f),
            new Hitbox("HB_Head", HumanBodyBones.Head, HumanBodyBones.Head, 0.37f, 0f, new Vector3(0f, 0.31f, 0.02f)),
        };

        [Header("Climbing")]
        public ClimbNumbers climbing = new ClimbNumbers();

        [Header("Camera")]
        public CameraNumbers cameraRig = new CameraNumbers();
    }
}
