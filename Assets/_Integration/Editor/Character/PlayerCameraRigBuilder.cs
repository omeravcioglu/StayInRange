#if UNITY_EDITOR
using System.IO;
using System.Text;
using MalbersAnimations;
using Unity.Cinemachine;
using UnityEditor;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds the team's third-person camera rig, PlayerCameraRig (CM3).prefab, as a variant of
    /// Malbers' Cameras CM3 (so Malbers' updates still reach it), with the body profile's Camera
    /// numbers:
    ///   - the brain and its blends update in LateUpdate. CM3 ships both in FixedUpdate, so the
    ///     camera moved once per physics step and juddered on fast screens;
    ///   - the main camera's distance, shoulder offset and damping. The distance is written on Malbers'
    ///     ThirdPersonFollowTarget too, which sets Cinemachine's from its own value on Awake;
    ///   - no noise: Malbers ships a handheld shake on every camera;
    ///   - every camera stays in front of walls on the profile's surface layers (CM3 only sees
    ///     Default, with a 1 mm radius on the main camera).
    /// The camera target's height belongs to the character (Malbers' CM Main Target), so
    /// PlayerCharacterBuilder sets it. Running this again rewrites the same overrides.
    /// </summary>
    public static class PlayerCameraRigBuilder
    {
        const string BaseGuid = "97ae43971f3549145bbc254732633bed";   // Malbers Cameras CM3
        const string MainCameraName = "CM Third Person Main (New Input)";

        [MenuItem(CharacterFitCheck.MenuRoot + "Build Player Camera Rig")]
        static void BuildFromMenu()
        {
            string log = Build(out bool ok);
            Debug.Log(log);
            EditorUtility.DisplayDialog("Build Player Camera Rig", (ok ? "Done.\n\n" : "Did not finish.\n\n") + log, "OK");
        }

        /// <summary>Batch entry point: -executeMethod CollarCali.EditorTools.PlayerCameraRigBuilder.BuildBatch</summary>
        public static void BuildBatch()
        {
            string log = Build(out bool ok);
            Debug.Log("[CameraRig] " + (ok ? "OK" : "FAILED") + "\n" + log);
        }

        public static string Build(out bool ok)
        {
            ok = false;
            var sb = new StringBuilder();
            var profile = PlayerRigAssets.LoadOrCreateProfile(sb);
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(BaseGuid));
            if (basePrefab == null)
            {
                sb.AppendLine("Malbers' Cameras CM3 was not found (guid " + BaseGuid + ").");
                return sb.ToString();
            }

            string path = PlayerRigAssets.CameraRigPath;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                PlayerRigAssets.EnsureFolder(PlayerRigAssets.Folder);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                try
                {
                    instance.name = Path.GetFileNameWithoutExtension(path);
                    PrefabUtility.SaveAsPrefabAsset(instance, path);
                    sb.AppendLine("Created " + path + " as a variant of Malbers' Cameras CM3.");
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Configure(root, profile, sb);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                ok = true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return sb.ToString();
        }

        static void Configure(GameObject root, CharacterBodyProfile profile, StringBuilder sb)
        {
            var numbers = profile.cameraRig;

            foreach (var brain in root.GetComponentsInChildren<CinemachineBrain>(true))
            {
                brain.UpdateMethod = CinemachineBrain.UpdateMethods.LateUpdate;
                brain.BlendUpdateMethod = CinemachineBrain.BrainUpdateMethods.LateUpdate;
                PlayerRigAssets.Record(brain);
                sb.AppendLine($"  {brain.name}: brain and blends update in LateUpdate.");
            }

            bool foundMain = false;
            foreach (var follow in root.GetComponentsInChildren<CinemachineThirdPersonFollow>(true))
            {
                if (follow.name == MainCameraName)
                {
                    foundMain = true;
                    follow.CameraDistance = numbers.distance;
                    follow.ShoulderOffset = new Vector3(numbers.shoulder, follow.ShoulderOffset.y, follow.ShoulderOffset.z);
                    follow.Damping = numbers.damping;

                    // Malbers sets Cinemachine's distance from its own on Awake; the mouse wheel zooms from there.
                    var malbers = follow.GetComponent<ThirdPersonFollowTarget>();
                    if (malbers != null)
                    {
                        malbers.CameraDistance.UseConstant = true;
                        malbers.CameraDistance.ConstantValue = numbers.distance;
                        PlayerRigAssets.Record(malbers);
                    }

                    sb.AppendLine($"  {follow.name}: distance {numbers.distance}, shoulder {numbers.shoulder}, damping {numbers.damping}.");
                }

                var avoid = follow.AvoidObstacles;
                avoid.Enabled = true;
                avoid.CollisionFilter = profile.surfaces;
                avoid.CameraRadius = numbers.collisionRadius;
                follow.AvoidObstacles = avoid;
                PlayerRigAssets.Record(follow);
            }

            if (!foundMain)
                sb.AppendLine("  ! No camera named '" + MainCameraName + "': distance, shoulder and damping were not set.");

            foreach (var deoccluder in root.GetComponentsInChildren<CinemachineDeoccluder>(true))
            {
                deoccluder.CollideAgainst = profile.surfaces;
                var avoid = deoccluder.AvoidObstacles;
                avoid.CameraRadius = numbers.collisionRadius;
                deoccluder.AvoidObstacles = avoid;
                PlayerRigAssets.Record(deoccluder);
            }

            int noises = 0;
            foreach (var noise in root.GetComponentsInChildren<CinemachineBasicMultiChannelPerlin>(true))
            {
                noise.enabled = numbers.noise;
                PlayerRigAssets.Record(noise);
                noises++;
            }

            sb.AppendLine($"  Walls on layers {LayerNames(profile.surfaces)} at radius {numbers.collisionRadius} m; " +
                          $"noise {(numbers.noise ? "on" : "off")} on {noises} camera(s).");
        }

        public static string LayerNames(LayerMask mask)
        {
            var names = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                if ((mask.value & (1 << i)) == 0)
                    continue;
                if (names.Length > 0)
                    names.Append(", ");
                string name = LayerMask.LayerToName(i);
                names.Append(string.IsNullOrEmpty(name) ? "#" + i : name);
            }

            return names.ToString();
        }
    }
}
#endif
