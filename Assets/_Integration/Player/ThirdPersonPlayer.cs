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
    /// The player in the third-person-only game. Malbers' MAnimal moves the body, Game Creator 2 Melee
    /// swings the sword through MAnimalGC2Bridge, and Malbers' Cinemachine rig films it. The rest of
    /// the game reaches it through IPlayerRig: telekinesis, the collar, the HUD, pause, enemies that
    /// grab. It takes over the third-person half of DualPlayerController. There is no first person
    /// and no switching between views.
    ///
    /// It sits on the character's root, next to the MAnimal, and exists only for the local player:
    /// placed in a scene to play offline, or made by PlayerNetworkBridge for its owner. The camera rig
    /// that films it is the one assigned, or a copy of the camera rig prefab made when play starts
    /// (and removed with the player).
    ///
    /// Everything that stops the character - a sword swing, a menu, being downed - goes through the
    /// character's shared lock set (MAnimalLocks), so one letting go never frees the character while
    /// another still holds it. Inputs other systems need to themselves are muted there too.
    ///
    /// Parts: this file (start-up, Malbers, the camera, where the player is), .AimView (the throw aim
    /// view), .Carry (carrying, and the keys telekinesis claims), .Hold (menus and being downed) and
    /// .Combat (the sword).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MAnimal))]
    public partial class ThirdPersonPlayer : MonoBehaviour, IPlayerRig
    {
        [Tooltip("The camera rig in the scene that films this player. Empty: a copy of Camera Rig Prefab is made when play starts.")]
        [SerializeField] GameObject cameraRig;

        [Tooltip("Copied when play starts if no Camera Rig is assigned: Characters/Player/PlayerCameraRig (CM3).")]
        [SerializeField] GameObject cameraRigPrefab;

        /// <summary>Height of the chest above the feet: where carrying and aiming start from.</summary>
        const float ChestHeight = 1.35f;

        /// <summary>A teleport further than this snaps the camera to the player rather than letting it glide there.</summary>
        const float CameraSnapDistance = 2f;

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

        /// <summary>The character's shared lock set.</summary>
        MAnimalLocks Locks => MAnimalLocks.Of(Animal);

        /// <summary>Called by PlayerNetworkBridge when it makes this player for its owner.</summary>
        public void BindNetworkOwner(PlayerNetworkBridge bridge) => _bridge = bridge;

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
            // instead of gliding in from wherever the rig was made. The input link has finished
            // starting by now too, and Game Creator's character is ready for a weapon.
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
            if (_ownsCameraRig && cameraRig != null)
                Destroy(cameraRig);
        }

        // ---- Camera ------------------------------------------------------------------------------

        void EnsureCameraRig()
        {
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

            cameraRig.SetActive(true);
            DisableSprintCameraZoom();
            WarnAboutOtherMainCameras();
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
            RemoveMalbersAttacks();
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

        /// <summary>
        /// The sword owns the attack buttons, so Malbers' own unarmed attacks are switched off at the
        /// source: every attack mode, and the combo manager that started them from a click.
        /// </summary>
        void RemoveMalbersAttacks()
        {
            var animal = Animal;
            if (animal == null)
                return;

            foreach (var mode in animal.modes)
            {
                if (mode?.ID != null && mode.ID.name.Contains("Attack"))
                    mode.Disable();
            }

            foreach (var combos in GetComponentsInChildren<ComboManager>(true))
                combos.enabled = false;
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

        /// <summary>The line through the middle of the screen, from the camera rendering it.</summary>
        public bool TryGetCrosshairRay(out Ray ray)
        {
            ray = default;
            var brain = Brain;
            if (_suspended || brain == null)
                return false;

            ray = new Ray(brain.transform.position, brain.transform.forward);
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
            origin = _aimEyeLive && _aimEyeCamera != null
                ? _aimEyeCamera.transform.position
                : transform.position + Vector3.up * ChestHeight;
            return true;
        }
    }
}
