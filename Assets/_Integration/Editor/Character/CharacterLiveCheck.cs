#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CollarCali.Diagnostics;
using MalbersAnimations.Controller;
using MalbersAnimations.IK;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
// Game Creator has its own State and Character: aliases keep Malbers' names plain here.
using GC2Character = GameCreator.Runtime.Characters.Character;
using MeleePhase = GameCreator.Runtime.Melee.MeleePhase;
using MeleeSkill = GameCreator.Runtime.Melee.Skill;
using MeleeStance = GameCreator.Runtime.Melee.MeleeStance;
using MeleeWeapon = GameCreator.Runtime.Melee.MeleeWeapon;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Plays the player character for a few seconds and reports what it did, so a change to the rig is
    /// judged by what happens in play mode and not only by the poses the Fit Check samples.
    ///
    /// It stands still, then jumps on the spot, jumps while walking and double-jumps, pressing Jump
    /// through the same hook as the key. For each jump it notes the take-off, the highest point and
    /// the landing, and a jump that has not landed after five seconds is reported as stuck, with the
    /// state the character is in. It checks that a tuning component holds the character's own copies of
    /// Malbers' states. While standing it measures where the head faces (the face direction comes from
    /// the avatar's T-pose) with the camera behind, with the body turned 40 degrees from the camera - the
    /// head should keep looking where the camera looks - and with the camera in front, when it should
    /// look at the camera. It lists every camera tagged MainCamera, since Malbers steers and aims by the
    /// first one it finds.
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
    public static partial class CharacterLiveCheck
    {
        const string JobKey = "CollarCali.LiveCheck.Job";
        const string MenuRoot = "Tools/CollarCali/Character/";
        const string DefaultPlayer = "Assets/_Integration/Characters/Player/PlayerCharacter.prefab";
        const string DefaultRig = "Assets/_Integration/Characters/Player/PlayerCameraRig (CM3).prefab";

        /// <summary>A jump that has not landed this long after the press is stuck.</summary>
        const float StuckSeconds = 5f;

        /// <summary>How far the face may point from where the look-at should turn it, on average (degrees).</summary>
        const float HeadOffLimit = 25f;

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
            floor.transform.localScale = new Vector3(200f, 1f, 200f);
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
        partial class Probe
        {
            enum Phase { Find, Settle, Stand, LookAside, FaceCamera, JumpStill, Rest1, WalkJump, Rest2, DoubleJump, Attack, MalbersMode, Melee, Done }

            /// <summary>How far the body is turned from the camera to see the head follow it (degrees).</summary>
            const float AsideAngle = 40f;

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
            readonly List<Vector4> _headFacing = new List<Vector4>();
            readonly List<Vector4> _headAside = new List<Vector4>();
            Quaternion _turnedFrom;

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
                public int takeoffCuesAtStart, takeoffCues = -1;
            }

            /// <summary>The animation's own take-off message (muted by the jump tuning's instant take-off).</summary>
            const string TakeoffCue = "ActivateJump";

            AnimatorTrafficProbe _traffic;

            // Game Creator melee, on the spike's player only.
            GC2Character _gc2;
            CollarCali.Combat.MAnimalGC2Bridge _bridge;
            MeleeStance _stance;
            MeleeWeapon _weapon;
            MeleeSkill _skill;
            Transform _hand;
            readonly List<Vector3> _handIdle = new List<Vector3>();
            AttackResult _attack;

            // Walking: how far 1.2 s of forward input carries the body (root motion).
            Vector3 _walkStart;
            float _walked = -1f;

            // One of Malbers' own modes (its fist attack), which must still start and finish.
            Mode _mode;
            float _modeStartedAt = -1f, _modeTook = -1f, _modeHandReach;
            bool _modeStarted, _modeSeenPlaying;

            class AttackResult
            {
                public string skill;
                public float startedAt, endedAt = -1f;
                public readonly List<string> phases = new List<string>();
                public float handReach, handIdleSpread, lunge;
                public Vector3 startPosition;
                public readonly HashSet<string> malbersStates = new HashSet<string>();
                public string error;
            }

            public bool Failed => _failures.Count > 0;

            // Warnings and errors logged while the check plays, by text (first line), with counts.
            readonly Dictionary<string, int> _log = new Dictionary<string, int>();
            int _logErrors, _logWarnings;

            void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Log)
                    return;
                string line = (message ?? "").Split('\n')[0].Trim();
                if (line.StartsWith("[LiveCheck]", StringComparison.Ordinal))
                    return;
                if (type == LogType.Warning) _logWarnings++; else _logErrors++;
                string key = (type == LogType.Warning ? "warning: " : "error: ") + (line.Length > 160 ? line.Substring(0, 160) : line);
                _log[key] = (_log.TryGetValue(key, out int n) ? n : 0) + 1;
            }

            public Probe(Job job)
            {
                _job = job;
                // The Editor otherwise runs flat out in batch mode; a 144 Hz screen is what players see.
                QualitySettings.vSyncCount = 0;
                Application.logMessageReceived += OnLog;
                Application.targetFrameRate = 144;
                _csv.AppendLine("time,phase,state,grounded,height,velocityY,useGravity,headYaw,headPitch,headRoll,headToCamera," +
                                "animState,animTag,animTime,inTransition,jumpProfile,jumpLogic,jumpLogicTime,jumpTime,inCore," +
                                "handX,handY,handZ,meleePhase,gestureWeight,outputWeight");
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
                            // The body turned a little away from the camera, which keeps its own direction:
                            // the head should keep looking where the camera looks.
                            var t = _animal.transform;
                            _turnedFrom = t.rotation;
                            t.rotation = Quaternion.AngleAxis(AsideAngle, t.up) * t.rotation;
                            Enter(Phase.LookAside);
                        }

                        break;

                    case Phase.LookAside:
                        if (InPhase > 2f)
                        {
                            // Turned to face the camera: Malbers' "Look At Camera" should turn the head to it.
                            var t = _animal.transform;
                            t.rotation = Quaternion.AngleAxis(180f, t.up) * _turnedFrom;
                            Enter(Phase.FaceCamera);
                        }

                        break;

                    case Phase.FaceCamera:
                        if (InPhase > 2f)
                        {
                            _animal.transform.rotation = _turnedFrom;
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
                            Move(new Vector3(0f, 0f, 1f));
                            _walkStart = _animal.transform.position;
                            Enter(Phase.WalkJump);
                        }

                        break;

                    case Phase.WalkJump:
                        if (_jump == null && _walked < 0f && InPhase > 1.2f)
                        {
                            _walked = Vector3.ProjectOnPlane(_animal.transform.position - _walkStart, Vector3.up).magnitude;
                            StartJump("Jump while walking");
                        }
                        if (_jump != null)
                        {
                            ReleaseAfter(0.15f);
                            if (WatchJump())
                            {
                                Move(Vector3.zero);
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
                            Enter(_gc2 != null ? Phase.Attack : Phase.MalbersMode);
                        break;

                    case Phase.Attack:
                        if (_attack == null && InPhase > 0.6f)
                            StartAttack();
                        if (_attack != null && WatchAttack())
                            Enter(Phase.MalbersMode);
                        break;

                    case Phase.MalbersMode:
                        if (InPhase > 0.6f && WatchMalbersMode())
                            Enter(_gc2 != null && _bridge != null ? Phase.Melee : Phase.Done);
                        break;

                    case Phase.Melee:
                        if (StepMelee())
                            Enter(Phase.Done);
                        break;

                    case Phase.Done:
                        JudgeHead();
                        JudgeAnimator();
                        JudgeMelee();
                        return true;
                }

                return false;
            }

            /// <summary>A twisted head is off by about 90 degrees; Steve's own look is within 7.</summary>
            void JudgeHead()
            {
                void Judge(string label, List<Vector4> samples)
                {
                    if (samples.Count > 0 && samples.Average(v => v.w) > HeadOffLimit)
                        _failures.Add($"Head with the {label}: the face is {samples.Average(v => v.w):0} degrees off " +
                                      $"where it should look (limit {HeadOffLimit:0}).");
                }

                Judge("camera behind", _headStanding);
                Judge($"body turned {AsideAngle:0} degrees from the camera", _headAside);
                Judge("camera in front", _headFacing);
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

                // Before the first jump: Malbers' behaviours look for listeners the first time they run.
                _traffic = _animal.GetComponent<AnimatorTrafficProbe>();
                if (_traffic == null)
                    _traffic = _animal.gameObject.AddComponent<AnimatorTrafficProbe>();

                _hand = _animal.Anim.GetBoneTransform(HumanBodyBones.RightHand);
                _gc2 = _animal.GetComponent<GC2Character>();
                if (_gc2 != null)
                {
                    _weapon = FindAsset<MeleeWeapon>("Brawl_Weapon");
                    _skill = FindAsset<MeleeSkill>("Brawl@Combo1");
                    var bridge = _bridge = _animal.GetComponent<CollarCali.Combat.MAnimalGC2Bridge>();
                    _notes.Add("Game Creator Character on the player (" + _gc2.Kernel.Animim?.GetType().Name + "); " +
                               $"its animation graph {(_gc2.AnimationGraph.IsValid() ? "drives" : "does not drive")} the Animator " +
                               $"(update mode {_animal.Anim.updateMode}); wiring: {(bridge != null ? bridge.WiringReport : "no bridge")}.");
                }

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

                var rotation = PlayerCharacterBuilder.TPoseRotation(_animal.transform, _head, anim.avatar);
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
                    takeoffCuesAtStart = _traffic != null ? _traffic.Count(TakeoffCue) : 0,
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

            /// <summary>
            /// Pushes the stick: Malbers' input link hands its own move axis to the character every
            /// frame, so that is the one to set (the character's own is set too, for a character without one).
            /// </summary>
            void Move(Vector3 axis)
            {
                var link = _animal.GetComponent<MalbersAnimations.InputSystem.MInputLink>();
                if (link != null)
                    link.MoveAxis = axis;
                _animal.SetInputAxis(axis);
            }

            /// <summary>Plays one Game Creator melee skill, the way a key press would end up doing.</summary>
            void StartAttack()
            {
                _attack = new AttackResult
                {
                    skill = _skill != null ? _skill.name : "none",
                    startedAt = Now,
                    startPosition = _animal.transform.position,
                };

                if (_weapon == null || _skill == null)
                {
                    _attack.error = "Brawl_Weapon or Brawl@Combo1 was not found in the project.";
                    return;
                }

                try
                {
                    _stance = _gc2.Combat.RequestStance<MeleeStance>();
                    _stance.PlaySkill(_weapon, _skill, null);
                }
                catch (Exception e)
                {
                    _attack.error = e.GetType().Name + ": " + e.Message;
                }
            }

            /// <summary>Follows the attack; true once Game Creator says it is over, or after 5 s.</summary>
            bool WatchAttack()
            {
                if (_attack.error != null)
                {
                    _failures.Add("Melee: " + _attack.error);
                    _attack.endedAt = 0f;
                    return true;
                }

                string phase = _stance != null ? _stance.CurrentPhase.ToString() : "none";
                if (_attack.phases.Count == 0 || _attack.phases[_attack.phases.Count - 1] != phase)
                    _attack.phases.Add(phase);
                _attack.malbersStates.Add(StateName());

                if (_hand != null && _handIdle.Count > 0)
                {
                    var local = _animal.transform.InverseTransformPoint(_hand.position);
                    _attack.handReach = Mathf.Max(_attack.handReach, (local - Mean(_handIdle)).magnitude);
                }

                var moved = Vector3.ProjectOnPlane(_animal.transform.position - _attack.startPosition, Vector3.up);
                _attack.lunge = Mathf.Max(_attack.lunge, moved.magnitude);

                float since = Now - _attack.startedAt;
                bool over = since > 0.2f && _stance != null && _stance.CurrentPhase == MeleePhase.None;
                if (!over && since <= 5f)
                    return false;

                _attack.endedAt = since;
                if (!over)
                    _failures.Add($"Melee: {_attack.skill} is still in {phase} 5 s after it started.");
                if (_handIdle.Count > 0)
                {
                    var mean = Mean(_handIdle);
                    _attack.handIdleSpread = _handIdle.Max(p => (p - mean).magnitude);
                }

                return true;
            }

            /// <summary>
            /// Plays Malbers' own fist attack (the mode on its Attack1 input) and follows it; true once it
            /// has finished, or after 4 s. Game Creator's graph makes its own copy of the Animator
            /// Controller after Malbers has set up its mode behaviours, so this checks they still work.
            /// </summary>
            bool WatchMalbersMode()
            {
                if (!_modeStarted)
                {
                    _modeStarted = true;
                    _mode = _animal.modes.FirstOrDefault(m => m != null && m.ID != null && m.Input == "Attack1") ??
                            _animal.modes.FirstOrDefault(m => m != null && m.ID != null && m.ID.name.Contains("Attack"));
                    if (_mode == null)
                    {
                        _notes.Add("No Malbers attack mode on this character to try.");
                        return true;
                    }

                    _modeStartedAt = Now;
                    if (!_animal.Mode_TryActivate(_mode.ID.ID))
                    {
                        _failures.Add($"Malbers' mode {_mode.ID.name} would not start.");
                        return true;
                    }

                    return false;
                }

                if (_animal.IsPlayingMode)
                    _modeSeenPlaying = true;
                if (_hand != null && _handIdle.Count > 0)
                {
                    var local = _animal.transform.InverseTransformPoint(_hand.position);
                    _modeHandReach = Mathf.Max(_modeHandReach, (local - Mean(_handIdle)).magnitude);
                }

                float since = Now - _modeStartedAt;
                if (_modeSeenPlaying && !_animal.IsPlayingMode && since > 0.2f)
                {
                    _modeTook = since;
                    return true;
                }

                if (since > 4f)
                {
                    _failures.Add(_modeSeenPlaying
                        ? $"Malbers' mode {_mode.ID.name} was still playing 4 s after it started."
                        : $"Malbers' mode {_mode.ID.name} never played.");
                    return true;
                }

                return false;
            }

            static Vector3 Mean(List<Vector3> points)
            {
                var sum = Vector3.zero;
                foreach (var p in points)
                    sum += p;
                return points.Count > 0 ? sum / points.Count : Vector3.zero;
            }

            static T FindAsset<T>(string name) where T : Object
            {
                foreach (string guid in AssetDatabase.FindAssets(name + " t:" + typeof(T).Name))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
                    if (asset != null && asset.name == name)
                        return asset;
                }

                return null;
            }

            /// <summary>
            /// Malbers' animation behaviours and root motion must run once, inside the physics step: a
            /// second copy of the Animator Controller, or an Animator moved off the physics step, shows
            /// here first.
            /// </summary>
            void JudgeAnimator()
            {
                if (_traffic == null)
                    return;

                int outside = _traffic.AnimatorMoves - _traffic.AnimatorMovesInPhysics;
                if (outside > 0)
                    _failures.Add($"The Animator moved the body outside the physics step {outside} times " +
                                  $"(of {_traffic.AnimatorMoves}): Malbers moves it in the physics step only.");
                if (_traffic.MostMovesInOneStep > 1)
                    _failures.Add($"The Animator moved the body {_traffic.MostMovesInOneStep} times in one physics step.");

                var tuning = _animal.GetComponent<ThirdPersonJumpTuning>();
                bool instant = tuning != null && tuning.isActiveAndEnabled && PlayerTuning.Active.thirdPersonJump.instantTakeoff;
                foreach (var jump in _jumps)
                {
                    if (jump.name == "Double jump" || jump.takeoffCues < 0)
                        continue;
                    int expected = instant ? 0 : 1;
                    if (jump.takeoffCues != expected)
                        _failures.Add($"{jump.name}: the animation's take-off cue reached the character {jump.takeoffCues} " +
                                      $"times, not {expected}" + (instant ? " (the jump tuning mutes it)" : "") +
                                      ": Malbers' animation behaviours ran more than once.");
                }
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
                    _jump.takeoffCues = _traffic != null ? _traffic.Count(TakeoffCue) - _jump.takeoffCuesAtStart : -1;
                    _jump = null;
                    return true;
                }

                if (since > StuckSeconds)
                {
                    var rb = _animal.RB;
                    _jump.endState = StateName();
                    _jump.takeoffCues = _traffic != null ? _traffic.Count(TakeoffCue) - _jump.takeoffCuesAtStart : -1;
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

                var head = HeadAngles(towardCamera: false);
                if (_phase == Phase.Stand && InPhase > 0.5f && head.HasValue)
                    _headStanding.Add(head.Value);
                if (_phase == Phase.Stand && InPhase > 0.5f && _hand != null)
                    _handIdle.Add(_animal.transform.InverseTransformPoint(_hand.position));
                if (_phase == Phase.LookAside && InPhase > 0.8f && head.HasValue)
                    _headAside.Add(head.Value);
                if (_phase == Phase.FaceCamera && InPhase > 0.8f && HeadAngles(towardCamera: true) is Vector4 facing)
                    _headFacing.Add(facing);

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
                else
                {
                    _csv.Append(",,,,,");
                }

                // The right hand in the body's space, and Game Creator's melee phase and gesture weight.
                var hand = _hand != null ? _animal.transform.InverseTransformPoint(_hand.position) : Vector3.zero;
                _csv.Append(',').Append(F(hand.x)).Append(',').Append(F(hand.y)).Append(',').Append(F(hand.z)).Append(',')
                    .Append(_stance != null ? _stance.CurrentPhase.ToString() : "").Append(',')
                    .Append(_gc2 != null && _gc2.Gestures != null ? F(_gc2.Gestures.CurrentWeight) : "").Append(',')
                    .Append(_bridge != null ? F(_bridge.OverlayWeight) : "");

                _csv.AppendLine();
            }

            static string Name(Dictionary<int, string> names, int hash) =>
                names.TryGetValue(hash, out var name) ? name : hash.ToString(CultureInfo.InvariantCulture);

            /// <summary>
            /// Yaw (right +), pitch (down +) and roll of the face against the body, and the angle between
            /// the face and the camera's forward - or, <paramref name="towardCamera"/>, the direction to
            /// the camera. Null without a head.
            /// </summary>
            Vector4? HeadAngles(bool towardCamera)
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
                var worldFace = _head.rotation * _faceLocal;
                float toCamera = cam == null ? -1f
                    : towardCamera ? Vector3.Angle(worldFace, cam.transform.position - _head.position)
                    : Vector3.Angle(worldFace, cam.transform.forward);
                return new Vector4(yaw, pitch, roll, toCamera);
            }

            public string Write(string error)
            {
                Application.logMessageReceived -= OnLog;
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
                md.AppendLine("| Jump | Took off | Highest | Landed after | Ended in | Take-off cues |");
                md.AppendLine("|---|---|---|---|---|---|");
                foreach (var j in _jumps)
                    md.AppendLine($"| {j.name} | {(j.tookOff ? "yes" : "no")} | {(j.apexY - j.startY):+0.00} m | " +
                                  $"{(j.landedAt >= 0f ? j.landedAt.ToString("0.00") + " s" : "**not landed**")} | " +
                                  $"{j.endState} | {(j.takeoffCues >= 0 ? j.takeoffCues.ToString() : "-")} |");
                md.AppendLine();

                if (_traffic != null)
                {
                    md.AppendLine("## Animator");
                    md.AppendLine($"- Root-motion callbacks: {_traffic.AnimatorMoves}, {_traffic.AnimatorMovesInPhysics} of them " +
                                  $"inside a physics step; {_traffic.PhysicsSteps} physics steps, at most " +
                                  $"{_traffic.MostMovesInOneStep} in one.");
                    md.AppendLine("- Messages from Malbers' animation behaviours: " + (_traffic.Messages.Count == 0
                        ? "none."
                        : string.Join(", ", _traffic.Messages.OrderBy(m => m.Key).Select(m => $"{m.Key} x{m.Value}")) + "."));
                    md.AppendLine();
                }

                if (_walked >= 0f)
                {
                    md.AppendLine("## Walking");
                    md.AppendLine($"- 1.2 s of forward input carried the body {_walked:0.00} m.");
                    md.AppendLine();
                }

                if (_mode != null)
                {
                    md.AppendLine("## Malbers mode");
                    md.AppendLine($"- {_mode.ID.name}: {(_modeTook >= 0f ? "played and finished in " + _modeTook.ToString("0.00") + " s" : "did not finish")}, " +
                                  $"right hand moved {_modeHandReach:0.00} m.");
                    md.AppendLine();
                }

                if (_attack != null)
                {
                    md.AppendLine("## Melee (Game Creator)");
                    md.AppendLine("| Skill | Phases | Right hand moved | Body moved | Malbers states | Took |");
                    md.AppendLine("|---|---|---|---|---|---|");
                    md.AppendLine($"| {_attack.skill} | {string.Join(" > ", _attack.phases)} | " +
                                  $"{_attack.handReach:0.00} m (idle sway {_attack.handIdleSpread:0.00} m) | {_attack.lunge:0.00} m | " +
                                  $"{string.Join(", ", _attack.malbersStates)} | " +
                                  $"{(_attack.endedAt >= 0f ? _attack.endedAt.ToString("0.00") + " s" : "-")} |");
                    md.AppendLine();
                }

                md.AppendLine("## Head");
                md.AppendLine("Degrees, against the body. Yaw: right +. Pitch: down +. Roll: against level. Off: the " +
                              "face against where it should look - the camera's view direction with the camera " +
                              "behind or the body turned from it, the camera itself with the camera in front.");
                md.AppendLine();
                md.AppendLine("| | Yaw | Pitch | Roll | Off |");
                md.AppendLine("|---|---|---|---|---|");
                HeadRows(md, "camera behind", _headStanding);
                HeadRows(md, $"body turned {AsideAngle:0}° from the camera", _headAside);
                HeadRows(md, "camera in front", _headFacing);

                md.AppendLine();
                WriteMelee(md);

                md.AppendLine("## Log");
                md.AppendLine($"- {_logErrors} errors and {_logWarnings} warnings while playing" + (_log.Count > 0 ? "; the most frequent:" : "."));
                foreach (var entry in _log.OrderByDescending(e => e.Value).Take(12))
                    md.AppendLine($"  - x{entry.Value} {entry.Key}");
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

            static void HeadRows(StringBuilder md, string label, List<Vector4> samples)
            {
                if (samples.Count == 0)
                {
                    md.AppendLine($"| {label} | no samples | | | |");
                    return;
                }

                string Cell(Func<Vector4, float> c, bool average)
                {
                    var values = samples.Select(c).ToList();
                    float value = average ? values.Average() : values.OrderByDescending(x => Mathf.Abs(x)).First();
                    return value.ToString("0.0", CultureInfo.InvariantCulture);
                }

                foreach (bool average in new[] { true, false })
                    md.AppendLine($"| {label}, {(average ? "average" : "widest")} | " +
                                  string.Join(" | ", Cell(v => v.x, average), Cell(v => v.y, average),
                                      Cell(v => v.z, average), Cell(v => v.w, average)) + " |");
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
