#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MalbersAnimations.Controller;
using MalbersAnimations.IK;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Plays the player character for a few seconds and reports what it did, so a change to the rig is
    /// judged by what happens in play mode and not only by the poses the Fit Check samples.
    ///
    /// It stands still, then jumps on the spot, jumps while walking and double-jumps, pressing Jump
    /// through the same hook as the key. For each jump it notes the take-off, the highest point and
    /// the landing, and a jump that has not landed after five seconds is reported as stuck, with the
    /// state the character is in. While standing it measures where the head faces against the body and
    /// the camera (the face direction comes from the avatar's T-pose), and it lists every camera tagged
    /// MainCamera, since Malbers steers and aims by the first one it finds.
    ///
    /// Batch (the Editor must be closed):
    ///   Unity -batchmode -projectPath . -executeMethod CollarCali.EditorTools.CharacterLiveCheck.RunBatch
    ///         [-liveScene Assets/....unity] [-livePlayer Assets/....prefab] [-liveNoIK] -logFile live.log
    /// Without -liveScene it plays a floor with the player and camera rig on it; with one, it plays
    /// that scene as saved and checks the player character in it. Leave out -quit: the check quits the
    /// Editor itself when it is done. Tools > CollarCali > Character > Live Check plays the open scene.
    /// Reports land in Builds/CharacterFit/&lt;timestamp&gt;-live-&lt;name&gt;: live.md and live.csv.
    /// </summary>
    [InitializeOnLoad]
    public static class CharacterLiveCheck
    {
        const string JobKey = "CollarCali.LiveCheck.Job";
        const string MenuRoot = "Tools/CollarCali/Character/";
        const string DefaultPlayer = "Assets/_Integration/Characters/Player/PlayerCharacter.prefab";
        const string DefaultRig = "Assets/_Integration/Characters/Player/PlayerCameraRig (CM3).prefab";

        /// <summary>A jump that has not landed this long after the press is stuck.</summary>
        const float StuckSeconds = 5f;

        /// <summary>Gives up on play mode altogether after this long (real seconds).</summary>
        const double WatchdogSeconds = 180;

        [Serializable]
        class Job
        {
            public string scene;
            public string player;
            public bool noIK;
            public string add;      // components added to the character when play starts, by type name (';')
            public string disable;  // components switched off when play starts, by type name (';')
            public bool batch;
            public string folder;
            public double requestedAt;
        }

        static Job _job;
        static Probe _probe;

        static CharacterLiveCheck()
        {
            string json = SessionState.GetString(JobKey, "");
            if (string.IsNullOrEmpty(json))
                return;

            _job = JsonUtility.FromJson<Job>(json);
            if (_job == null)
            {
                SessionState.EraseString(JobKey);
                return;
            }

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        [MenuItem(MenuRoot + "Live Check (play the open scene)")]
        static void RunFromMenu()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Live Check", "Stop play mode first.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Start(new Job { scene = SceneManager.GetActiveScene().path, player = DefaultPlayer, batch = false });
        }

        public static void RunBatch()
        {
            var job = new Job
            {
                scene = CharacterFitCheck.ArgValue("-liveScene") ?? "",
                player = CharacterFitCheck.ArgValue("-livePlayer") ?? DefaultPlayer,
                noIK = Environment.GetCommandLineArgs().Contains("-liveNoIK"),
                add = CharacterFitCheck.ArgValue("-liveAdd") ?? "",
                disable = CharacterFitCheck.ArgValue("-liveDisable") ?? "",
                batch = true,
            };

            try
            {
                if (string.IsNullOrEmpty(job.scene))
                {
                    if (!BuildFloorScene(job.player))
                    {
                        EditorApplication.Exit(3);
                        return;
                    }
                }
                else
                {
                    EditorSceneManager.OpenScene(job.scene, OpenSceneMode.Single);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[LiveCheck] Could not set up the scene: " + e);
                EditorApplication.Exit(3);
                return;
            }

            Start(job);
        }

        static void Start(Job job)
        {
            string name = (string.IsNullOrEmpty(job.scene) ? "floor-" + Path.GetFileNameWithoutExtension(job.player)
                              : Path.GetFileNameWithoutExtension(job.scene)) +
                          (job.noIK ? "-noIK" : "") +
                          (string.IsNullOrEmpty(job.add) ? "" : "-add") +
                          (string.IsNullOrEmpty(job.disable) ? "" : "-off");
            job.folder = CharacterFitCheck.NewReportFolder("live-" + name);
            job.requestedAt = EditorApplication.timeSinceStartup;
            _job = job;
            SessionState.SetString(JobKey, JsonUtility.ToJson(job));

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        /// <summary>A floor, a light, the player and its camera rig, in an unsaved scene.</summary>
        static bool BuildFloorScene(string playerPath)
        {
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(playerPath);
            var rig = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultRig);
            if (player == null || rig == null)
            {
                Debug.LogError("[LiveCheck] Missing " + (player == null ? playerPath : DefaultRig));
                return false;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun", typeof(Light));
            sun.GetComponent<Light>().type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(80f, 1f, 80f);
            floor.transform.position = new Vector3(0f, -0.5f, 0f);

            PrefabUtility.InstantiatePrefab(player);
            PrefabUtility.InstantiatePrefab(rig);
            return true;
        }

        static void Tick()
        {
            if (_job == null)
            {
                EditorApplication.update -= Tick;
                return;
            }

            if (EditorApplication.timeSinceStartup - _job.requestedAt > WatchdogSeconds)
            {
                Finish("Gave up: play mode did not finish the check within " + WatchdogSeconds + " s.", 2);
                return;
            }

            if (!EditorApplication.isPlaying)
                return;

            if (_probe == null)
                _probe = new Probe(_job);

            if (_probe.Step())
                Finish(null, _probe.Failed ? 1 : 0);
        }

        static void Finish(string error, int code)
        {
            var job = _job;
            _job = null;
            SessionState.EraseString(JobKey);
            EditorApplication.update -= Tick;

            string report = null;
            if (_probe != null)
                report = _probe.Write(error);
            else if (job != null && error != null)
            {
                report = Path.Combine(job.folder, "live.md");
                File.WriteAllText(report, "# Live check\n\n**" + error + "**\n");
            }

            _probe = null;
            Debug.Log("[LiveCheck] " + (code == 0 ? "PASS" : "FAIL") + (error != null ? " - " + error : "") +
                      "\nReport: " + report);

            if (job != null && job.batch)
            {
                EditorApplication.Exit(code);
                return;
            }

            if (EditorApplication.isPlaying)
                EditorApplication.ExitPlaymode();
            if (report != null)
                EditorUtility.RevealInFinder(report);
        }

        /// <summary>Drives the character one Editor frame at a time and writes down what it does.</summary>
        class Probe
        {
            enum Phase { Find, Settle, Stand, JumpStill, Rest1, WalkJump, Rest2, DoubleJump, Done }

            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

            static readonly MethodInfo ActivateByInput = typeof(State).GetMethod("ActivatebyInput", Private);

            // The basic jump's own bookkeeping, to see why a jump does or does not hand over to falling.
            static readonly FieldInfo JumpLogicOn = typeof(JumpBasic).GetField("ActivateJumpLogic", Private);
            static readonly FieldInfo JumpLogicTime = typeof(JumpBasic).GetField("StartedJumpLogicTime", Private);
            static readonly FieldInfo JumpProfile = typeof(JumpBasic).GetField("activeJump", Private);

            Dictionary<int, string> _stateNames = new Dictionary<int, string>();
            Dictionary<int, string> _tagNames = new Dictionary<int, string>();
            string _lastAnim;

            readonly Job _job;
            readonly StringBuilder _csv = new StringBuilder();
            readonly List<string> _timeline = new List<string>();
            readonly List<string> _notes = new List<string>();
            readonly List<string> _failures = new List<string>();
            readonly List<JumpResult> _jumps = new List<JumpResult>();
            readonly List<Vector4> _headStanding = new List<Vector4>();

            Phase _phase = Phase.Find;
            float _phaseStart;
            MAnimal _animal;
            State _jumpState;
            Transform _head;
            Vector3 _faceLocal, _upLocal;
            float _groundY;
            string _lastState;
            JumpResult _jump;
            bool _held;
            int _frames;
            float _firstTime = -1f;

            class JumpResult
            {
                public string name;
                public float pressedAt, startY, apexY, landedAt = -1f;
                public bool tookOff;
                public string endState, stuckDetail;
            }

            public bool Failed => _failures.Count > 0;

            public Probe(Job job)
            {
                _job = job;
                // The Editor otherwise runs flat out in batch mode; a 144 Hz screen is what players see.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 144;
                _csv.AppendLine("time,phase,state,grounded,height,velocityY,useGravity,headYaw,headPitch,headRoll,headToCamera," +
                                "animState,animTag,animTime,inTransition,jumpProfile,jumpLogic,jumpLogicTime,jumpTime,inCore");
            }

            float Now => Time.time;
            float InPhase => Now - _phaseStart;

            void Enter(Phase phase)
            {
                _phase = phase;
                _phaseStart = Now;
            }

            /// <summary>One Editor frame. True when the check is over.</summary>
            public bool Step()
            {
                _frames++;
                if (_firstTime < 0f)
                    _firstTime = Now;

                if (_phase == Phase.Find)
                {
                    if (!FindPlayer())
                    {
                        if (Now - _firstTime > 5f)
                        {
                            _failures.Add("No player character (an MAnimal with a humanoid Animator) in the scene.");
                            return true;
                        }

                        return false;
                    }

                    Enter(Phase.Settle);
                }

                Sample();

                switch (_phase)
                {
                    case Phase.Settle:
                        if (Standing() && InPhase > 1f)
                        {
                            _groundY = _animal.transform.position.y;
                            GroundProbe();
                            StateOwnership();
                            Enter(Phase.Stand);
                        }
                        else if (InPhase > 6f)
                        {
                            _failures.Add($"Never settled on the ground: after 6 s it is in {StateName()} " +
                                          $"(grounded {_animal.Grounded}).");
                            _groundY = _animal.transform.position.y;
                            Enter(Phase.Stand);
                        }

                        break;

                    case Phase.Stand:
                        if (InPhase > 2f)
                        {
                            StartJump("Jump standing still");
                            Enter(Phase.JumpStill);
                        }

                        break;

                    case Phase.JumpStill:
                        ReleaseAfter(0.15f);
                        if (WatchJump())
                            Enter(Phase.Rest1);
                        break;

                    case Phase.Rest1:
                        if (InPhase > 0.6f)
                        {
                            _animal.SetInputAxis(new Vector3(0f, 0f, 1f));
                            Enter(Phase.WalkJump);
                        }

                        break;

                    case Phase.WalkJump:
                        if (_jump == null && InPhase > 1.2f)
                            StartJump("Jump while walking");
                        if (_jump != null)
                        {
                            ReleaseAfter(0.15f);
                            if (WatchJump())
                            {
                                _animal.SetInputAxis(Vector3.zero);
                                Enter(Phase.Rest2);
                            }
                        }

                        break;

                    case Phase.Rest2:
                        if (InPhase > 0.8f)
                        {
                            StartJump("Double jump");
                            Enter(Phase.DoubleJump);
                        }

                        break;

                    case Phase.DoubleJump:
                        if (_held && InPhase > 0.12f)
                            Press(false);
                        if (!_held && InPhase > 0.35f && InPhase < 0.5f)
                            Press(true);
                        if (WatchJump())
                            Enter(Phase.Done);
                        break;

                    case Phase.Done:
                        return true;
                }

                return false;
            }

            bool FindPlayer()
            {
                var animals = Object.FindObjectsByType<MAnimal>(FindObjectsSortMode.None);
                if (animals.Length == 0)
                    return false;

                _animal = animals.FirstOrDefault(a => a.GetComponent<CharacterVisualInterpolator>() != null) ??
                          animals.FirstOrDefault(a => a.Anim != null && a.Anim.isHuman);
                if (_animal == null || _animal.Anim == null || !_animal.Anim.isInitialized)
                    return false;

                if (animals.Length > 1)
                    _notes.Add($"{animals.Length} Malbers characters in the scene: " +
                               string.Join(", ", animals.Select(a => a.name)) + $". Checking {_animal.name}.");

                if (_job.noIK)
                {
                    foreach (var ik in _animal.GetComponentsInChildren<IKManager>(true))
                        ik.enabled = false;
                    _notes.Add("Malbers' IK manager is off for this run.");
                }

                foreach (var typeName in Names(_job.add))
                {
                    var type = FindType(typeName);
                    if (type == null)
                        _failures.Add("No component type " + typeName + " to add.");
                    else if (_animal.GetComponent(type) == null)
                    {
                        _animal.gameObject.AddComponent(type);
                        _notes.Add("Added " + type.Name + " when play started, as the game does.");
                    }
                }

                foreach (var typeName in Names(_job.disable))
                {
                    foreach (var behaviour in _animal.GetComponents<Behaviour>())
                    {
                        if (behaviour != null && (behaviour.GetType().Name == typeName || behaviour.GetType().FullName == typeName))
                        {
                            behaviour.enabled = false;
                            _notes.Add("Switched off " + behaviour.GetType().Name + " for this run.");
                        }
                    }
                }

                _stateNames = AnimatorNames(_animal.Anim, out _tagNames);

                _jumpState = _animal.states.FirstOrDefault(s => s != null && s.Input == "Jump" && s is JumpBasic) ??
                             _animal.states.FirstOrDefault(s => s != null && s.Input == "Jump");
                if (_jumpState == null)
                    _failures.Add("No state takes the Jump input.");

                HeadAxes();
                Cameras();
                _notes.Add($"Main pivot {(_animal.Main_Pivot_Point.y - _animal.transform.position.y):0.00} m up, " +
                           $"Height {_animal.Height:0.00}, ground layers {LayerNames(_animal.GroundLayer)}.");
                return true;
            }

            void Cameras()
            {
                var mains = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)
                    .Where(c => c.isActiveAndEnabled && c.CompareTag("MainCamera")).ToList();
                var main = Camera.main;
                string list = string.Join(", ", mains.Select(c => "\"" + PathOf(c.transform) + "\""));
                if (mains.Count > 1)
                    _failures.Add($"{mains.Count} active cameras are tagged MainCamera ({list}); Camera.main is " +
                                  $"\"{(main != null ? PathOf(main.transform) : "none")}\". Malbers moves and aims by it.");
                else
                    _notes.Add("Main camera: " + (main != null ? "\"" + PathOf(main.transform) + "\"" : "none") + ".");
            }

            /// <summary>The head's face and up directions in its own space, from the avatar's T-pose.</summary>
            void HeadAxes()
            {
                var anim = _animal.Anim;
                _head = anim.GetBoneTransform(HumanBodyBones.Head);
                if (_head == null || anim.avatar == null)
                    return;

                var tpose = new Dictionary<string, Quaternion>();
                foreach (var bone in anim.avatar.humanDescription.skeleton)
                    tpose[bone.name] = bone.rotation;

                var chain = new List<Transform>();
                for (var t = _head; t != null && t != _animal.transform; t = t.parent)
                    chain.Add(t);
                chain.Reverse();

                var rotation = Quaternion.identity;
                foreach (var t in chain)
                    rotation *= tpose.TryGetValue(t.name, out var r) ? r : t.localRotation;

                _faceLocal = Quaternion.Inverse(rotation) * Vector3.forward;
                _upLocal = Quaternion.Inverse(rotation) * Vector3.up;
            }

            void GroundProbe()
            {
                var from = _animal.Main_Pivot_Point;
                if (Physics.Raycast(from, -_animal.transform.up, out var hit, 10f, _animal.GroundLayer,
                        QueryTriggerInteraction.Ignore))
                    _notes.Add($"Standing on \"{PathOf(hit.collider.transform)}\" (layer " +
                               $"{LayerMask.LayerToName(hit.collider.gameObject.layer)}), feet " +
                               $"{(_animal.transform.position.y - hit.point.y) * 1000f:+0;-0} mm from it.");
                else
                    _failures.Add("Nothing below the main pivot on the ground layers.");
            }

            /// <summary>
            /// Our components that tune Malbers' states must hold the character's own runtime copies:
            /// one holding the shared asset tunes every character but this one.
            /// </summary>
            void StateOwnership()
            {
                foreach (var component in _animal.GetComponents<MonoBehaviour>())
                {
                    if (component == null || component.GetType().Namespace != "CollarCali")
                        continue;

                    foreach (var field in component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!typeof(State).IsAssignableFrom(field.FieldType))
                            continue;

                        var held = field.GetValue(component) as State;
                        if (held == null)
                            continue;

                        if (held.animal == _animal && _animal.states.Contains(held))
                            _notes.Add($"{component.GetType().Name}.{field.Name} holds this character's own {held.name}.");
                        else
                            _failures.Add($"{component.GetType().Name}.{field.Name} holds \"{held.name}\", which is not " +
                                          "this character's own copy of the state: its tuning never reaches the character.");
                    }
                }
            }

            bool Standing()
            {
                string state = StateName();
                return _animal.Grounded && (state == "Idle" || state == "Locomotion");
            }

            string StateName() => _animal.ActiveState != null && _animal.ActiveState.ID != null
                ? _animal.ActiveState.ID.name
                : "none";

            void StartJump(string name)
            {
                _jump = new JumpResult
                {
                    name = name,
                    pressedAt = Now,
                    startY = _animal.transform.position.y,
                    apexY = _animal.transform.position.y,
                };
                _jumps.Add(_jump);
                Press(true);
            }

            void Press(bool down)
            {
                if (_jumpState != null && ActivateByInput != null)
                    ActivateByInput.Invoke(_jumpState, new object[] { down });
                _held = down;
            }

            void ReleaseAfter(float seconds)
            {
                if (_held && _jump != null && Now - _jump.pressedAt > seconds)
                    Press(false);
            }

            /// <summary>Follows the current jump; true once it has landed or is judged stuck.</summary>
            bool WatchJump()
            {
                if (_jump == null)
                    return true;

                float y = _animal.transform.position.y;
                _jump.apexY = Mathf.Max(_jump.apexY, y);
                if (y > _jump.startY + 0.05f || !_animal.Grounded)
                    _jump.tookOff = true;

                float since = Now - _jump.pressedAt;
                if (_jump.tookOff && since > 0.2f && Standing())
                {
                    _jump.landedAt = since;
                    _jump.endState = StateName();
                    _jump = null;
                    return true;
                }

                if (since > StuckSeconds)
                {
                    var rb = _animal.RB;
                    _jump.endState = StateName();
                    _jump.stuckDetail = $"{since:0.0} s after the press it is in {StateName()}, grounded " +
                                        $"{_animal.Grounded}, {(y - _groundY):+0.00;-0.00} m off the ground, " +
                                        $"vertical speed {(rb != null ? rb.linearVelocity.y : 0f):0.00} m/s, " +
                                        $"gravity {(_animal.UseGravity ? "on" : "off")}.";
                    _failures.Add(_jump.name + ": did not land. " + _jump.stuckDetail);
                    if (_held)
                        Press(false);
                    _jump = null;
                    return true;
                }

                return false;
            }

            void Sample()
            {
                string state = StateName();
                if (state != _lastState)
                {
                    _timeline.Add($"{Now - _firstTime,6:0.00}s  {state}{(_animal.Grounded ? "" : "  (in the air)")}");
                    _lastState = state;
                }

                var head = HeadAngles();
                if (_phase == Phase.Stand && InPhase > 0.5f && head.HasValue)
                    _headStanding.Add(head.Value);

                var rb = _animal.RB;
                _csv.Append(F(Now - _firstTime)).Append(',').Append(_phase).Append(',').Append(state).Append(',')
                    .Append(_animal.Grounded ? 1 : 0).Append(',')
                    .Append(F(_animal.transform.position.y - _groundY)).Append(',')
                    .Append(F(rb != null ? rb.linearVelocity.y : 0f)).Append(',')
                    .Append(_animal.UseGravity ? 1 : 0).Append(',');
                if (head.HasValue)
                    _csv.Append(F(head.Value.x)).Append(',').Append(F(head.Value.y)).Append(',')
                        .Append(F(head.Value.z)).Append(',').Append(F(head.Value.w));
                else
                    _csv.Append(",,,");

                var anim = _animal.Anim;
                var info = anim.GetCurrentAnimatorStateInfo(0);
                string animState = Name(_stateNames, info.fullPathHash);
                string animTag = Name(_tagNames, info.tagHash);
                _csv.Append(',').Append(animState).Append(',').Append(animTag).Append(',').Append(F(info.normalizedTime))
                    .Append(',').Append(anim.IsInTransition(0) ? 1 : 0);

                if (anim.IsInTransition(0))
                    animState += " -> " + Name(_stateNames, anim.GetNextAnimatorStateInfo(0).fullPathHash);
                if (animState != _lastAnim)
                {
                    _timeline.Add($"{Now - _firstTime,6:0.00}s      animator: {animState} [{animTag}]");
                    _lastAnim = animState;
                }

                if (_jumpState is JumpBasic jump && JumpLogicOn != null && JumpLogicTime != null && JumpProfile != null)
                {
                    object profile = JumpProfile.GetValue(jump);
                    var profileType = profile.GetType();
                    string profileName = profileType.GetField("name")?.GetValue(profile) as string;
                    object jumpTime = profileType.GetField("JumpTime")?.GetValue(profile);
                    _csv.Append(',').Append(profileName?.Replace(',', ' ')).Append(',')
                        .Append((bool)JumpLogicOn.GetValue(jump) ? 1 : 0).Append(',')
                        .Append(F((float)JumpLogicTime.GetValue(jump))).Append(',')
                        .Append(jumpTime is float t ? F(t) : "").Append(',')
                        .Append(jump.InCoreAnimation ? 1 : 0);
                }

                _csv.AppendLine();
            }

            static string Name(Dictionary<int, string> names, int hash) =>
                names.TryGetValue(hash, out var name) ? name : hash.ToString(CultureInfo.InvariantCulture);

            /// <summary>
            /// Yaw (right +), pitch (down +) and roll of the face against the body, and the angle between
            /// the face and the camera's forward. Null without a head.
            /// </summary>
            Vector4? HeadAngles()
            {
                if (_head == null)
                    return null;

                var root = _animal.transform.rotation;
                var face = Quaternion.Inverse(root) * (_head.rotation * _faceLocal);
                var up = Quaternion.Inverse(root) * (_head.rotation * _upLocal);

                float yaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
                float pitch = -Mathf.Asin(Mathf.Clamp(face.y, -1f, 1f)) * Mathf.Rad2Deg;
                var level = Vector3.ProjectOnPlane(Vector3.up, face);
                var tilted = Vector3.ProjectOnPlane(up, face);
                float roll = level.sqrMagnitude > 1e-6f && tilted.sqrMagnitude > 1e-6f
                    ? Vector3.SignedAngle(level, tilted, face)
                    : 0f;

                var cam = Camera.main;
                float toCamera = cam != null ? Vector3.Angle(_head.rotation * _faceLocal, cam.transform.forward) : -1f;
                return new Vector4(yaw, pitch, roll, toCamera);
            }

            public string Write(string error)
            {
                Directory.CreateDirectory(_job.folder);
                File.WriteAllText(Path.Combine(_job.folder, "live.csv"), _csv.ToString());

                if (error != null)
                    _failures.Insert(0, error);

                var md = new StringBuilder();
                md.AppendLine("# Live check: " + (string.IsNullOrEmpty(_job.scene) ? "floor scene" : _job.scene));
                md.AppendLine();
                md.AppendLine($"- When: {DateTime.Now:u}");
                md.AppendLine($"- Player: {(_animal != null ? _animal.name : "not found")}; IK manager " +
                              (_job.noIK ? "off" : "on"));
                md.AppendLine($"- Frames: {_frames} in {Now - _firstTime:0.0} s of play");
                md.AppendLine($"- Result: **{(Failed ? "FAIL" : "PASS")}**");
                md.AppendLine();

                if (_failures.Count > 0)
                {
                    md.AppendLine("## Failures");
                    foreach (var f in _failures)
                        md.AppendLine("- " + f);
                    md.AppendLine();
                }

                if (_notes.Count > 0)
                {
                    md.AppendLine("## Notes");
                    foreach (var n in _notes)
                        md.AppendLine("- " + n);
                    md.AppendLine();
                }

                md.AppendLine("## Jumps");
                md.AppendLine("| Jump | Took off | Highest | Landed after | Ended in |");
                md.AppendLine("|---|---|---|---|---|");
                foreach (var j in _jumps)
                    md.AppendLine($"| {j.name} | {(j.tookOff ? "yes" : "no")} | {(j.apexY - j.startY):+0.00} m | " +
                                  $"{(j.landedAt >= 0f ? j.landedAt.ToString("0.00") + " s" : "**not landed**")} | " +
                                  $"{j.endState} |");
                md.AppendLine();

                md.AppendLine("## Head while standing (camera behind)");
                if (_headStanding.Count > 0)
                {
                    md.AppendLine("Degrees. Yaw: right +. Pitch: down +. Roll: against level. To camera: face against " +
                                  "the camera's view direction.");
                    md.AppendLine();
                    md.AppendLine("| | Yaw | Pitch | Roll | To camera |");
                    md.AppendLine("|---|---|---|---|---|");
                    md.AppendLine("| average | " + Row(average: true) + " |");
                    md.AppendLine("| widest | " + Row(average: false) + " |");
                }
                else
                {
                    md.AppendLine("No samples.");
                }

                md.AppendLine();
                md.AppendLine("## States");
                md.AppendLine("```");
                foreach (var line in _timeline)
                    md.AppendLine(line);
                md.AppendLine("```");

                string path = Path.Combine(_job.folder, "live.md");
                File.WriteAllText(path, md.ToString());
                return path;
            }

            string Row(bool average)
            {
                string Cell(Func<Vector4, float> c)
                {
                    var values = _headStanding.Select(c).ToList();
                    float value = average ? values.Average() : values.OrderByDescending(x => Mathf.Abs(x)).First();
                    return value.ToString("0.0", CultureInfo.InvariantCulture);
                }

                return string.Join(" | ", Cell(v => v.x), Cell(v => v.y), Cell(v => v.z), Cell(v => v.w));
            }

            static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static IEnumerable<string> Names(string list) =>
            (list ?? "").Split(';').Select(n => n.Trim()).Where(n => n.Length > 0);

        static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name, false);
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            return null;
        }

        /// <summary>Layer-0-and-up animator state names by full path hash, and tag names by hash.</summary>
        static Dictionary<int, string> AnimatorNames(Animator animator, out Dictionary<int, string> tags)
        {
            var states = new Dictionary<int, string>();
            tags = new Dictionary<int, string> { [0] = "" };
            var runtime = animator.runtimeAnimatorController;
            if (runtime is AnimatorOverrideController overrides)
                runtime = overrides.runtimeAnimatorController;
            var controller = runtime as UnityEditor.Animations.AnimatorController;
            if (controller == null)
                return states;

            var found = tags;
            void Walk(UnityEditor.Animations.AnimatorStateMachine machine, string path)
            {
                foreach (var child in machine.states)
                {
                    string full = path + "." + child.state.name;
                    states[Animator.StringToHash(full)] = full.Substring(full.IndexOf('.') + 1);
                    if (!string.IsNullOrEmpty(child.state.tag))
                        found[Animator.StringToHash(child.state.tag)] = child.state.tag;
                }

                foreach (var sub in machine.stateMachines)
                    Walk(sub.stateMachine, path + "." + sub.stateMachine.name);
            }

            foreach (var layer in controller.layers)
                Walk(layer.stateMachine, layer.name);
            return states;
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string LayerNames(LayerMask mask)
        {
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask.value & (1 << i)) != 0)
                    names.Add(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? i.ToString() : LayerMask.LayerToName(i));
            }

            return names.Count > 0 ? string.Join(", ", names) : "none";
        }
    }
}
#endif
