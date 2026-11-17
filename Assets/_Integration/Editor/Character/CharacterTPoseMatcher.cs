#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Poses the player model's avatar T-pose like Malbers' Steve's, so Malbers' animations move the
    /// player's limbs the way they move Steve's.
    ///
    /// WHY. Unity plays a humanoid animation as turns away from the avatar's stored T-pose: whatever
    /// the stored pose is, the animation treats it as standing straight. The player model's stored
    /// pose came from its bind pose (CharacterAvatarFixer), where the thighs lean about 14° forward and
    /// out and the knees bend a little, while Steve's legs hang straight and his hands tip up. So
    /// wherever Steve's legs are straight the player's are bent and splayed, and its hands point about
    /// 10° away from his - in every clip (Animation Match measures it).
    ///
    /// WHAT THIS DOES, idempotently:
    ///   1. Turns the stored pose's spine, neck, collarbones, upper arms, forearms, hands, thighs and
    ///      shins so each points the way Steve's does in his stored pose, and rolls the hands so the
    ///      knuckles line up with his. The feet and the head keep their own turn, so the soles stay flat
    ///      and the face looks ahead. Rotations only: no bone moves or changes length, and the mesh is
    ///      untouched (in a neutral pose the collarbones now sit level, as Steve's do).
    ///   2. Switches on Translation DoF, as Steve's avatar has it.
    ///   3. Re-imports; then CharacterAvatarFixer finds the hips height again (straighter legs reach lower).
    ///
    /// Menu: Tools > CollarCali > Character > Pose Avatar Like Steve (player model).
    /// Batch: -executeMethod CollarCali.EditorTools.CharacterTPoseMatcher.RunBatch -quit
    /// Report: Builds/CharacterFit/&lt;time&gt;-tpose-match/tpose.md.
    /// Afterwards rebuild the player (PlayerCharacterBuilder, then ThirdPersonPlayerBuilder): its bone
    /// turns against Steve's come from the T-pose.
    /// </summary>
    public static class CharacterTPoseMatcher
    {
        const string SteveModelPath = "Assets/Malbers Animations/Animal Controller/Human/Model/Steve_v2.fbx";

        /// <summary>Degrees: a limb this close to Steve's needs no turn.</summary>
        const float Close = 0.05f;

        /// <summary>Which parts of the body are posed like Steve's.</summary>
        [Flags]
        public enum Parts
        {
            None = 0,
            Legs = 1,
            Arms = 2,
            Hands = 4,
            Shoulders = 8,
            Spine = 16,
            /// <summary>
            /// What the player model is posed with: every part that moves the limbs. Measured on all 320 of
            /// Malbers' human clips (Animation Match, candidates): arms, hands and legs alone left 23 clips
            /// visibly off Steve somewhere; with the shoulders and spine too, none, and 256 the same throughout.
            /// The hips stay: this model's spine bone sits forward of its pelvis, and posing that like Steve's
            /// would tilt the pelvis.
            /// </summary>
            Default = Legs | Arms | Hands | Shoulders | Spine,
        }

        /// <summary>
        /// The bones posed like Steve's, parents first: the bone turned, the bone it points to, and the part
        /// it belongs to. The spine and shoulders come first because the arms hang from them.
        /// </summary>
        static readonly (HumanBodyBones bone, HumanBodyBones toward, Parts part)[] Chain =
        {
            (HumanBodyBones.Spine, HumanBodyBones.Chest, Parts.Spine),
            (HumanBodyBones.Chest, HumanBodyBones.UpperChest, Parts.Spine),
            (HumanBodyBones.UpperChest, HumanBodyBones.Neck, Parts.Spine),
            (HumanBodyBones.Neck, HumanBodyBones.Head, Parts.Spine),
            (HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, Parts.Shoulders),
            (HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, Parts.Shoulders),
            (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, Parts.Legs),
            (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, Parts.Legs),
            (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, Parts.Legs),
            (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, Parts.Legs),
            (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, Parts.Arms),
            (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, Parts.Arms),
            (HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, Parts.Hands),
            (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, Parts.Arms),
            (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, Parts.Arms),
            (HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, Parts.Hands),
        };

        /// <summary>
        /// Bones that keep their own turn when a parent turns: the feet, so the soles stay flat, and the
        /// head, so the face keeps looking ahead.
        /// </summary>
        static readonly HumanBodyBones[] KeepTurn = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.Head };

        /// <summary>Hands rolled so the knuckles line up with Steve's: hand, middle, index and little finger roots.</summary>
        static readonly (HumanBodyBones hand, HumanBodyBones middle, HumanBodyBones index, HumanBodyBones little)[] Palms =
        {
            (HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftLittleProximal),
            (HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightLittleProximal),
        };

        /// <summary>Everything the report compares: the matched limbs and the rest of the body.</summary>
        static readonly (HumanBodyBones bone, HumanBodyBones toward, string name)[] Compared =
        {
            (HumanBodyBones.Hips, HumanBodyBones.Spine, "hips to spine"),
            (HumanBodyBones.Spine, HumanBodyBones.Chest, "spine"),
            (HumanBodyBones.Chest, HumanBodyBones.UpperChest, "chest"),
            (HumanBodyBones.UpperChest, HumanBodyBones.Neck, "upper chest"),
            (HumanBodyBones.Neck, HumanBodyBones.Head, "neck"),
            (HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, "left shoulder"),
            (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, "left upper arm"),
            (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, "left forearm"),
            (HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, "left hand"),
            (HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, "right shoulder"),
            (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, "right upper arm"),
            (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, "right forearm"),
            (HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, "right hand"),
            (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, "left thigh"),
            (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, "left shin"),
            (HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, "left foot"),
            (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, "right thigh"),
            (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, "right shin"),
            (HumanBodyBones.RightFoot, HumanBodyBones.RightToes, "right foot"),
        };

        [MenuItem(CharacterFitCheck.MenuRoot + "Pose Avatar Like Steve (player model)")]
        static void FromMenu()
        {
            if (!EditorUtility.DisplayDialog("Pose Avatar Like Steve",
                    "Turns the player model's stored T-pose spine, shoulders and limbs to point like Malbers' Steve's, switches on " +
                    "Translation DoF, re-imports the model and finds the hips height again.\n\nRebuild the " +
                    "player afterwards (Player Character, then Third Person Player).",
                    "Pose it", "Cancel"))
                return;

            bool ok = Run(out string log);
            Debug.Log(log);
            EditorUtility.DisplayDialog("Pose Avatar Like Steve", (ok ? "Done.\n\n" : "Did not finish.\n\n") + log, "OK");
        }

        /// <summary>
        /// Batch entry point: -executeMethod CollarCali.EditorTools.CharacterTPoseMatcher.RunBatch -quit.
        /// Add -tposeDryRun to only write the report of what would turn, and -tposeParts "Legs, Arms, ..."
        /// to choose the parts (default: <see cref="Parts.Default"/>).
        /// </summary>
        public static void RunBatch()
        {
            bool dryRun = Environment.GetCommandLineArgs().Contains("-tposeDryRun");
            string named = CharacterFitCheck.ArgValue("-tposeParts");
            var parts = string.IsNullOrEmpty(named) ? Parts.Default : (Parts)Enum.Parse(typeof(Parts), named, true);
            bool ok = Run(out string log, dryRun, parts);
            Debug.Log("[TPoseMatch] " + (dryRun ? "DRY RUN " : "") + parts + ": " + (ok ? "OK" : "FAILED") + "\n" + log);
        }

        public static bool Run(out string log) => Run(out log, false, Parts.Default);

        public static bool Run(out string log, bool dryRun, Parts parts)
        {
            var sb = new StringBuilder();
            log = string.Empty;
            string path = AssetDatabase.GUIDToAssetPath(CharacterAvatarFixer.ModelGuid);
            var importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as ModelImporter;
            var steveImporter = AssetImporter.GetAtPath(SteveModelPath) as ModelImporter;
            var model = importer != null ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
            var steve = steveImporter != null ? AssetDatabase.LoadAssetAtPath<GameObject>(SteveModelPath) : null;
            if (model == null || steve == null)
            {
                log = model == null ? "Player model not found (guid " + CharacterAvatarFixer.ModelGuid + ")." : SteveModelPath + " not found.";
                return false;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                log = "Leave Play Mode first.";
                return false;
            }

            string folder = CharacterFitCheck.NewReportFolder("tpose-match");
            var theirs = steveImporter.humanDescription;
            var description = importer.humanDescription;
            var before = Compare(model, description, steve, theirs);

            var skeleton = Match(model, description, steve, theirs, parts, sb, out int turned);
            bool dof = theirs.hasTranslationDoF && !description.hasTranslationDoF;
            var md = new StringBuilder();
            md.AppendLine("# The player model's avatar T-pose against Malbers' Steve's");
            md.AppendLine();
            md.AppendLine($"- When: {DateTime.Now:u}");
            md.AppendLine($"- Model: `{path}`; Steve: `{SteveModelPath}`");
            if (dryRun)
                md.AppendLine("- Dry run: nothing written; After = as it is now.");
            md.AppendLine();

            bool ok = true;
            if (turned == 0 && !dof)
            {
                sb.AppendLine("Already posed like Steve: nothing to change.");
            }
            else if (dryRun)
            {
                sb.AppendLine($"Dry run: {turned} limb turns{(dof ? " and Translation DoF" : "")} would be written; nothing changed.");
            }
            else
            {
                File.Copy(path + ".meta", Path.Combine(folder, Path.GetFileName(path) + ".meta.before"), true);
                description.skeleton = skeleton;
                if (dof)
                {
                    description.hasTranslationDoF = true;
                    sb.AppendLine("  Translation DoF switched on, as Steve's avatar has it.");
                }

                importer.humanDescription = description;
                importer.SaveAndReimport();
                sb.AppendLine($"Re-imported with {turned} limb turns.");

                // Straighter legs reach lower: find the hips height again.
                ok = CharacterAvatarFixer.Run(out string fixLog);
                sb.AppendLine("Hips height (CharacterAvatarFixer): " + (ok ? "OK" : "did not settle"));
                sb.AppendLine(fixLog);
            }

            importer = AssetImporter.GetAtPath(path) as ModelImporter;
            var after = Compare(AssetDatabase.LoadAssetAtPath<GameObject>(path), importer.humanDescription, steve, theirs);
            md.AppendLine($"Each limb's direction in the stored T-pose, degrees away from Steve's. Posed like Steve's: {parts};");
            md.AppendLine("the rest is listed for comparison.");
            md.AppendLine();
            md.AppendLine("| Limb | Before ° | After ° |");
            md.AppendLine("|---|---:|---:|");
            foreach (var pair in before)
                md.AppendLine($"| {pair.Key} | {pair.Value:0.0} | {(after.TryGetValue(pair.Key, out var a) ? a.ToString("0.0") : "-")} |");
            md.AppendLine();
            md.AppendLine("## Log");
            md.AppendLine();
            md.AppendLine("```");
            md.Append(sb);
            md.AppendLine("```");
            File.WriteAllText(Path.Combine(folder, "tpose.md"), md.ToString());

            sb.AppendLine("Report: " + Path.Combine(folder, "tpose.md"));
            log = sb.ToString();
            return ok;
        }

        /// <summary>
        /// The player model's human description with <paramref name="parts"/> posed like Steve's, built
        /// from its current import settings without changing them: for trying a pose out (Animation Match).
        /// </summary>
        public static bool TryCandidate(Parts parts, out HumanDescription description, StringBuilder log)
        {
            description = default;
            string path = AssetDatabase.GUIDToAssetPath(CharacterAvatarFixer.ModelGuid);
            var importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as ModelImporter;
            var steveImporter = AssetImporter.GetAtPath(SteveModelPath) as ModelImporter;
            var model = importer != null ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
            var steve = steveImporter != null ? AssetDatabase.LoadAssetAtPath<GameObject>(SteveModelPath) : null;
            if (model == null || steve == null)
                return false;

            var theirs = steveImporter.humanDescription;
            description = importer.humanDescription;
            description.skeleton = Match(model, description, steve, theirs, parts, log, out _);
            description.hasTranslationDoF |= theirs.hasTranslationDoF;
            return true;
        }

        /// <summary>An avatar for the player model built in memory from <paramref name="description"/>. Destroy it after use.</summary>
        public static Avatar BuildAvatar(HumanDescription description, string name)
        {
            string path = AssetDatabase.GUIDToAssetPath(CharacterAvatarFixer.ModelGuid);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                if (description.skeleton.Length > 0)
                    instance.name = description.skeleton[0].name; // the importer built the avatar on a "(Clone)"
                var avatar = AvatarBuilder.BuildHumanAvatar(instance, description);
                avatar.name = name;
                return avatar;
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>A copy of <paramref name="ours"/>' stored skeleton with <paramref name="parts"/> turned to point like Steve's.</summary>
        static SkeletonBone[] Match(GameObject model, HumanDescription ours, GameObject steveModel, HumanDescription theirs,
            Parts parts, StringBuilder sb, out int turned)
        {
            turned = 0;
            using var a = new Posed(model, ours);
            using var b = new Posed(steveModel, theirs);
            var changed = new HashSet<Transform>();

            foreach (var (bone, toward, part) in Chain)
            {
                if ((parts & part) == 0)
                    continue;

                if (!a.Direction(bone, toward, out var mine) || !b.Direction(bone, toward, out var his))
                {
                    sb.AppendLine($"  {bone}: not on both skeletons; left as it is.");
                    continue;
                }

                float angle = Vector3.Angle(mine, his);
                if (angle < Close)
                    continue;

                var t = a.bones[bone];
                var kept = KeepTurn
                    .Where(k => a.bones.TryGetValue(k, out var kt) && kt != t && kt.IsChildOf(t))
                    .Select(k => (bone: a.bones[k], rotation: a.bones[k].rotation))
                    .ToList();
                t.rotation = Quaternion.FromToRotation(mine, his) * t.rotation;
                changed.Add(t);
                foreach (var (keptBone, rotation) in kept)
                {
                    keptBone.rotation = rotation;
                    changed.Add(keptBone);
                }

                turned++;
                sb.AppendLine($"  {bone}: turned {angle:0.0}° to point like Steve's.");
            }

            foreach (var (hand, middle, index, little) in Palms)
            {
                if ((parts & Parts.Hands) == 0)
                    break;
                if (!a.Knuckles(hand, middle, index, little, out var mine, out var axis) || !b.Knuckles(hand, middle, index, little, out var his, out _))
                    continue;

                float roll = Vector3.SignedAngle(mine, his, axis);
                if (Mathf.Abs(roll) < Close)
                    continue;

                var t = a.bones[hand];
                t.rotation = Quaternion.AngleAxis(roll, axis) * t.rotation;
                changed.Add(t);
                turned++;
                sb.AppendLine($"  {hand}: rolled {roll:0.0}° so the knuckles line up with Steve's.");
            }

            var copy = (SkeletonBone[])ours.skeleton.Clone();
            for (int i = 1; i < copy.Length; i++)
            {
                var t = a.Find(copy[i].name);
                if (t != null && changed.Contains(t))
                    copy[i].rotation = t.localRotation;
            }

            return copy;
        }

        /// <summary>Each compared limb's angle from Steve's in the two stored T-poses, by name; the palms' roll too.</summary>
        static Dictionary<string, float> Compare(GameObject model, HumanDescription ours, GameObject steveModel, HumanDescription theirs)
        {
            var result = new Dictionary<string, float>();
            using var a = new Posed(model, ours);
            using var b = new Posed(steveModel, theirs);
            foreach (var (bone, toward, name) in Compared)
            {
                if (a.Direction(bone, toward, out var mine) && b.Direction(bone, toward, out var his))
                    result[name] = Vector3.Angle(mine, his);
            }

            foreach (var (hand, middle, index, little) in Palms)
            {
                if (a.Knuckles(hand, middle, index, little, out var mine, out var axis) && b.Knuckles(hand, middle, index, little, out var his, out _))
                    result[(hand == HumanBodyBones.LeftHand ? "left" : "right") + " knuckles (roll)"] = Mathf.Abs(Vector3.SignedAngle(mine, his, axis));
            }

            return result;
        }

        /// <summary>A model standing in its avatar's stored T-pose, in a preview scene, its root at the origin facing +Z.</summary>
        sealed class Posed : IDisposable
        {
            readonly Scene _scene;
            readonly Dictionary<string, Transform> _byName = new Dictionary<string, Transform>();
            public readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();

            public Posed(GameObject model, HumanDescription description)
            {
                _scene = EditorSceneManager.NewPreviewScene();
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, _scene);
                foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                    _byName.TryAdd(t.name, t);

                var skeleton = description.skeleton;
                for (int i = 0; i < skeleton.Length; i++)
                {
                    var t = i == 0 ? instance.transform : Find(skeleton[i].name);
                    if (t == null)
                        continue;
                    t.localPosition = skeleton[i].position;
                    t.localRotation = skeleton[i].rotation;
                    t.localScale = skeleton[i].scale;
                }

                // Unity reads the T-pose against the avatar's root: measured with the root unturned at the origin.
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                var names = HumanTrait.BoneName;
                foreach (var human in description.human)
                {
                    int index = Array.IndexOf(names, human.humanName);
                    var t = Find(human.boneName);
                    if (index >= 0 && index < (int)HumanBodyBones.LastBone && t != null)
                        bones[(HumanBodyBones)index] = t;
                }
            }

            public Transform Find(string name)
            {
                if (name.EndsWith("(Clone)", StringComparison.Ordinal))
                    name = name.Substring(0, name.Length - "(Clone)".Length);
                return _byName.TryGetValue(name, out var t) ? t : null;
            }

            public bool Direction(HumanBodyBones from, HumanBodyBones to, out Vector3 direction)
            {
                direction = default;
                if (!bones.TryGetValue(from, out var a) || !bones.TryGetValue(to, out var b))
                    return false;
                direction = (b.position - a.position).normalized;
                return direction.sqrMagnitude > 0.5f;
            }

            /// <summary>The line across the knuckles (little finger to index finger), square to the hand's own line.</summary>
            public bool Knuckles(HumanBodyBones hand, HumanBodyBones middle, HumanBodyBones index, HumanBodyBones little,
                out Vector3 across, out Vector3 axis)
            {
                across = axis = default;
                if (!bones.TryGetValue(hand, out var h) || !bones.TryGetValue(middle, out var m) ||
                    !bones.TryGetValue(index, out var i) || !bones.TryGetValue(little, out var l))
                    return false;
                axis = (m.position - h.position).normalized;
                across = Vector3.ProjectOnPlane(i.position - l.position, axis).normalized;
                return across.sqrMagnitude > 0.5f && axis.sqrMagnitude > 0.5f;
            }

            public void Dispose() => EditorSceneManager.ClosePreviewScene(_scene);
        }
    }
}
#endif
