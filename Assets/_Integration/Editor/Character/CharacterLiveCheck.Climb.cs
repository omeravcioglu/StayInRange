#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MalbersAnimations;
using MalbersAnimations.Controller;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    public static partial class CharacterLiveCheck
    {
        /// <summary>
        /// -livePoses, climbing: the player and Malbers' Steve side by side on the same climbable wall
        /// (Malbers' Climbable physics material, Default layer), holding on and then climbing up, and
        /// each grabbing a ledge 1.62 m up from standing (Steve's Ledge Grab window). Malbers climbs with
        /// no hand IK - the hands go where the animation puts them - so what counts is how near the wall
        /// the body is held: measured as each hand's, foot's and the chest's distance from the wall, every
        /// frame, against Steve's.
        /// </summary>
        partial class Probe
        {
            /// <summary>How much further from the wall the player's hands and feet may come than Steve's (m).</summary>
            const float ClimbReachLimit = 0.03f;

            /// <summary>How far into the wall a hand or foot may go (m).</summary>
            const float ClimbInsideLimit = 0.03f;

            const float LedgeTop = 1.62f;

            const string LedgePhase = "grabbing the ledge";

            enum ClimbStep { Drop, Wall, Start, Hold, Up, Down, LedgeSetup, Ledge, Done }

            ClimbStep _climbStep = ClimbStep.Drop;
            float _climbStepStart = -1f;
            int _climbTaken;
            BoxCollider _wall;
            readonly Dictionary<MAnimal, BoxCollider> _ledges = new Dictionary<MAnimal, BoxCollider>();
            readonly Dictionary<MAnimal, float> _ledgeStarted = new Dictionary<MAnimal, float>();
            readonly HashSet<MAnimal> _ledgeJumped = new HashSet<MAnimal>();
            readonly Dictionary<MAnimal, float> _climbFrom = new Dictionary<MAnimal, float>();
            readonly Dictionary<(MAnimal, string), List<float>> _climbSamples = new Dictionary<(MAnimal, string), List<float>>();
            readonly List<string> _climbRows = new List<string>();
            readonly List<string> _climbNotes = new List<string>();

            float InClimbStep => Now - _climbStepStart;

            void NextClimb(ClimbStep step)
            {
                _climbStep = step;
                _climbStepStart = Now;
                _climbTaken = 0;
            }

            static bool InState(MAnimal body, int state) => body != null && body.ActiveStateID != null && body.ActiveStateID.ID == state;

            void MoveBoth(Vector3 axis)
            {
                Move(axis);
                if (_steve != null)
                    _steve.SetInputAxis(axis);
            }

            /// <summary>One frame of the climbing checks; true when they are done.</summary>
            bool StepClimb()
            {
                if (_climbStepStart < 0f)
                    _climbStepStart = Now;

                switch (_climbStep)
                {
                    case ClimbStep.Drop:
                        // Put the crates down (Malbers' drop also switches the Carry Box hands off), then clear them away.
                        if (_climbTaken == 0)
                        {
                            _climbTaken++;
                            foreach (var body in Bodies())
                            {
                                var picker = body.GetComponentInChildren<MPickUp>(true);
                                if (picker != null && picker.Has_Item)
                                    picker.TryDrop();
                            }
                        }
                        else if (InClimbStep > 1.8f)
                        {
                            foreach (var item in Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None))
                                item.gameObject.SetActive(false);
                            NextClimb(ClimbStep.Wall);
                        }

                        break;

                    case ClimbStep.Wall:
                        if (_climbTaken == 0)
                        {
                            _climbTaken++;
                            var climb = _animal.State_Get<Climb>();
                            if (climb == null || climb.Surface == null)
                            {
                                _climbNotes.Add("The player has no Malbers Free Climb state (or it has no climbable material): climbing not checked.");
                                NextClimb(ClimbStep.Done);
                                break;
                            }

                            // Both stand 0.55 m from the wall's face, facing it.
                            PlaceBody(_animal, new Vector3(0f, 0f, 8f), 0f);
                            PlaceBody(_steve, new Vector3(3f, 0f, 8f), 0f);
                            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            wall.name = "Climb wall";
                            wall.layer = 0;
                            // Tall enough that nobody reaches its top (where Ledge Grab would take over) in the test.
                            wall.transform.SetPositionAndRotation(new Vector3(1.5f, 6f, 8.55f + 1f), Quaternion.identity);
                            wall.transform.localScale = new Vector3(10f, 12f, 2f);
                            _wall = wall.GetComponent<BoxCollider>();
                            _wall.sharedMaterial = climb.Surface;
                        }
                        else if (InClimbStep > 1.2f)
                        {
                            foreach (var body in Bodies())
                                body.State_Activate(StateEnum.Climb);
                            NextClimb(ClimbStep.Start);
                        }

                        break;

                    case ClimbStep.Start:
                        if (InClimbStep > 0.4f && _climbTaken == 0)
                        {
                            _climbTaken++;
                            foreach (var body in Bodies())
                            {
                                if (!InState(body, StateEnum.Climb))
                                    _climbNotes.Add($"{Who(body)} did not start climbing (in {body.ActiveStateID?.name ?? "no state"}).");
                            }
                        }
                        else if (InClimbStep > 1.6f)
                        {
                            NextClimb(ClimbStep.Hold);
                        }

                        break;

                    case ClimbStep.Hold:
                        SampleWall("holding on");
                        if (InClimbStep > 0.6f)
                        {
                            ClimbSheet("10 climbing, holding on");
                            NextClimb(ClimbStep.Up);
                        }

                        break;

                    case ClimbStep.Up:
                        MoveBoth(Vector3.forward);
                        foreach (var body in Bodies())
                        {
                            if (!_climbFrom.ContainsKey(body))
                                _climbFrom[body] = body.transform.position.y;
                        }

                        if (InClimbStep > 0.5f)
                            SampleWall("climbing up");
                        if (_climbTaken == 0 && InClimbStep > 1.6f)
                        {
                            _climbTaken++;
                            ClimbSheet("11 climbing up");
                        }
                        else if (InClimbStep > 3.2f)
                        {
                            MoveBoth(Vector3.zero);
                            foreach (var body in Bodies())
                            {
                                if (_climbFrom.TryGetValue(body, out float from))
                                    _climbNotes.Add($"{Who(body)} climbed {body.transform.position.y - from:0.00} m in {InClimbStep:0.0} s of holding up.");
                            }

                            NextClimb(ClimbStep.Down);
                        }

                        break;

                    case ClimbStep.Down:
                        // Without its wall the climb lets go, and the bodies drop to the floor.
                        if (_climbTaken == 0)
                        {
                            _climbTaken++;
                            foreach (var body in Bodies())
                            {
                                if (!InState(body, StateEnum.Climb))
                                    _climbNotes.Add($"{Who(body)} stopped climbing before the climb up was over (in {body.ActiveStateID?.name ?? "no state"}).");
                            }

                            // Moved away rather than destroyed: Malbers' footstep dust hangs on the surface it was
                            // kicked up from, and its footsteps wait on that dust.
                            if (_wall != null)
                                _wall.transform.position += Vector3.up * 1000f;
                        }
                        else if (InClimbStep > 2.5f)
                        {
                            NextClimb(ClimbStep.LedgeSetup);
                        }

                        break;

                    case ClimbStep.LedgeSetup:
                        if (_climbTaken == 0)
                        {
                            _climbTaken++;
                            // Each body 1.6 m back from its own ledge, so it walks up to it as a player would
                            // (Malbers' Ledge Grab takes a ledge on its own once it is near enough).
                            float x = 0f;
                            foreach (var body in Bodies())
                            {
                                PlaceBody(body, new Vector3(x, 0f, 16.45f - 1.6f), 0f);
                                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                                box.name = "Ledge for " + Who(body);
                                box.layer = 0;
                                box.transform.SetPositionAndRotation(new Vector3(x, LedgeTop * 0.5f, 16.45f + 1f), Quaternion.identity);
                                box.transform.localScale = new Vector3(1.6f, LedgeTop, 2f);
                                _ledges[body] = box.GetComponent<BoxCollider>();
                                x += 3f;
                            }
                        }
                        else if (InClimbStep > 1.5f)
                        {
                            NextClimb(ClimbStep.Ledge);
                        }

                        break;

                    case ClimbStep.Ledge:
                        foreach (var body in Bodies())
                        {
                            if (!_ledgeStarted.ContainsKey(body) && InState(body, StateEnum.LedgeGrab))
                                _ledgeStarted[body] = Now;
                            bool grabbing = _ledgeStarted.TryGetValue(body, out float started);
                            // Walk up until the grab, then let go of the stick so nothing walks on over the top.
                            var axis = grabbing || InClimbStep > 3.5f ? Vector3.zero : Vector3.forward;
                            if (body == _animal)
                                Move(axis);
                            else
                                body.SetInputAxis(axis);
                            // The grab itself: the hands on the edge, before the pull up lifts them off.
                            if (grabbing && Now - started < 0.45f)
                                SampleLedge(body);
                        }

                        if (_climbTaken == 0 && _ledgeStarted.Count == 2 && Now - _ledgeStarted.Values.Max() > 0.2f)
                        {
                            _climbTaken++;
                            ClimbSheet("12 grabbing a ledge", ledge: true);
                        }

                        if (InClimbStep > 5f)
                        {
                            foreach (var body in Bodies())
                            {
                                if (!_ledgeStarted.ContainsKey(body))
                                    _climbNotes.Add($"{Who(body)} did not grab the {LedgeTop:0.00} m ledge (in {body.ActiveStateID?.name ?? "no state"}).");
                            }

                            foreach (var box in _ledges.Values)
                            {
                                if (box != null)
                                    box.transform.position += Vector3.up * 1000f;
                            }

                            JudgeClimb();
                            NextClimb(ClimbStep.Done);
                        }

                        break;

                    case ClimbStep.Done:
                        return true;
                }

                return false;
            }

            string Who(MAnimal body) => body == _animal ? "player" : "Steve";

            void Add(MAnimal body, string key, float value)
            {
                if (!_climbSamples.TryGetValue((body, key), out var list))
                    _climbSamples[(body, key)] = list = new List<float>();
                list.Add(value);
            }

            /// <summary>The nearest of a hand's finger joints, and its palm, to a box (signed: negative inside).</summary>
            static (float finger, float palm) HandTo(BoxCollider box, Animator animator, bool left)
            {
                var f = FrameOf(animator, left);
                if (!f.ok)
                    return (float.NaN, float.NaN);
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

                return (nearest, SignedDistance(box, f.wrist + f.a * (0.5f * f.length)));
            }

            static float FootTo(BoxCollider box, Animator animator, bool left)
            {
                var toes = animator.GetBoneTransform(left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes);
                var foot = animator.GetBoneTransform(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                float d = float.MaxValue;
                if (toes != null)
                    d = Mathf.Min(d, SignedDistance(box, toes.position));
                if (foot != null)
                    d = Mathf.Min(d, SignedDistance(box, foot.position));
                return d == float.MaxValue ? float.NaN : d;
            }

            void SampleWall(string phase)
            {
                if (_wall == null)
                    return;
                foreach (var body in Bodies())
                {
                    if (!InState(body, StateEnum.Climb))
                        continue;
                    var animator = AnimatorOf(body);
                    if (animator == null)
                        continue;
                    var (lf, lp) = HandTo(_wall, animator, true);
                    var (rf, rp) = HandTo(_wall, animator, false);
                    Add(body, phase + "|left hand", lf);
                    Add(body, phase + "|right hand", rf);
                    Add(body, phase + "|left palm", lp);
                    Add(body, phase + "|right palm", rp);
                    Add(body, phase + "|left foot", FootTo(_wall, animator, true));
                    Add(body, phase + "|right foot", FootTo(_wall, animator, false));
                    var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                    if (chest != null)
                        Add(body, phase + "|chest", SignedDistance(_wall, chest.position));
                }
            }

            void SampleLedge(MAnimal body)
            {
                if (!_ledges.TryGetValue(body, out var box) || box == null)
                    return;
                var animator = AnimatorOf(body);
                if (animator == null)
                    return;
                var (lf, lp) = HandTo(box, animator, true);
                var (rf, rp) = HandTo(box, animator, false);
                Add(body, LedgePhase + "|left hand", lf);
                Add(body, LedgePhase + "|right hand", rf);
                Add(body, LedgePhase + "|left palm", lp);
                Add(body, LedgePhase + "|right palm", rp);
                var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                if (chest != null)
                    Add(body, LedgePhase + "|chest", SignedDistance(box, chest.position));
            }

            static float Median(List<float> values)
            {
                var sorted = values.Where(v => !float.IsNaN(v)).OrderBy(v => v).ToList();
                return sorted.Count == 0 ? float.NaN : sorted[sorted.Count / 2];
            }

            static float Least(List<float> values)
            {
                var valid = values.Where(v => !float.IsNaN(v)).ToList();
                return valid.Count == 0 ? float.NaN : valid.Min();
            }

            /// <summary>
            /// Each phase, each body: the closest each hand and foot came to the wall (its nearest finger
            /// joint, its toes) and the median, and the chest's median distance. The player fails where a
            /// hand or foot never comes as near the wall as Steve's does, or goes into it.
            /// </summary>
            void JudgeClimb()
            {
                var phases = _climbSamples.Keys.Select(k => k.Item2.Split('|')[0]).Distinct().ToList();
                foreach (var phase in phases)
                {
                    foreach (var body in Bodies())
                    {
                        string Cell(string part)
                        {
                            return _climbSamples.TryGetValue((body, phase + "|" + part), out var list) && list.Count > 0
                                ? $"{Cm(Least(list))} / {Cm(Median(list))}"
                                : "-";
                        }

                        _climbRows.Add($"| {phase} | {Who(body)} | {Cell("left hand")} | {Cell("right hand")} | {Cell("left palm")} | {Cell("right palm")} | " +
                                       $"{Cell("left foot")} | {Cell("right foot")} | {Cell("chest")} |");
                    }

                    foreach (var part in new[] { "left hand", "right hand", "left foot", "right foot" })
                    {
                        if (!_climbSamples.TryGetValue((_animal, phase + "|" + part), out var ours) ||
                            !_climbSamples.TryGetValue((_steve, phase + "|" + part), out var his))
                            continue;

                        // A ledge grab passes the hands through the top for a few frames on Steve too (by 5 to
                        // 16 cm from one run to the next), so it is judged on where the hands rest.
                        if (phase == LedgePhase)
                        {
                            float rest = Median(ours), hisRest = Median(his);
                            if (!float.IsNaN(rest) && !float.IsNaN(hisRest) && Mathf.Abs(rest) > ClimbReachLimit &&
                                Mathf.Abs(rest - hisRest) > ClimbReachLimit)
                                _failures.Add($"Climbing, {phase}: the player's {part} rests {Cm(rest)} from the ledge, Steve's {Cm(hisRest)}.");
                            continue;
                        }

                        float a = Least(ours), b = Least(his);
                        if (float.IsNaN(a) || float.IsNaN(b))
                            continue;
                        if (a > b + ClimbReachLimit)
                            _failures.Add($"Climbing, {phase}: the player's {part} comes no nearer than {Cm(a)} to the surface, Steve's {Cm(b)} " +
                                          $"(limit {Cm(ClimbReachLimit)} further).");
                        else if (a < -ClimbInsideLimit && a < b - ClimbInsideLimit)
                            _failures.Add($"Climbing, {phase}: the player's {part} goes {Cm(-a)} into the surface, Steve's {Cm(-b)}.");
                    }
                }
            }

            /// <summary>
            /// Climbing pictures: the player on the top row, Steve below; from the left, from the right,
            /// from behind and above, and the right hand close up from the side.
            /// </summary>
            void ClimbSheet(string title, bool ledge = false)
            {
                if (_poseCamera == null)
                    return;

                float near = _poseCamera.nearClipPlane;
                _poseCamera.nearClipPlane = 0.02f;
                var sheet = new Texture2D(Cell * 4, Cell * 2, TextureFormat.RGB24, false);
                var rt = RenderTexture.GetTemporary(Cell, Cell, 24, RenderTextureFormat.ARGB32);
                var before = RenderTexture.active;
                try
                {
                    int row = 1;
                    foreach (var body in new[] { _animal, _steve })
                    {
                        var animator = AnimatorOf(body);
                        var chest = animator != null ? animator.GetBoneTransform(HumanBodyBones.Chest) : null;
                        if (body != null && chest != null)
                        {
                            var t = body.transform;
                            var forward = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
                            var right = Vector3.Cross(Vector3.up, forward);
                            var at = chest.position;
                            View(rt, sheet, 0, row, at - right * 1.6f - forward * 0.5f + Vector3.up * 0.2f, at);
                            View(rt, sheet, 1, row, at + right * 1.4f - forward * 0.5f + Vector3.up * 0.2f, at);
                            View(rt, sheet, 2, row, at - forward * 1.8f + Vector3.up * (ledge ? 1.4f : 0.6f), at);
                            var f = FrameOf(animator, false);
                            if (f.ok)
                            {
                                var hand = (f.wrist + f.fist) * 0.5f;
                                View(rt, sheet, 3, row, hand + (right * 0.9f - forward * 0.45f + Vector3.up * 0.25f).normalized * (6f * f.length), hand);
                            }
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

            void WriteClimb(StringBuilder md)
            {
                if (_climbRows.Count == 0 && _climbNotes.Count == 0)
                    return;

                md.AppendLine("## Climbing");
                md.AppendLine("Malbers climbs with no hand IK: the hands go where the animation puts them, so they meet the wall only " +
                              "if the body is held as near it as the arms reach (CharacterStateCalibration), and ClimbHandContact keeps " +
                              "this body's hands on it. Each cell: the closest the part came to the wall (or the ledge) / its median, " +
                              "over every frame of the phase; negative is inside. Hands: the nearest finger joint; palms: the middle " +
                              "of the palm; feet: the toes or the foot, whichever is nearer. The ledge grab is judged on its medians.");
                foreach (var note in _climbNotes)
                    md.AppendLine("- " + note);
                md.AppendLine();
                if (_climbRows.Count > 0)
                {
                    md.AppendLine("| Phase | Body | Left hand | Right hand | Left palm | Right palm | Left foot | Right foot | Chest |");
                    md.AppendLine("|---|---|---|---|---|---|---|---|---|");
                    foreach (var row in _climbRows)
                        md.AppendLine(row);
                    md.AppendLine();
                    md.AppendLine($"The player fails where a hand or foot never comes within {Cm(ClimbReachLimit)} of how near Steve's comes, " +
                                  $"or goes more than {Cm(ClimbInsideLimit)} into the surface.");
                    md.AppendLine();
                }
            }
        }
    }
}
#endif
