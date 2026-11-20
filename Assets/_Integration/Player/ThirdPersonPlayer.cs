using System.Collections;
using System.Collections.Generic;
using MalbersAnimations;
using MalbersAnimations.Controller;
using MalbersAnimations.InputSystem;
using MalbersAnimations.Scriptables;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali
{
    /// <summary>
    /// The player in the third-person-only game: Malbers' own third-person controller. Its MAnimal
    /// moves the body, its weapon manager and combos fight (fists, and the sword from its holster),
    /// and its Cinemachine rig films it. This component only connects it to the rest of the game,
    /// which reaches it through IPlayerRig: telekinesis, the collar, the HUD, pause, enemies that
    /// grab. It takes over the third-person half of DualPlayerController. There is no first person
    /// and no switching between views.
    ///
    /// It sits on the character's root, next to the MAnimal, and exists only for the local player:
    /// placed in a scene to play offline, or made by PlayerNetworkBridge for its owner. The camera rig
    /// that films it is the one assigned, or a copy of the camera rig prefab made when play starts
    /// (and removed with the player).
    ///
    /// Everything that stops the character - a menu, being downed - goes through the character's
    /// shared lock set (MAnimalLocks), so one letting go never frees the character while another still
    /// holds it. Inputs other systems need to themselves are muted there too.
    ///
    /// Parts: this file (start-up, Malbers, the camera, where the player is), .AimView (the throw aim
    /// view), .Carry (carrying, and the keys telekinesis claims), .Hold (menus and being downed) and
    /// .Combat (Malbers' weapons and fists against the game's enemies).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MAnimal))]
    public partial class ThirdPersonPlayer : MonoBehaviour, IPlayerRig
    {
        [Tooltip("The camera rig in the scene that films this player. Empty: the team's camera rig already in the scene " +
                 "(PlayerCameraRig (CM3)) is used, or else a copy of Camera Rig Prefab is made when play starts. Any other " +
                 "Malbers camera rig that is on is switched off.")]
        [SerializeField] GameObject cameraRig;

        /// <summary>Camera rigs a player films with, so two players never share one.</summary>
        static readonly HashSet<GameObject> ClaimedRigs = new HashSet<GameObject>();

        [Tooltip("Copied when play starts if no Camera Rig is assigned: Characters/Player/PlayerCameraRig (CM3).")]
        [SerializeField] GameObject cameraRigPrefab;

        /// <summary>
        /// Height of the chest above the feet, where carrying and aiming start from, for a character
        /// without a "Socket Chest" (PlayerCharacterBuilder puts one on this body's chest).
        /// </summary>
        const float ChestHeight = 1.35f;

        /// <summary>The body's chest socket: where carrying and aiming start from.</summary>
        const string ChestSocketName = "Socket Chest";

        /// <summary>A teleport further than this snaps the camera to the player rather than letting it glide there.</summary>
        const float CameraSnapDistance = 2f;

        /// <summary>
        /// How far in front of the chest the crosshair ray starts (m): a fat ray started any further back
        /// would already overlap a wall right behind the player's back, and stop on it.
        /// </summary>
        const float CrosshairStartAhead = 0.1f;

        /// <summary>
        /// Malbers' Aim looks up its Camera component once, when it wakes, and again only when a
        /// shared camera variable changes. The player's rig is made after the character wakes, so the
        /// Aim is asked to look again once it is pointed at the rig.
        /// </summary>
        static readonly System.Reflection.MethodInfo AimFindCamera = typeof(Aim).GetMethod("FindCamera",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, System.Type.EmptyTypes, null);

        /// <summary>Names a hold in the character's lock set, so logs and checks can tell the holds apart.</summary>
        sealed class Hold
        {
            readonly string _name;
            public Hold(string name) => _name = name;
            public override string ToString() => "ThirdPersonPlayer: " + _name;
        }

        readonly Hold _downedHold = new Hold("downed");
        readonly Hold _pauseKey = new Hold("pause key");
        readonly Hold _creepHold = new Hold("held by a creep");

        PlayerVitals _vitals;
        bool _heldByCreep;
        Transform _chest;
        MAnimal _animal;
        MInputLink _link;
        TransformHook[] _hooks;
        CinemachineBrain _brain;
        PlayerNetworkBridge _bridge;
        bool _ownsCameraRig;
        bool _running;
        bool _suspended;
        bool _tuned;

        /// <summary>The Malbers character this drives.</summary>
        public MAnimal Animal
        {
            get
            {
                if (_animal == null)
                    _animal = GetComponent<MAnimal>();
                return _animal;
            }
        }

        /// <summary>The character itself: this object.</summary>
        public GameObject Body => gameObject;

        public bool IsThirdPerson => true;

        /// <summary>True while downed: nothing moves and no input is taken.</summary>
        public bool IsSuspended => _suspended;

        /// <summary>True once Malbers takes the player's input, until the player goes down.</summary>
        public bool IsRunning => _running;

        /// <summary>On a ladder or climbing a wall right now.</summary>
        public bool IsClimbing
        {
            get
            {
                var animal = Animal;
                return animal != null && (animal.ActiveState is MalbersAnimations.Ladder ||
                                          animal.ActiveState is MalbersAnimations.Controller.Climb);
            }
        }

        /// <summary>The camera rig filming this player.</summary>
        public GameObject CameraRig => cameraRig;

        /// <summary>The camera rig's brain: what the screen shows.</summary>
        public CinemachineBrain Brain
        {
            get
            {
                if (_brain == null && cameraRig != null)
                    _brain = cameraRig.GetComponentInChildren<CinemachineBrain>(true);
                return _brain;
            }
        }

        /// <summary>The network bridge of this player, on its owner's machine. Null offline.</summary>
        public PlayerNetworkBridge NetworkBridge => _bridge;

        /// <summary>
        /// This player's health. In a session the bridge routes damage through it to the owner's
        /// machine and mirrors health and death to everyone; offline damage applies straight away.
        /// Made when the character wakes, so every copy of the prefab has one.
        /// </summary>
        public PlayerVitals Vitals
        {
            get
            {
                if (_vitals == null)
                    _vitals = GetComponent<PlayerVitals>();
                if (_vitals == null)
                    _vitals = gameObject.AddComponent<PlayerVitals>();
                return _vitals;
            }
        }

        /// <summary>The character's shared lock set.</summary>
        MAnimalLocks Locks => MAnimalLocks.Of(Animal);

        /// <summary>Called by PlayerNetworkBridge when it makes this player for its owner.</summary>
        public void BindNetworkOwner(PlayerNetworkBridge bridge)
        {
            _bridge = bridge;
            // Damage taken anywhere goes to the owner's machine and is mirrored from there.
            Vitals.Router = bridge;
        }

        /// <summary>
        /// A creep has this player in its grip (PlayerNetworkBridge carries the body along with it):
        /// no moving, no input and no attacks until it lets go. Anything held is dropped.
        /// </summary>
        public void SetHeldByCreep(bool held)
        {
            if (held == _heldByCreep)
                return;
            _heldByCreep = held;

            var locks = Locks;
            if (held)
            {
                EndAimViewImmediate();
                StopAttacks();
                locks?.Lock(_creepHold, movement: true, input: true);
            }
            else
            {
                locks?.Release(_creepHold);
            }

            SyncAttackKeys();
        }

        /// <summary>A creep has this player in its grip.</summary>
        public bool IsHeldByCreep => _heldByCreep;

        /// <summary>
        /// The camera rig to film with, when the one to use already exists (a scene's own rig). Only
        /// takes effect before play starts on this player.
        /// </summary>
        public void UseCameraRig(GameObject rig)
        {
            if (rig == null || _running)
                return;
            cameraRig = rig;
            _ownsCameraRig = false;
            _brain = null;
        }

        void Awake()
        {
            _animal = GetComponent<MAnimal>();
            _link = GetComponent<MInputLink>();
            _hooks = GetComponentsInChildren<TransformHook>(true);
            _ = Vitals;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == ChestSocketName)
                {
                    _chest = t;
                    break;
                }
            }
        }

        /// <summary>
        /// A ladder is in reach: Malbers' ladder has been offered one, so E climbs it. Telekinesis
        /// leaves E alone meanwhile, rather than lifting something in front of the ladder.
        /// </summary>
        public bool LadderInReach
        {
            get
            {
                var animal = Animal;
                var ladder = animal != null ? animal.State_Get<MalbersAnimations.Ladder>() : null;
                return ladder != null && ladder.MLadder != null;
            }
        }

        void OnEnable() => PlayerRigs.Register(this);

        void OnDisable() => PlayerRigs.Unregister(this);

        IEnumerator Start()
        {
            EnsureCameraRig();
            StartMalbers();
            LockCursor();

            // The rig's follow targets find the character's camera target as both wake up. Bound again
            // and snapped once they have, so the shoulder camera starts settled behind the player
            // instead of gliding in from wherever the rig was made. The input link and Malbers'
            // weapon manager have finished starting by now too.
            yield return null;
            BindCamera();
            SnapCameras();
            Locks?.Reassert();
            StartCombat();
        }

        void Update()
        {
            KeepHooks();
            UpdateCombat();
        }

        void OnDestroy()
        {
            EndAimViewImmediate();
            if (cameraRig != null)
                ClaimedRigs.Remove(cameraRig);
            if (_ownsCameraRig && cameraRig != null)
                Destroy(cameraRig);
        }

        // ---- Camera ------------------------------------------------------------------------------

        void EnsureCameraRig()
        {
            if (cameraRig == null)
            {
                cameraRig = FindSceneRig();
                if (cameraRig != null)
                {
                    _ownsCameraRig = false;
                    _brain = null;
                }
            }

            if (cameraRig == null && cameraRigPrefab != null)
            {
                cameraRig = Instantiate(cameraRigPrefab);
                cameraRig.name = cameraRigPrefab.name;
                _ownsCameraRig = true;
                _brain = null;
            }

            if (cameraRig == null)
            {
                Debug.LogWarning("[ThirdPersonPlayer] No camera rig: assign Camera Rig or Camera Rig Prefab.", this);
                return;
            }

            ClaimedRigs.Add(cameraRig);
            SwitchOffOtherRigs();
            CompleteCameraRig();
            cameraRig.SetActive(true);
            DisableSprintCameraZoom();
            WarnAboutOtherMainCameras();
        }

        /// <summary>
        /// The team's camera rig already in the scene - a level's own copy of PlayerCameraRig (CM3), told
        /// apart by the CameraOccluderFade on its brain - that no player films with, switched on or off, to
        /// use instead of making a second one (one that is on comes first). Malbers' stock Cameras CM3 is
        /// never used: none of the rig's tuning is on it (its brain moves the camera once per physics step,
        /// its cameras shake, nothing fades), and Game.unity keeps one, switched off, for its offline player.
        /// </summary>
        static GameObject FindSceneRig()
        {
            GameObject switchedOff = null;
            foreach (var brain in FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (brain == null || !brain.gameObject.scene.IsValid() || brain.GetComponent<CameraOccluderFade>() == null)
                    continue;
                var rig = RigOf(brain);
                if (rig == null || IsClaimed(rig) || rig.GetComponentInChildren<ThirdPersonPlayer>(true) != null)
                    continue;
                if (rig.activeInHierarchy)
                    return rig;
                // Off itself, not under something that is off (a level's offline group): switching the rig on
                // would not undo that, and the player would film with nothing.
                var parent = rig.transform.parent;
                if (switchedOff == null && (parent == null || parent.gameObject.activeInHierarchy))
                    switchedOff = rig;
            }

            return switchedOff;
        }

        /// <summary>
        /// Every other Malbers camera rig that is on is switched off, so only this player's films. With two,
        /// both filmed at once and the other one's cameras could take the screen: its sprint camera - never
        /// switched off by this player - did on sprinting, still facing wherever it was left, and the player
        /// turned round and ran the other way. Rigs that are off stay off, a rig another player films with
        /// is left to it, and one that holds more than cameras (level objects, a character) is only warned
        /// about, never switched off with them.
        /// </summary>
        void SwitchOffOtherRigs()
        {
            foreach (var brain in FindObjectsByType<CinemachineBrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                // Off already: its rig was switched off a moment ago, with another brain of it.
                if (brain == null || !brain.gameObject.activeInHierarchy || !brain.gameObject.scene.IsValid())
                    continue;
                // This player's own rig is claimed by now.
                var rig = RigOf(brain);
                if (rig == null || IsClaimed(rig))
                    continue;

                if (rig.GetComponentInChildren<ThirdPersonPlayer>(true) != null || rig.GetComponentInChildren<MAnimal>(true) != null ||
                    rig.GetComponentInChildren<Renderer>(true) != null || rig.GetComponentInChildren<Collider>(true) != null)
                {
                    Debug.LogWarning("[ThirdPersonPlayer] '" + rig.name + "' is a second camera rig, so two rigs film at once, but it " +
                                     "holds more than cameras and was left on. Keep the level's rig on its own, or remove it.", rig);
                    continue;
                }

                rig.SetActive(false);
                Debug.LogWarning("[ThirdPersonPlayer] Switched off the scene's camera rig '" + rig.name + "' so only one rig films" +
                                 (brain.GetComponent<CameraOccluderFade>() == null
                                     ? ": it is not the team's (no CameraOccluderFade on its brain). Use PlayerCameraRig (CM3) in levels, or none."
                                     : "."), rig);
            }
        }

        /// <summary>
        /// The camera rig a brain belongs to: the smallest part of the scene that holds the brain and a
        /// Malbers shoulder camera (a level may keep its rig under a grouping object). Null without such a
        /// camera: then it is not a Malbers rig, and none of this player's business.
        /// </summary>
        static GameObject RigOf(CinemachineBrain brain)
        {
            for (var t = brain.transform; t != null; t = t.parent)
            {
                if (t.GetComponentInChildren<ThirdPersonFollowTarget>(true) != null)
                    return t.gameObject;
            }

            return null;
        }

        /// <summary>A player films with this rig, with one inside it, or with one it is part of.</summary>
        static bool IsClaimed(GameObject rig)
        {
            foreach (var claimed in ClaimedRigs)
            {
                if (claimed != null && (claimed == rig || claimed.transform.IsChildOf(rig.transform) || rig.transform.IsChildOf(claimed.transform)))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whatever rig films the player films the way the team's is built (PlayerCameraRigBuilder): no
        /// shoulder camera jumps in front of what comes between it and the player, which dissolves instead
        /// (CameraOccluderFade on the brain), and every shoulder camera stays above the ground
        /// (CameraGroundCollider). A rig assigned in a level, or one built before these existed, gets what it
        /// lacks here, on the layers this character walks on.
        /// </summary>
        void CompleteCameraRig()
        {
            var animal = Animal;
            LayerMask ground = animal != null ? animal.GroundLayer : (LayerMask)1;

            foreach (var follow in cameraRig.GetComponentsInChildren<CinemachineThirdPersonFollow>(true))
            {
                var avoid = follow.AvoidObstacles;
                if (avoid.Enabled)
                {
                    avoid.Enabled = false;
                    follow.AvoidObstacles = avoid;
                }

                if (follow.GetComponent<CameraGroundCollider>() == null)
                    follow.gameObject.AddComponent<CameraGroundCollider>().layers = ground;
            }

            // Without obstacle avoidance a rig with nothing to dissolve walls would film them instead of the player.
            var brain = Brain;
            if (brain != null && brain.GetComponent<CameraOccluderFade>() == null)
            {
                var fade = brain.gameObject.AddComponent<CameraOccluderFade>();
                var built = cameraRigPrefab != null ? cameraRigPrefab.GetComponentInChildren<CameraOccluderFade>(true) : null;
                if (built != null && built.dissolveShader != null)
                    fade.dissolveShader = built.dissolveShader;
                fade.layers = ground;
            }
        }

        /// <summary>
        /// Points Malbers at the rig's camera: movement is relative to it, and the aimer looks through
        /// it. Explicit, because a scene can hold other cameras tagged MainCamera.
        /// </summary>
        void BindCamera()
        {
            var animal = Animal;
            var brain = Brain;
            if (animal == null)
                return;

            animal.UseCameraInput = true;
            if (!_aimStrafeSet)
                animal.Strafe = false;

            // The rig's cameras follow what the character's hooks (its camera target) point them at.
            ClaimHooks();

            if (brain == null)
                return;

            brain.gameObject.tag = "MainCamera";
            animal.m_MainCamera.UseConstant = true;
            animal.m_MainCamera.Value = brain.transform;
            animal.FindCamera();

            if (animal.Aimer == null)
                animal.Aimer = GetComponentInChildren<Aim>(true);
            if (animal.Aimer != null)
            {
                // The head looks where the Aim aims; without its camera it aims along the body.
                animal.Aimer.MainCamera = brain.transform;
                AimFindCamera?.Invoke(animal.Aimer, null);
            }
        }

        /// <summary>Points the character's live hooks (camera target and the like) at this character.</summary>
        void ClaimHooks()
        {
            if (_hooks == null)
                return;
            foreach (var hook in _hooks)
            {
                if (hook != null && hook.isActiveAndEnabled)
                    hook.UpdateHook();
            }
        }

        /// <summary>
        /// Another Malbers character waking up takes the shared hooks, and clears them when it goes,
        /// which would leave the camera with nothing to follow. A cleared hook is taken back.
        /// </summary>
        void KeepHooks()
        {
            if (_hooks == null)
                return;
            foreach (var hook in _hooks)
            {
                if (hook != null && hook.isActiveAndEnabled && hook.Hook != null && hook.Hook.Value == null)
                    hook.UpdateHook();
            }
        }

        /// <summary>Puts every live shoulder camera straight onto the player, without its damping.</summary>
        void SnapCameras()
        {
            if (cameraRig == null)
                return;

            foreach (var follow in cameraRig.GetComponentsInChildren<ThirdPersonFollowTarget>(true))
            {
                if (follow != null && follow.isActiveAndEnabled)
                    follow.TargetTeleport();
            }
        }

        /// <summary>
        /// The rig's sprint cameras pull the view back while sprinting. The game never used that zoom
        /// (DualPlayerController switched it off too), so they are switched off.
        /// </summary>
        void DisableSprintCameraZoom()
        {
            foreach (var t in cameraRig.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.IndexOf("Sprint", System.StringComparison.OrdinalIgnoreCase) < 0 ||
                    t.name.IndexOf("Sprint Settings", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                var vcam = t.GetComponent<CinemachineCamera>();
                if (vcam != null)
                    vcam.enabled = false;
            }
        }

        void WarnAboutOtherMainCameras()
        {
            var others = new List<string>();
            foreach (var camera in Camera.allCameras)
            {
                if (camera != null && camera.CompareTag("MainCamera") && !camera.transform.IsChildOf(cameraRig.transform))
                    others.Add(camera.name);
            }

            if (others.Count > 0)
                Debug.LogWarning("[ThirdPersonPlayer] The scene has another camera tagged MainCamera (" +
                                 string.Join(", ", others) + "). It still renders; remove it or switch it off " +
                                 "where this player is used.", this);
        }

        static void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // ---- Malbers -----------------------------------------------------------------------------

        /// <summary>
        /// Gives the player's input to Malbers. PlayerInput has to be on before MInputLink wakes up,
        /// or the link switches itself off, so the link is restarted once PlayerInput is ready.
        /// </summary>
        void StartMalbers()
        {
            var animal = Animal;
            if (animal == null)
                return;

            FreeInputDevices();

            foreach (var playerInput in GetComponentsInChildren<PlayerInput>(true))
            {
                // Woken again so it picks up the devices just freed, if it started without any.
                if (playerInput.enabled && playerInput.devices.Count == 0)
                    playerInput.enabled = false;
                playerInput.enabled = true;
                playerInput.notificationBehavior = PlayerNotifications.InvokeCSharpEvents;
                try
                {
                    if (playerInput.actions != null)
                        playerInput.SwitchCurrentActionMap("Gameplay");
                }
                catch
                {
                    // Another map name: MInputLink picks its default map.
                }
            }

            animal.SetMainPlayer();
            animal.Sleep = false;
            Locks?.Release(_downedHold);

            if (_link == null)
                _link = GetComponent<MInputLink>();
            if (_link != null)
            {
                _link.enabled = false;
                _link.enabled = true;
                animal.InputSource = _link;
                animal.ResetInputSource();
            }
            else
            {
                animal.UpdateInputSource(true);
                animal.InputSource?.Enable(true);
            }

            TuneOnce();
            BindCamera();

            // Escape is the game's own pause menu. Malbers' pause froze time and freed the cursor
            // through a settings canvas the game doesn't have, so its key is taken away for good.
            var locks = Locks;
            if (locks != null)
            {
                locks.Mute(_pauseKey, "Pause");
                // The restarted link is back on its default map; whatever was muted stays muted.
                locks.Reassert();
            }

            ApplyCarrySlowdown();
            _running = true;
        }

        /// <summary>
        /// Unity gives each input device to the first PlayerInput that wakes up wanting it, so another
        /// player left in the scene (a test character, a scene's own player) takes the keyboard and
        /// mouse and leaves this one standing still. Before this player's input starts, the keyboard,
        /// mouse and gamepad are taken from any other player; this player's PlayerInput picks them up
        /// as the input link restarts it. Unity only hands devices between players on its own when
        /// there is a single one, so the other player does not take them back.
        /// </summary>
        void FreeInputDevices()
        {
            var mine = GetComponentsInChildren<PlayerInput>(true);
            var wanted = new List<InputDevice>(3);
            if (Keyboard.current != null)
                wanted.Add(Keyboard.current);
            if (Mouse.current != null)
                wanted.Add(Mouse.current);
            if (Gamepad.current != null)
                wanted.Add(Gamepad.current);

            List<string> holders = null;
            foreach (var other in PlayerInput.all)
            {
                if (other == null || System.Array.IndexOf(mine, other) >= 0 || !other.user.valid)
                    continue;

                bool took = false;
                foreach (var device in wanted)
                {
                    if (!Holds(other.user, device))
                        continue;
                    other.user.UnpairDevice(device);
                    took = true;
                }

                if (took)
                    (holders ??= new List<string>()).Add(other.name);
            }

            if (holders != null)
                Debug.LogWarning("[ThirdPersonPlayer] Took the keyboard, mouse and gamepad from " + string.Join(", ", holders) +
                                 ": another player in the scene had them. Remove it where this player is used.", this);
        }

        static bool Holds(UnityEngine.InputSystem.Users.InputUser user, InputDevice device)
        {
            foreach (var paired in user.pairedDevices)
            {
                if (paired == device)
                    return true;
            }

            return false;
        }

        /// <summary>Takes the player's input away from Malbers and holds the character still.</summary>
        void StopMalbers()
        {
            var animal = Animal;
            if (animal == null)
                return;

            foreach (var playerInput in GetComponentsInChildren<PlayerInput>(true))
                playerInput.enabled = false;

            if (_link != null)
                _link.enabled = false;
            else
                animal.InputSource?.Enable(false);

            Locks?.Lock(_downedHold, movement: true, input: true);
            animal.DisableMainPlayer();
            _running = false;
        }

        void TuneOnce()
        {
            // The jump is tuned by ThirdPersonJumpTuning (PlayerTuning > Third Person Jump) and the
            // walk, run and climb speeds by CharacterBodyCalibration (PlayerTuning > Body). The player
            // prefab carries both; a hand-made character gets them here.
            if (GetComponent<ThirdPersonJumpTuning>() == null)
                gameObject.AddComponent<ThirdPersonJumpTuning>();
            if (GetComponent<CharacterBodyCalibration>() == null)
                gameObject.AddComponent<CharacterBodyCalibration>();

            if (_tuned)
                return;
            _tuned = true;

            // Stamina as the game has always had it: slow to drain, quick to come back.
            var stats = GetComponent<Stats>();
            if (stats == null)
                stats = GetComponentInChildren<Stats>(true);
            var stamina = stats != null ? stats.Stat_Get("Stamina") : null;
            if (stamina != null)
            {
                stamina.DegenRate.Value = 5f;
                if (stamina.RegenRate.Value < 15f)
                    stamina.RegenRate.Value = 15f;
            }
        }

        // ---- Where the player is -----------------------------------------------------------------

        /// <summary>The player's feet: Malbers' root sits at the feet.</summary>
        public Vector3 GetGameplayWorldPosition() => transform.position;

        public float GetGameplayYaw() => transform.eulerAngles.y;

        public void TeleportGameplay(Vector3 position, float yawDegrees)
        {
            var rotation = Quaternion.Euler(0f, yawDegrees, 0f);
            float distance = Vector3.Distance(transform.position, position);
            var animal = Animal;

            // Malbers' teleport resets its own bookkeeping, but it needs the character running (it
            // times a short cooldown on it) and ignores a second teleport within that cooldown, so
            // the body is also put there directly. A downed body is only moved; Malbers catches up
            // when it wakes.
            if (animal != null && animal.isActiveAndEnabled)
            {
                animal.Rotation = rotation;
                animal.Teleport(position);
            }

            if ((transform.position - position).sqrMagnitude > 0.000001f || transform.rotation != rotation)
            {
                transform.SetPositionAndRotation(position, rotation);
                if (animal != null && animal.RB != null)
                {
                    animal.RB.position = position;
                    animal.RB.rotation = rotation;
                }
            }

            if (distance > CameraSnapDistance)
                SnapCameras();
        }

        /// <summary>How fast the body is moving.</summary>
        public Vector3 GetBodyVelocity()
        {
            var animal = Animal;
            var body = animal != null ? animal.RB : null;
            return body != null && !body.isKinematic ? body.linearVelocity : Vector3.zero;
        }

        /// <summary>Every collider of the player's body, hitboxes included, active or not.</summary>
        public void CollectOwnColliders(List<Collider> into)
        {
            if (into != null)
                into.AddRange(GetComponentsInChildren<Collider>(true));
        }

        public bool OwnsCollider(Collider other) =>
            other != null && (other.transform == transform || other.transform.IsChildOf(transform));

        /// <summary>
        /// The line through the middle of the screen, from the camera rendering it, starting level with
        /// the player. The camera keeps its distance behind a wall rather than jumping in front of it (the
        /// wall dissolves: CameraOccluderFade), but the wall's collider is still there: a ray from the
        /// camera stopped on it before it reached anything in front of the player.
        /// </summary>
        public bool TryGetCrosshairRay(out Ray ray)
        {
            ray = default;
            var brain = Brain;
            if (_suspended || brain == null)
                return false;

            var view = brain.transform;
            var origin = view.position;
            var forward = view.forward;
            // Where the player is along the view: the chest, or in the throw aim view the eyes, where the
            // camera itself is then.
            if (TryGetAim(out var player, out _))
            {
                float ahead = Vector3.Dot(player - origin, forward) + CrosshairStartAhead;
                if (ahead > 0f)
                    origin += forward * ahead;
            }

            ray = new Ray(origin, forward);
            return true;
        }

        /// <summary>
        /// Where a throw starts and where the player aims: along the camera, from the chest (the
        /// camera itself is metres behind the character, the wrong place to hold a body from), or
        /// from the eyes while the throw aim view is up.
        /// </summary>
        public bool TryGetAim(out Vector3 origin, out Vector3 forward)
        {
            origin = default;
            forward = transform.forward;
            if (_suspended)
                return false;

            var brain = Brain;
            if (brain != null)
                forward = brain.transform.forward;
            origin = _aimEyeLive && _aimEyeCamera != null ? _aimEyeCamera.transform.position
                : _chest != null ? _chest.position
                : transform.position + Vector3.up * ChestHeight;
            return true;
        }
    }
}
