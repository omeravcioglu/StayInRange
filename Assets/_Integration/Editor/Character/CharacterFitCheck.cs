#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Measures how a character's body fits the ground and its colliders, so a change to the model,
    /// its avatar or the player prefab is judged by numbers instead of by eye.
    ///
    /// The character stands in a preview scene with its root on the ground at the origin - where
    /// Malbers keeps the root - and is posed with the ground clips of Malbers' human controller
    /// (AC Human v5): idles, crouch, walk, jog and sprint, with both kinds of root placement Malbers'
    /// clips use ("from the feet" and "original"), at 24 moments of each clip. Every state that plays
    /// those clips has Foot IK on, so they are posed with Foot IK, as in the game. For every pose the
    /// deformed mesh is baked; each foot's lowest point is its sole, and a sole below zero is a foot
    /// in the floor.
    ///
    /// On a player prefab it also checks that the head stays inside the main capsule, that the HB_*
    /// hitboxes hold the head and torso, and that no reference points at an object that is gone.
    ///
    /// Reports land in Builds/CharacterFit/&lt;timestamp&gt;: report.md, report.json, side.png (the
    /// idle pose with the ground line, the main capsule and a 10 cm ruler) and poses.png (each clip
    /// at its deepest moment). Run it from Tools ▸ CollarCali ▸ Character with a prefab or model
    /// selected, or in batch mode:
    ///   -executeMethod CollarCali.EditorTools.CharacterFitCheck.RunBatch -fitTarget "Assets/....prefab"
    /// </summary>
    public static class CharacterFitCheck
    {
        public const string MenuRoot = "Tools/CollarCali/Character/";

        const string ClipRoot = "Assets/Malbers Animations/Common/Human Anims/";

        /// <summary>
        /// The clips posed, each with the AC Human v5 state that plays it. Standing clips keep both
        /// feet down; moving clips lift them in turn and may leave the ground (jog and sprint).
        /// </summary>
        static readonly (string path, bool standing)[] ClipList =
        {
            ("Idle/Idle v2.fbx", true),                     // Idle Default/Idle, from feet
            ("Idle/Idle_Combat.fbx", true),                 // Idles/Idle Combat, original
            ("Locomotion/Strafe/S_Strafe_Idle.fbx", true),  // Idles/Idle Strafe, original
            ("Crouch/S_Crouch_Idle.fbx", true),             // Idles/Idle Crouch, original
            ("Locomotion/S_Walk_V2_Forward.fbx", false),    // Locomotion/Locomotion, from feet
            ("Crouch/S_Crouch_Walk_Forward.fbx", false),    // Locomotion/Locomotion Crouch, original
            ("Locomotion/S_Jog_F2.fbx", false),             // Locomotion/Locomotion, original
            ("Locomotion/S_Sprint.fbx", false),             // Locomotion/Locomotion, from feet
        };

        const int SamplesPerClip = 24;

        /// <summary>The bones whose height the report lists, for placing pivots, capsules and sockets.</summary>
        static readonly HumanBodyBones[] ProfileBones =
        {
            HumanBodyBones.LeftToes, HumanBodyBones.LeftFoot, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
            HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.LeftShoulder, HumanBodyBones.LeftHand,
        };

        const float ProfileStep = 0.05f;

        // Pass thresholds, in metres. Malbers' own Steve stays within them with these clips.
        const float StandingMeanMin = -0.010f, StandingMeanMax = 0.010f; // standing soles on the ground
        const float StandingDeepestMin = -0.015f;                       // never deeper than 15 mm standing
        const float MovingDeepestMin = -0.040f;                         // a stride never goes 40 mm in
        const float MovingDeepestMax = 0.015f;                          // and every stride reaches the ground
        const float BodyDeepestMin = -0.025f;                           // nothing else goes into the ground
        const float HeightMin = 1.82f, HeightMax = 1.88f;               // the player's standing height
        const float CapsuleHeadroom = 0.10f;                            // the head may rise this far above the main capsule
        const float HitboxCoverageMin = 0.85f;                          // of the head and torso, inside the hitboxes

        /// <summary>The name every hitbox collider starts with (HB_Pelvis, HB_Torso, HB_Head).</summary>
        public const string HitboxPrefix = "HB_";

        [Serializable]
        public class ClipResult
        {
            public string clip;
            public string rootFrom;         // "feet" or "original": how the clip places the body
            public bool standing;
            public float deepest;           // the lower foot's lowest point over the clip
            public float plantedMean;       // the lower foot's average height
            public float highest;           // the lower foot's highest point (both feet up = flight)
            public float leftMean;          // each foot's average height
            public float rightMean;
            public float bodyDeepest;       // the lowest point of anything
            public string bodyDeepestPart;  // the bone that carries it
            public float deepestTime;       // seconds into the clip
        }

        [Serializable]
        public class BoneHeight
        {
            public string bone;
            public float height;
        }

        /// <summary>One horizontal slice of the body: its widest point from the root's vertical axis.</summary>
        [Serializable]
        public class Band
        {
            public float from;
            public float to;
            public float radius;
            public int vertices;
        }

        [Serializable]
        public class Report
        {
            public string target;
            public string when;
            public bool footIK;
            public bool humanoid;
            public float humanScale;
            public float leftFeetBottomHeight;
            public float rightFeetBottomHeight;
            public float hipsHeight;
            public float meshTop;           // standing height of the mesh (first idle pose)
            public float meshBottom;        // soles, first idle pose
            public float capsuleBottom = float.NaN;
            public float capsuleTop = float.NaN;
            public float capsuleRadius = float.NaN;
            public int hitboxes;                       // colliders named HB_* on the target
            public float hitboxCoverage = float.NaN;   // share of the head and torso inside them, first idle pose
            public List<string> dangling = new List<string>();  // references to objects that no longer exist
            public List<BoneHeight> bones = new List<BoneHeight>();  // first idle pose
            public List<Band> profile = new List<Band>();            // first idle pose
            public List<ClipResult> clips = new List<ClipResult>();
            public List<string> failures = new List<string>();
            public List<string> notes = new List<string>();
            public bool pass;

            /// <summary>Average sole height over the standing clips; what the avatar fix calibrates to zero.</summary>
            public float StandingSoleMean
            {
                get
                {
                    var standing = clips.Where(c => c.standing).ToList();
                    return standing.Count > 0 ? standing.Average(c => c.plantedMean) : float.NaN;
                }
            }

            /// <summary>The clip result named <paramref name="clip"/>, or null.</summary>
            public ClipResult Clip(string clip) => clips.FirstOrDefault(c => c.clip == clip);
        }

        [MenuItem(MenuRoot + "Fit Check (selected prefab or model)")]
        static void RunFromMenu()
        {
            var target = Selection.activeObject as GameObject;
            string path = target != null ? AssetDatabase.GetAssetPath(target) : null;
            if (string.IsNullOrEmpty(path))
            {
                EditorUtility.DisplayDialog("Character Fit Check",
                    "Select a character prefab or model in the Project window first.", "OK");
                return;
            }

            var report = RunAndWrite(path, out string folder);
            EditorUtility.DisplayDialog("Character Fit Check",
                (report.pass ? "PASS" : "FAIL") + "\n\n" + Summary(report) + "\n\nReport: " + folder, "OK");
            EditorUtility.RevealInFinder(Path.Combine(folder, "report.md"));
        }

        /// <summary>
        /// Batch entry point: -fitTarget "Assets/a.prefab;Assets/b.fbx". Writes a report per target and
        /// logs each summary.
        /// </summary>
        public static void RunBatch()
        {
            string list = ArgValue("-fitTarget");
            if (string.IsNullOrEmpty(list))
            {
                Debug.LogError("[FitCheck] Pass -fitTarget \"Assets/...prefab\" (several separated by ';').");
                return;
            }

            foreach (var path in list.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                var report = RunAndWrite(path, out string folder);
                Debug.Log("[FitCheck] " + (report.pass ? "PASS" : "FAIL") + " " + path + "\n" + Summary(report) +
                          "\nReport: " + folder);
            }
        }

        public static Report RunAndWrite(string assetPath, out string folder)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            folder = NewReportFolder(Path.GetFileNameWithoutExtension(assetPath));

            var report = Measure(asset, folder);
            report.target = assetPath;
            File.WriteAllText(Path.Combine(folder, "report.json"), JsonUtility.ToJson(report, true));
            File.WriteAllText(Path.Combine(folder, "report.md"), Markdown(report));
            return report;
        }

        /// <summary>A new folder under Builds/CharacterFit named by the time and <paramref name="name"/>.</summary>
        public static string NewReportFolder(string name)
        {
            string folder = Path.Combine("Builds", "CharacterFit", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Sanitize(name));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Poses <paramref name="asset"/> (a prefab or a model) with the ground clips and measures it.
        /// Saves the pictures into <paramref name="folder"/> when one is given. <paramref name="avatar"/>
        /// replaces the character's own avatar (the avatar fix tries candidates this way);
        /// <paramref name="footIK"/> false poses without Foot IK, for comparison.
        /// </summary>
        public static Report Measure(GameObject asset, string folder = null, bool footIK = true, Avatar avatar = null)
        {
            var report = new Report { when = DateTime.Now.ToString("u"), footIK = footIK };
            if (asset == null)
            {
                report.failures.Add("Target not found.");
                return report;
            }

            Subject subject = null;
            try
            {
                subject = Subject.Create(asset, avatar, report);
                if (subject == null)
                    return report;

                report.humanoid = true;
                var deepestTimes = new List<(AnimationClip clip, double time)>();
                foreach (var (relative, standing) in ClipList)
                {
                    var clip = LoadClip(ClipRoot + relative);
                    if (clip == null)
                    {
                        report.notes.Add("Clip missing: " + relative);
                        continue;
                    }

                    var result = MeasureClip(subject, clip, standing, footIK);
                    result.rootFrom = RootFromFeet(ClipRoot + relative) ? "feet" : "original";
                    report.clips.Add(result);
                    deepestTimes.Add((clip, result.deepestTime));
                }

                var animator = subject.Animator;
                report.humanScale = animator.humanScale;
                report.leftFeetBottomHeight = animator.leftFeetBottomHeight;
                report.rightFeetBottomHeight = animator.rightFeetBottomHeight;

                // The first idle pose: standing height, idle soles, hips.
                var idle = deepestTimes.Count > 0 ? deepestTimes[0].clip : null;
                if (idle != null)
                {
                    subject.PoseAt(idle, 0, footIK);
                    var pose = subject.MeasurePose();
                    report.meshBottom = pose.Planted;
                    report.meshTop = pose.Top;
                    report.hipsHeight = animator.GetBoneTransform(HumanBodyBones.Hips).position.y;
                    foreach (var bone in ProfileBones)
                    {
                        var t = animator.GetBoneTransform(bone);
                        if (t != null)
                            report.bones.Add(new BoneHeight { bone = bone.ToString(), height = t.position.y });
                    }

                    report.profile = subject.Profile(ProfileStep);
                }

                var capsule = MainCapsule(subject.Instance);
                if (capsule != null)
                {
                    float scale = Mathf.Abs(capsule.transform.lossyScale.y);
                    float half = Mathf.Max(capsule.height * 0.5f, capsule.radius) * scale;
                    float centre = capsule.transform.TransformPoint(capsule.center).y;
                    report.capsuleBottom = centre - half;
                    report.capsuleTop = centre + half;
                    report.capsuleRadius = capsule.radius * scale;
                }

                report.dangling = DanglingReferences(subject.Instance);
                var hitboxes = subject.Instance.GetComponentsInChildren<Collider>(true)
                    .Where(c => c.name.StartsWith(HitboxPrefix, StringComparison.Ordinal)).ToList();
                report.hitboxes = hitboxes.Count;
                if (hitboxes.Count > 0 && idle != null)
                {
                    subject.PoseAt(idle, 0, footIK);
                    report.hitboxCoverage = subject.Coverage(hitboxes, CoreBones(animator));
                }

                if (!string.IsNullOrEmpty(folder))
                    TryPictures(subject, report, idle, deepestTimes, footIK, folder);

                Judge(report);
                return report;
            }
            catch (Exception e)
            {
                report.failures.Add("Exception: " + e.Message);
                Debug.LogException(e);
                return report;
            }
            finally
            {
                subject?.Dispose();
            }
        }

        static ClipResult MeasureClip(Subject subject, AnimationClip clip, bool standing, bool footIK)
        {
            var result = new ClipResult
            {
                clip = clip.name,
                standing = standing,
                deepest = float.PositiveInfinity,
                highest = float.NegativeInfinity,
                bodyDeepest = float.PositiveInfinity,
            };

            float planted = 0f, left = 0f, right = 0f;
            foreach (var (time, pose) in subject.Sample(clip, SamplesPerClip, footIK))
            {
                planted += pose.Planted;
                left += pose.Left;
                right += pose.Right;
                if (pose.Planted < result.deepest)
                {
                    result.deepest = pose.Planted;
                    result.deepestTime = (float)time;
                }

                result.highest = Mathf.Max(result.highest, pose.Planted);
                if (pose.Lowest < result.bodyDeepest)
                {
                    result.bodyDeepest = pose.Lowest;
                    result.bodyDeepestPart = pose.LowestPart;
                }
            }

            result.plantedMean = planted / SamplesPerClip;
            result.leftMean = left / SamplesPerClip;
            result.rightMean = right / SamplesPerClip;
            return result;
        }

        static void Judge(Report report)
        {
            foreach (var c in report.clips)
            {
                if (c.standing)
                {
                    if (c.plantedMean < StandingMeanMin || c.plantedMean > StandingMeanMax)
                        report.failures.Add($"{c.clip}: soles average {Mm(c.plantedMean)} (want {Mm(StandingMeanMin)}..{Mm(StandingMeanMax)}).");
                    if (c.deepest < StandingDeepestMin)
                        report.failures.Add($"{c.clip}: a sole goes {Mm(-c.deepest)} into the ground (limit {Mm(-StandingDeepestMin)}).");
                }
                else
                {
                    if (c.deepest < MovingDeepestMin)
                        report.failures.Add($"{c.clip}: a foot goes {Mm(-c.deepest)} into the ground (limit {Mm(-MovingDeepestMin)}).");
                    if (c.deepest > MovingDeepestMax)
                        report.failures.Add($"{c.clip}: the feet never reach the ground (closest {Mm(c.deepest)}, limit {Mm(MovingDeepestMax)}).");
                }

                if (c.bodyDeepest < BodyDeepestMin && c.bodyDeepest < c.deepest - 0.001f)
                    report.failures.Add($"{c.clip}: {c.bodyDeepestPart} goes {Mm(-c.bodyDeepest)} into the ground.");
            }

            if (report.humanoid && (report.meshTop < HeightMin || report.meshTop > HeightMax))
                report.failures.Add($"Standing height {report.meshTop:0.000} m (want {HeightMin:0.00}..{HeightMax:0.00} m).");

            if (!float.IsNaN(report.capsuleTop) && report.meshTop - report.capsuleTop > CapsuleHeadroom)
                report.failures.Add($"The head rises {Mm(report.meshTop - report.capsuleTop)} above the main capsule (limit {Mm(CapsuleHeadroom)}).");
            if (report.hitboxes > 0 && !(report.hitboxCoverage >= HitboxCoverageMin))
                report.failures.Add($"The hitboxes hold {report.hitboxCoverage:P0} of the head and torso (want {HitboxCoverageMin:P0}).");
            foreach (var d in report.dangling)
                report.failures.Add("Dangling reference: " + d);

            report.pass = report.humanoid && report.failures.Count == 0;
        }

        // ---- the posed character -----------------------------------------------------------------

        /// <summary>One foot's and the whole body's extent in one pose, in world metres.</summary>
        public struct Pose
        {
            public float Left;          // the left foot's lowest point
            public float Right;         // the right foot's lowest point
            public float Lowest;        // the lowest point of anything
            public string LowestPart;   // the bone that carries it
            public float Top;           // the highest point of anything

            /// <summary>The lower foot: the one on the ground.</summary>
            public float Planted => Mathf.Min(Left, Right);
        }

        /// <summary>A character instance in its own preview scene, ready to pose and measure.</summary>
        public sealed class Subject : IDisposable
        {
            public Scene Scene { get; private set; }
            public GameObject Instance { get; private set; }
            public Animator Animator { get; private set; }

            readonly List<SkinnedMeshRenderer> _renderers = new List<SkinnedMeshRenderer>();
            readonly List<sbyte[]> _sides = new List<sbyte[]>();   // per vertex: 0 body, 1 left foot, 2 right foot
            readonly List<int[]> _dominant = new List<int[]>();    // per vertex: the bone with the most weight
            readonly List<Vector3> _vertices = new List<Vector3>();
            Mesh _baked;

            /// <summary>
            /// Instantiates <paramref name="asset"/> at the origin, with <paramref name="avatar"/> if one
            /// is given. Returns null, with the reason in the report, when it can't be measured.
            /// </summary>
            public static Subject Create(GameObject asset, Avatar avatar, Report report)
            {
                var subject = new Subject { Scene = EditorSceneManager.NewPreviewScene(), _baked = new Mesh() };
                subject.Instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, subject.Scene);
                subject.Instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                var animator = FindDrivingAnimator(subject.Instance);
                if (animator != null && avatar != null)
                    animator.avatar = avatar;
                if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                {
                    report.failures.Add("No valid humanoid Animator on the target.");
                    subject.Dispose();
                    return null;
                }

                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                subject.Animator = animator;

                // Only the character's body when the prefab names it - a player prefab also carries
                // first-person arms, weapons and the like.
                var body = BodyRoot(subject.Instance);
                if (body != subject.Instance.transform)
                    report.notes.Add("Measured the body under '" + body.name + "'.");

                subject.Classify(body, report);
                if (subject._renderers.Count == 0)
                {
                    report.failures.Add("No enabled skinned meshes.");
                    subject.Dispose();
                    return null;
                }

                if (!subject._sides.Any(s => s.Contains((sbyte)1)) || !subject._sides.Any(s => s.Contains((sbyte)2)))
                    report.notes.Add("No vertices found on one of the feet; the soles fall back to the whole body.");
                return subject;
            }

            /// <summary>
            /// Sorts every vertex onto the left foot, the right foot or the rest of the body, by the bone
            /// that carries most of its weight. A mesh whose weights can't be read (Read/Write off) is
            /// sorted by position instead, in the rest pose: the bottom 15 cm, by the nearer foot.
            /// </summary>
            void Classify(Transform body, Report report)
            {
                var left = Chain(HumanBodyBones.LeftFoot);
                var right = Chain(HumanBodyBones.RightFoot);
                var unweighted = new List<int>();
                foreach (var r in body.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (!r.enabled || r.sharedMesh == null)
                        continue;

                    BoneWeight[] weights;
                    try
                    {
                        weights = r.sharedMesh.boneWeights;
                    }
                    catch (Exception)
                    {
                        weights = Array.Empty<BoneWeight>();
                    }

                    var bones = r.bones;
                    int count = r.sharedMesh.vertexCount;
                    var side = new sbyte[count];
                    var dominant = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        int b = i < weights.Length ? Dominant(weights[i]) : -1;
                        dominant[i] = b;
                        var bone = b >= 0 && b < bones.Length ? bones[b] : null;
                        side[i] = bone == null ? (sbyte)0 : left.Contains(bone) ? (sbyte)1 : right.Contains(bone) ? (sbyte)2 : (sbyte)0;
                    }

                    if (weights.Length < count && count > 0)
                        unweighted.Add(_renderers.Count);
                    _renderers.Add(r);
                    _sides.Add(side);
                    _dominant.Add(dominant);
                }

                if (unweighted.Count == 0)
                    return;

                var leftFoot = Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rightFoot = Animator.GetBoneTransform(HumanBodyBones.RightFoot);
                if (leftFoot == null || rightFoot == null)
                    return;

                report.notes.Add("Bone weights unreadable on " + unweighted.Count + " mesh(es); their feet were found by position.");
                float bottom = MeasurePose().Lowest;
                foreach (int r in unweighted)
                {
                    var renderer = _renderers[r];
                    renderer.BakeMesh(_baked, false);
                    _baked.GetVertices(_vertices);
                    var t = renderer.transform;
                    var m = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                    var side = _sides[r];
                    for (int i = 0; i < Mathf.Min(_vertices.Count, side.Length); i++)
                    {
                        var p = m.MultiplyPoint3x4(_vertices[i]);
                        if (p.y > bottom + 0.15f)
                            continue;
                        float toLeft = (p - leftFoot.position).sqrMagnitude, toRight = (p - rightFoot.position).sqrMagnitude;
                        side[i] = toLeft <= toRight ? (sbyte)1 : (sbyte)2;
                    }
                }
            }

            HashSet<Transform> Chain(HumanBodyBones bone)
            {
                var t = Animator.GetBoneTransform(bone);
                return t != null ? new HashSet<Transform>(t.GetComponentsInChildren<Transform>(true)) : new HashSet<Transform>();
            }

            static int Dominant(BoneWeight w)
            {
                int best = w.boneIndex0;
                float weight = w.weight0;
                if (w.weight1 > weight) { best = w.boneIndex1; weight = w.weight1; }
                if (w.weight2 > weight) { best = w.boneIndex2; weight = w.weight2; }
                if (w.weight3 > weight) { best = w.boneIndex3; }
                return best;
            }

            /// <summary>Poses <paramref name="clip"/> at <paramref name="samples"/> evenly spread moments and measures each.</summary>
            public IEnumerable<(double time, Pose pose)> Sample(AnimationClip clip, int samples, bool footIK)
            {
                var graph = PlayableGraph.Create("CharacterFitCheck");
                try
                {
                    var playable = Prepare(graph, clip, footIK);
                    for (int i = 0; i < samples; i++)
                    {
                        double time = clip.length * i / samples;
                        Evaluate(graph, playable, time);
                        yield return (time, MeasurePose());
                    }
                }
                finally
                {
                    graph.Destroy();
                }
            }

            /// <summary>Leaves the character in <paramref name="clip"/>'s pose at <paramref name="time"/>.</summary>
            public void PoseAt(AnimationClip clip, double time, bool footIK)
            {
                var graph = PlayableGraph.Create("CharacterFitCheck Pose");
                try
                {
                    Evaluate(graph, Prepare(graph, clip, footIK), time);
                }
                finally
                {
                    graph.Destroy();
                }
            }

            AnimationClipPlayable Prepare(PlayableGraph graph, AnimationClip clip, bool footIK)
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(graph, "Fit", Animator);
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(footIK);
                output.SetSourcePlayable(playable);
                return playable;
            }

            static void Evaluate(PlayableGraph graph, AnimationClipPlayable playable, double time)
            {
                // Twice, so the clip doesn't treat the jump from the previous time as motion.
                playable.SetTime(time);
                playable.SetTime(time);
                graph.Evaluate(0f);
            }

            /// <summary>
            /// The current pose's extent. Baked without the renderer's scale, the vertices are already at
            /// world size (this model's Blender objects carry scales like 0.37 or 1.16), so only the
            /// renderer's position and rotation place them.
            /// </summary>
            public Pose MeasurePose()
            {
                var pose = new Pose
                {
                    Left = float.PositiveInfinity,
                    Right = float.PositiveInfinity,
                    Lowest = float.PositiveInfinity,
                    Top = float.NegativeInfinity,
                };
                int lowestRenderer = -1, lowestVertex = -1;
                for (int r = 0; r < _renderers.Count; r++)
                {
                    var renderer = _renderers[r];
                    renderer.BakeMesh(_baked, false);
                    _baked.GetVertices(_vertices);
                    var t = renderer.transform;
                    var m = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                    var side = _sides[r];
                    int count = Mathf.Min(_vertices.Count, side.Length);
                    for (int i = 0; i < count; i++)
                    {
                        float y = m.MultiplyPoint3x4(_vertices[i]).y;
                        if (y > pose.Top)
                            pose.Top = y;
                        if (y < pose.Lowest)
                        {
                            pose.Lowest = y;
                            lowestRenderer = r;
                            lowestVertex = i;
                        }

                        if (side[i] == 1)
                        {
                            if (y < pose.Left) pose.Left = y;
                        }
                        else if (side[i] == 2)
                        {
                            if (y < pose.Right) pose.Right = y;
                        }
                    }
                }

                // Without foot vertices, the whole body's lowest point stands in for both soles.
                if (float.IsPositiveInfinity(pose.Left)) pose.Left = pose.Lowest;
                if (float.IsPositiveInfinity(pose.Right)) pose.Right = pose.Lowest;

                if (lowestRenderer >= 0)
                {
                    int b = _dominant[lowestRenderer][lowestVertex];
                    var bones = _renderers[lowestRenderer].bones;
                    pose.LowestPart = b >= 0 && b < bones.Length && bones[b] != null
                        ? bones[b].name
                        : _renderers[lowestRenderer].name;
                }

                return pose;
            }

            /// <summary>
            /// The body's widest point from the root's vertical axis in every <paramref name="step"/>-high
            /// slice of the current pose, bottom to top.
            /// </summary>
            public List<Band> Profile(float step)
            {
                var bands = new SortedDictionary<int, Band>();
                var root = Instance.transform.position;
                foreach (var renderer in _renderers)
                {
                    renderer.BakeMesh(_baked, false);
                    _baked.GetVertices(_vertices);
                    var t = renderer.transform;
                    var m = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                    foreach (var v in _vertices)
                    {
                        var p = m.MultiplyPoint3x4(v) - root;
                        int k = Mathf.FloorToInt(p.y / step);
                        if (!bands.TryGetValue(k, out var band))
                            bands[k] = band = new Band { from = k * step, to = (k + 1) * step };
                        band.radius = Mathf.Max(band.radius, new Vector2(p.x, p.z).magnitude);
                        band.vertices++;
                    }
                }

                return bands.Values.ToList();
            }

            /// <summary>
            /// The share of the vertices carried by <paramref name="bones"/> that lie inside any of
            /// <paramref name="hitboxes"/> in the current pose, or NaN when none are carried by them.
            /// </summary>
            public float Coverage(IList<Collider> hitboxes, HashSet<Transform> bones)
            {
                int inside = 0, total = 0;
                for (int r = 0; r < _renderers.Count; r++)
                {
                    var renderer = _renderers[r];
                    var rendererBones = renderer.bones;
                    var dominant = _dominant[r];
                    renderer.BakeMesh(_baked, false);
                    _baked.GetVertices(_vertices);
                    var t = renderer.transform;
                    var m = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                    for (int i = 0; i < Mathf.Min(_vertices.Count, dominant.Length); i++)
                    {
                        int b = dominant[i];
                        if (b < 0 || b >= rendererBones.Length || rendererBones[b] == null || !bones.Contains(rendererBones[b]))
                            continue;

                        total++;
                        var p = m.MultiplyPoint3x4(_vertices[i]);
                        foreach (var hitbox in hitboxes)
                        {
                            if (Contains(hitbox, p))
                            {
                                inside++;
                                break;
                            }
                        }
                    }
                }

                return total > 0 ? (float)inside / total : float.NaN;
            }

            public void Dispose()
            {
                if (_baked != null)
                    UnityEngine.Object.DestroyImmediate(_baked);
                _baked = null;
                if (Scene.IsValid())
                    EditorSceneManager.ClosePreviewScene(Scene);
            }
        }

        /// <summary>
        /// Every serialized reference on <paramref name="instance"/> that points at an object that no
        /// longer exists (shown as Missing in the Inspector), and every missing script, as
        /// "path/Component.field". Capped, so a badly broken prefab still gives a readable report.
        /// </summary>
        public static List<string> DanglingReferences(GameObject instance, int cap = 40)
        {
            var found = new List<string>();
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            {
                string path = PathOf(t, instance.transform);
                int missingScripts = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                if (missingScripts > 0)
                    found.Add($"{path}: {missingScripts} missing script(s)");

                foreach (var component in t.GetComponents<Component>())
                {
                    if (component == null || component is Transform)
                        continue;

                    var property = new SerializedObject(component).GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType == SerializedPropertyType.ObjectReference &&
                            property.objectReferenceValue == null && property.objectReferenceInstanceIDValue != 0)
                            found.Add($"{path}/{component.GetType().Name}.{property.propertyPath}");
                    }
                }

                if (found.Count >= cap)
                {
                    found.Add("(more not listed)");
                    break;
                }
            }

            return found;
        }

        static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent)
                parts.Add(c.name);
            parts.Add(root.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>The head and torso: the hips and everything under them except the arms and legs.</summary>
        static HashSet<Transform> CoreBones(Animator animator)
        {
            var limbs = new HashSet<Transform>();
            foreach (var bone in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
                         HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg })
            {
                var t = animator.GetBoneTransform(bone);
                if (t != null)
                    limbs.UnionWith(t.GetComponentsInChildren<Transform>(true));
            }

            var core = new HashSet<Transform>();
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips != null)
                core.UnionWith(hips.GetComponentsInChildren<Transform>(true).Where(t => !limbs.Contains(t)));
            return core;
        }

        /// <summary>Whether <paramref name="p"/> lies inside <paramref name="c"/>, worked out from its shape (no physics scene needed).</summary>
        static bool Contains(Collider c, Vector3 p)
        {
            var t = c.transform;
            var s = t.lossyScale;
            switch (c)
            {
                case SphereCollider sphere:
                {
                    float r = sphere.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                    return (p - t.TransformPoint(sphere.center)).sqrMagnitude <= r * r;
                }
                case CapsuleCollider capsule:
                {
                    int d = capsule.direction;
                    float axisScale = Mathf.Abs(d == 0 ? s.x : d == 1 ? s.y : s.z);
                    float radiusScale = d == 0 ? Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))
                        : d == 1 ? Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z))
                        : Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
                    float r = capsule.radius * radiusScale;
                    float half = Mathf.Max(capsule.height * axisScale * 0.5f - r, 0f);
                    var centre = t.TransformPoint(capsule.center);
                    var axis = t.TransformDirection(d == 0 ? Vector3.right : d == 1 ? Vector3.up : Vector3.forward).normalized;
                    float along = Mathf.Clamp(Vector3.Dot(p - centre, axis), -half, half);
                    return (p - (centre + axis * along)).sqrMagnitude <= r * r;
                }
                case BoxCollider box:
                {
                    var local = t.InverseTransformPoint(p) - box.center;
                    var e = box.size * 0.5f;
                    return Mathf.Abs(local.x) <= e.x && Mathf.Abs(local.y) <= e.y && Mathf.Abs(local.z) <= e.z;
                }
                default:
                    return c.bounds.Contains(p);
            }
        }

        /// <summary>The Animator whose avatar drives the visible bones: the root's, else the first child's.</summary>
        static Animator FindDrivingAnimator(GameObject instance)
        {
            var own = instance.GetComponent<Animator>();
            if (own != null && own.avatar != null)
                return own;
            return instance.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.avatar != null) ?? own;
        }

        /// <summary>Names of the child that holds a player prefab's body, in the order they are looked for.</summary>
        static readonly string[] BodyNames = { "CharacterVisual", "Player Render", "Body" };

        static Transform BodyRoot(GameObject instance)
        {
            foreach (var name in BodyNames)
            {
                var found = instance.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
                if (found != null)
                    return found;
            }

            return instance.transform;
        }

        /// <summary>The character's main capsule: the root's upright CapsuleCollider, else the first one on the root.</summary>
        static CapsuleCollider MainCapsule(GameObject instance)
        {
            var onRoot = instance.GetComponents<CapsuleCollider>();
            return onRoot.FirstOrDefault(c => c.direction == 1 && !c.isTrigger) ?? onRoot.FirstOrDefault();
        }

        static AnimationClip LoadClip(string path)
        {
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal));
        }

        static bool RootFromFeet(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            var clips = importer != null ? importer.clipAnimations : null;
            if (clips == null || clips.Length == 0)
                clips = importer != null ? importer.defaultClipAnimations : null;
            return clips != null && clips.Length > 0 && clips[0].heightFromFeet;
        }

        // ---- pictures ----------------------------------------------------------------------------

        /// <summary>
        /// side.png (the first idle pose, large) and poses.png (every clip at its deepest moment, in
        /// the order of the report's table). Best effort: a failure here never fails the check.
        /// </summary>
        static void TryPictures(Subject subject, Report report, AnimationClip idle,
            List<(AnimationClip clip, double time)> deepest, bool footIK, string folder)
        {
            Studio studio = null;
            try
            {
                studio = new Studio(subject.Scene, report);
                if (idle != null)
                {
                    subject.PoseAt(idle, 0, footIK);
                    File.WriteAllBytes(Path.Combine(folder, "side.png"), studio.Shoot(900, 1200).EncodeToPNG());
                }

                if (deepest.Count > 0)
                {
                    const int w = 360, h = 480, columns = 4;
                    int rows = (deepest.Count + columns - 1) / columns;
                    var sheet = new Texture2D(w * columns, h * rows, TextureFormat.RGB24, false);
                    var fill = Enumerable.Repeat(new Color(0.1f, 0.1f, 0.12f), sheet.width * sheet.height).ToArray();
                    sheet.SetPixels(fill);
                    for (int i = 0; i < deepest.Count; i++)
                    {
                        subject.PoseAt(deepest[i].clip, deepest[i].time, footIK);
                        var shot = studio.Shoot(w, h);
                        int x = (i % columns) * w, y = (rows - 1 - i / columns) * h;
                        sheet.SetPixels(x, y, w, h, shot.GetPixels());
                    }

                    sheet.Apply();
                    File.WriteAllBytes(Path.Combine(folder, "poses.png"), sheet.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(sheet);
                }
            }
            catch (Exception e)
            {
                report.notes.Add("Pictures not saved: " + e.Message);
            }
            finally
            {
                studio?.Dispose();
            }
        }

        /// <summary>
        /// A side camera with a light, the ground line (green), the main capsule's bottom and top
        /// (orange) and a ruler every 10 cm, in the subject's preview scene.
        /// </summary>
        sealed class Studio : IDisposable
        {
            readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();
            readonly Camera _camera;
            Texture2D _last;

            public Studio(Scene scene, Report report)
            {
                float top = Mathf.Max(report.meshTop, float.IsNaN(report.capsuleTop) ? 0f : report.capsuleTop, 1f);

                Line(scene, new Vector3(0f, -0.002f, 0f), new Vector3(0.01f, 0.004f, 2.5f), new Color(0.2f, 0.85f, 0.3f));
                if (!float.IsNaN(report.capsuleBottom))
                {
                    Line(scene, new Vector3(0f, report.capsuleBottom, 0f), new Vector3(0.01f, 0.004f, 1.6f), new Color(1f, 0.6f, 0.2f));
                    Line(scene, new Vector3(0f, report.capsuleTop, 0f), new Vector3(0.01f, 0.004f, 1.6f), new Color(1f, 0.6f, 0.2f));
                }

                for (float y = 0f; y <= top + 0.05f; y += 0.1f)
                {
                    bool metre = Mathf.Abs(y - Mathf.Round(y)) < 0.01f;
                    Line(scene, new Vector3(0f, y, -1.15f), new Vector3(0.01f, 0.004f, metre ? 0.3f : 0.12f), Color.white);
                }

                var lightGo = new GameObject("Fit Light");
                SceneManager.MoveGameObjectToScene(lightGo, scene);
                _created.Add(lightGo);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.2f;
                lightGo.transform.rotation = Quaternion.Euler(30f, -60f, 0f);

                var camGo = new GameObject("Fit Camera");
                SceneManager.MoveGameObjectToScene(camGo, scene);
                _created.Add(camGo);
                _camera = camGo.AddComponent<Camera>();
                _camera.scene = scene;
                _camera.orthographic = true;
                _camera.orthographicSize = top * 0.5f + 0.15f;
                _camera.transform.SetPositionAndRotation(new Vector3(6f, top * 0.5f, 0f), Quaternion.Euler(0f, -90f, 0f));
                _camera.nearClipPlane = 0.1f;
                _camera.farClipPlane = 20f;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.16f, 0.17f, 0.2f);
            }

            /// <summary>Renders the scene as it is now. The texture stays valid until the next shot.</summary>
            public Texture2D Shoot(int width, int height)
            {
                var rt = new RenderTexture(width, height, 24);
                try
                {
                    _camera.targetTexture = rt;
                    _camera.Render();
                    var previous = RenderTexture.active;
                    RenderTexture.active = rt;
                    if (_last != null)
                        UnityEngine.Object.DestroyImmediate(_last);
                    _last = new Texture2D(width, height, TextureFormat.RGB24, false);
                    _last.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    _last.Apply();
                    RenderTexture.active = previous;
                    return _last;
                }
                finally
                {
                    _camera.targetTexture = null;
                    UnityEngine.Object.DestroyImmediate(rt);
                }
            }

            void Line(Scene scene, Vector3 centre, Vector3 size, Color colour)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(go, scene);
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.position = centre;
                go.transform.localScale = size;
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                var material = new Material(shader);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
                if (material.HasProperty("_Color")) material.SetColor("_Color", colour);
                go.GetComponent<MeshRenderer>().sharedMaterial = material;
                _created.Add(go);
                _created.Add(material);
            }

            public void Dispose()
            {
                if (_last != null)
                    UnityEngine.Object.DestroyImmediate(_last);
                foreach (var o in _created)
                    if (o != null) UnityEngine.Object.DestroyImmediate(o);
            }
        }

        // ---- reporting ---------------------------------------------------------------------------

        public static string Summary(Report r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Height {r.meshTop:0.000} m, soles {Mm(r.meshBottom)} (idle), hips {r.hipsHeight:0.000} m, humanScale {r.humanScale:0.000}, Foot IK {(r.footIK ? "on" : "off")}");
            if (!float.IsNaN(r.capsuleBottom))
                sb.AppendLine($"Capsule {r.capsuleBottom:0.000}..{r.capsuleTop:0.000} m, radius {r.capsuleRadius:0.000}");
            if (r.hitboxes > 0)
                sb.AppendLine($"Hitboxes {r.hitboxes}, holding {r.hitboxCoverage:P0} of the head and torso");
            foreach (var c in r.clips)
                sb.AppendLine($"  {c.clip} ({c.rootFrom}): deepest {Mm(c.deepest)}, average {Mm(c.plantedMean)}, highest {Mm(c.highest)}; lowest part {c.bodyDeepestPart} {Mm(c.bodyDeepest)}");
            foreach (var f in r.failures)
                sb.AppendLine("  FAIL " + f);
            return sb.ToString().TrimEnd();
        }

        static string Markdown(Report r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Character Fit Check: " + (r.pass ? "PASS" : "FAIL"));
            sb.AppendLine();
            sb.AppendLine($"- Target: `{r.target}`");
            sb.AppendLine($"- When: {r.when}");
            sb.AppendLine($"- Posed with Foot IK {(r.footIK ? "on, as in the game" : "off")}");
            sb.AppendLine($"- Standing height (mesh top, idle): **{r.meshTop:0.000} m**");
            sb.AppendLine($"- Soles, idle: **{Mm(r.meshBottom)}** (below zero = in the ground)");
            sb.AppendLine($"- Hips: {r.hipsHeight:0.000} m; humanScale {r.humanScale:0.000}; feet bottom heights L {r.leftFeetBottomHeight:0.000} / R {r.rightFeetBottomHeight:0.000}");
            if (!float.IsNaN(r.capsuleBottom))
                sb.AppendLine($"- Main capsule: {r.capsuleBottom:0.000} to {r.capsuleTop:0.000} m, radius {r.capsuleRadius:0.000} m");
            if (r.hitboxes > 0)
                sb.AppendLine($"- Hitboxes: {r.hitboxes}, holding {r.hitboxCoverage:P0} of the head and torso");
            sb.AppendLine();
            sb.AppendLine("Soles are the lower foot's lowest point. Deepest is the worst moment of the clip, highest the");
            sb.AppendLine("best (above zero on a run = both feet off the ground). poses.png shows each clip at its deepest.");
            sb.AppendLine();
            sb.AppendLine("| Clip | Root from | Deepest | Average | Highest | Left / right average | Lowest part |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var c in r.clips)
                sb.AppendLine($"| {c.clip}{(c.standing ? " (standing)" : "")} | {c.rootFrom} | {Mm(c.deepest)} | {Mm(c.plantedMean)} | {Mm(c.highest)} | {Mm(c.leftMean)} / {Mm(c.rightMean)} | {c.bodyDeepestPart} {Mm(c.bodyDeepest)} |");
            sb.AppendLine();
            if (r.failures.Count > 0)
            {
                sb.AppendLine("## Failures");
                foreach (var f in r.failures)
                    sb.AppendLine("- " + f);
                sb.AppendLine();
            }

            if (r.bones.Count > 0)
            {
                sb.AppendLine("## Body, first idle pose");
                sb.AppendLine();
                sb.AppendLine("Bone heights: " + string.Join(", ", r.bones.Select(b => $"{b.bone} {b.height:0.000}")) + " m.");
                sb.AppendLine();
                sb.AppendLine("Widest point of each 5 cm slice, from the root's vertical axis (the main capsule's axis):");
                sb.AppendLine();
                sb.AppendLine("| Slice (m) | Widest (m) | Vertices |");
                sb.AppendLine("|---|---|---|");
                foreach (var band in Enumerable.Reverse(r.profile))
                    sb.AppendLine($"| {band.from:0.00}..{band.to:0.00} | {band.radius:0.000} | {band.vertices} |");
                sb.AppendLine();
            }

            if (r.notes.Count > 0)
            {
                sb.AppendLine("## Notes");
                foreach (var n in r.notes)
                    sb.AppendLine("- " + n);
            }

            return sb.ToString();
        }

        public static string Mm(float metres) => float.IsNaN(metres) || float.IsInfinity(metres)
            ? "n/a"
            : (metres * 1000f).ToString("+0.0;-0.0;0.0") + " mm";

        static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }

        public static string ArgValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == flag)
                    return args[i + 1];
            }

            return null;
        }
    }
}
#endif
