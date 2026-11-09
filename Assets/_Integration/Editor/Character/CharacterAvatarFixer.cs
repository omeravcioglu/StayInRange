#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Fixes the player model's size and its sinking feet at the source: the model's import settings.
    ///
    /// WHY THE FEET SANK. The model's toe bones sit about 11 cm up inside its chunky shoes, so when
    /// Unity built the humanoid avatar it decided the character was floating and lowered the hips
    /// it stores for the avatar's reference pose by 8.3 cm. Every humanoid animation then placed the
    /// body for those shorter legs, and the real legs pushed the shoes through the floor (15 cm in
    /// the game's idle, measured by the Fit Check).
    ///
    /// WHAT THIS DOES, idempotently:
    ///   1. Backs up the model's .meta to Builds/CharacterFit/backup.
    ///   2. Sets the import scale (0.75 brings the 2.48 m model to about 1.85 m).
    ///   3. Rewrites the avatar's stored reference pose from the model's real bind pose - positions
    ///      only, so the reference pose's arm corrections stay - and puts the hips where they really are.
    ///   4. Calibrates the hips height a few millimetres at a time until the Fit Check finds the soles
    ///      of the game's idle clips on the ground.
    ///   5. Records the result in the importer's user data, which CharacterModelImportGuard checks on
    ///      every later import of the model.
    ///
    /// Everything that embeds a copy of the model has to be rebuilt afterwards (Character Visuals) -
    /// an unpacked copy keeps the old bone lengths. Pressing Configure ▸ Enforce T-Pose / Reset on the
    /// model's Rig tab undoes the fix; the guard logs an error when that happens, and running this
    /// again restores it. The lasting fix is to move the toe bones down to the soles in Blender.
    /// </summary>
    public static class CharacterAvatarFixer
    {
        public const string ModelGuid = "342451a4fcae76e4b963e07b2a6f9794"; // omerfeozs - 3D Low Poly

        /// <summary>Import scale: the 2.48 m model at about 1.85 m.</summary>
        public const float TargetScale = 0.75f;

        /// <summary>The bone the avatar maps as Hips.</summary>
        public const string HipsBone = "root";

        const int MaxCalibrationRounds = 5;
        const float CalibrationTolerance = 0.003f;
        const float MaxHipsOffset = 0.15f;

        [Serializable]
        public class Record
        {
            public float scale;
            public float bindHipsUp;       // the hips' height in the bind pose (its parent's local z, which points up)
            public float hipsOffset;       // added to it by the calibration
            public float standingSoleMean; // what the Fit Check measured afterwards
            public bool footIK;            // measured with Foot IK on, as the game poses
            public string when;
        }

        [Serializable]
        class UserData
        {
            public Record collarcali;
        }

        [MenuItem(CharacterFitCheck.MenuRoot + "Fix Avatar & Scale (player model)")]
        static void RunFromMenu()
        {
            if (!EditorUtility.DisplayDialog("Fix Avatar & Scale",
                    "Re-imports the player model at scale " + TargetScale + " and rewrites its avatar's " +
                    "reference pose so the feet stand on the ground.\n\nEverything that embeds a copy of the " +
                    "model must be rebuilt afterwards (Tools > CollarCali > Build Character Visuals).",
                    "Fix it", "Cancel"))
                return;

            bool ok = Run(out string log);
            Debug.Log(log);
            EditorUtility.DisplayDialog("Fix Avatar & Scale", (ok ? "Done.\n\n" : "Did not finish.\n\n") + log, "OK");
        }

        /// <summary>Batch entry point: -executeMethod CollarCali.EditorTools.CharacterAvatarFixer.RunBatch</summary>
        public static void RunBatch()
        {
            bool ok = Run(out string log);
            Debug.Log("[AvatarFixer] " + (ok ? "OK" : "FAILED") + "\n" + log);
        }

        public static bool Run(out string log)
        {
            var sb = new StringBuilder();
            log = string.Empty;
            string path = AssetDatabase.GUIDToAssetPath(ModelGuid);
            var importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                log = "Player model not found (guid " + ModelGuid + ").";
                return false;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                log = "Leave Play Mode first.";
                return false;
            }

            Backup(path, sb);

            // 2. Scale.
            if (Mathf.Abs(importer.globalScale - TargetScale) > 0.0001f || !importer.useFileScale)
            {
                sb.AppendLine($"Import scale {importer.globalScale} -> {TargetScale}.");
                importer.globalScale = TargetScale;
                importer.useFileScale = true;
                importer.SaveAndReimport();
            }

            // 3. Reference pose from the bind pose.
            var bind = BindPose(path);
            if (!bind.TryGetValue(HipsBone, out var hipsBind))
            {
                log = sb + "The model has no '" + HipsBone + "' bone.";
                return false;
            }

            float bindHipsUp = hipsBind.z;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var description = importer.humanDescription;

            // 4. Find the hips offset that puts the standing soles on the ground, posed as the game poses
            //    them (Foot IK on), on avatars built in memory: one import at the end, not one a round.
            float offset = 0f;
            float mean = MeasureWithOffset(model, description, bind, offset);
            sb.AppendLine($"  Hips {CharacterFitCheck.Mm(offset)}: standing soles average {CharacterFitCheck.Mm(mean)}.");
            float lastOffset = float.NaN, lastMean = float.NaN;
            for (int round = 0; round < MaxCalibrationRounds && !float.IsNaN(mean) && Mathf.Abs(mean) > CalibrationTolerance; round++)
            {
                // A sole below the ground needs higher hips. The first step assumes the soles rise one
                // for one with the hips; later steps use the rate measured so far.
                float rate = 1f;
                if (!float.IsNaN(lastMean) && Mathf.Abs(offset - lastOffset) > 0.0005f)
                    rate = (mean - lastMean) / (offset - lastOffset);
                if (rate < 0.2f)
                {
                    sb.AppendLine($"  The soles barely follow the hips (rate {rate:0.00}); keeping the bind pose.");
                    offset = 0f;
                    mean = MeasureWithOffset(model, description, bind, offset);
                    break;
                }

                lastOffset = offset;
                lastMean = mean;
                offset = Mathf.Clamp(offset - mean / rate, -MaxHipsOffset, MaxHipsOffset);
                mean = MeasureWithOffset(model, description, bind, offset);
                sb.AppendLine($"  Hips {CharacterFitCheck.Mm(offset)}: standing soles average {CharacterFitCheck.Mm(mean)}.");
            }

            if (float.IsNaN(mean))
            {
                log = sb + "The Fit Check could not measure the model.";
                return false;
            }

            // 5. Write it into the import settings, re-import, and measure the imported avatar.
            int written = WriteReferencePose(importer, bind, offset);
            var verify = CharacterFitCheck.Measure(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            mean = verify.StandingSoleMean;
            sb.AppendLine($"Reference pose rewritten from the bind pose ({written} bones), hips {CharacterFitCheck.Mm(offset)}; " +
                          $"imported avatar: standing soles average {CharacterFitCheck.Mm(mean)}.");

            // 6. Record.
            var record = new Record
            {
                scale = TargetScale,
                bindHipsUp = bindHipsUp,
                hipsOffset = offset,
                standingSoleMean = mean,
                footIK = true,
                when = DateTime.Now.ToString("u"),
            };
            importer.userData = JsonUtility.ToJson(new UserData { collarcali = record });
            importer.SaveAndReimport();

            sb.AppendLine($"Recorded: scale {TargetScale}, hips {bindHipsUp + offset:0.0000} m (bind {bindHipsUp:0.0000} {CharacterFitCheck.Mm(offset)}).");
            log = sb.ToString();
            return Mathf.Abs(mean) <= CalibrationTolerance * 2f;
        }

        /// <summary>The standing soles' average height with the hips raised by <paramref name="offset"/>, posed with Foot IK.</summary>
        static float MeasureWithOffset(GameObject model, HumanDescription description, Dictionary<string, Vector3> bind, float offset)
        {
            var avatar = BuildAvatar(model, description, bind, offset);
            try
            {
                return CharacterFitCheck.Measure(model, null, true, avatar).StandingSoleMean;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        /// <summary>The fix this model was given, or null if it never was.</summary>
        public static Record ReadRecord(ModelImporter importer)
        {
            if (importer == null || string.IsNullOrEmpty(importer.userData) || !importer.userData.Contains("collarcali"))
                return null;
            try
            {
                return JsonUtility.FromJson<UserData>(importer.userData)?.collarcali;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Every transform of the imported model by name, with its bind-pose local position.</summary>
        static Dictionary<string, Vector3> BindPose(string path)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var map = new Dictionary<string, Vector3>();
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                if (!map.ContainsKey(t.name))
                    map[t.name] = t.localPosition;
            }

            return map;
        }

        /// <summary>
        /// Writes the bind-pose positions into the avatar's stored reference pose, with the hips
        /// raised by <paramref name="hipsOffset"/>, and re-imports. Rotations and scales are kept.
        /// </summary>
        static int WriteReferencePose(ModelImporter importer, Dictionary<string, Vector3> bind, float hipsOffset)
        {
            var description = importer.humanDescription;
            description.skeleton = ReferenceSkeleton(description.skeleton, bind, hipsOffset, out int written);
            importer.humanDescription = description;
            importer.SaveAndReimport();
            return written;
        }

        /// <summary>
        /// Diagnostic, batch: -executeMethod CollarCali.EditorTools.CharacterAvatarFixer.SweepBatch
        /// Builds the player model's avatar in memory with the hips at several heights, measures each
        /// with and without Foot IK next to the imported avatar and Malbers' Steve, and writes the
        /// table to Builds/CharacterFit/&lt;time&gt;-avatar-sweep. Changes no asset.
        /// </summary>
        public static void SweepBatch()
        {
            string path = AssetDatabase.GUIDToAssetPath(ModelGuid);
            var importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as ModelImporter;
            var model = importer != null ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
            if (model == null)
            {
                Debug.LogError("[AvatarSweep] Player model not found (guid " + ModelGuid + ").");
                return;
            }

            var bind = BindPose(path);
            float bindUp = bind[HipsBone].z;
            var steve = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Malbers Animations/Animal Controller/Human/Model/Steve_v2.fbx");
            string folder = CharacterFitCheck.NewReportFolder("avatar-sweep");
            float[] offsets = { -0.060f, -0.030f, 0f, 0.030f, 0.0653f, 0.090f };

            var sb = new StringBuilder();
            sb.AppendLine("# Avatar sweep");
            sb.AppendLine();
            sb.AppendLine($"Model `{path}`, import scale {importer.globalScale}, bind-pose hips {bindUp:0.0000} m. " +
                          "Each cell: average / deepest of the lower sole, mm.");
            foreach (bool footIK in new[] { true, false })
            {
                sb.AppendLine();
                sb.AppendLine("## Foot IK " + (footIK ? "on (as the game)" : "off"));
                sb.AppendLine();
                bool header = false;
                void Row(string label, CharacterFitCheck.Report r)
                {
                    if (!header)
                    {
                        sb.AppendLine("| Avatar | humanScale | feet bottom | top | " + string.Join(" | ", r.clips.Select(c => c.clip)) + " |");
                        sb.AppendLine("|---|---|---|---|" + string.Concat(r.clips.Select(_ => "---|")));
                        header = true;
                    }

                    sb.AppendLine($"| {label} | {r.humanScale:0.000} | {r.leftFeetBottomHeight:0.000} | {r.meshTop:0.000} | " +
                                  string.Join(" | ", r.clips.Select(c => $"{c.plantedMean * 1000f:+0;-0} / {c.deepest * 1000f:+0;-0}")) + " |");
                    foreach (var f in r.failures.Where(f => f.StartsWith("Exception", StringComparison.Ordinal) || f.StartsWith("No ", StringComparison.Ordinal)))
                        sb.AppendLine("  - " + f);
                }

                foreach (float offset in offsets)
                {
                    var avatar = BuildAvatar(model, importer.humanDescription, bind, offset);
                    string pictures = footIK ? Path.Combine(folder, $"hips{offset * 1000f:+0;-0;0}") : null;
                    if (pictures != null)
                        Directory.CreateDirectory(pictures);
                    var report = CharacterFitCheck.Measure(model, pictures, footIK, avatar);
                    Row($"hips {offset * 1000f:+0.0;-0.0;0.0} mm", report);
                    UnityEngine.Object.DestroyImmediate(avatar);
                }

                Row("imported", CharacterFitCheck.Measure(model, null, footIK));
                if (steve != null)
                {
                    string pictures = footIK ? Path.Combine(folder, "steve") : null;
                    if (pictures != null)
                        Directory.CreateDirectory(pictures);
                    Row("Steve_v2 (reference)", CharacterFitCheck.Measure(steve, pictures, footIK));
                }
            }

            File.WriteAllText(Path.Combine(folder, "sweep.md"), sb.ToString());
            Debug.Log("[AvatarSweep] " + folder + "\n" + sb);
        }

        /// <summary>
        /// The player model's avatar, built in memory from the importer's human description with the
        /// reference pose taken from the bind pose and the hips raised by <paramref name="hipsOffset"/>.
        /// </summary>
        static Avatar BuildAvatar(GameObject model, HumanDescription description, Dictionary<string, Vector3> bind, float hipsOffset)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                var skeleton = ReferenceSkeleton(description.skeleton, bind, hipsOffset, out _);
                if (skeleton.Length > 0)
                    instance.name = skeleton[0].name; // the importer built the avatar on a "(Clone)"
                description.skeleton = skeleton;
                var avatar = AvatarBuilder.BuildHumanAvatar(instance, description);
                avatar.name = $"Hips {hipsOffset * 1000f:+0.0;-0.0;0.0} mm";
                return avatar;
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>
        /// A copy of <paramref name="skeleton"/> with every bone's position from the bind pose and the
        /// hips raised by <paramref name="hipsOffset"/>. Rotations and scales are kept.
        /// </summary>
        static SkeletonBone[] ReferenceSkeleton(SkeletonBone[] skeleton, Dictionary<string, Vector3> bind, float hipsOffset, out int written)
        {
            var copy = (SkeletonBone[])skeleton.Clone();
            written = 0;
            for (int i = 0; i < copy.Length; i++)
            {
                string name = copy[i].name;
                if (name.EndsWith("(Clone)", StringComparison.Ordinal))
                    name = name.Substring(0, name.Length - "(Clone)".Length);

                if (!bind.TryGetValue(name, out var position))
                    continue;

                if (name == HipsBone)
                    position.z += hipsOffset; // the metarig is turned -90° about X, so its local z is up
                copy[i].position = position;
                written++;
            }

            return copy;
        }

        static void Backup(string path, StringBuilder sb)
        {
            string folder = Path.Combine("Builds", "CharacterFit", "backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            File.Copy(path + ".meta", Path.Combine(folder, Path.GetFileName(path) + ".meta"), true);
            sb.AppendLine("Backed up the import settings to " + folder + ".");
        }
    }

    /// <summary>
    /// Logs an error when a re-import of the player model loses the avatar fix (someone pressed
    /// Enforce T-Pose or Reset on its Rig tab, or re-downloaded it), so the sinking feet can't come
    /// back unnoticed. It never changes anything itself.
    /// </summary>
    class CharacterModelImportGuard : AssetPostprocessor
    {
        const float Tolerance = 0.002f;

        void OnPostprocessModel(GameObject root)
        {
            if (AssetDatabase.AssetPathToGUID(assetPath) != CharacterAvatarFixer.ModelGuid)
                return;

            var importer = assetImporter as ModelImporter;
            var record = CharacterAvatarFixer.ReadRecord(importer);
            if (record == null)
                return; // not fixed yet: nothing to guard

            var hips = importer.humanDescription.skeleton
                .FirstOrDefault(b => b.name == CharacterAvatarFixer.HipsBone);
            bool scaleOk = Mathf.Abs(importer.globalScale - record.scale) < 0.0001f;
            bool hipsOk = !string.IsNullOrEmpty(hips.name) &&
                          Mathf.Abs(hips.position.z - (record.bindHipsUp + record.hipsOffset)) <= Tolerance;
            if (scaleOk && hipsOk)
                return;

            Debug.LogError("[CollarCali] The player model's avatar fix was lost (" +
                           (scaleOk ? "" : "import scale changed; ") + (hipsOk ? "" : "hips moved; ") +
                           "the feet will sink again). Run Tools > CollarCali > Character > Fix Avatar & Scale, " +
                           "then Build Character Visuals.");
        }
    }
}
#endif
