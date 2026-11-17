#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using MalbersAnimations;
using MalbersAnimations.Controller;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    public static partial class CharacterLiveCheck
    {
        /// <summary>
        /// -livePoses: pictures of the player beside Malbers' own Steve (Steve AI, with his own body) in
        /// the poses a body swap breaks first - unarmed, the sword drawn and mid-swing, the bow held and
        /// aimed, the head turned towards a target, and Malbers' carry-a-box pose - both holding the
        /// same Malbers weapons. Each picture is one sheet: the player on the top row, Steve on the
        /// bottom, each seen from the front right, the front left, the left side and the face (for the
        /// head poses, from the target). PNGs land in the report's poses folder.
        /// </summary>
        partial class Probe
        {
            const string ReferencePath = "Assets/Malbers Animations/Animal Controller/Human/Steve AI.prefab";
            const string PoseSwordPath = "Assets/Malbers Animations/Common/Prefabs/Weapons/Sword.prefab";
            const string PoseBowPath = "Assets/Malbers Animations/Common/Prefabs/Weapons/Bow Main.prefab";
            const string PoseCratePath = "Assets/Malbers Animations/Animal Controller/Human/Crate.prefab";
            const int Cell = 480;

            enum PoseStep { Setup, Unarmed, Sword, Swing, Bow, HeadLeft, HeadRight, Carry, Climb, Done }

            PoseStep _poseStep = PoseStep.Setup;
            float _poseStart = -1f;
            int _poseTaken;
            MAnimal _steve;
            Camera _poseCamera;
            Transform _poseTarget;
            readonly List<string> _poseSheets = new List<string>();
            readonly List<string> _poseNotes = new List<string>();

            float InPose => Now - _poseStart;

            void NextPose(PoseStep step)
            {
                _poseStep = step;
                _poseStart = Now;
                _poseTaken = 0;
            }

            /// <summary>One frame of the pose pictures; true when they are done.</summary>
            bool StepPoses()
            {
                if (_poseStart < 0f)
                    _poseStart = Now;

                switch (_poseStep)
                {
                    case PoseStep.Setup:
                        if (!SetUpPoses())
                            return true;
                        NextPose(PoseStep.Unarmed);
                        break;

                    case PoseStep.Unarmed:
                        if (InPose > 1.2f)
                        {
                            Sheet("1 unarmed");
                            EachWeapons(w => w.Holster_SetWeapon(AssetDatabase.LoadAssetAtPath<GameObject>(PoseSwordPath)));
                            NextPose(PoseStep.Sword);
                        }

                        break;

                    case PoseStep.Sword:
                        if (InPose > 1.5f)
                        {
                            Sheet("2 sword drawn");
                            MeasureGrips("sword drawn");
                            HandSheet("2b sword drawn, the sword hand", left: false);
                            EachWeapons(w => w.MainAttack());
                            NextPose(PoseStep.Swing);
                        }

                        break;

                    case PoseStep.Swing:
                        if (_poseTaken == 0 && InPose > 0.3f)
                        {
                            _poseTaken++;
                            Sheet("3 sword swing a");
                            MeasureGrips("sword swing a");
                        }
                        else if (_poseTaken == 1 && InPose > 0.55f)
                        {
                            _poseTaken++;
                            Sheet("4 sword swing b");
                            MeasureGrips("sword swing b");
                            HandSheet("4b sword swing b, the sword hand", left: false);
                            EachWeapons(w => w.MainAttackReleased());
                        }
                        else if (_poseTaken == 2 && InPose > 1.8f)
                        {
                            EachWeapons(w => w.Holster_SetWeapon(AssetDatabase.LoadAssetAtPath<GameObject>(PoseBowPath)));
                            NextPose(PoseStep.Bow);
                        }

                        break;

                    case PoseStep.Bow:
                        if (_poseTaken == 0 && InPose > 1.5f)
                        {
                            _poseTaken++;
                            Sheet("5 bow held");
                            MeasureGrips("bow held");
                            HandSheet("5b bow held, the bow hand", left: true);
                            EachWeapons(w => w.Aim_Set(true));
                        }
                        else if (_poseTaken == 1 && InPose > 2.6f)
                        {
                            _poseTaken++;
                            Sheet("6 bow aimed");
                            MeasureGrips("bow aimed");
                            // Malbers draws the string while the attack is held, and looses on release.
                            EachWeapons(w => w.MainAttack());
                        }
                        else if (_poseTaken == 2 && InPose > 3.2f && (BowsDrawnFor(0.35f) || InPose > 5.6f))
                        {
                            // Both strings at the hand a moment, so the two bodies are compared at full draw.
                            _poseTaken++;
                            if (!BowsDrawnFor(0f))
                                _handNotes.Add("bow drawn: the two strings were not both at the hand within 3 s of drawing; measured anyway.");
                            Sheet("6c bow drawn");
                            MeasureGrips("bow drawn");
                            MeasureString("bow drawn");
                            HandSheet("6d bow drawn, the bow hand", left: true);
                            HandSheet("6e bow drawn, the string hand", left: false);
                            EachWeapons(w => w.MainAttackReleased());
                            _bowLoosed = Now;
                        }
                        else if (_poseTaken == 3 && Now - _bowLoosed > 0.7f)
                        {
                            _poseTaken++;
                            EachWeapons(w => w.Aim_Set(false));
                        }
                        else if (_poseTaken == 4 && Now - _bowLoosed > 1.3f)
                        {
                            EachWeapons(w => w.UnEquip_Fast());
                            TurnBodies(50f);
                            NextPose(PoseStep.HeadLeft);
                        }

                        break;

                    case PoseStep.HeadLeft:
                        if (InPose > 1.2f)
                        {
                            Sheet("7 head turned to the target on the left", faceTheTarget: true);
                            TurnBodies(-50f);
                            NextPose(PoseStep.HeadRight);
                        }

                        break;

                    case PoseStep.HeadRight:
                        if (InPose > 1.2f)
                        {
                            Sheet("8 head turned to the target on the right", faceTheTarget: true);
                            TurnBodies(0f);
                            NextPose(PoseStep.Carry);
                        }

                        break;

                    case PoseStep.Carry:
                        // Malbers' own crate, picked up as its Carry Box demo does: Malbers' pick-up plays the
                        // lift and the crate switches on the Carry Box IK set.
                        if (_poseTaken == 0 && InPose > 0.8f)
                        {
                            _poseTaken++;
                            PickUpCrates();
                        }
                        else if (_poseTaken == 1 && InPose > 3.5f)
                        {
                            _poseTaken++;
                            Sheet("9 carrying Malbers' crate");
                            MeasureCrate("carrying Malbers' crate");
                            HandSheet("9b carrying Malbers' crate, the left hand", left: true);
                            HandSheet("9c carrying Malbers' crate, the right hand", left: false);
                            foreach (var body in Bodies())
                            {
                                var picker = body.GetComponentInChildren<MPickUp>(true);
                                _poseNotes.Add($"{body.name}: holding the crate: {(picker != null && picker.Has_Item ? "yes" : "no")}.");
                            }

                            NextPose(PoseStep.Climb);
                        }

                        break;

                    case PoseStep.Climb:
                        if (StepClimb())
                            NextPose(PoseStep.Done);
                        break;

                    case PoseStep.Done:
                        return true;
                }

                return false;
            }

            bool SetUpPoses()
            {
                var reference = AssetDatabase.LoadAssetAtPath<GameObject>(ReferencePath);
                if (reference == null)
                {
                    _poseNotes.Add("No pictures: " + ReferencePath + " is missing.");
                    return false;
                }

                Directory.CreateDirectory(Path.Combine(_job.folder, "poses"));

                // The player at the origin facing +Z, unarmed, Steve 3 m to its right.
                PlaceBody(_animal, Vector3.zero, 0f);
                var playerWeapons = _animal.GetComponentInChildren<MWeaponManager>(true);
                if (playerWeapons != null)
                    playerWeapons.UnEquip_Fast();
                var steve = Object.Instantiate(reference, new Vector3(3f, 0f, 0f), Quaternion.identity);
                steve.name = "Steve (Malbers' own body, for comparison)";
                foreach (var behaviour in steve.GetComponentsInChildren<Behaviour>(true))
                {
                    string type = behaviour.GetType().Name;
                    if (behaviour is UnityEngine.AI.NavMeshAgent || type.Contains("Brain") || type.Contains("AIControl"))
                        behaviour.enabled = false;
                }

                _steve = steve.GetComponent<MAnimal>();

                // Both look at the same point ahead, so the heads and the aim have somewhere to go.
                _poseTarget = new GameObject("Pose look target").transform;
                _poseTarget.position = new Vector3(1.5f, 1.5f, 12f);
                foreach (var body in Bodies())
                {
                    foreach (var aim in body.GetComponentsInChildren<Aim>(true))
                        aim.SetTarget(_poseTarget);
                }

                var camera = new GameObject("Pose camera").AddComponent<Camera>();
                camera.enabled = false;
                camera.fieldOfView = 42f;
                camera.nearClipPlane = 0.05f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.33f, 0.36f, 0.42f);
                _poseCamera = camera;
                _poseNotes.Add("Each sheet: the player on the top row, Malbers' Steve (his own body) on the bottom; " +
                               "from the front right, the front left, the left side, and the face (the head poses: from the target ahead).");
                return true;
            }

            IEnumerable<MAnimal> Bodies()
            {
                if (_animal != null)
                    yield return _animal;
                if (_steve != null)
                    yield return _steve;
            }

            void EachWeapons(System.Action<MWeaponManager> act)
            {
                foreach (var body in Bodies())
                {
                    var weapons = body.GetComponentInChildren<MWeaponManager>(true);
                    if (weapons == null)
                    {
                        _poseNotes.Add(body.name + " has no Malbers weapon manager.");
                        continue;
                    }

                    try
                    {
                        act(weapons);
                    }
                    catch (System.Exception e)
                    {
                        _poseNotes.Add($"{body.name}: {e.GetType().Name}: {e.Message}");
                    }
                }
            }

            void PlaceBody(MAnimal body, Vector3 position, float yaw)
            {
                if (body == null)
                    return;
                if (body == _animal && _tps != null)
                {
                    _tps.TeleportGameplay(position, yaw);
                    return;
                }

                var rotation = Quaternion.Euler(0f, yaw, 0f);
                body.Rotation = rotation;
                body.Teleport(position);
                body.transform.SetPositionAndRotation(position, rotation);
            }

            void PickUpCrates()
            {
                var crate = AssetDatabase.LoadAssetAtPath<GameObject>(PoseCratePath);
                if (crate == null)
                {
                    _poseNotes.Add("No crate pictures: " + PoseCratePath + " is missing.");
                    return;
                }

                foreach (var body in Bodies())
                {
                    var picker = body.GetComponentInChildren<MPickUp>(true);
                    if (picker == null)
                    {
                        _poseNotes.Add(body.name + " has no Malbers pick-up.");
                        continue;
                    }

                    var t = body.transform;
                    var forward = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
                    var box = Object.Instantiate(crate, t.position + forward * 0.7f, Quaternion.LookRotation(-forward));
                    var item = box.GetComponentInChildren<Pickable>();
                    if (item == null)
                    {
                        _poseNotes.Add("Malbers' crate has no Pickable.");
                        continue;
                    }

                    picker.FocusedItem = item;
                    picker.TryPickUp();
                }
            }

            void EachIK(System.Action<MalbersAnimations.IK.IKManager> act)
            {
                foreach (var body in Bodies())
                {
                    var ik = body.GetComponentInChildren<MalbersAnimations.IK.IKManager>(true);
                    if (ik == null)
                    {
                        _poseNotes.Add(body.name + " has no Malbers IK manager.");
                        continue;
                    }

                    act(ik);
                }
            }

            /// <summary>Turns both bodies where they stand; the look target stays ahead.</summary>
            void TurnBodies(float yaw)
            {
                PlaceBody(_animal, _animal.transform.position, yaw);
                if (_steve != null)
                    PlaceBody(_steve, _steve.transform.position, yaw);
            }

            /// <summary>One sheet: four views of the player on the top row, the same of Steve below.</summary>
            void Sheet(string title, bool faceTheTarget = false)
            {
                if (_poseCamera == null)
                    return;

                var sheet = new Texture2D(Cell * 4, Cell * 2, TextureFormat.RGB24, false);
                var rt = RenderTexture.GetTemporary(Cell, Cell, 24, RenderTextureFormat.ARGB32);
                var before = RenderTexture.active;
                try
                {
                    int row = 1;
                    foreach (var body in new[] { _animal, _steve })
                    {
                        if (body != null)
                        {
                            var t = body.transform;
                            var forward = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
                            var right = Vector3.Cross(Vector3.up, forward);
                            var feet = t.position;
                            View(rt, sheet, 0, row, feet + forward * 1.8f + right * 1.1f + Vector3.up * 1.5f, feet + Vector3.up * 1.0f);
                            View(rt, sheet, 1, row, feet + forward * 1.8f - right * 1.1f + Vector3.up * 1.5f, feet + Vector3.up * 1.0f);
                            View(rt, sheet, 2, row, feet - right * 2.3f + Vector3.up * 1.2f, feet + Vector3.up * 1.0f);
                            var animator = body.GetComponentInChildren<Animator>();
                            var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                            var face = head != null ? head.position : feet + Vector3.up * 1.6f;
                            var toward = faceTheTarget ? Vector3.forward : forward;
                            View(rt, sheet, 3, row, face + toward * 1.1f + Vector3.up * 0.08f, face);
                        }

                        row--;
                    }
                }
                finally
                {
                    RenderTexture.active = before;
                    _poseCamera.targetTexture = null;
                    RenderTexture.ReleaseTemporary(rt);
                }

                sheet.Apply();
                string file = Path.Combine(_job.folder, "poses", title + ".png");
                File.WriteAllBytes(file, sheet.EncodeToPNG());
                Object.DestroyImmediate(sheet);
                _poseSheets.Add(file);
            }

            void View(RenderTexture rt, Texture2D sheet, int column, int row, Vector3 from, Vector3 at)
            {
                _poseCamera.transform.position = from;
                _poseCamera.transform.LookAt(at);
                _poseCamera.targetTexture = rt;
                _poseCamera.Render();
                RenderTexture.active = rt;
                sheet.ReadPixels(new Rect(0, 0, Cell, Cell), column * Cell, row * Cell);
            }

            void WritePoses(StringBuilder md)
            {
                if (_poseSheets.Count == 0 && _poseNotes.Count == 0)
                    return;

                md.AppendLine("## Pose pictures");
                foreach (var note in _poseNotes)
                    md.AppendLine("- " + note);
                foreach (var file in _poseSheets)
                    md.AppendLine($"- [{Path.GetFileNameWithoutExtension(file)}](poses/{Path.GetFileName(file)})");
                md.AppendLine();
            }
        }
    }
}
#endif
