#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MalbersAnimations;
using MalbersAnimations.Controller;
using MalbersAnimations.Weapons;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    public static partial class CharacterLiveCheck
    {
        /// <summary>
        /// -livePoses, the hands: where a held weapon sits in the fist, how the fingers close round it,
        /// where the bow string and the arrow meet the drawing hand, and where the hands and the head
        /// meet a carried crate. Each is measured on the player and on Malbers' Steve in the same pose,
        /// in the hand's own frame and in hand lengths, so a small hand holding a sword the way Steve
        /// holds his reads the same as his. Close-up sheets of the hands land with the pose pictures.
        /// </summary>
        partial class Probe
        {
            /// <summary>How far a grip may sit from where Steve's sits in his fist, in hand lengths.</summary>
            const float GripLimit = 0.15f;

            /// <summary>How far a held weapon may be turned from the way Steve holds it (degrees).</summary>
            const float GripTurnLimit = 10f;

            /// <summary>How far the fingers may close differently from Steve's round the same grip (degrees, on average).</summary>
            const float FingerLimit = 12f;

            static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Little" };
            static readonly string[] Joints = { "Proximal", "Intermediate", "Distal" };

            readonly List<string> _gripRows = new List<string>();
            readonly List<string> _crateRows = new List<string>();
            readonly List<string> _handNotes = new List<string>();

            /// <summary>Each weapon's long axis in its own space, taken the first time it is measured (before an arrow sits on a string).</summary>
            static readonly Dictionary<string, Vector3> LongAxes = new Dictionary<string, Vector3>();

            float _bothDrawnSince = -1f;
            float _bowLoosed;

            /// <summary>True once both bodies have held their bow's string at the hand for this long.</summary>
            bool BowsDrawnFor(float seconds)
            {
                bool both = Bodies().All(b => b.GetComponentInChildren<MWeaponManager>(true)?.Weapon is MBow bow && bow.KnotToHand);
                if (!both)
                {
                    _bothDrawnSince = -1f;
                    return false;
                }

                if (_bothDrawnSince < 0f)
                    _bothDrawnSince = Now;
                return Now - _bothDrawnSince >= seconds;
            }

            /// <summary>A hand's own frame in a pose.</summary>
            struct HandFrame
            {
                public bool ok;
                public Vector3 wrist, a, b, c, fist;
                public float length;

                /// <summary>
                /// A point in hand lengths: along the hand (wrist to the middle knuckle), across the palm
                /// (toward the index finger) and out of the palm's plane.
                /// </summary>
                public Vector3 Point(Vector3 world) => Direction(world - wrist) / length;

                public Vector3 Direction(Vector3 v) => new Vector3(Vector3.Dot(v, a), Vector3.Dot(v, b), Vector3.Dot(v, c));
            }

            static Transform Bone(Animator animator, bool left, string name)
            {
                if (animator == null || !animator.isHuman)
                    return null;
                var bone = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), (left ? "Left" : "Right") + name);
                return animator.GetBoneTransform(bone);
            }

            /// <summary>
            /// The hand's frame now: the wrist, the middle knuckle, the index and little knuckles, and the
            /// middle of the fist - the average of the four fingers' joints, round which a grip closes.
            /// </summary>
            static HandFrame FrameOf(Animator animator, bool left)
            {
                var f = new HandFrame();
                var hand = Bone(animator, left, "Hand");
                var middle = Bone(animator, left, "MiddleProximal");
                var index = Bone(animator, left, "IndexProximal");
                var little = Bone(animator, left, "LittleProximal");
                if (hand == null || middle == null || index == null || little == null)
                    return f;

                f.wrist = hand.position;
                var along = middle.position - f.wrist;
                f.length = along.magnitude;
                if (f.length < 1e-4f)
                    return f;
                f.a = along / f.length;
                var across = Vector3.ProjectOnPlane(index.position - little.position, f.a);
                if (across.sqrMagnitude < 1e-10f)
                    return f;
                f.b = across.normalized;
                f.c = Vector3.Cross(f.a, f.b);

                var sum = Vector3.zero;
                int n = 0;
                foreach (var finger in FingerNames.Skip(1))
                {
                    foreach (var joint in Joints)
                    {
                        var t = Bone(animator, left, finger + joint);
                        if (t == null)
                            continue;
                        sum += t.position;
                        n++;
                    }
                }

                f.fist = n > 0 ? sum / n : f.wrist + f.a * f.length;
                f.ok = true;
                return f;
            }

            /// <summary>Each finger bone's direction in the hand's frame (two per finger).</summary>
            static Dictionary<string, Vector3> FingerDirections(Animator animator, bool left, HandFrame f)
            {
                var result = new Dictionary<string, Vector3>();
                foreach (var finger in FingerNames)
                {
                    var joints = Joints.Select(j => Bone(animator, left, finger + j)).ToArray();
                    for (int i = 0; i + 1 < joints.Length; i++)
                    {
                        if (joints[i] != null && joints[i + 1] != null)
                            result[finger + " " + (i + 1)] = f.Direction(joints[i + 1].position - joints[i].position).normalized;
                    }
                }

                return result;
            }

            /// <summary>The weapon's longest axis, in its own space, pointing to its longer end; the arrow on the string left out.</summary>
            static Vector3 LongAxis(Transform weapon, Transform leaveOut)
            {
                var min = Vector3.positiveInfinity;
                var max = Vector3.negativeInfinity;

                void Add(Transform t, Bounds b)
                {
                    if (leaveOut != null && t.IsChildOf(leaveOut))
                        return;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = b.center + Vector3.Scale(b.extents,
                            new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                        var p = weapon.InverseTransformPoint(t.TransformPoint(corner));
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                }

                foreach (var renderer in weapon.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null)
                        Add(renderer.transform, filter.sharedMesh.bounds);
                }

                foreach (var skinned in weapon.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (skinned.sharedMesh != null)
                        Add(skinned.transform, skinned.sharedMesh.bounds);
                }

                if (min.x > max.x)
                    return Vector3.up;
                var size = max - min;
                int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
                var direction = Vector3.zero;
                direction[axis] = Mathf.Abs(max[axis]) >= Mathf.Abs(min[axis]) ? 1f : -1f;
                return direction;
            }

            /// <summary>The weapon in one body's hand, measured in that hand's frame.</summary>
            struct Held
            {
                public bool ok;
                public string note;
                public string weapon;
                public bool left;
                public HandFrame hand;
                public Vector3 grip;      // the grip (the weapon's origin) from the middle of the fist, in hand lengths
                public Vector3 axis;      // the weapon's long axis, in the hand's frame
                public float axisMiss;    // how far that axis passes from the middle of the fist, in hand lengths
                public Dictionary<string, Vector3> fingers;
            }

            static Animator AnimatorOf(MAnimal body) => body != null ? body.GetComponentInChildren<Animator>() : null;

            static Held MeasureHeld(MAnimal body)
            {
                var held = new Held();
                var weapons = body != null ? body.GetComponentInChildren<MWeaponManager>(true) : null;
                var weapon = weapons != null ? weapons.Weapon : null;
                if (weapon == null)
                {
                    held.note = "nothing in hand";
                    return held;
                }

                held.weapon = weapon.name.Replace("(Clone)", "").Trim();
                held.left = !weapon.IsRightHanded;
                var animator = AnimatorOf(body);
                held.hand = FrameOf(animator, held.left);
                if (!held.hand.ok)
                {
                    held.note = "no hand and finger bones";
                    return held;
                }

                var shootable = weapon as MShootable;
                var arrow = shootable != null && shootable.ProjectileInstance != null ? shootable.ProjectileInstance.transform : null;
                var t = weapon.transform;
                if (!LongAxes.TryGetValue(held.weapon, out var local))
                    LongAxes[held.weapon] = local = LongAxis(t, arrow);
                var axis = t.TransformDirection(local).normalized;
                held.grip = held.hand.Direction(t.position - held.hand.fist) / held.hand.length;
                held.axis = held.hand.Direction(axis);
                held.axisMiss = Vector3.ProjectOnPlane(held.hand.fist - t.position, axis).magnitude / held.hand.length;
                held.fingers = FingerDirections(animator, held.left, held.hand);
                held.ok = true;
                return held;
            }

            static string Cm(float metres) => $"{metres * 100f:0.0} cm";

            /// <summary>The weapon in hand on the player and on Steve, side by side; a failure when the player's grip is off his.</summary>
            void MeasureGrips(string pose)
            {
                var ours = MeasureHeld(_animal);
                var his = MeasureHeld(_steve);
                if (!ours.ok || !his.ok)
                {
                    _handNotes.Add($"{pose}: not measured (player: {ours.note ?? "ok"}, Steve: {his.note ?? "ok"}).");
                    return;
                }

                float off = (ours.grip - his.grip).magnitude;
                float turn = Vector3.Angle(ours.axis, his.axis);
                var shared = ours.fingers.Keys.Intersect(his.fingers.Keys).ToList();
                var bends = shared.Select(k => (k, angle: Vector3.Angle(ours.fingers[k], his.fingers[k]))).ToList();
                float fingerMean = bends.Count > 0 ? bends.Average(x => x.angle) : 0f;
                var worstFinger = bends.Count > 0 ? bends.OrderByDescending(x => x.angle).First() : (k: "-", angle: 0f);
                bool pass = off <= GripLimit && turn <= GripTurnLimit && fingerMean <= FingerLimit;

                _gripRows.Add($"| {pose} | {ours.weapon}, {(ours.left ? "left" : "right")} hand | " +
                              $"{Cm(ours.grip.magnitude * ours.hand.length)} ({ours.grip.magnitude:0.00} hand) | " +
                              $"{Cm(his.grip.magnitude * his.hand.length)} ({his.grip.magnitude:0.00} hand) | " +
                              $"{Cm(ours.axisMiss * ours.hand.length)} / {Cm(his.axisMiss * his.hand.length)} | " +
                              $"**{Cm(off * ours.hand.length)}** ({off:0.00} hand) | **{turn:0.0}°** | " +
                              $"{fingerMean:0.0}° (worst {worstFinger.k} {worstFinger.angle:0}°) | {(pass ? "PASS" : "FAIL")} |");
                if (!pass)
                {
                    _failures.Add($"Hands, {pose}: the {ours.weapon} sits {Cm(off * ours.hand.length)} from where Steve holds his " +
                                  $"(limit {GripLimit:0.00} of the hand, {Cm(GripLimit * ours.hand.length)}), turned {turn:0}° " +
                                  $"(limit {GripTurnLimit:0}°), fingers {fingerMean:0}° off his (limit {FingerLimit:0}°).");
                }
            }

            /// <summary>
            /// While the bow is drawn: the string's knot and the arrow against the drawing hand, on the
            /// player and on Steve.
            /// </summary>
            /// <summary>Both ends of an object along its long axis, in the world.</summary>
            static (Vector3 a, Vector3 b) Ends(Transform t)
            {
                var axis = LongAxis(t, null);
                float min = float.MaxValue, max = float.MinValue;

                void Add(Transform part, Bounds bounds)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = bounds.center + Vector3.Scale(bounds.extents,
                            new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                        float along = Vector3.Dot(t.InverseTransformPoint(part.TransformPoint(corner)), axis);
                        min = Mathf.Min(min, along);
                        max = Mathf.Max(max, along);
                    }
                }

                foreach (var renderer in t.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null)
                        Add(renderer.transform, filter.sharedMesh.bounds);
                }

                if (min > max)
                    return (t.position, t.position);
                return (t.TransformPoint(axis * min), t.TransformPoint(axis * max));
            }

            void MeasureString(string pose)
            {
                (bool ok, Vector3 knot, float nock, bool hasArrow, float length, string note) One(MAnimal body)
                {
                    var weapons = body != null ? body.GetComponentInChildren<MWeaponManager>(true) : null;
                    var bow = weapons != null ? weapons.Weapon as MBow : null;
                    if (bow == null || bow.knot == null)
                        return (false, default, 0f, false, 0f, "no bow in hand");
                    var hand = FrameOf(AnimatorOf(body), !bow.IsLeftHanded);
                    if (!hand.ok)
                        return (false, default, 0f, false, 0f, "no hand bones");
                    var knot = hand.Direction(bow.knot.position - hand.fist) / hand.length;
                    // The arrow's nock: whichever end of its shaft is nearer the string.
                    var projectile = bow.ProjectileInstance;
                    float nock = 0f;
                    if (projectile != null)
                    {
                        var (a, b) = Ends(projectile.transform);
                        nock = Mathf.Min(Vector3.Distance(a, bow.knot.position), Vector3.Distance(b, bow.knot.position));
                    }

                    return (true, knot, nock, projectile != null, hand.length, null);
                }

                var ours = One(_animal);
                var his = One(_steve);
                if (!ours.ok || !his.ok)
                {
                    _handNotes.Add($"{pose}: the string was not measured (player: {ours.note ?? "ok"}, Steve: {his.note ?? "ok"}).");
                    return;
                }

                float knotOff = (ours.knot - his.knot).magnitude;
                string arrow = ours.hasArrow && his.hasArrow
                    ? $"the arrow's nock {Cm(ours.nock)} from the string (Steve's {Cm(his.nock)})"
                    : $"arrow on the string: player {(ours.hasArrow ? "yes" : "no")}, Steve {(his.hasArrow ? "yes" : "no")}";
                bool pass = knotOff <= GripLimit;
                _gripRows.Add($"| {pose} | the bow string, drawing hand | {Cm(ours.knot.magnitude * ours.length)} from the fist | " +
                              $"{Cm(his.knot.magnitude * his.length)} from the fist | - | **{Cm(knotOff * ours.length)}** ({knotOff:0.00} hand) | - | {arrow} | " +
                              $"{(pass ? "PASS" : "FAIL")} |");
                if (!pass)
                    _failures.Add($"Hands, {pose}: the bow string sits {Cm(knotOff * ours.length)} from where Steve's sits in his drawing hand " +
                                  $"(limit {GripLimit:0.00} of the hand).");
                if (ours.hasArrow && his.hasArrow && ours.nock > his.nock + 0.03f)
                    _failures.Add($"Hands, {pose}: the arrow's nock is {Cm(ours.nock)} from the string, Steve's {Cm(his.nock)}.");
            }

            /// <summary>Signed distance from a point to a box collider's surface: negative inside.</summary>
            static float SignedDistance(BoxCollider box, Vector3 world)
            {
                var t = box.transform;
                var local = t.InverseTransformPoint(world) - box.center;
                var half = box.size * 0.5f;
                var scale = t.lossyScale;
                var q = new Vector3((Mathf.Abs(local.x) - half.x) * Mathf.Abs(scale.x),
                                    (Mathf.Abs(local.y) - half.y) * Mathf.Abs(scale.y),
                                    (Mathf.Abs(local.z) - half.z) * Mathf.Abs(scale.z));
                return Vector3.Max(q, Vector3.zero).magnitude + Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
            }

            /// <summary>Each body's hands and head against the crate it carries.</summary>
            void MeasureCrate(string pose)
            {
                foreach (var body in Bodies())
                {
                    var picker = body.GetComponentInChildren<MPickUp>(true);
                    var item = picker != null && picker.Has_Item ? picker.Item.gameObject : null;
                    var box = item != null ? item.GetComponentInChildren<BoxCollider>(true) : null;
                    var animator = AnimatorOf(body);
                    if (box == null || animator == null)
                    {
                        _handNotes.Add($"{pose}: {body.name} holds no crate.");
                        continue;
                    }

                    string Hand(bool left)
                    {
                        var f = FrameOf(animator, left);
                        if (!f.ok)
                            return "-";
                        var palm = f.wrist + f.a * (0.5f * f.length);
                        float nearest = float.MaxValue;
                        foreach (var finger in FingerNames)
                        {
                            foreach (var joint in Joints)
                            {
                                var bone = Bone(animator, left, finger + joint);
                                if (bone != null)
                                    nearest = Mathf.Min(nearest, SignedDistance(box, bone.position));
                            }
                        }

                        return $"palm {Cm(SignedDistance(box, palm))}, nearest finger {Cm(nearest)}";
                    }

                    var head = animator.GetBoneTransform(HumanBodyBones.Head);
                    var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
                    string Inside(Transform t) => t == null ? "-" : SignedDistance(box, t.position) < 0f ? $"**inside** ({Cm(SignedDistance(box, t.position))})" : Cm(SignedDistance(box, t.position));
                    _crateRows.Add($"| {(body == _animal ? "player" : "Steve")} | {Hand(true)} | {Hand(false)} | {Inside(head)} | {Inside(neck)} |");
                    if (body == _animal && head != null && SignedDistance(box, head.position) < 0f)
                        _failures.Add($"Hands, {pose}: the player's head is inside the crate it carries.");
                }
            }

            /// <summary>
            /// Close-ups of one hand of each body: the player on the top row, Steve below; from the front,
            /// the hand's outer side, above and behind. The camera stands five hand lengths away, so both
            /// hands fill the picture alike.
            /// </summary>
            void HandSheet(string title, bool left)
            {
                if (_poseCamera == null)
                    return;

                float near = _poseCamera.nearClipPlane;
                _poseCamera.nearClipPlane = 0.01f;
                var sheet = new Texture2D(Cell * 4, Cell * 2, TextureFormat.RGB24, false);
                var rt = RenderTexture.GetTemporary(Cell, Cell, 24, RenderTextureFormat.ARGB32);
                var before = RenderTexture.active;
                try
                {
                    int row = 1;
                    foreach (var body in new[] { _animal, _steve })
                    {
                        var f = FrameOf(AnimatorOf(body), left);
                        if (body != null && f.ok)
                        {
                            var t = body.transform;
                            var forward = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
                            var outward = Vector3.Cross(Vector3.up, forward) * (left ? -1f : 1f);
                            var at = (f.fist + f.wrist) * 0.5f;
                            float d = 5f * f.length;
                            View(rt, sheet, 0, row, at + (forward + Vector3.up * 0.3f).normalized * d, at);
                            View(rt, sheet, 1, row, at + (outward + Vector3.up * 0.2f).normalized * d, at);
                            View(rt, sheet, 2, row, at + (Vector3.up + forward * 0.25f).normalized * d, at);
                            View(rt, sheet, 3, row, at + (-forward + outward * 0.6f + Vector3.up * 0.3f).normalized * d, at);
                        }

                        row--;
                    }
                }
                finally
                {
                    RenderTexture.active = before;
                    _poseCamera.targetTexture = null;
                    _poseCamera.nearClipPlane = near;
                    RenderTexture.ReleaseTemporary(rt);
                }

                sheet.Apply();
                string file = Path.Combine(_job.folder, "poses", title + ".png");
                File.WriteAllBytes(file, sheet.EncodeToPNG());
                Object.DestroyImmediate(sheet);
                _poseSheets.Add(file);
            }

            void WriteHands(StringBuilder md)
            {
                if (_gripRows.Count == 0 && _crateRows.Count == 0 && _handNotes.Count == 0)
                    return;

                md.AppendLine("## Hands");
                md.AppendLine("Measured in each hand's own frame and in hand lengths (wrist to middle knuckle), against Malbers' Steve " +
                              "in the same pose: \"off Steve's\" is how far the player's grip sits from where Steve's sits in his fist, " +
                              "scaled to the player's hand. The middle of the fist is the average of the four fingers' joints.");
                foreach (var note in _handNotes)
                    md.AppendLine("- " + note);
                md.AppendLine();
                if (_gripRows.Count > 0)
                {
                    md.AppendLine("| Pose | Held | Player: grip from the fist | Steve: grip from the fist | Long axis passes the fist (player / Steve) | Off Steve's | Turned from Steve's | Fingers against Steve's | |");
                    md.AppendLine("|---|---|---|---|---|---|---|---|---|");
                    foreach (var row in _gripRows)
                        md.AppendLine(row);
                    md.AppendLine();
                    md.AppendLine($"Limits: off Steve's {GripLimit:0.00} of the hand, turned {GripTurnLimit:0}°, fingers {FingerLimit:0}° on average.");
                    md.AppendLine();
                }

                if (_crateRows.Count > 0)
                {
                    md.AppendLine("Malbers' crate, carried: each part's distance from the crate's surface (negative: inside it).");
                    md.AppendLine();
                    md.AppendLine("| Body | Left hand | Right hand | Head | Neck |");
                    md.AppendLine("|---|---|---|---|---|");
                    foreach (var row in _crateRows)
                        md.AppendLine(row);
                    md.AppendLine();
                }
            }
        }
    }
}
#endif
