using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Does the player move like Malbers' Steve? Plays every humanoid clip of the player's Malbers
    /// controller on the player's body and on Steve's own body (Steve AI) at the same moments, and
    /// measures how far each bone is from Steve's:
    /// - limbs (upper arm, forearm, hand, thigh, shin): the way the limb points, against the
    ///   character, in degrees;
    /// - every bone: its whole turn away from the T-pose (twist included), in degrees.
    /// Pure animation, the retargeting itself: no IK, no Malbers scripts. The IK-driven poses (aim,
    /// carry, head look) are the live check's pose pictures (-livePoses).
    ///
    /// Also compares the two avatars' T-poses (the way each limb points when standing in it), and
    /// draws a sheet of chosen clips: in each cell the player on the left, Steve on the right.
    ///
    /// Menu: Tools > CollarCali > Character > Animation Match (every Malbers clip vs Steve).
    /// Batch: -executeMethod CollarCali.EditorTools.AnimationMatchCheck.RunBatch -quit
    /// Report: Builds/CharacterFit/&lt;time&gt;-animation-match: match.md, match.csv, match-*.png.
    /// </summary>
    public static class AnimationMatchCheck
    {
        const string PlayerPath = "Assets/_Integration/Characters/Player/PlayerCharacter.prefab";
        const string StevePath = "Assets/Malbers Animations/Animal Controller/Human/Steve AI.prefab";

        /// <summary>Degrees: a bone this close to Steve's reads as the same pose.</summary>
        const float Same = 5f;

        /// <summary>Degrees: a bone this far from Steve's is visibly a different pose.</summary>
        const float Visible = 15f;

        const int CellWidth = 300;
        const int CellHeight = 400;

        /// <summary>Limbs compared by the way they point: (bone, the bone it points to, name).</summary>
        static readonly (HumanBodyBones from, HumanBodyBones to, string name)[] Limbs =
        {
            (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, "left upper arm"),
            (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, "left forearm"),
            (HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, "left hand"),
            (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, "right upper arm"),
            (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, "right forearm"),
            (HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, "right hand"),
            (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, "left thigh"),
            (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, "left shin"),
            (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, "right thigh"),
            (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, "right shin"),
        };

        /// <summary>Bones compared by their whole turn away from the T-pose.</summary>
        static readonly HumanBodyBones[] Turned =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
            HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
        };

        /// <summary>Clips drawn on the sheet when the controller has them: idle, moving, jumping, fighting, climbing.</summary>
        static readonly string[] SheetClips =
        {
            "H_Idle2", "S_Walk_F", "S_Jog_F", "S_Sprint", "Jump_Run_TakeOff", "H_Fall_High",
            "S_Land_Easy", "S_Crouch_Walk_Forward", "Idle_Combat", "Human_SwordOneHand_Right", "Sword Attack Right Forward RH", "H_Punching Right",
            "H_Bow_N_Hold_1", "H_Bow_N_Fire", "H_Kick Right", "H_Free_Climb_North", "H_Ledge Grab", "S_Roll",
        };

        /// <summary>The T-pose table: the limbs, and the rest of the body for comparison.</summary>
        static readonly (HumanBodyBones from, HumanBodyBones to, string name)[] TPoseParts =
        {
            (HumanBodyBones.Hips, HumanBodyBones.Spine, "hips to spine"),
            (HumanBodyBones.Spine, HumanBodyBones.Chest, "spine"),
            (HumanBodyBones.Chest, HumanBodyBones.UpperChest, "chest"),
            (HumanBodyBones.UpperChest, HumanBodyBones.Neck, "upper chest"),
            (HumanBodyBones.Neck, HumanBodyBones.Head, "neck"),
            (HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, "left shoulder"),
            (HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, "right shoulder"),
            (HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, "left foot"),
            (HumanBodyBones.RightFoot, HumanBodyBones.RightToes, "right foot"),
        };

        [MenuItem(CharacterFitCheck.MenuRoot + "Animation Match (every Malbers clip vs Steve)")]
        static void FromMenu()
        {
            string folder = Run(out bool passed);
            if (folder != null)
                EditorUtility.RevealInFinder(Path.Combine(folder, "match.md"));
            Debug.Log("Animation match: " + (passed ? "PASS" : "see the report") + " - " + folder);
        }

        /// <summary>Batch entry. Exit code 0 = every clip within <see cref="Visible"/> degrees everywhere, 1 = not, 3 = could not run.</summary>
        public static void RunBatch()
        {
            int code;
            try
            {
                string folder = Run(out bool passed);
                code = folder == null ? 3 : passed ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 3;
            }

            EditorApplication.Exit(code);
        }

        /// <summary>
        /// Diagnostic, batch: -executeMethod CollarCali.EditorTools.AnimationMatchCheck.RunCandidatesBatch.
        /// Tries the player model posed like Steve in more places (shoulders, spine), on avatars built in
        /// memory, and measures every clip with each. Changes no asset. Report:
        /// Builds/CharacterFit/&lt;time&gt;-animation-candidates/candidates.md.
        /// </summary>
        public static void RunCandidatesBatch()
        {
            int code = 0;
            try
            {
                RunCandidates();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 3;
            }

            EditorApplication.Exit(code);
        }

        static void RunCandidates()
        {
            var problems = new List<string>();
            string folder = CharacterFitCheck.NewReportFolder("animation-candidates");
            var md = new StringBuilder();
            md.AppendLine("# Animation match: the player posed like Steve in more places");
            md.AppendLine();
            md.AppendLine($"- When: {DateTime.Now:u}");
            md.AppendLine("- Each row: the player model's avatar with those parts posed like Steve's T-pose (built in memory), every clip measured as in match.md.");
            md.AppendLine();

            var player = Body.Load(PlayerPath, "player", problems);
            var steve = Body.Load(StevePath, "Steve", problems);
            var imported = player?.animator.avatar;
            var built = new List<Avatar>();
            try
            {
                if (player == null || steve == null)
                {
                    md.AppendLine("Could not run: " + string.Join(" ", problems));
                    return;
                }

                var clips = Clips(player);
                md.AppendLine($"{clips.Count} humanoid clips.");
                md.AppendLine();
                md.AppendLine("| Posed like Steve | Same | Close | Visible | Mean ° | Worst ° (clip) | Worst parts (mean °) |");
                md.AppendLine("|---|---:|---:|---:|---:|---|---|");

                var candidates = new[]
                {
                    CharacterTPoseMatcher.Parts.Default,
                    CharacterTPoseMatcher.Parts.Default | CharacterTPoseMatcher.Parts.Shoulders,
                    CharacterTPoseMatcher.Parts.Default | CharacterTPoseMatcher.Parts.Spine,
                    CharacterTPoseMatcher.Parts.Default | CharacterTPoseMatcher.Parts.Shoulders | CharacterTPoseMatcher.Parts.Spine,
                };
                var log = new StringBuilder();
                foreach (var parts in candidates)
                {
                    log.AppendLine("## " + parts);
                    if (!CharacterTPoseMatcher.TryCandidate(parts, out var description, log))
                    {
                        md.AppendLine($"| {parts} | - | - | - | - | could not build | |");
                        continue;
                    }

                    var avatar = CharacterTPoseMatcher.BuildAvatar(description, parts.ToString());
                    built.Add(avatar);
                    if (avatar == null || !avatar.isValid || !avatar.isHuman)
                    {
                        md.AppendLine($"| {parts} | - | - | - | - | invalid avatar | |");
                        continue;
                    }

                    player.Bind(avatar);
                    var results = new List<ClipResult>();
                    var stats = new Dictionary<string, PartStats>();
                    var csv = new StringBuilder();
                    foreach (var clip in clips)
                        results.Add(Compare(clip, player, steve, stats, csv));

                    int same = results.Count(r => r.worst <= Same);
                    int visible = results.Count(r => r.worst > Visible);
                    var worst = results.OrderByDescending(r => r.worst).First();
                    string parts3 = string.Join(", ", stats.OrderByDescending(x => x.Value.Mean).Take(3).Select(x => $"{x.Key} {x.Value.Mean:0.0}"));
                    md.AppendLine($"| {parts} | {same} | {results.Count - same - visible} | {visible} | " +
                                  $"{results.Sum(r => r.sum) / Math.Max(1, results.Sum(r => r.count)):0.00} | {worst.worst:0.0} ({worst.clip.name}) | {parts3} |");
                    File.WriteAllText(Path.Combine(folder, $"candidate-{(int)parts}.csv"), "clip,time,part,kind,degrees\n" + csv);
                }

                md.AppendLine();
                md.AppendLine("Same: every bone within " + Same + "° throughout; Visible: over " + Visible + "° somewhere.");
                md.AppendLine();
                md.AppendLine("## Turns per candidate");
                md.AppendLine();
                md.AppendLine("```");
                md.Append(log);
                md.AppendLine("```");
            }
            finally
            {
                if (player != null && imported != null)
                    player.animator.avatar = imported;
                foreach (var avatar in built)
                {
                    if (avatar != null)
                        Object.DestroyImmediate(avatar);
                }

                player?.Dispose();
                steve?.Dispose();
                File.WriteAllText(Path.Combine(folder, "candidates.md"), md.ToString());
            }
        }

        static List<AnimationClip> Clips(Body player)
        {
            var controller = player.animator.runtimeAnimatorController;
            return (controller != null ? controller.animationClips : Array.Empty<AnimationClip>())
                .Where(c => c != null && c.humanMotion)
                .Distinct()
                .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        sealed class Body : IDisposable
        {
            public string label;
            public Scene scene;
            public GameObject instance;
            public Animator animator;
            public readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
            public readonly Dictionary<HumanBodyBones, Quaternion> tposeTurn = new Dictionary<HumanBodyBones, Quaternion>();
            public readonly Dictionary<HumanBodyBones, Vector3> tposeAt = new Dictionary<HumanBodyBones, Vector3>();
            public readonly List<string> notes = new List<string>();
            Camera _camera;

            public static Body Load(string path, string label, List<string> problems)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null)
                {
                    problems.Add(path + " is missing.");
                    return null;
                }

                var body = new Body { label = label, scene = EditorSceneManager.NewPreviewScene() };
                body.instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, body.scene);
                body.instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                // Only the Animator moves the body: no Malbers script runs in a preview scene, and none may
                // touch the bones from an animation callback.
                foreach (var behaviour in body.instance.GetComponentsInChildren<MonoBehaviour>(true))
                    behaviour.enabled = false;

                // Aim lines, trails, effects and UI would cross the pictures.
                foreach (var renderer in body.instance.GetComponentsInChildren<Renderer>(true))
                {
                    // Malbers' Internal Components/Effects (the sword slash) are shown by scripts when they play.
                    if (renderer is LineRenderer || renderer is TrailRenderer || renderer is ParticleSystemRenderer ||
                        Path(renderer.transform).Contains("/Effects/"))
                        renderer.enabled = false;
                    else if (renderer.enabled && renderer.gameObject.activeInHierarchy &&
                             (renderer.bounds.size.x > 1.5f || renderer.bounds.size.z > 1.5f))
                        body.notes.Add($"{label}: wide renderer '{Path(renderer.transform)}' ({renderer.GetType().Name}, " +
                                       $"{renderer.bounds.size.x:0.0} x {renderer.bounds.size.y:0.0} x {renderer.bounds.size.z:0.0} m)");
                }

                foreach (var canvas in body.instance.GetComponentsInChildren<Canvas>(true))
                {
                    canvas.enabled = false;
                    body.notes.Add($"{label}: canvas '{Path(canvas.transform)}' hidden.");
                }

                var own = body.instance.GetComponent<Animator>();
                body.animator = own != null && own.avatar != null
                    ? own
                    : body.instance.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.avatar != null && a.avatar.isHuman);
                if (body.animator == null || body.animator.avatar == null || !body.animator.avatar.isValid || !body.animator.avatar.isHuman)
                {
                    problems.Add(label + ": no valid humanoid Animator.");
                    body.Dispose();
                    return null;
                }

                body.animator.applyRootMotion = false;
                body.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                body.animator.enabled = true;
                body.Bind(body.animator.avatar);
                return body;
            }

            static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

            /// <summary>Drives the body with <paramref name="avatar"/> and reads its bones and its T-pose again.</summary>
            public void Bind(Avatar avatar)
            {
                animator.avatar = avatar;
                animator.Rebind();
                bones.Clear();
                tposeTurn.Clear();
                tposeAt.Clear();
                var root = animator.transform;
                for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                {
                    var t = animator.GetBoneTransform(b);
                    if (t == null)
                        continue;
                    bones[b] = t;
                    var (at, turn) = TPose(root, t, avatar);
                    tposeTurn[b] = turn;
                    tposeAt[b] = at;
                }
            }

            /// <summary>
            /// The bone's place and turn against the Animator in the avatar's T-pose: the bones take the
            /// avatar's stored T-pose, the objects above the model (Rotator, ModelOffset) keep their own.
            /// </summary>
            static (Vector3 at, Quaternion turn) TPose(Transform root, Transform bone, Avatar avatar)
            {
                var stored = new Dictionary<string, SkeletonBone>();
                foreach (var s in avatar.humanDescription.skeleton)
                    stored.TryAdd(s.name, s);

                var chain = new List<Transform>();
                for (var t = bone; t != null && t != root; t = t.parent)
                    chain.Add(t);
                chain.Reverse();

                var matrix = Matrix4x4.identity;
                var turn = Quaternion.identity;
                foreach (var t in chain)
                {
                    bool known = stored.TryGetValue(t.name, out var s);
                    var position = known ? s.position : t.localPosition;
                    var rotation = known ? s.rotation : t.localRotation;
                    var scale = known ? s.scale : t.localScale;
                    matrix *= Matrix4x4.TRS(position, rotation, scale);
                    turn *= rotation;
                }

                return (matrix.MultiplyPoint3x4(Vector3.zero), turn);
            }

            public double Height => tposeAt.TryGetValue(HumanBodyBones.Head, out var head) ? head.y : 0.0;

            public float Length(HumanBodyBones a, HumanBodyBones b, HumanBodyBones c) =>
                tposeAt.ContainsKey(a) && tposeAt.ContainsKey(b) && tposeAt.ContainsKey(c)
                    ? Vector3.Distance(tposeAt[a], tposeAt[b]) + Vector3.Distance(tposeAt[b], tposeAt[c])
                    : 0f;

            /// <summary>The way a limb points in the T-pose, against the character.</summary>
            public bool TPoseDirection(HumanBodyBones from, HumanBodyBones to, out Vector3 direction)
            {
                direction = default;
                if (!tposeAt.TryGetValue(from, out var a) || !tposeAt.TryGetValue(to, out var b))
                    return false;
                direction = (b - a).normalized;
                return true;
            }

            /// <summary>The way a limb points now, against the character.</summary>
            public bool Direction(HumanBodyBones from, HumanBodyBones to, out Vector3 direction)
            {
                direction = default;
                if (!bones.TryGetValue(from, out var a) || !bones.TryGetValue(to, out var b))
                    return false;
                direction = Quaternion.Inverse(animator.transform.rotation) * (b.position - a.position).normalized;
                return true;
            }

            /// <summary>The bone's whole turn away from the T-pose, against the character.</summary>
            public bool TurnFromTPose(HumanBodyBones bone, out Quaternion turn)
            {
                turn = Quaternion.identity;
                if (!bones.TryGetValue(bone, out var t))
                    return false;
                turn = Quaternion.Inverse(animator.transform.rotation) * t.rotation * Quaternion.Inverse(tposeTurn[bone]);
                return true;
            }

            public void Pose(AnimationClip clip, double time)
            {
                var graph = PlayableGraph.Create("AnimationMatchCheck " + label);
                try
                {
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var output = AnimationPlayableOutput.Create(graph, "Match", animator);
                    var playable = AnimationClipPlayable.Create(graph, clip);
                    playable.SetApplyFootIK(false);
                    playable.SetApplyPlayableIK(false);
                    output.SetSourcePlayable(playable);
                    // Twice, so the clip doesn't treat the jump from the previous time as motion.
                    playable.SetTime(time);
                    playable.SetTime(time);
                    graph.Evaluate(0f);
                }
                finally
                {
                    graph.Destroy();
                }
            }

            /// <summary>A picture of the body as it stands now, from the front right, framed on its hips.</summary>
            public Texture2D Shoot(int width, int height)
            {
                if (_camera == null)
                {
                    var lightGo = new GameObject("Match Light");
                    SceneManager.MoveGameObjectToScene(lightGo, scene);
                    var light = lightGo.AddComponent<Light>();
                    light.type = LightType.Directional;
                    light.intensity = 1.2f;
                    lightGo.transform.rotation = Quaternion.Euler(35f, -150f, 0f);

                    var camGo = new GameObject("Match Camera");
                    SceneManager.MoveGameObjectToScene(camGo, scene);
                    _camera = camGo.AddComponent<Camera>();
                    _camera.scene = scene;
                    _camera.fieldOfView = 38f;
                    _camera.nearClipPlane = 0.05f;
                    _camera.farClipPlane = 30f;
                    _camera.clearFlags = CameraClearFlags.SolidColor;
                    _camera.backgroundColor = new Color(0.33f, 0.36f, 0.42f);
                }

                var hips = bones[HumanBodyBones.Hips].position;
                var focus = new Vector3(hips.x, hips.y * 0.95f, hips.z);
                var root = animator.transform;
                var from = focus + root.rotation * new Vector3(2.0f, 0.35f, 3.6f);
                _camera.transform.SetPositionAndRotation(from, Quaternion.LookRotation(focus - from));

                // A skinned mesh is only re-skinned once a frame, and every pose here is taken within one
                // frame: each shot draws the skinned meshes as they are baked in the current pose instead.
                var baked = new List<(SkinnedMeshRenderer skin, GameObject stand, Mesh mesh)>();
                foreach (var skin in instance.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (!skin.enabled || skin.sharedMesh == null)
                        continue;
                    var mesh = new Mesh();
                    skin.BakeMesh(mesh);
                    var stand = new GameObject("Baked " + skin.name);
                    SceneManager.MoveGameObjectToScene(stand, scene);
                    stand.transform.SetPositionAndRotation(skin.transform.position, skin.transform.rotation);
                    stand.AddComponent<MeshFilter>().sharedMesh = mesh;
                    stand.AddComponent<MeshRenderer>().sharedMaterials = skin.sharedMaterials;
                    skin.enabled = false;
                    baked.Add((skin, stand, mesh));
                }

                var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                var previous = RenderTexture.active;
                try
                {
                    _camera.targetTexture = rt;
                    _camera.Render();
                    RenderTexture.active = rt;
                    var picture = new Texture2D(width, height, TextureFormat.RGB24, false);
                    picture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    picture.Apply();
                    return picture;
                }
                finally
                {
                    RenderTexture.active = previous;
                    _camera.targetTexture = null;
                    RenderTexture.ReleaseTemporary(rt);
                    foreach (var (skin, stand, mesh) in baked)
                    {
                        skin.enabled = true;
                        Object.DestroyImmediate(stand);
                        Object.DestroyImmediate(mesh);
                    }
                }
            }

            public void Dispose()
            {
                if (scene.IsValid())
                    EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        sealed class ClipResult
        {
            public AnimationClip clip;
            public float worst;
            public string worstPart;
            public double worstTime;
            public double sum;
            public int count;
            public float Mean => count > 0 ? (float)(sum / count) : 0f;
        }

        sealed class PartStats
        {
            public double sum;
            public int count;
            public float max;
            public string maxClip;
            public float Mean => count > 0 ? (float)(sum / count) : 0f;
        }

        static string Run(out bool passed)
        {
            passed = false;
            var problems = new List<string>();
            string folder = CharacterFitCheck.NewReportFolder("animation-match");
            var md = new StringBuilder();
            md.AppendLine("# Animation match: the player against Malbers' Steve");
            md.AppendLine();
            md.AppendLine($"- When: {DateTime.Now:u}");
            md.AppendLine($"- Player: `{PlayerPath}`");
            md.AppendLine($"- Steve: `{StevePath}` (his own body)");

            var player = Body.Load(PlayerPath, "player", problems);
            var steve = Body.Load(StevePath, "Steve", problems);
            try
            {
                if (player == null || steve == null)
                {
                    md.AppendLine();
                    md.AppendLine("Could not run: " + string.Join(" ", problems));
                    File.WriteAllText(Path.Combine(folder, "match.md"), md.ToString());
                    return null;
                }

                md.AppendLine($"- Avatars: player `{player.animator.avatar.name}`, Steve `{steve.animator.avatar.name}`");
                var controller = player.animator.runtimeAnimatorController;
                md.AppendLine($"- Controller: `{(controller != null ? controller.name : "none")}`" +
                              (steve.animator.runtimeAnimatorController != controller
                                  ? $" (Steve's: `{(steve.animator.runtimeAnimatorController != null ? steve.animator.runtimeAnimatorController.name : "none")}`)"
                                  : " (Steve's too)"));
                md.AppendLine("- Pure animation: no IK, no Malbers scripts. Limbs: the way they point; bones: the whole turn away from the T-pose.");
                foreach (var note in player.notes.Concat(steve.notes))
                    md.AppendLine("- " + note);
                md.AppendLine($"- Same pose: within {Same:0}°. Visibly different: over {Visible:0}°.");
                md.AppendLine();

                WriteBodies(md, player, steve);

                var clips = (controller != null ? controller.animationClips : Array.Empty<AnimationClip>())
                    .Where(c => c != null && c.humanMotion)
                    .Distinct()
                    .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (clips.Count == 0)
                {
                    md.AppendLine("No humanoid clips in the player's controller.");
                    File.WriteAllText(Path.Combine(folder, "match.md"), md.ToString());
                    return null;
                }

                var results = new List<ClipResult>();
                var parts = new Dictionary<string, PartStats>();
                var csv = new StringBuilder("clip,time,part,kind,degrees\n");
                foreach (var clip in clips)
                    results.Add(Compare(clip, player, steve, parts, csv));
                File.WriteAllText(Path.Combine(folder, "match.csv"), csv.ToString());

                int same = results.Count(r => r.worst <= Same);
                int visible = results.Count(r => r.worst > Visible);
                passed = visible == 0;

                md.AppendLine("## Result");
                md.AppendLine();
                md.AppendLine($"**{(passed ? "PASS" : "DIFFERENCES")}**: {results.Count} humanoid clips; " +
                              $"{same} the same pose throughout (every bone within {Same:0}°), " +
                              $"{results.Count - same - visible} close ({Same:0}-{Visible:0}° at worst), " +
                              $"{visible} visibly different somewhere (over {Visible:0}°).");
                md.AppendLine();
                md.AppendLine($"Mean difference over every clip, moment and bone: {results.Sum(r => r.sum) / Math.Max(1, results.Sum(r => r.count)):0.0}°.");
                md.AppendLine();

                md.AppendLine("## By bone (every clip)");
                md.AppendLine();
                md.AppendLine("| Part | Mean ° | Worst ° | Worst in |");
                md.AppendLine("|---|---:|---:|---|");
                foreach (var pair in parts.OrderByDescending(p => p.Value.Mean))
                    md.AppendLine($"| {pair.Key} | {pair.Value.Mean:0.0} | {pair.Value.max:0.0} | {pair.Value.maxClip} |");
                md.AppendLine();

                md.AppendLine("## Clips, most different first");
                md.AppendLine();
                md.AppendLine("| Clip | Worst ° | Where | At s | Mean ° |");
                md.AppendLine("|---|---:|---|---:|---:|");
                foreach (var r in results.OrderByDescending(r => r.worst))
                    md.AppendLine($"| {r.clip.name} | {r.worst:0.0} | {r.worstPart} | {r.worstTime:0.00} | {r.Mean:0.0} |");
                md.AppendLine();

                var sheets = DrawSheets(folder, clips, results, player, steve);
                if (sheets.Count > 0)
                {
                    md.AppendLine("## Pictures");
                    md.AppendLine();
                    md.AppendLine("Each row is one clip at three moments; in each cell the player on the left, Steve on the right.");
                    md.AppendLine();
                    foreach (var sheet in sheets)
                        md.AppendLine($"- `{Path.GetFileName(sheet)}`");
                }

                File.WriteAllText(Path.Combine(folder, "match.md"), md.ToString());
                return folder;
            }
            finally
            {
                player?.Dispose();
                steve?.Dispose();
            }
        }

        static void WriteBodies(StringBuilder md, Body player, Body steve)
        {
            md.AppendLine("## The two bodies");
            md.AppendLine();
            float Ratio(float ours, float theirs) => theirs > 0f ? ours / theirs : 0f;
            float armOurs = player.Length(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
            float armSteve = steve.Length(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
            float legOurs = player.Length(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
            float legSteve = steve.Length(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
            md.AppendLine("| | Player | Steve | Player / Steve |");
            md.AppendLine("|---|---:|---:|---:|");
            md.AppendLine($"| Head height in the T-pose (m) | {player.Height:0.00} | {steve.Height:0.00} | {Ratio((float)player.Height, (float)steve.Height):0.00} |");
            md.AppendLine($"| Arm, shoulder to wrist (m) | {armOurs:0.00} | {armSteve:0.00} | {Ratio(armOurs, armSteve):0.00} |");
            md.AppendLine($"| Leg, hip to ankle (m) | {legOurs:0.00} | {legSteve:0.00} | {Ratio(legOurs, legSteve):0.00} |");
            md.AppendLine($"| Avatar human scale | {player.animator.humanScale:0.00} | {steve.animator.humanScale:0.00} | {Ratio(player.animator.humanScale, steve.animator.humanScale):0.00} |");
            md.AppendLine();

            md.AppendLine("T-poses: the way each limb points when the avatar stands in its stored T-pose. Unity starts every");
            md.AppendLine("animation from this pose, so a limb that points differently here is off by about as much in every clip.");
            md.AppendLine();
            md.AppendLine("| Limb | Player | Steve | Apart ° |");
            md.AppendLine("|---|---|---|---:|");
            foreach (var (from, to, name) in Limbs.Concat(TPoseParts))
            {
                if (!player.TPoseDirection(from, to, out var ours) || !steve.TPoseDirection(from, to, out var theirs))
                {
                    md.AppendLine($"| {name} | - | - | - |");
                    continue;
                }

                md.AppendLine($"| {name} | {V(ours)} | {V(theirs)} | {Vector3.Angle(ours, theirs):0.0} |");
            }

            md.AppendLine();
        }

        static string V(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:0.00}, {1:0.00}, {2:0.00})", v.x, v.y, v.z);

        static int SamplesFor(AnimationClip clip) => Mathf.Clamp(Mathf.CeilToInt(clip.length * 8f), 4, 16);

        static ClipResult Compare(AnimationClip clip, Body player, Body steve, Dictionary<string, PartStats> parts, StringBuilder csv)
        {
            var result = new ClipResult { clip = clip };
            int samples = SamplesFor(clip);
            for (int i = 0; i < samples; i++)
            {
                double time = clip.length * i / samples;
                player.Pose(clip, time);
                steve.Pose(clip, time);

                foreach (var (from, to, name) in Limbs)
                {
                    if (player.Direction(from, to, out var ours) && steve.Direction(from, to, out var theirs))
                        Add(result, parts, csv, clip, time, name, "points", Vector3.Angle(ours, theirs));
                }

                foreach (var bone in Turned)
                {
                    if (player.TurnFromTPose(bone, out var ours) && steve.TurnFromTPose(bone, out var theirs))
                        Add(result, parts, csv, clip, time, Name(bone) + " (turn)", "turn", Quaternion.Angle(ours, theirs));
                }
            }

            return result;
        }

        static void Add(ClipResult result, Dictionary<string, PartStats> parts, StringBuilder csv, AnimationClip clip, double time,
            string part, string kind, float degrees)
        {
            result.sum += degrees;
            result.count++;
            if (degrees > result.worst)
            {
                result.worst = degrees;
                result.worstPart = part;
                result.worstTime = time;
            }

            if (!parts.TryGetValue(part, out var stats))
                parts[part] = stats = new PartStats();
            stats.sum += degrees;
            stats.count++;
            if (degrees > stats.max)
            {
                stats.max = degrees;
                stats.maxClip = clip.name;
            }

            csv.Append('"').Append(clip.name.Replace("\"", "'")).Append("\",")
               .Append(time.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
               .Append(part).Append(',').Append(kind).Append(',')
               .Append(degrees.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
        }

        static string Name(HumanBodyBones bone)
        {
            var words = new StringBuilder();
            foreach (char c in bone.ToString())
            {
                if (char.IsUpper(c) && words.Length > 0)
                    words.Append(' ');
                words.Append(char.ToLowerInvariant(c));
            }

            return words.ToString();
        }

        /// <summary>
        /// Sheets of chosen clips (those in <see cref="SheetClips"/> the controller has) and the
        /// most different clips, six rows a sheet.
        /// </summary>
        static List<string> DrawSheets(string folder, List<AnimationClip> clips, List<ClipResult> results, Body player, Body steve)
        {
            var chosen = new List<AnimationClip>();
            foreach (var wanted in SheetClips)
            {
                var clip = clips.FirstOrDefault(c => c.name == wanted && !chosen.Contains(c));
                if (clip != null)
                    chosen.Add(clip);
            }

            var worst = results.OrderByDescending(r => r.worst).Select(r => r.clip).Where(c => !chosen.Contains(c)).Take(6).ToList();
            var files = new List<string>();
            files.AddRange(Sheets(folder, "match-chosen", chosen, player, steve));
            files.AddRange(Sheets(folder, "match-most-different", worst, player, steve));
            return files;
        }

        static IEnumerable<string> Sheets(string folder, string name, List<AnimationClip> clips, Body player, Body steve)
        {
            const int Rows = 6;
            const int Moments = 3;
            for (int first = 0, page = 1; first < clips.Count; first += Rows, page++)
            {
                var rows = clips.Skip(first).Take(Rows).ToList();
                var sheet = new Texture2D(CellWidth * 2 * Moments, CellHeight * rows.Count, TextureFormat.RGB24, false);
                for (int r = 0; r < rows.Count; r++)
                {
                    var clip = rows[r];
                    for (int m = 0; m < Moments; m++)
                    {
                        double time = clip.length * (m + 0.5) / Moments;
                        player.Pose(clip, time);
                        steve.Pose(clip, time);
                        int y = CellHeight * (rows.Count - 1 - r);
                        Paste(sheet, player.Shoot(CellWidth, CellHeight), CellWidth * 2 * m, y);
                        Paste(sheet, steve.Shoot(CellWidth, CellHeight), CellWidth * (2 * m + 1), y);
                    }
                }

                sheet.Apply();
                string file = Path.Combine(folder, $"{name}-{page}.png");
                File.WriteAllBytes(file, sheet.EncodeToPNG());
                Object.DestroyImmediate(sheet);

                var list = new StringBuilder();
                for (int r = 0; r < rows.Count; r++)
                    list.AppendLine($"{r + 1}. {rows[r].name} ({rows[r].length:0.00} s)");
                File.WriteAllText(Path.ChangeExtension(file, ".txt"), list.ToString());
                yield return file;
            }
        }

        static void Paste(Texture2D sheet, Texture2D picture, int x, int y)
        {
            sheet.SetPixels(x, y, picture.width, picture.height, picture.GetPixels());
            Object.DestroyImmediate(picture);
        }
    }
}
