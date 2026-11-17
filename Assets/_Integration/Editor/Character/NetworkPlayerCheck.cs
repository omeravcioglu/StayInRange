#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Fusion;
using MalbersAnimations.InputSystem;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using LogType = UnityEngine.LogType;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// The networked player in a real Fusion session, run on this machine alone (single-player mode:
    /// no Photon Cloud, and this machine has state authority, so every owner path runs as it does
    /// for a player in a room). It spawns Assets/_Integration/Prefabs/NetworkedFPSPlayer.prefab on a
    /// floor, as FusionConnection does, and checks with the controller Resources/PlayerRigRecipe
    /// picks:
    ///   - the third-person player is made for the owner and drives the networked root (which the
    ///     collar measures), standing and walking, and after a teleport;
    ///   - the Cowsins rig the prefab carries is parked out of the way;
    ///   - damage, a hit on the character itself, death, a revive and the collar's yank (kill and
    ///     respawn at the team's spawn) all go through the player's own vitals and reach the
    ///     networked health and IsDead;
    ///   - a hazard can tell which player the detached character belongs to;
    ///   - a creep's grip stops the character moving and attacking until it lets go.
    /// Batch (Editor closed): -executeMethod CollarCali.EditorTools.NetworkPlayerCheck.RunBatch, no
    /// -quit (it exits itself: 0 pass, 1 failures, 2 timed out, 3 could not start). Tools >
    /// CollarCali > Multiplayer > Network Player Check runs it in the open Editor. Report:
    /// Builds/CharacterFit/&lt;time&gt;-network-player/network.md.
    ///
    /// -networkShared runs it in Fusion's shared mode instead, the game's own, in a private closed room
    /// on Photon Cloud with this machine alone in it. Single-player mode gives every object to the
    /// server (PlayerRef.None), so whatever is decided by player - who holds a prop, who carries a body
    /// - can only be checked in shared mode, where the player owns its own object as in a game.
    /// </summary>
    [InitializeOnLoad]
    public static class NetworkPlayerCheck
    {
        const string JobKey = "CollarCali.NetworkPlayerCheck.Job";
        const string PlayerPrefabPath = "Assets/_Integration/Prefabs/NetworkedFPSPlayer.prefab";
        const double WatchdogSeconds = 150;

        [Serializable]
        class Job
        {
            public string folder;
            public bool batch;
            public bool shared;
            public double requestedAt;
        }

        static Job _job;
        static Probe _probe;

        static NetworkPlayerCheck()
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

        [MenuItem("Tools/CollarCali/Multiplayer/Network Player Check (local session)")]
        static void RunFromMenu()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Network Player Check", "Stop play mode first.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            BuildScene();
            Start(new Job { batch = false });
        }

        public static void RunBatch()
        {
            try
            {
                BuildScene();
            }
            catch (Exception e)
            {
                Debug.LogError("[NetworkPlayerCheck] Could not set up the scene: " + e);
                EditorApplication.Exit(3);
                return;
            }

            Start(new Job { batch = true, shared = Environment.GetCommandLineArgs().Contains("-networkShared") });
        }

        /// <summary>A floor and a light, in an unsaved scene: the session spawns the player.</summary>
        static void BuildScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var sun = new GameObject("Sun", typeof(Light));
            sun.GetComponent<Light>().type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(200f, 1f, 200f);
            floor.transform.position = new Vector3(0f, -0.5f, 0f);
        }

        static void Start(Job job)
        {
            job.folder = CharacterFitCheck.NewReportFolder("network-player");
            job.requestedAt = EditorApplication.timeSinceStartup;
            _job = job;
            SessionState.SetString(JobKey, JsonUtility.ToJson(job));
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
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
                Finish("Gave up: the check did not finish within " + WatchdogSeconds + " s.", 2);
                return;
            }

            if (!EditorApplication.isPlaying)
                return;

            if (_probe == null)
                _probe = new Probe(_job.folder, _job.shared);

            if (_probe.Step())
                Finish(_probe.Error, _probe.Error != null ? 3 : _probe.Failed ? 1 : 0);
        }

        static void Finish(string error, int code)
        {
            var job = _job;
            _job = null;
            SessionState.EraseString(JobKey);
            EditorApplication.update -= Tick;

            string report = Path.Combine(job != null ? job.folder : "Builds", "network.md");
            try
            {
                File.WriteAllText(report, _probe != null ? _probe.Report(error) : "# Network player check\n\n**" + error + "**\n");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            _probe?.Dispose();
            _probe = null;
            Debug.Log("[NetworkPlayerCheck] " + (code == 0 ? "PASS" : "FAIL") + (error != null ? " - " + error : "") + "\nReport: " + report);

            if (job != null && job.batch)
            {
                EditorApplication.Exit(code);
                return;
            }

            if (EditorApplication.isPlaying)
                EditorApplication.ExitPlaymode();
        }

        sealed class Probe : IDisposable
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            readonly List<(string check, bool pass, string detail)> _checks = new List<(string, bool, string)>();
            readonly List<string> _notes = new List<string>();
            readonly Dictionary<string, int> _log = new Dictionary<string, int>();
            readonly List<(float at, Func<bool> step, string name)> _plan = new List<(float, Func<bool>, string)>();
            int _errors;

            NetworkRunner _runner;
            Task<StartGameResult> _start;
            PlayerNetworkBridge _bridge;
            ThirdPersonPlayer _tps;
            float _t0 = -1f;
            int _next;
            int _frameInStep;
            float _stepStarted;
            float _maxHealth;

            public string Error { get; private set; }
            public bool Failed => _checks.Exists(c => !c.pass);

            float Now => Time.realtimeSinceStartup;

            readonly string _folder;
            readonly bool _shared;

            public Probe(string folder, bool shared)
            {
                _folder = folder;
                _shared = shared;
                Application.logMessageReceived += OnLog;
                Plan();
            }

            public void Dispose()
            {
                Application.logMessageReceived -= OnLog;
                PlayerNetworkBridge.MirrorWeaponsOnOwner = false;
            }

            void OnLog(string message, string stack, LogType type)
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                    return;
                _errors++;
                string key = message.Length > 180 ? message.Substring(0, 180) : message;
                _log[key] = _log.TryGetValue(key, out int n) ? n + 1 : 1;
            }

            void Check(string check, bool pass, string detail = null) => _checks.Add((check, pass, detail ?? string.Empty));

            /// <summary>Each step runs until it returns true (or gives up after its time).</summary>
            void Then(string name, Func<bool> step, float timeout = 8f) => _plan.Add((timeout, step, name));

            /// <summary>One frame; true when the check is over.</summary>
            public bool Step()
            {
                if (_t0 < 0f)
                {
                    _t0 = Now;
                    _stepStarted = Now;
                }

                if (Error != null || _next >= _plan.Count)
                    return true;

                var (timeout, step, name) = _plan[_next];
                _frameInStep++;
                bool done;
                try
                {
                    done = step();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Check(name, false, e.GetType().Name + ": " + e.Message);
                    done = true;
                }

                if (!done && Now - _stepStarted > timeout)
                {
                    Check(name, false, $"timed out after {timeout:0} s");
                    done = true;
                }

                if (done)
                {
                    _next++;
                    _frameInStep = 0;
                    _stepStarted = Now;
                }

                return Error != null || _next >= _plan.Count;
            }

            float InStep => Now - _stepStarted;

            /// <summary>The first frame of the current step: where its one-off action goes.</summary>
            bool First => _frameInStep == 1;

            void Plan()
            {
                Then("a local Fusion session starts", StartSession, 30f);
                Then("the networked player spawns and makes its controller", SpawnPlayer, 15f);
                Then("settled", () => InStep > 1.5f);
                Then("checks after spawning", CheckSpawned);
                Then("teleported", () =>
                {
                    if (First)
                        _bridge.AuthorityTeleport(new Vector3(3f, 0f, 2f), 90f);
                    return InStep > 0.6f;
                });
                Then("checks after the teleport", CheckTeleport);
                Then("walked", Walk, 4f);
                Then("hurt", Hurt, 3f);
                Then("held by a creep", CreepGrip, 4f);
                Then("killed", Kill, 4f);
                Then("revived", Revive, 4f);
                Then("yanked by the collar", Yank, 5f);
                if (_shared)
                {
                    Then("a prop is set up to lift", SetUpProp, 6f);
                    Then("lifted with telekinesis", LiftProp, 5f);
                    Then("dropped", DropProp, 3f);
                }
                else
                {
                    _notes.Add("Telekinesis is checked in shared mode only (-networkShared): single-player mode gives every object to the server, so nothing can name the player as its holder.");
                }
                Then("the body the others see takes Malbers' animation", RemoteBodyReady, 4f);
                Then("standing", () => Window("standing", 1.2f, null));
                Then("walking", () => Window("walking", 1.4f, () => Move(Vector3.forward), () => Move(Vector3.zero)));
                Then("punching", () => Window("punching with the fists", 1.2f, () => { if (First) _tps.TryAttack(); }));
                Then("jumping", () => Window("jumping", 1.3f, () => { if (First) _tps.Animal.State_Activate(MalbersAnimations.StateEnum.Jump); }));
                Then("drawing the sword", DrawSword, 3f);
                Then("swinging the sword", () => Window("swinging the sword", 1.2f, () => { if (First) _tps.TryAttack(); }));
                Then("the sword on the body the others see", CheckRemoteSword, 2f);
                Then("pictured", () => { if (First) PictureBodies("remote body"); return true; });
                Then("judged", JudgeBodies);
            }

            bool StartSession()
            {
                if (_start == null)
                {
                    var go = new GameObject("Network check runner");
                    _runner = go.AddComponent<NetworkRunner>();
                    _runner.ProvideInput = false;
                    var scenes = go.AddComponent<NetworkSceneManagerDefault>();
                    _start = _runner.StartGame(new StartGameArgs
                    {
                        GameMode = _shared ? GameMode.Shared : GameMode.Single,
                        // A room of its own that nobody can find or join.
                        SessionName = "collarcali-check-" + Guid.NewGuid().ToString("N"),
                        IsVisible = false,
                        IsOpen = false,
                        PlayerCount = 1,
                        SceneManager = scenes,
                    });
                    return false;
                }

                if (!_start.IsCompleted)
                    return false;

                var result = _start.Result;
                if (!result.Ok)
                {
                    Error = $"Fusion did not start a local session: {result.ShutdownReason} {result.ErrorMessage}";
                    return true;
                }

                _notes.Add($"Fusion {(_shared ? GameMode.Shared + " session (a private room on Photon Cloud)" : GameMode.Single + " session")}, local player {_runner.LocalPlayer}.");
                return true;
            }

            bool SpawnPlayer()
            {
                if (_bridge == null)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
                    var networkObject = prefab != null ? prefab.GetComponent<NetworkObject>() : null;
                    if (networkObject == null)
                    {
                        Error = "No NetworkObject at " + PlayerPrefabPath;
                        return true;
                    }

                    var spawned = _runner.Spawn(networkObject, new Vector3(0f, 0.05f, 0f), Quaternion.identity);
                    if (spawned == null)
                    {
                        Error = "Fusion did not spawn " + PlayerPrefabPath;
                        return true;
                    }

                    _runner.SetPlayerObject(_runner.LocalPlayer, spawned);
                    _bridge = spawned.GetComponent<PlayerNetworkBridge>();
                    var recipe = PlayerRigRecipe.Load();
                    _notes.Add($"Spawned {prefab.name}; Resources/PlayerRigRecipe uses the third-person player: {(recipe != null && recipe.useThirdPersonPlayer ? "yes" : "no")}.");
                    return false;
                }

                _tps = _bridge.ThirdPerson;
                return _tps != null && _tps.IsRunning;
            }

            T Field<T>(object owner, string name) where T : class
            {
                var field = owner?.GetType().GetField(name, Private);
                return field?.GetValue(owner) as T;
            }

            bool CheckSpawned()
            {
                Check("the recipe's third-person player is made for the owner", _tps != null,
                    _tps != null ? _tps.name : "none");
                var dual = _bridge.GetComponent<DualPlayerController>();
                Check("the old DualPlayerController is off", dual == null || !dual.enabled);
                Check("the bridge drives the networked root from the character", _bridge.IsMalbersThirdPerson);
                Check("the rig everyone else uses is the third-person player", ReferenceEquals(_bridge.Rig, _tps));

                var fpsRoot = Field<GameObject>(_bridge, "fpsRoot");
                var fpsBody = Field<Transform>(_bridge, "fpsBody");
                int live = 0;
                if (fpsRoot != null)
                {
                    foreach (var col in fpsRoot.GetComponentsInChildren<Collider>(true))
                    {
                        if (col.enabled && col.gameObject.activeInHierarchy)
                            live++;
                    }
                }

                Check("the Cowsins rig is parked: no live colliders, far below the level",
                    fpsRoot == null || (live == 0 && (fpsBody == null || fpsBody.position.y < -400f)),
                    fpsRoot == null ? "the prefab has no Cowsins rig" : $"{live} live collider(s), body at y {(fpsBody != null ? fpsBody.position.y : 0f):0}");

                var vitals = _tps != null ? _tps.Vitals : null;
                _maxHealth = vitals != null ? vitals.MaxHealth : 0f;
                Check("the player has vitals that report to the bridge", vitals != null && ReferenceEquals(vitals.Router, _bridge),
                    vitals != null ? $"max health {vitals.MaxHealth:0}" : "none");
                Check("the networked health starts full", vitals != null && Mathf.Approximately(_bridge.SyncedHealth, vitals.Health) && !_bridge.IsDead,
                    $"synced {_bridge.SyncedHealth:0}, vitals {(vitals != null ? vitals.Health : 0f):0}");
                var feet = _tps != null ? _tps.transform.position : Vector3.zero;
                Check("the networked root stands at the character's feet", Vector3.Distance(_bridge.transform.position, feet) < 0.05f,
                    $"{Vector3.Distance(_bridge.transform.position, feet):0.000} m apart");
                var main = _tps != null && _tps.Animal != null ? _tps.Animal.MainCollider : null;
                Check("a hazard finds the player from the detached character's collider",
                    main != null && PlayerNetworkBridge.FindByCollider(main) == _bridge);
                Check("the HUD is on the player", _bridge.GetComponent<UI.HudRoot>() != null);
                Check("telekinesis is on the player and bound to its controller", _bridge.GetComponent<BodyTelekinesis>() != null);
                return true;
            }

            bool CheckTeleport()
            {
                var feet = _tps.transform.position;
                Check("a teleport moves the character", Vector3.Distance(feet, new Vector3(3f, 0f, 2f)) < 0.25f,
                    $"at {feet}");
                Check("the networked root follows it", Vector3.Distance(_bridge.transform.position, feet) < 0.05f &&
                                                     Mathf.Abs(Mathf.DeltaAngle(_bridge.transform.eulerAngles.y, _tps.transform.eulerAngles.y)) < 1f,
                    $"{Vector3.Distance(_bridge.transform.position, feet):0.000} m, yaw {_bridge.transform.eulerAngles.y:0} / {_tps.transform.eulerAngles.y:0}");
                return true;
            }

            Vector3 _walkFrom;
            float _walkLag;

            void Move(Vector3 axis)
            {
                var link = _tps.GetComponent<MInputLink>();
                if (link != null)
                    link.MoveAxis = axis;
                _tps.Animal.SetInputAxis(axis);
            }

            bool Walk()
            {
                if (First)
                {
                    _walkFrom = _tps.transform.position;
                    _walkLag = 0f;
                }

                if (InStep < 1.4f)
                {
                    Move(Vector3.forward);
                    _walkLag = Mathf.Max(_walkLag, Vector3.Distance(_bridge.transform.position, _tps.transform.position));
                    return false;
                }

                Move(Vector3.zero);
                float walked = Vector3.Distance(_walkFrom, _tps.transform.position);
                Check("the character walks in the session", walked > 1f, $"{walked:0.00} m in 1.4 s");
                Check("the networked root keeps up while walking", _walkLag < 0.15f, $"at most {_walkLag:0.000} m behind");
                return true;
            }

            bool Hurt()
            {
                var vitals = _tps.Vitals;
                if (First)
                {
                    _bridge.Damage(30f, false);
                    return false;
                }

                if (InStep < 0.3f)
                    return false;

                bool first = Mathf.Approximately(vitals.Health, _maxHealth - 30f) && Mathf.Approximately(_bridge.SyncedHealth, vitals.Health);
                Check("damage through the bridge comes off the player's vitals and reaches the networked health", first,
                    $"vitals {vitals.Health:0}/{_maxHealth:0}, synced {_bridge.SyncedHealth:0}");
                var target = _tps.GetComponent<IDamageTarget>();
                target?.Damage(10f, false);
                Check("a hit on the character itself goes through the bridge too",
                    target != null && Mathf.Approximately(vitals.Health, _maxHealth - 40f) && Mathf.Approximately(_bridge.SyncedHealth, vitals.Health),
                    target == null ? "the character is not an IDamageTarget" : $"vitals {vitals.Health:0}, synced {_bridge.SyncedHealth:0}");
                return true;
            }

            Vector3 _gripFrom;
            bool _gripReleased;

            bool CreepGrip()
            {
                if (First)
                {
                    _gripReleased = false;
                    _tps.SetHeldByCreep(true);
                    _gripFrom = _tps.transform.position;
                    return false;
                }

                if (InStep < 1.0f)
                {
                    Move(Vector3.forward);
                    return false;
                }

                if (!_gripReleased)
                {
                    _gripReleased = true;
                    Move(Vector3.zero);
                    float moved = Vector3.Distance(_gripFrom, _tps.transform.position);
                    bool refused = !_tps.CanAttack(out string reason);
                    Check("held by a creep, the character does not walk", moved < 0.1f, $"moved {moved:0.00} m pushing forward");
                    Check("held by a creep, attacks are refused", refused, reason ?? "allowed");
                    _tps.SetHeldByCreep(false);
                    _gripFrom = _tps.transform.position;
                    return false;
                }

                if (InStep < 2.3f)
                {
                    Move(Vector3.forward);
                    return false;
                }

                Move(Vector3.zero);
                float after = Vector3.Distance(_gripFrom, _tps.transform.position);
                Check("let go, the character walks again", after > 0.5f, $"{after:0.00} m in 1.2 s");
                return true;
            }

            bool Kill()
            {
                if (First)
                {
                    _bridge.Damage(99999f, false);
                    return false;
                }

                if (InStep < 1.0f)
                    return false;

                Check("lethal damage kills the player (networked IsDead)", _bridge.IsDead && _tps.Vitals.IsDead,
                    $"IsDead {_bridge.IsDead}, vitals {_tps.Vitals.Health:0}");
                Check("a dead player's character is put away", _tps.IsSuspended && !_tps.gameObject.activeSelf);
                Check("the body everyone sees goes down as a ragdoll", _bridge.GetComponent<PlayerDownState>() != null);
                return true;
            }

            bool Revive()
            {
                if (First)
                {
                    _bridge.ReviveAt(new Vector3(-2f, 0f, 0f), 0f);
                    return false;
                }

                if (InStep < 1.2f)
                    return false;

                var vitals = _tps.Vitals;
                Check("a revive brings the player back (networked IsDead off)", !_bridge.IsDead && !vitals.IsDead);
                Check("the revived character is active and running", _tps.gameObject.activeSelf && _tps.IsRunning && !_tps.IsSuspended);
                Check("it stands where the revive put it", Vector3.Distance(_tps.transform.position, new Vector3(-2f, 0f, 0f)) < 0.3f,
                    $"at {_tps.transform.position}");
                Check("it comes back with full health", Mathf.Approximately(vitals.Health, vitals.MaxHealth) &&
                                                       Mathf.Approximately(_bridge.SyncedHealth, vitals.Health),
                    $"vitals {vitals.Health:0}/{vitals.MaxHealth:0}, synced {_bridge.SyncedHealth:0}");
                return true;
            }

            bool _sawYankDeath;

            bool Yank()
            {
                if (First)
                {
                    _sawYankDeath = false;
                    _bridge.Damage(20f, false);
                    _bridge.AuthorityTeamFailAndRespawn(new Vector3(5f, 0f, 5f), 180f);
                    return false;
                }

                if (_bridge.IsDead)
                    _sawYankDeath = true;
                if (InStep < 2.0f)
                    return false;

                var vitals = _tps.Vitals;
                Check("the collar's yank kills the player first", _sawYankDeath);
                Check("and respawns them at the team's spawn", !_bridge.IsDead && _tps.gameObject.activeSelf &&
                                                               Vector3.Distance(_tps.transform.position, new Vector3(5f, 0f, 5f)) < 0.3f,
                    $"IsDead {_bridge.IsDead}, at {_tps.transform.position}");
                Check("with full health", Mathf.Approximately(vitals.Health, vitals.MaxHealth) && Mathf.Approximately(_bridge.SyncedHealth, vitals.Health),
                    $"vitals {vitals.Health:0}/{vitals.MaxHealth:0}, synced {_bridge.SyncedHealth:0}");
                Check("the networked root is there too", Vector3.Distance(_bridge.transform.position, _tps.transform.position) < 0.05f);
                return true;
            }

            // ---- The body the others see ---------------------------------------------------------------

            /// <summary>
            /// How far the remote body's bones may be turned from the character's, on average over the
            /// humanoid bones (degrees), at the median frame and at the 90th percentile.
            /// </summary>
            const float PoseGapMedianLimit = 6f;
            const float PoseGapHighLimit = 15f;

            Animator _remoteAnimator;
            readonly List<(Transform own, Transform remote)> _bonePairs = new List<(Transform, Transform)>();
            readonly List<(string window, List<float> gaps)> _windows = new List<(string, List<float>)>();

            bool RemoteBodyReady()
            {
                _remoteAnimator = _bridge.GetComponent<Animator>();
                var recipe = PlayerRigRecipe.Load();
                var wanted = recipe != null ? recipe.remoteBodyController : null;
                bool ready = _remoteAnimator != null && wanted != null && _remoteAnimator.runtimeAnimatorController == wanted;
                if (!ready && InStep < 3.5f)
                    return false;

                // The owner's own copy is hidden and carries no weapons in a game; here it shows them.
                PlayerNetworkBridge.MirrorWeaponsOnOwner = true;
                Check("the owner publishes a Malbers body", _bridge.UsesMalbersBody);
                Check("the body the others see animates with Malbers' controller, stripped", ready,
                    wanted == null ? "PlayerRigRecipe has no remote body controller (run Build Third Person Player)"
                        : (_remoteAnimator != null && _remoteAnimator.runtimeAnimatorController != null ? _remoteAnimator.runtimeAnimatorController.name : "none"));
                var own = _tps.Animal.Anim;
                if (ready && own != null && own.avatar != null && _remoteAnimator.avatar != null)
                {
                    Check("both bodies share the avatar", own.avatar == _remoteAnimator.avatar,
                        own.avatar.name + " / " + _remoteAnimator.avatar.name);
                    // The same model under both, so each bone's turn against its parent compares directly.
                    for (var b = HumanBodyBones.Spine; b < HumanBodyBones.LastBone; b++)
                    {
                        var mine = own.GetBoneTransform(b);
                        var theirs = _remoteAnimator.GetBoneTransform(b);
                        if (mine != null && theirs != null)
                            _bonePairs.Add((mine, theirs));
                    }

                    _notes.Add($"The bodies are compared bone by bone: {_bonePairs.Count} humanoid bones below the hips, each one's turn against its parent.");
                }

                return true;
            }

            /// <summary>The average angle between the two bodies' bones, each against its parent (degrees).</summary>
            float PoseGap()
            {
                if (_bonePairs.Count == 0)
                    return float.NaN;
                float sum = 0f;
                foreach (var (own, remote) in _bonePairs)
                    sum += Quaternion.Angle(own.localRotation, remote.localRotation);
                return sum / _bonePairs.Count;
            }

            /// <summary>Samples the pose gap every frame for some seconds, after a short settle, doing each() every frame.</summary>
            bool Window(string name, float seconds, Action each, Action after = null)
            {
                if (First)
                    _windows.Add((name, new List<float>()));
                each?.Invoke();
                if (InStep > 0.25f)
                {
                    float gap = PoseGap();
                    if (!float.IsNaN(gap))
                        _windows[_windows.Count - 1].gaps.Add(gap);
                }

                if (InStep < seconds)
                    return false;
                after?.Invoke();
                return true;
            }

            bool DrawSword()
            {
                var weapons = _tps.Weapons;
                if (First)
                {
                    var holster = weapons != null ? weapons.holsters.Find(h => h?.Weapon != null && h.Weapon.name.StartsWith("Sword")) : null;
                    if (holster == null)
                    {
                        Check("the sword is drawn for the body test", false, "no sword in a holster");
                        return true;
                    }

                    weapons.Holster_Equip(holster.GetID);
                    _windows.Add(("drawing the sword", new List<float>()));
                    return false;
                }

                if (InStep > 0.25f)
                {
                    float gap = PoseGap();
                    if (!float.IsNaN(gap))
                        _windows[_windows.Count - 1].gaps.Add(gap);
                }

                return InStep > 1.6f && weapons != null && weapons.Weapon != null;
            }

            /// <summary>
            /// The sword in the character's hand, and its copy on the body the others see: the same hand,
            /// at the same place against it.
            /// </summary>
            bool CheckRemoteSword()
            {
                var weapon = _tps.Weapons != null ? _tps.Weapons.Weapon : null;
                if (weapon == null)
                {
                    Check("the body the others see holds the same sword", false, "nothing in the character's hand");
                    return true;
                }

                var own = _tps.Animal.Anim;
                Transform ownHand = null, remoteHand = null;
                foreach (var b in new[] { HumanBodyBones.RightHand, HumanBodyBones.LeftHand })
                {
                    var hand = own.GetBoneTransform(b);
                    if (hand != null && weapon.transform.IsChildOf(hand))
                    {
                        ownHand = hand;
                        remoteHand = _remoteAnimator.GetBoneTransform(b);
                    }
                }

                Transform copy = null;
                if (remoteHand != null)
                {
                    foreach (var t in remoteHand.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name.EndsWith("(remote)"))
                            copy = t;
                    }
                }

                if (copy == null)
                {
                    if (InStep < 1.5f)
                        return false;
                    Check("the body the others see holds the same sword", false,
                        ownHand == null ? "the character's weapon is not under a hand" : "no copy on the other body's hand");
                    return true;
                }

                var ownLocal = ownHand.InverseTransformPoint(weapon.transform.position);
                var copyLocal = remoteHand.InverseTransformPoint(copy.position);
                float apart = Vector3.Distance(ownHand.TransformVector(ownLocal), remoteHand.TransformVector(copyLocal));
                float turned = Quaternion.Angle(Quaternion.Inverse(ownHand.rotation) * weapon.transform.rotation,
                    Quaternion.Inverse(remoteHand.rotation) * copy.rotation);
                Check("the body the others see holds the same sword, in the same hand at the same grip",
                    apart < 0.01f && turned < 1f, $"{copy.name}: {apart * 100f:0.00} cm, {turned:0.0}° from the character's grip");
                return true;
            }

            static float Percentile(List<float> values, float q)
            {
                if (values.Count == 0)
                    return float.NaN;
                var sorted = new List<float>(values);
                sorted.Sort();
                return sorted[Mathf.Clamp(Mathf.RoundToInt(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
            }

            bool JudgeBodies()
            {
                foreach (var (window, gaps) in _windows)
                {
                    float median = Percentile(gaps, 0.5f), high = Percentile(gaps, 0.9f);
                    Check("the body the others see matches the character, " + window,
                        gaps.Count > 0 && median <= PoseGapMedianLimit && high <= PoseGapHighLimit,
                        gaps.Count == 0 ? "no samples" : $"bones {median:0.0}° apart at the median frame, {high:0.0}° at 90% ({gaps.Count} frames)");
                }

                return true;
            }

            /// <summary>The character and, a step to its right, the body the others see, from the front and the side.</summary>
            void PictureBodies(string title)
            {
                var render = _bridge.transform.Find("Player Render");
                if (render == null)
                {
                    _notes.Add("No Player Render body to picture.");
                    return;
                }

                var shown = new List<Renderer>();
                foreach (var r in render.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled)
                    {
                        r.enabled = true;
                        shown.Add(r);
                    }
                }

                var offset = _tps.transform.right * 1.3f;
                var oldPosition = render.position;
                render.position += offset;
                var camera = new GameObject("Check camera").AddComponent<Camera>();
                camera.enabled = false;
                camera.fieldOfView = 40f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.33f, 0.36f, 0.42f);
                const int cell = 640;
                var sheet = new Texture2D(cell * 2, cell, TextureFormat.RGB24, false);
                var rt = RenderTexture.GetTemporary(cell, cell, 24);
                var before = RenderTexture.active;
                try
                {
                    var t = _tps.transform;
                    var middle = t.position + offset * 0.5f + Vector3.up * 0.8f;

                    void Shot(int column, Vector3 from)
                    {
                        camera.transform.position = from;
                        camera.transform.LookAt(middle);
                        camera.targetTexture = rt;
                        camera.Render();
                        RenderTexture.active = rt;
                        sheet.ReadPixels(new Rect(0, 0, cell, cell), column * cell, 0);
                    }

                    Shot(0, middle + t.forward * 3.4f + Vector3.up * 0.4f);
                    Shot(1, middle - t.right * 3.6f + t.forward * 1.2f + Vector3.up * 0.4f);
                }
                finally
                {
                    RenderTexture.active = before;
                    camera.targetTexture = null;
                    RenderTexture.ReleaseTemporary(rt);
                    Object.DestroyImmediate(camera.gameObject);
                    render.position = oldPosition;
                    foreach (var r in shown)
                        r.enabled = false;
                }

                sheet.Apply();
                File.WriteAllBytes(Path.Combine(_folder, title + ".png"), sheet.EncodeToPNG());
                Object.DestroyImmediate(sheet);
                _notes.Add("Picture: " + title + ".png - the player's character on the left, the body the others see on the right, mid sword swing.");
            }

            // ---- Telekinesis ---------------------------------------------------------------------------

            TelekineticProp _prop;
            float _propFloor;

            /// <summary>
            /// A box in front of the player, made a telekinetic prop with its network actor, the way the
            /// session's master gives every prop in a level one (MultiplayerSessionBootstrap).
            /// </summary>
            bool SetUpProp()
            {
                if (First)
                {
                    var t = _tps.transform;
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.name = "Check prop";
                    box.transform.localScale = Vector3.one * 0.45f;
                    box.transform.position = t.position + t.forward * 1.4f + Vector3.up * 0.25f;
                    var body = box.AddComponent<Rigidbody>();
                    body.mass = 5f;
                    _prop = box.AddComponent<TelekineticProp>();
                    _propFloor = box.transform.position.y;

                    MultiplayerPrefabs.EnsureLoaded();
                    var actor = MultiplayerPrefabs.WorldActor;
                    if (actor == null)
                    {
                        Check("a prop gets its network actor", false, "MultiplayerPrefabs has no world actor prefab");
                        return true;
                    }

                    int key = NetworkWorldActor.MakeKey(box.transform);
                    _runner.Spawn(actor, box.transform.position, box.transform.rotation, null,
                        (r, spawned) => spawned.GetComponent<NetworkWorldActor>()?.InitializeSpawnState(box, key, NetworkWorldKind.Prop));
                    return false;
                }

                if (!((ITelekineticTarget)_prop).IsValidTarget)
                    return false;
                Check("a prop gets its network actor and can be lifted", true);
                return true;
            }

            bool LiftProp()
            {
                var telekinesis = _bridge.GetComponent<BodyTelekinesis>();
                var target = (ITelekineticTarget)_prop;
                if (First)
                {
                    // What E does once the prop is in reach: ask its authority for it.
                    target.RequestHold(target.Epoch, false);
                    return false;
                }

                if (InStep < 1.6f)
                    return false;

                Check("the lift is granted and telekinesis holds the prop", telekinesis != null && telekinesis.IsHolding && target.Holder == _runner.LocalPlayer,
                    telekinesis == null ? "no telekinesis" : $"holding {telekinesis.IsHolding}, holder {target.Holder}");
                Check("holding it, the player carries (slower, the throw's keys muted)", _tps.IsCarrying);
                Check("carrying mutes the shoulder camera's zoom, so the wheel only turns the prop", _tps.IsCameraZoomMuted);
                float rise = _prop.transform.position.y - _propFloor;
                var flat = Vector3.ProjectOnPlane(_prop.transform.position - _tps.transform.position, Vector3.up);
                float ahead = Vector3.Dot(flat, _tps.transform.forward);
                Check("the prop floats up in front of the player", rise > 0.4f && ahead > 0.5f,
                    $"{rise:0.00} m up, {ahead:0.00} m ahead");
                Check("attacks are refused while holding", !_tps.CanAttack(out string reason), reason ?? "allowed");
                return true;
            }

            bool DropProp()
            {
                var telekinesis = _bridge.GetComponent<BodyTelekinesis>();
                if (First)
                {
                    var release = typeof(BodyTelekinesis).GetMethod("Release", Private);
                    release?.Invoke(telekinesis, new object[] { false, Vector3.zero, Vector3.zero });
                    return false;
                }

                if (InStep < 1.0f)
                    return false;

                var target = (ITelekineticTarget)_prop;
                Check("letting go ends the carry", telekinesis != null && !telekinesis.IsHolding && !_tps.IsCarrying);
                Check("and gives the camera its zoom back", !_tps.IsCameraZoomMuted);
                Check("the prop is nobody's any more", target.Holder == PlayerRef.None, target.Holder.ToString());
                return true;
            }

            public string Report(string error)
            {
                var md = new StringBuilder();
                int passed = _checks.FindAll(c => c.pass).Count;
                md.AppendLine("# Network player check");
                md.AppendLine();
                md.AppendLine($"- When: {DateTime.Now:u}");
                md.AppendLine($"- Result: **{(error != null ? "COULD NOT RUN" : Failed ? "FAIL" : "PASS")}**, {passed} of {_checks.Count} checks pass");
                if (error != null)
                    md.AppendLine($"- {error}");
                foreach (var note in _notes)
                    md.AppendLine("- " + note);
                md.AppendLine();
                md.AppendLine("| Check | Result | Detail |");
                md.AppendLine("|---|---|---|");
                foreach (var (check, pass, detail) in _checks)
                    md.AppendLine($"| {check} | {(pass ? "PASS" : "FAIL")} | {detail} |");
                md.AppendLine();
                md.AppendLine($"## Log\n- {_errors} errors while playing" + (_log.Count > 0 ? "; the most frequent:" : "."));
                var sorted = new List<KeyValuePair<string, int>>(_log);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < sorted.Count && i < 12; i++)
                    md.AppendLine($"  - x{sorted[i].Value} {sorted[i].Key.Replace("\n", " ")}");
                return md.ToString();
            }
        }
    }
}
#endif
