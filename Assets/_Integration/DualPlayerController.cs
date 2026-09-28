using System.Collections.Generic;
using cowsins;
using MalbersAnimations;
using MalbersAnimations.Controller;
using MalbersAnimations.InputSystem;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali
{
    /// <summary>
    /// Local dual-controller player: Cowsins FPS + Malbers Steve on one prefab.
    /// Children are spawned from prefab refs at runtime if not already nested.
    /// </summary>
    public class DualPlayerController : MonoBehaviour
    {
        [Header("FPS Engine")]
        [SerializeField] GameObject fpsRoot;
        [SerializeField] Transform fpsBody;
        [SerializeField] GameObject fpsCameraRoot;

        [Header("Malbers TPP")]
        [SerializeField] GameObject steveRoot;
        [SerializeField] MAnimal animal;
        [SerializeField] GameObject camerasCm3;

        [Header("Source prefabs (spawned if children missing)")]
        [SerializeField] GameObject cowsinsPrefab;
        [SerializeField] GameObject stevePrefab;
        [SerializeField] GameObject camerasPrefab;

        [Header("Body height")]
        [Tooltip("Fallback only. The feet are read from the FPS body's capsule collider; this is used " +
                 "just when the body has no upright CapsuleCollider.")]
        [SerializeField] float fpsBodyLocalY = 1f;

        // Camera transition timings live in Resources/PlayerTuning (Camera Transition), next to every
        // other player tuning value, so they can be adjusted in one place during playtests.

        // How much to raise the Malbers jump so the taller Meshy characters clear obstacles like Steve did.
        const float SteveJumpBoost = 1.3f;

        /// <summary>Height of the chest above Steve's feet: where third-person carrying and aiming start from.</summary>
        const float TpsChestHeight = 1.35f;

        /// <summary>
        /// Cowsins actions muted while a body is held. Every one of them shares a key with the
        /// telekinesis (LMB, RMB, E, Q) or would act mid-throw (melee, reload, weapon switch).
        /// </summary>
        static readonly string[] CowsinsCarryBlocked =
        {
            "Firing", "Aiming", "Melee", "Reloading", "Inspect", "Drop", "Interacting",
            "InventoryFavOpen", "ToggleTipsCanvas", "InventoryOpen", "Scrolling",
        };

        /// <summary>The Malbers equivalents: both mouse buttons, the action keys, interact and Q.</summary>
        const string MalbersCarryBlocked = "Action1,Action2,Action3,Action4,Interact,Ability1";

        /// <summary>Muted while a body is merely targeted, so E lifts it and nothing else.</summary>
        static readonly string[] CowsinsInteractOnly = { "Interacting" };
        const string MalbersInteractOnly = "Interact";

        bool _suspended;
        bool _transitioning;
        Coroutine _exitTppRoutine;

        bool _carrying;
        bool _interactClaimed;
        float _carrySpeedMultiplier = 1f;
        bool _timeScaled;
        float _savedTimeMultiplier = 1f;
        readonly List<InputAction> _mutedCowsinsActions = new();
        string _mutedMalbersInputs = string.Empty;
        bool _punchRemoved;

        readonly List<Renderer> _steveRenderersHidden = new();
        readonly List<Collider> _steveCollidersDisabled = new();
        bool _steveFrozen;
        bool _steveWasKinematic;

        CinemachineBlenderSettings _savedCustomBlends;

        // Third-person throw aim view: a camera in the character's eyes while a throw is aimed.
        CinemachineCamera _aimEyeCamera;
        Coroutine _aimViewRoutine;
        bool _aimViewRequested;
        bool _aimEyeLive;
        float _aimYaw;
        float _aimPitch;
        float _aimEyeY;
        bool _aimEyeYValid;
        bool _aimStrafeSet;
        bool _aimSavedStrafe;
        ThirdPersonFollowTarget[] _lookSources;
        readonly List<Renderer> _steveRenderersShadowOnly = new();
        readonly List<UnityEngine.Rendering.ShadowCastingMode> _steveShadowModes = new();

        PlayerControl _playerControl;
        PlayerMovement _movement;
        Rigidbody _fpsRigidbody;
        readonly List<Collider> _fpsCollidersDisabledForTpp = new();
        bool _thirdPerson;
        bool _built;
        bool _steveTuned;
        bool _steveStarted;
        Coroutine _enterTppRoutine;

        /// <summary>Stand-in camera that holds the FPS eye pose so the brain can blend away from it.</summary>
        CinemachineCamera _handoffCamera;
        CinemachineBlendDefinition _savedBlend;
        bool _blendSaved;

        /// <summary>FPS renderers switched off for third person, remembered so they can come back.</summary>
        readonly List<Renderer> _fpsRenderersHiddenForTpp = new();

        public bool IsThirdPerson => _thirdPerson;
        public MAnimal Animal => animal;

        /// <summary>True while the player is dead: neither controller runs and mode switches are ignored.</summary>
        public bool IsSuspended => _suspended;

        /// <summary>True while the camera is travelling between first and third person.</summary>
        public bool IsTransitioning => _transitioning;

        /// <summary>True while the third-person view is in the character's eyes for aiming a throw.</summary>
        public bool IsAimViewLive => _aimEyeLive;

        FpsNetworkBridge _networkBridge;
        bool _networkMode;
        System.Action<bool> _onModeChanged;

        public Transform FpsBody => fpsBody;
        public GameObject SteveRoot => steveRoot;
        public FpsNetworkBridge NetworkBridge => _networkBridge;
        public bool IsNetworkMode => _networkMode;

        public void SetSourcePrefabs(GameObject cowsins, GameObject steve, GameObject cameras)
        {
            if (cowsins != null)
                cowsinsPrefab = cowsins;
            if (steve != null)
                stevePrefab = steve;
            if (cameras != null)
                camerasPrefab = cameras;
        }

        public void CopySourcePrefabsFrom(DualPlayerController other)
        {
            if (other == null)
                return;
            SetSourcePrefabs(other.cowsinsPrefab, other.stevePrefab, other.camerasPrefab);
        }

        public void BindNetworkOwner(FpsNetworkBridge bridge, System.Action<bool> onModeChanged)
        {
            _networkBridge = bridge;
            _networkMode = true;
            _onModeChanged = onModeChanged;
        }

        public bool OwnsCollider(Collider other)
        {
            if (other == null)
                return false;
            if (fpsBody != null && (other.transform == fpsBody || other.transform.IsChildOf(fpsBody)))
                return true;
            if (steveRoot != null &&
                (other.transform == steveRoot.transform || other.transform.IsChildOf(steveRoot.transform)))
                return true;
            return false;
        }

        public Vector3 GetGameplayWorldPosition()
        {
            if (_thirdPerson && steveRoot != null)
                return steveRoot.transform.position;
            return GetFpsWorldPosition();
        }

        public float GetGameplayYaw()
        {
            if (_thirdPerson && steveRoot != null)
                return steveRoot.transform.eulerAngles.y;
            return GetFpsYaw();
        }

        public void TeleportGameplay(Vector3 position, float yawDegrees)
        {
            if (_thirdPerson)
                PlaceSteve(position, yawDegrees);
            else
                PlaceFps(position, yawDegrees);
        }

        void Awake()
        {
            if (GetComponent<FpsNetworkBridge>() != null)
            {
                _networkMode = true;
                return;
            }

            EnsureChildren();
            CacheFps();
            EnsureFlashlight();
            ApplyMode(thirdPerson: false, teleport: false);
        }

        void OnDestroy()
        {
            if (!_networkMode)
                return;
            if (steveRoot != null)
                Destroy(steveRoot);
            if (camerasCm3 != null)
                Destroy(camerasCm3);
        }

        public void ToggleMode()
        {
            if (_thirdPerson)
                ExitToFirstPerson();
            else
                EnterThirdPerson();
        }

        public void WireExisting(GameObject fps, GameObject steve, GameObject cameras)
        {
            if (fps != null && fps != gameObject)
                fpsRoot = fps;
            if (steve != null && steve != gameObject && steve.GetComponent<DualPlayerController>() == null)
            {
                steveRoot = steve;
                animal = steve.GetComponentInChildren<MAnimal>(true);
            }
            if (cameras != null)
                camerasCm3 = cameras;

            _built = false;
            EnsureChildren();
            CacheFps();
            EnsureFlashlight();
            ApplyMode(thirdPerson: false, teleport: false);
            _onModeChanged?.Invoke(_thirdPerson);

            // #region agent log
            AgentDebugLog.LogPlayerSetup(
                "H1",
                "DualPlayerController.WireExisting",
                _networkMode ? "network" : "offline",
                fpsRoot,
                steveRoot,
                _networkMode);
            if (isActiveAndEnabled)
                StartCoroutine(AgentDelayedWeaponProbe());
            // #endregion
        }

        // #region agent log
        System.Collections.IEnumerator AgentDelayedWeaponProbe()
        {
            yield return null;
            yield return null;
            AgentDebugLog.LogPlayerSetup(
                "H3",
                "DualPlayerController.DelayedWeaponProbe",
                _networkMode ? "network_delayed" : "offline_delayed",
                fpsRoot,
                steveRoot,
                _networkMode);
        }
        // #endregion

        void EnsureChildren()
        {
            if (_built)
                return;
            _built = true;

            FindExisting();
            if (!_networkMode)
            {
                AdoptUnderPlayerMain(fpsRoot);
                AdoptUnderPlayerMain(steveRoot);
                AdoptUnderPlayerMain(camerasCm3);
            }

            if (fpsRoot == null && cowsinsPrefab != null)
            {
                fpsRoot = Instantiate(cowsinsPrefab, transform);
                fpsRoot.name = "CowsinsFPSController";
                fpsRoot.transform.localPosition = Vector3.zero;
                fpsRoot.transform.localRotation = Quaternion.identity;

                var pause = fpsRoot.GetComponentInChildren<PauseMenu>(true);
                if (pause != null)
                    pause.enabled = false;
            }

            if (steveRoot == null && stevePrefab != null)
            {
                steveRoot = Instantiate(stevePrefab, transform);
                steveRoot.name = "Steve Player";
                steveRoot.transform.localPosition = Vector3.zero;
                steveRoot.transform.localRotation = Quaternion.identity;
                steveRoot.SetActive(false);
                animal = steveRoot.GetComponentInChildren<MAnimal>(true);
            }

            if (camerasCm3 == null && camerasPrefab != null)
            {
                camerasCm3 = Instantiate(camerasPrefab, transform);
                camerasCm3.name = "Cameras CM3";
                camerasCm3.transform.localPosition = Vector3.zero;
                camerasCm3.transform.localRotation = Quaternion.identity;
                camerasCm3.SetActive(false);
            }

            if (_networkMode)
            {
                DetachFromNetworkRoot(steveRoot);
                DetachFromNetworkRoot(camerasCm3);
            }
        }

        static void DetachFromNetworkRoot(GameObject go)
        {
            if (go != null && go.transform.parent != null)
                go.transform.SetParent(null, true);
        }

        void FindExisting()
        {
            if (fpsRoot == null)
            {
                var movement = GetComponentInChildren<PlayerMovement>(true);
                if (movement == null && !_networkMode)
                    movement = FindFirstObjectByType<PlayerMovement>(FindObjectsInactive.Include);
                if (movement != null)
                    fpsRoot = ClimbToOwnedRoot(movement.transform);
            }

            if (steveRoot == null)
            {
                animal = GetComponentInChildren<MAnimal>(true);
                if (animal == null && !_networkMode)
                {
                    foreach (var candidate in FindObjectsByType<MAnimal>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    {
                        if (IsPlayerSteve(candidate))
                        {
                            animal = candidate;
                            break;
                        }
                    }
                }

                if (animal != null)
                    steveRoot = ClimbToOwnedRoot(animal.transform);
            }
            else if (animal == null)
            {
                animal = steveRoot.GetComponentInChildren<MAnimal>(true);
            }

            if (camerasCm3 == null)
            {
                var child = transform.Find("Cameras CM3");
                if (child != null)
                    camerasCm3 = child.gameObject;
                else
                    camerasCm3 = _networkMode ? null : FindNamedIncludingInactive("Cameras CM3");
            }
        }

        void AdoptUnderPlayerMain(GameObject go)
        {
            if (_networkMode)
                return;
            if (go == null || go == gameObject || go.transform.parent == transform)
                return;
            go.transform.SetParent(transform, true);
        }

        GameObject ClimbToOwnedRoot(Transform t)
        {
            while (t.parent != null && t.parent != transform)
                t = t.parent;
            return t.gameObject;
        }

        static GameObject FindNamedIncludingInactive(string name)
        {
            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t != null && t.name == name)
                    return t.gameObject;
            }

            return null;
        }

        static bool IsPlayerSteve(MAnimal candidate)
        {
            if (candidate == null)
                return false;

            var root = candidate.transform.root;
            if (root.name.IndexOf("Steve", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return candidate.GetComponentInChildren<MInputLink>(true) != null;
        }

        void CacheFps()
        {
            if (fpsRoot == null)
                return;

            if (fpsBody == null)
            {
                var movement = fpsRoot.GetComponentInChildren<PlayerMovement>(true);
                if (movement != null)
                    fpsBody = movement.transform;
            }

            if (fpsCameraRoot == null)
            {
                var cam = fpsRoot.transform.Find("Camera");
                if (cam == null)
                    cam = FindDeepChild(fpsRoot.transform, "Camera");
                if (cam != null)
                    fpsCameraRoot = cam.gameObject;
            }

            if (fpsBody != null)
            {
                _playerControl = fpsBody.GetComponent<PlayerControl>();
                _movement = fpsBody.GetComponent<PlayerMovement>();
                _fpsRigidbody = fpsBody.GetComponent<Rigidbody>();
            }

            EnsureFlashlight();
        }

        void EnsureFlashlight()
        {
            var flashlight = GetComponent<WeaponFlashlight>();
            if (flashlight == null)
                flashlight = gameObject.AddComponent<WeaponFlashlight>();

            WeaponController weapons = null;
            if (fpsBody != null)
                weapons = fpsBody.GetComponent<WeaponController>();
            if (weapons == null && fpsRoot != null)
                weapons = fpsRoot.GetComponentInChildren<WeaponController>(true);
            flashlight.Bind(weapons, this);
        }

        public void EnterThirdPerson()
        {
            // Dead players have no controller to switch, and a switch requested mid-transition would
            // start a second camera blend on top of the first.
            if (_thirdPerson || _suspended || _transitioning)
                return;

            EnsureChildren();
            var pos = GetFpsWorldPosition();
            var yaw = GetFpsYaw();
            if (_enterTppRoutine != null)
                StopCoroutine(_enterTppRoutine);
            _enterTppRoutine = StartCoroutine(EnterThirdPersonRoutine(pos, yaw));
        }

        System.Collections.IEnumerator EnterThirdPersonRoutine(Vector3 pos, float yaw)
        {
            _thirdPerson = true;
            _transitioning = true;

            // Before SetFpsActive, which switches the FPS camera off: the handoff needs to read that
            // camera's pose while it still has one, and both must change in the same frame so the two
            // cameras are never live together.
            BeginCameraHandoff();

            SetFpsActive(false);
            SetFpsSideRenderersHidden(true);
            SetFpsCharacterMeshVisible(false);
            SetCowsinsInputEnabled(false);

            if (steveRoot == null)
            {
                Debug.LogError("[DualPlayer] Steve is missing. Cannot enter third person.");
                _transitioning = false;
                yield break;
            }

            // PlayerInput must be on before Steve's hierarchy enables, or MInputLink disables itself.
            foreach (var playerInput in steveRoot.GetComponentsInChildren<PlayerInput>(true))
            {
                playerInput.enabled = true;
                playerInput.notificationBehavior = PlayerNotifications.InvokeCSharpEvents;
            }

            steveRoot.SetActive(true);
            _onModeChanged?.Invoke(true);
            yield return null;

            if (animal == null)
                animal = steveRoot.GetComponentInChildren<MAnimal>(true);

            if (animal != null)
            {
                animal.Sleep = false;
                animal.LockInput = false;
                animal.LockMovement = false;
                animal.SetMainPlayer();
                animal.Rotation = Quaternion.Euler(0f, yaw, 0f);
                animal.Teleport(pos);
            }
            else
            {
                steveRoot.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            }

            EnableMalbers(true);
            // Still snapped: the shoulder camera settles instantly at its correct pose behind Steve,
            // and the movement the player sees comes from the brain blending off the handoff camera
            // towards it. Without the snap it would also be chasing its own damping from wherever it
            // happened to be parked, which is what made the old transition lurch.
            ActivateMalbersCameras(snap: true);
            BindMalbersToTpsCamera();
            yield return null;
            BindMalbersToTpsCamera();

            // Released once the shoulder camera is bound and settled, so the blend has a valid
            // destination to travel to.
            EndCameraHandoff();

            _steveStarted = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _enterTppRoutine = null;
            _transitioning = false;
            Debug.Log("[DualPlayer] Switched to Malbers third person.");
        }

        public void ExitToFirstPerson()
        {
            if (!_thirdPerson || _suspended || _transitioning)
                return;

            // The throw aim view is a third-person camera; leaving third person ends it outright.
            EndAimViewImmediate();

            var pos = steveRoot != null ? steveRoot.transform.position : transform.position;
            var yaw = steveRoot != null ? steveRoot.transform.eulerAngles.y : transform.eulerAngles.y;
            var velocity = SteveVelocity();

            var tuning = PlayerTuning.Active.cameraTransition;
            var brain = camerasCm3 != null ? camerasCm3.GetComponentInChildren<CinemachineBrain>(true) : null;
            if (tuning.toFirstPersonSeconds > 0f && brain != null && brain.isActiveAndEnabled &&
                ResolveFpsCamera() != null)
            {
                _exitTppRoutine = StartCoroutine(ExitThirdPersonRoutine(pos, yaw, velocity, brain,
                    tuning.toFirstPersonSeconds));
                return;
            }

            ApplyMode(thirdPerson: false, teleport: true, pos, yaw);
            if (tuning.keepMomentumIntoFirstPerson)
                SetFpsVelocity(velocity);
            _onModeChanged?.Invoke(false);
            ApplyCombatInputBlock();
            Debug.Log("[DualPlayer] Switched to FPS.");
        }

        /// <summary>
        /// The way back into the eyes, mirrored from the way out.
        ///
        /// Control changes hands at once - Malbers stops, Cowsins takes over at Steve's position and
        /// velocity - so the player is never left without a controller. Only the VIEW travels: the
        /// Cinemachine brain keeps rendering and blends from the shoulder camera to a camera that is
        /// re-parked on the live first-person eye every frame, so position, rotation and field of view
        /// all ease into exactly what the FPS camera will show. Only then are the FPS cameras switched
        /// on and the third-person rig off, on a frame where the two views are identical.
        /// </summary>
        System.Collections.IEnumerator ExitThirdPersonRoutine(Vector3 pos, float yaw, Vector3 velocity,
            CinemachineBrain brain, float seconds)
        {
            _thirdPerson = false;
            _transitioning = true;

            // Malbers stops now and steps out of the way. It stays active, frozen and invisible, so
            // the shoulder camera still has its target while the view travels away from it.
            if (_steveStarted)
                EnableMalbers(false);
            FreezeSteveForExit(true);

            PlaceFps(pos, yaw);
            SetCowsinsInputEnabled(true);
            SetFpsActive(true);
            _onModeChanged?.Invoke(false);
            // The bridge has just switched the whole Cowsins stack back on, cameras included; they
            // stay dark until the travel ends so exactly one camera renders throughout.
            MuteFpsCameras(true);

            if (PlayerTuning.Active.cameraTransition.keepMomentumIntoFirstPerson)
                SetFpsVelocity(velocity);
            ApplyCombatInputBlock();

            var eye = ResolveFpsCamera();
            PrepareHandoffBlend(brain, seconds);
            PlaceHandoffOnEye(eye);
            _handoffCamera.Priority.Value = 1000;

            float end = Time.time + seconds;
            while (Time.time < end)
            {
                yield return null;
                if (eye == null)
                    break;
                // Tracks the eye, not a snapshot of it: the player can already look around, and the
                // blend has to land on wherever they are looking when it finishes.
                PlaceHandoffOnEye(eye);
                MuteFpsCameras(true);
            }

            // One more frame parked on the eye, so the brain has fully arrived before the cut.
            yield return null;

            MuteFpsCameras(false);
            SetFpsSideRenderersHidden(false);
            FreezeSteveForExit(false);
            if (camerasCm3 != null)
                camerasCm3.SetActive(false);
            if (steveRoot != null)
                steveRoot.SetActive(false);
            ResetCameraHandoff();

            if (UIController.Instance != null)
                UIController.Instance.LockMouse();

            _exitTppRoutine = null;
            _transitioning = false;
            Debug.Log("[DualPlayer] Switched to FPS.");
        }

        void ApplyMode(bool thirdPerson, bool teleport, Vector3 pos = default, float yaw = 0f)
        {
            _thirdPerson = thirdPerson;

            if (!thirdPerson)
                EndAimViewImmediate();

            if (thirdPerson)
            {
                // Park FPS first so its capsule cannot fight Steve on land (demo has no ghost body).
                SetFpsActive(false);
                // No camera handoff on this path: it is the instant teleport route and runs entirely
                // within one frame, so there is nothing for a blend to happen across. The geometry
                // still has to be hidden either way.
                SetFpsSideRenderersHidden(true);
                SetFpsCharacterMeshVisible(false);
                SetCowsinsInputEnabled(false);
                if (teleport)
                    PlaceSteve(pos, yaw);
                else if (steveRoot != null)
                    steveRoot.SetActive(true);
                EnableMalbers(true);
                // Activate cameras after Steve is placed, then snap follow (avoids damp chase / land bounce).
                ActivateMalbersCameras(snap: true);
                BindMalbersToTpsCamera();
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else
            {
                if (_steveStarted)
                    EnableMalbers(false);

                ResetCameraHandoff();
                // The character mesh is deliberately NOT restored here. Whether it should be visible
                // in first person is the bridge's call - hidden for whoever is playing this body,
                // shown for everyone watching it - and second-guessing that from here is what would
                // put your own head in front of your own camera.
                SetFpsSideRenderersHidden(false);

                if (camerasCm3 != null)
                    camerasCm3.SetActive(false);
                if (steveRoot != null)
                    steveRoot.SetActive(false);
                if (teleport)
                    PlaceFps(pos, yaw);
                SetCowsinsInputEnabled(true);
                SetFpsActive(true);
                // Enabling Cowsins input re-enables every one of its actions, including any that are
                // muted because a body is being held.
                ApplyCombatInputBlock();
            }
        }

        void SetFpsActive(bool active)
        {
            CacheFps();

            if (_playerControl != null)
            {
                if (active)
                    _playerControl.CheckIfCanGrantControl();
                else
                    _playerControl.LoseControl();
            }

            if (_fpsRigidbody != null)
            {
                if (!active)
                {
                    _fpsRigidbody.linearVelocity = Vector3.zero;
                    _fpsRigidbody.angularVelocity = Vector3.zero;
                }

                _fpsRigidbody.isKinematic = !active;
                _fpsRigidbody.useGravity = active;
            }

            if (fpsRoot != null && fpsRoot != gameObject)
                fpsRoot.SetActive(true);

            SetFpsCollidersEnabled(active);

            // Keep the unused FPS body out of Steve's space while in TPP.
            if (!active && fpsBody != null)
                fpsBody.position = GetFpsWorldPosition() + Vector3.down * 500f;

            SetFpsCamerasEnabled(active);

            if (active && UIController.Instance != null)
                UIController.Instance.LockMouse();
        }

        void SetFpsCamerasEnabled(bool enabled)
        {
            CacheFps();

            if (enabled && fpsRoot != null)
                EnableActiveChain(fpsRoot.transform);

            if (fpsCameraRoot != null)
            {
                if (enabled)
                    EnableActiveChain(fpsCameraRoot.transform);
                fpsCameraRoot.SetActive(enabled);
            }

            if (fpsRoot == null)
                return;

            foreach (var cam in fpsRoot.GetComponentsInChildren<Camera>(true))
            {
                if (cam == null)
                    continue;
                if (camerasCm3 != null && cam.transform.IsChildOf(camerasCm3.transform))
                    continue;

                if (enabled)
                    EnableActiveChain(cam.transform);
                cam.enabled = enabled;
                cam.gameObject.SetActive(enabled);
            }
        }

        static void EnableActiveChain(Transform t)
        {
            while (t != null)
            {
                t.gameObject.SetActive(true);
                t = t.parent;
            }
        }

        static Transform FindDeepChild(Transform root, string name)
        {
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeepChild(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        void SetFpsCollidersEnabled(bool enabled)
        {
            if (fpsRoot == null)
                return;

            if (!enabled)
            {
                _fpsCollidersDisabledForTpp.Clear();
                foreach (var col in fpsRoot.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null || !col.enabled)
                        continue;
                    col.enabled = false;
                    _fpsCollidersDisabledForTpp.Add(col);
                }
                return;
            }

            foreach (var col in _fpsCollidersDisabledForTpp)
            {
                if (col != null)
                    col.enabled = true;
            }
            _fpsCollidersDisabledForTpp.Clear();
        }

        #region Mode switch camera and leftover geometry

        /// <summary>
        /// Starts the pull-back by parking a Cinemachine camera exactly where the FPS eyes are.
        ///
        /// The snap happened because the two views have nothing in common: the FPS camera is a plain
        /// Camera with no Cinemachine involvement, so the brain had no idea where the view was coming
        /// from and simply cut to the shoulder camera the instant the rig switched on.
        ///
        /// Giving the brain a camera at the eye pose, at a priority nothing else can beat, means its
        /// first frame is identical to the last FPS frame - no visible handover. Dropping that
        /// priority afterwards is what produces the movement: the brain is already live on this pose,
        /// so losing it is a blend rather than a cut, and Cinemachine dollies back to the shoulder.
        ///
        /// Must be called BEFORE the FPS camera is switched off, while its pose is still readable,
        /// and in the same frame so the two are never both rendering.
        /// </summary>
        void BeginCameraHandoff()
        {
            float seconds = PlayerTuning.Active.cameraTransition.toThirdPersonSeconds;
            if (camerasCm3 == null || seconds <= 0f)
                return;

            AdoptUnderPlayerMain(camerasCm3);
            camerasCm3.SetActive(true);

            var brain = camerasCm3.GetComponentInChildren<CinemachineBrain>(true);
            if (brain == null)
                return;

            var eye = ResolveFpsCamera();
            if (eye == null)
                return;

            PrepareHandoffBlend(brain, seconds);
            // Lens included: the handoff starts at the FPS field of view, so the brain eases the FOV
            // to the shoulder camera's along with the position instead of popping it on frame one.
            PlaceHandoffOnEye(eye);
            // Above the rig's highest (LockOn at 20) by a wide margin, so nothing outbids the eye
            // pose while the character and its follow targets are still being placed.
            _handoffCamera.Priority.Value = 1000;
        }

        /// <summary>
        /// Hands the view over to the third-person camera, which blends because the brain is
        /// currently live on the handoff pose.
        /// </summary>
        void EndCameraHandoff()
        {
            if (_handoffCamera == null)
                return;

            _handoffCamera.Priority.Value = -1000;
            // Left active until the blend has finished: deactivating the camera it is blending FROM
            // would collapse the blend into the cut this exists to avoid.
            StartCoroutine(RetireHandoffCamera(PlayerTuning.Active.cameraTransition.toThirdPersonSeconds + 0.1f));
        }

        System.Collections.IEnumerator RetireHandoffCamera(float delay)
        {
            yield return new WaitForSeconds(delay);
            // Not while a later transition is using it again.
            if (_handoffCamera != null && !_transitioning)
                _handoffCamera.gameObject.SetActive(false);
            if (!_transitioning && !_aimEyeLive)
                RestoreBrainBlends();
        }

        /// <summary>
        /// Sets the brain up to blend over <paramref name="seconds"/> with the tuned easing, and makes
        /// sure the handoff camera exists.
        ///
        /// Custom blends are lifted for the duration: a rig-specific "any camera" entry would
        /// otherwise override this blend and quietly bring the snap back.
        /// </summary>
        void PrepareHandoffBlend(CinemachineBrain brain, float seconds)
        {
            PrepareBrainBlend(brain, seconds);

            if (_handoffCamera == null)
            {
                var go = new GameObject("CM FPS Handoff");
                go.transform.SetParent(camerasCm3.transform, false);
                _handoffCamera = go.AddComponent<CinemachineCamera>();
            }

            _handoffCamera.gameObject.SetActive(true);
        }

        /// <summary>
        /// Makes the brain's next blend take <paramref name="seconds"/> with the tuned easing, keeping
        /// the rig's own settings to put back afterwards (RestoreBrainBlends).
        /// </summary>
        void PrepareBrainBlend(CinemachineBrain brain, float seconds)
        {
            if (!_blendSaved)
            {
                _savedBlend = brain.DefaultBlend;
                _savedCustomBlends = brain.CustomBlends;
                _blendSaved = true;
            }

            brain.CustomBlends = null;
            brain.DefaultBlend = new CinemachineBlendDefinition(
                PlayerTuning.Active.cameraTransition.blendStyle, seconds);
        }

        /// <summary>Parks the handoff camera exactly on the FPS eye: pose, field of view and near plane.</summary>
        void PlaceHandoffOnEye(Camera eye)
        {
            if (_handoffCamera == null || eye == null)
                return;

            _handoffCamera.transform.SetPositionAndRotation(eye.transform.position, eye.transform.rotation);
            var lens = _handoffCamera.Lens;
            lens.FieldOfView = eye.fieldOfView;
            lens.NearClipPlane = eye.nearClipPlane;
            _handoffCamera.Lens = lens;
        }

        void ResetCameraHandoff()
        {
            if (_handoffCamera != null)
            {
                _handoffCamera.Priority.Value = -1000;
                _handoffCamera.gameObject.SetActive(false);
            }

            RestoreBrainBlends();
        }

        void RestoreBrainBlends()
        {
            if (!_blendSaved || camerasCm3 == null)
                return;

            var brain = camerasCm3.GetComponentInChildren<CinemachineBrain>(true);
            if (brain != null)
            {
                brain.DefaultBlend = _savedBlend;
                brain.CustomBlends = _savedCustomBlends;
            }
            _blendSaved = false;
        }

        /// <summary>The live FPS eye, preferring the tagged main camera over the weapon overlay.</summary>
        Transform ResolveFpsCameraTransform()
        {
            var camera = ResolveFpsCamera();
            return camera != null ? camera.transform : null;
        }

        Camera ResolveFpsCamera()
        {
            var root = fpsCameraRoot != null ? fpsCameraRoot : fpsRoot;
            if (root == null)
                return null;

            Camera fallback = null;
            foreach (var camera in root.GetComponentsInChildren<Camera>(true))
            {
                if (camera == null)
                    continue;
                if (camerasCm3 != null && camera.transform.IsChildOf(camerasCm3.transform))
                    continue;
                if (camera.CompareTag("MainCamera"))
                    return camera;
                if (fallback == null)
                    fallback = camera;
            }

            return fallback;
        }

        /// <summary>
        /// Keeps the FPS cameras from rendering (and their listener from hearing) while leaving their
        /// objects active, so mouse look and the FOV manager keep updating the eye the blend is
        /// travelling towards.
        /// </summary>
        void MuteFpsCameras(bool mute)
        {
            if (fpsRoot == null)
                return;

            foreach (var camera in fpsRoot.GetComponentsInChildren<Camera>(true))
            {
                if (camera == null || (camerasCm3 != null && camera.transform.IsChildOf(camerasCm3.transform)))
                    continue;
                camera.enabled = !mute;
            }

            foreach (var listener in fpsRoot.GetComponentsInChildren<AudioListener>(true))
            {
                if (listener != null)
                    listener.enabled = !mute;
            }
        }

        /// <summary>
        /// Steve during the camera's journey back into the eyes: invisible, so the camera does not
        /// pass through its head; collision-free, so the FPS capsule standing in the same spot is not
        /// shoved; and kinematic, so it cannot fall through the floor it no longer collides with.
        /// </summary>
        void FreezeSteveForExit(bool freeze)
        {
            if (steveRoot == null)
                return;

            if (freeze)
            {
                if (_steveFrozen)
                    return;
                _steveFrozen = true;

                _steveRenderersHidden.Clear();
                foreach (var renderer in steveRoot.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !renderer.enabled)
                        continue;
                    renderer.enabled = false;
                    _steveRenderersHidden.Add(renderer);
                }

                _steveCollidersDisabled.Clear();
                foreach (var collider in steveRoot.GetComponentsInChildren<Collider>(true))
                {
                    if (collider == null || !collider.enabled)
                        continue;
                    collider.enabled = false;
                    _steveCollidersDisabled.Add(collider);
                }

                var rb = animal != null ? animal.RB : null;
                if (rb != null)
                {
                    _steveWasKinematic = rb.isKinematic;
                    if (!rb.isKinematic)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    rb.isKinematic = true;
                }
                return;
            }

            if (!_steveFrozen)
                return;
            _steveFrozen = false;

            foreach (var renderer in _steveRenderersHidden)
            {
                if (renderer != null)
                    renderer.enabled = true;
            }
            _steveRenderersHidden.Clear();

            foreach (var collider in _steveCollidersDisabled)
            {
                if (collider != null)
                    collider.enabled = true;
            }
            _steveCollidersDisabled.Clear();

            var body = animal != null ? animal.RB : null;
            if (body != null)
                body.isKinematic = _steveWasKinematic;
        }

        Vector3 SteveVelocity()
        {
            var rb = animal != null ? animal.RB : null;
            return rb != null && !rb.isKinematic ? rb.linearVelocity : Vector3.zero;
        }

        void SetFpsVelocity(Vector3 velocity)
        {
            if (_fpsRigidbody != null && !_fpsRigidbody.isKinematic)
                _fpsRigidbody.linearVelocity = velocity;
        }

        /// <summary>
        /// Switches off FPS geometry that would otherwise be drawn by the third-person camera.
        ///
        /// SetFpsActive deactivates the camera subtree and force-sets fpsRoot ACTIVE, so anything
        /// renderable that lives under fpsRoot but outside that subtree survives the switch - and the
        /// Cinemachine brain camera culls nothing at all, so it draws every one of them, including
        /// first-person arms and any head mesh left behind.
        ///
        /// Only renderers this component is switching off are remembered, so restoring cannot turn on
        /// something that was already hidden for its own reasons.
        /// </summary>
        void SetFpsSideRenderersHidden(bool hidden)
        {
            if (!hidden)
            {
                foreach (var renderer in _fpsRenderersHiddenForTpp)
                {
                    if (renderer != null)
                        renderer.enabled = true;
                }
                _fpsRenderersHiddenForTpp.Clear();
                return;
            }

            // Additive: hiding twice (third person, then death) must not forget what the first call
            // hid, or those renderers would never come back.
            if (fpsRoot == null)
                return;

            foreach (var renderer in fpsRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled || _fpsRenderersHiddenForTpp.Contains(renderer))
                    continue;
                // Steve and the camera rig live elsewhere in the hierarchy, but guard anyway - they
                // can be re-parented under PlayerMain at runtime.
                if (steveRoot != null && renderer.transform.IsChildOf(steveRoot.transform))
                    continue;
                if (camerasCm3 != null && renderer.transform.IsChildOf(camerasCm3.transform))
                    continue;

                renderer.enabled = false;
                _fpsRenderersHiddenForTpp.Add(renderer);
            }
        }

        /// <summary>
        /// Hides the third-person character mesh belonging to the FPS side.
        ///
        /// Routed through the bridge when there is one, because it owns the rules about who may see
        /// that mesh; the direct path is for offline play, where the bridge does not exist at all and
        /// nothing was hiding it.
        /// </summary>
        void SetFpsCharacterMeshVisible(bool visible)
        {
            if (_networkBridge != null)
            {
                _networkBridge.SetRemoteBodyVisible(visible);
                return;
            }

            var render = transform.Find("Player Render");
            if (render == null)
                return;

            foreach (var renderer in render.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }
        }

        #endregion

        void ActivateMalbersCameras(bool snap)
        {
            if (camerasCm3 == null)
                return;

            AdoptUnderPlayerMain(camerasCm3);
            camerasCm3.SetActive(true);

            DisableSprintCameraZoom();

            if (!snap)
                return;

            foreach (var follow in camerasCm3.GetComponentsInChildren<ThirdPersonFollowTarget>(true))
            {
                if (follow != null && follow.isActiveAndEnabled)
                    follow.TargetTeleport();
            }
        }

        void SetCowsinsInputEnabled(bool enabled)
        {
            if (fpsRoot == null)
                return;

            foreach (var inputManager in fpsRoot.GetComponentsInChildren<InputManager>(true))
                inputManager.enabled = enabled;

            foreach (var playerInput in fpsRoot.GetComponentsInChildren<PlayerInput>(true))
            {
                if (!enabled)
                    playerInput.DeactivateInput();
                playerInput.enabled = enabled;
                if (enabled)
                    playerInput.ActivateInput();
            }
        }

        void EnableMalbers(bool enabled)
        {
            if (animal == null && steveRoot != null)
                animal = steveRoot.GetComponentInChildren<MAnimal>(true);
            if (animal == null || steveRoot == null)
                return;

            // PlayerInput MUST be enabled before MInputLink.OnEnable, or MInputLink disables itself.
            foreach (var playerInput in steveRoot.GetComponentsInChildren<PlayerInput>(true))
            {
                playerInput.enabled = enabled;
                if (enabled)
                {
                    playerInput.notificationBehavior = PlayerNotifications.InvokeCSharpEvents;
                    try
                    {
                        if (playerInput.actions != null)
                            playerInput.SwitchCurrentActionMap("Gameplay");
                    }
                    catch
                    {
                        // Map name may differ; MInputLink will pick DefaultMap.
                    }
                }
            }

            if (enabled)
            {
                animal.SetMainPlayer();
                animal.Sleep = false;
                animal.LockInput = false;
                animal.LockMovement = false;
                CapSteveJumps();
                RemoveMalbersPunch();

                // Bounce MInputLink so it reconnects with an active PlayerInput.
                var link = steveRoot.GetComponentInChildren<MInputLink>(true);
                if (link != null)
                {
                    link.enabled = false;
                    link.enabled = true;
                    animal.InputSource = link;
                    animal.ResetInputSource();
                }
                else
                {
                    animal.UpdateInputSource(true);
                    if (animal.InputSource != null)
                        animal.InputSource.Enable(true);
                }

                BindMalbersToTpsCamera();

                // A body held across the switch keeps its slowdown and its muted inputs in the new mode.
                ApplyCarrySlowdown();
                ApplyCombatInputBlock();
            }
            else
            {
                var link = steveRoot.GetComponentInChildren<MInputLink>(true);
                if (link != null)
                    link.enabled = false;
                else if (animal.InputSource != null)
                    animal.InputSource.Enable(false);

                animal.LockInput = true;
                animal.LockMovement = true;
                animal.DisableMainPlayer();
            }
        }

        void BindMalbersToTpsCamera()
        {
            if (animal != null)
            {
                animal.UseCameraInput = true;
                animal.Strafe = false;

                var brain = camerasCm3 != null
                    ? camerasCm3.GetComponentInChildren<CinemachineBrain>(true)
                    : FindFirstObjectByType<CinemachineBrain>(FindObjectsInactive.Include);
                if (brain != null)
                {
                    brain.gameObject.tag = "MainCamera";
                    animal.m_MainCamera.UseConstant = true;
                    animal.m_MainCamera.Value = brain.transform;
                    animal.FindCamera();

                    if (animal.Aimer == null)
                        animal.Aimer = animal.GetComponentInChildren<Aim>(true);
                    if (animal.Aimer != null)
                        animal.Aimer.MainCamera = brain.transform;
                }
            }
        }

        void CapSteveJumps()
        {
            if (_steveTuned || animal == null)
                return;
            _steveTuned = true;

            var jumpBasic = animal.State_Get<JumpBasic>();
            if (jumpBasic != null)
            {
                // One jump, matching first person (Cowsins maxJumps is 1): double jumping was removed
                // from the game, and a mode switch must not quietly bring it back.
                jumpBasic.Jumps.Value = 1;
                // The Meshy characters are taller than Steve, so the stock jump reads as a low hop.
                // Nudge each jump profile's apex + launch speed up a little. Malbers clones states per
                // animal at runtime, so this touches only this player's instance and resets each play.
                if (jumpBasic.profiles != null)
                {
                    for (int i = 0; i < jumpBasic.profiles.Count; i++)
                    {
                        var p = jumpBasic.profiles[i];
                        if (p.Height != null)
                            p.Height.Value *= SteveJumpBoost;
                        p.VerticalSpeed *= SteveJumpBoost;
                        jumpBasic.profiles[i] = p;
                    }
                }
            }

            var stats = animal.GetComponent<Stats>() ?? steveRoot.GetComponentInChildren<Stats>(true);
            var stamina = stats != null ? stats.Stat_Get("Stamina") : null;
            if (stamina != null)
            {
                stamina.DegenRate.Value = 5f;
                if (stamina.RegenRate.Value < 15f)
                    stamina.RegenRate.Value = 15f;
            }

            DisableSprintCameraZoom();
        }

        void DisableSprintCameraZoom()
        {
            if (camerasCm3 == null)
                return;

            foreach (var t in camerasCm3.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name.IndexOf("Sprint", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (t.name.IndexOf("Sprint Settings", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                var vcam = t.GetComponent<CinemachineCamera>();
                if (vcam != null)
                    vcam.enabled = false;

                var follow = t.GetComponent<CinemachineThirdPersonFollow>();
                if (follow != null)
                    follow.CameraDistance = 6f;
            }
        }

        void PlaceSteve(Vector3 position, float yawDegrees)
        {
            if (steveRoot == null)
                return;

            if (animal == null)
                animal = steveRoot.GetComponentInChildren<MAnimal>(true);

            AdoptUnderPlayerMain(steveRoot);
            steveRoot.SetActive(true);

            if (animal != null)
            {
                animal.Rotation = Quaternion.Euler(0f, yawDegrees, 0f);
                animal.Teleport(position);
            }
            else
            {
                steveRoot.transform.SetPositionAndRotation(
                    position,
                    Quaternion.Euler(0f, yawDegrees, 0f));
            }
        }

        /// <summary>
        /// How far the bottom of a body's capsule sits below the body's own pivot - where its feet
        /// actually are - read from the collider rather than assumed.
        ///
        /// On the Cowsins player the capsule is raised off its pivot (height 2, centre +0.5), so the
        /// feet are 0.5 m below the pivot, not the 1 m the old fixed offset assumed. That half metre
        /// is how far into the floor everyone else saw a first-person player standing, since the
        /// network root and the body drawn on it are placed at the feet this reports. Crouching
        /// scales the body, which the lossy scale here follows.
        /// </summary>
        public static float CapsuleFeetOffset(Transform body, float fallback)
        {
            if (body == null)
                return fallback;

            var capsule = body.GetComponent<CapsuleCollider>();
            if (capsule == null || capsule.direction != 1)
                return fallback;

            float halfHeight = Mathf.Max(capsule.height * 0.5f, capsule.radius);
            return Mathf.Max(0f, (halfHeight - capsule.center.y) * Mathf.Abs(body.lossyScale.y));
        }

        float FpsFeetOffset() => CapsuleFeetOffset(fpsBody, fpsBodyLocalY);

        void PlaceFps(Vector3 position, float yawDegrees)
        {
            CacheFps();
            if (fpsBody == null)
                return;

            // A couple of centimetres of clearance, so the capsule never starts inside the floor.
            var bodyPos = position + Vector3.up * (FpsFeetOffset() + 0.02f);
            var rot = Quaternion.Euler(0f, yawDegrees, 0f);
            fpsBody.SetPositionAndRotation(bodyPos, rot);

            if (_fpsRigidbody != null)
            {
                _fpsRigidbody.linearVelocity = Vector3.zero;
                _fpsRigidbody.angularVelocity = Vector3.zero;
                _fpsRigidbody.position = bodyPos;
            }

            if (_movement != null)
                _movement.TeleportPlayer(bodyPos, rot, true, true);

            transform.SetPositionAndRotation(position, rot);
        }

        /// <summary>The first-person player's feet: the bottom of the Cowsins capsule.</summary>
        Vector3 GetFpsWorldPosition()
        {
            if (fpsBody != null)
                return fpsBody.position - Vector3.up * FpsFeetOffset();
            return transform.position;
        }

        float GetFpsYaw()
        {
            if (_movement != null)
                return _movement.Orientation.Rotation.eulerAngles.y;
            if (fpsBody != null)
                return fpsBody.eulerAngles.y;
            return transform.eulerAngles.y;
        }

        #region Third-person throw aim view

        /// <summary>
        /// Moves the third-person view into the character's eyes to aim a throw (true), and back over
        /// the shoulder afterwards (false) - after <paramref name="returnDelay"/> seconds, so a throw
        /// can be watched leaving before the camera pulls back.
        ///
        /// Only the CAMERA changes. Malbers stays in control the whole time: no controller swap, no
        /// teleport, nothing for the network to see beyond the character turning to face the aim.
        /// The view is a Cinemachine camera parked on the eyes, so the brain blends position,
        /// rotation and field of view in and out exactly as it does for the mode switch.
        ///
        /// Safe to call every frame; only a change of request does anything.
        /// </summary>
        public void SetThirdPersonAimView(bool active, float returnDelay = 0f)
        {
            var settings = PlayerTuning.Active.cameraTransition;

            if (active)
            {
                if (_aimViewRequested || !_thirdPerson || _suspended || _transitioning ||
                    !settings.firstPersonAimInThirdPerson || steveRoot == null)
                    return;

                var brain = camerasCm3 != null ? camerasCm3.GetComponentInChildren<CinemachineBrain>(true) : null;
                if (brain == null || !brain.isActiveAndEnabled)
                    return;

                _aimViewRequested = true;
                if (_aimViewRoutine != null)
                    StopCoroutine(_aimViewRoutine);
                _aimViewRoutine = StartCoroutine(AimViewInRoutine(brain, settings));
                return;
            }

            if (!_aimViewRequested)
                return;

            _aimViewRequested = false;
            if (_aimViewRoutine != null)
                StopCoroutine(_aimViewRoutine);

            var outBrain = camerasCm3 != null ? camerasCm3.GetComponentInChildren<CinemachineBrain>(true) : null;
            if (outBrain == null || !_aimEyeLive)
            {
                EndAimViewImmediate();
                return;
            }

            _aimViewRoutine = StartCoroutine(AimViewOutRoutine(outBrain, settings, Mathf.Max(0f, returnDelay)));
        }

        System.Collections.IEnumerator AimViewInRoutine(CinemachineBrain brain,
            PlayerTuning.CameraSettings settings)
        {
            if (_aimEyeCamera == null)
            {
                var go = new GameObject("CM TPS Aim Eye");
                go.transform.SetParent(camerasCm3.transform, false);
                _aimEyeCamera = go.AddComponent<CinemachineCamera>();
            }

            // A fresh aim starts looking wherever the shoulder camera was looking. Re-aiming while
            // the view is still on its way out carries on from the eye camera's own direction.
            if (!_aimEyeLive)
            {
                var view = brain.transform;
                _aimYaw = view.eulerAngles.y;
                _aimPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, view.eulerAngles.x),
                    settings.aimViewPitchLimits.x, settings.aimViewPitchLimits.y);
                _aimEyeYValid = false;
            }

            _aimEyeLive = true;
            _aimEyeCamera.gameObject.SetActive(true);
            UpdateAimEye(settings, applyLook: false);

            PrepareBrainBlend(brain, settings.aimViewInSeconds);
            // Under the mode-switch handoff (1000), above every camera in the Malbers rig.
            _aimEyeCamera.Priority.Value = 900;

            // Facing the aim, like first person: sideways keys strafe, and teammates see the
            // character turned towards where the body is about to go.
            if (animal != null && !_aimStrafeSet)
            {
                _aimSavedStrafe = animal.Strafe;
                _aimStrafeSet = true;
                animal.Strafe = true;
            }

            float elapsed = 0f;
            bool hidden = false;
            while (true)
            {
                yield return null;
                elapsed += Time.deltaTime;
                UpdateAimEye(settings, applyLook: true);

                // Hidden only once the camera is nearly inside the head, so the character does not
                // vanish while the view is still behind it.
                if (!hidden && elapsed >= settings.aimViewInSeconds * 0.55f)
                {
                    SetSteveHiddenForAimView(true);
                    hidden = true;
                }
            }
        }

        System.Collections.IEnumerator AimViewOutRoutine(CinemachineBrain brain,
            PlayerTuning.CameraSettings settings, float delay)
        {
            // Still aiming from the eyes while the thrown body flies away.
            float waited = 0f;
            while (waited < delay)
            {
                yield return null;
                waited += Time.deltaTime;
                UpdateAimEye(settings, applyLook: true);
            }

            RestoreAimStrafe();
            PrepareBrainBlend(brain, settings.aimViewOutSeconds);
            // The brain blends back to the shoulder camera, which Malbers has kept aligned with the
            // eye camera's direction the whole time it was live - so the view comes back facing
            // where the throw went, not where the shoulder camera was left.
            _aimEyeCamera.Priority.Value = -1000;

            float elapsed = 0f;
            bool shown = false;
            while (elapsed < settings.aimViewOutSeconds + 0.05f)
            {
                yield return null;
                elapsed += Time.deltaTime;
                // Keeps riding with the character, but no longer takes the mouse: the shoulder
                // camera it is blending into has it now.
                UpdateAimEye(settings, applyLook: false);

                if (!shown && elapsed >= settings.aimViewOutSeconds * 0.35f)
                {
                    SetSteveHiddenForAimView(false);
                    shown = true;
                }
            }

            EndAimViewImmediate();
        }

        /// <summary>Drops the aim view at once, with no blend - on death, on a mode switch, or when finished.</summary>
        void EndAimViewImmediate()
        {
            _aimViewRequested = false;
            if (_aimViewRoutine != null)
            {
                StopCoroutine(_aimViewRoutine);
                _aimViewRoutine = null;
            }

            if (_aimEyeCamera != null)
            {
                _aimEyeCamera.Priority.Value = -1000;
                _aimEyeCamera.gameObject.SetActive(false);
            }

            SetSteveHiddenForAimView(false);
            RestoreAimStrafe();

            if (_aimEyeLive)
            {
                _aimEyeLive = false;
                if (!_transitioning)
                    RestoreBrainBlends();
            }
        }

        void RestoreAimStrafe()
        {
            if (!_aimStrafeSet)
                return;
            _aimStrafeSet = false;
            if (animal != null)
                animal.Strafe = _aimSavedStrafe;
        }

        /// <summary>
        /// Parks the eye camera on the character's eyes and, when it has the view, turns it with the
        /// mouse. The look is read from the same Malbers input the shoulder camera uses, with the
        /// same sensitivity and inversion, because Malbers stops turning its own camera the moment
        /// it is not the live one.
        /// </summary>
        void UpdateAimEye(PlayerTuning.CameraSettings settings, bool applyLook)
        {
            if (_aimEyeCamera == null || steveRoot == null)
                return;

            // The eye camera follows the mouse itself; a menu over the game holds it still too.
            if (applyLook && !_menuOpen)
                ApplyAimLook(settings);

            var feet = steveRoot.transform.position;
            var head = animal != null && animal.Anim != null && animal.Anim.isHuman
                ? animal.Anim.GetBoneTransform(HumanBodyBones.Head)
                : null;
            float targetY = head != null ? head.position.y + 0.08f : feet.y + settings.aimViewEyeHeight;

            // Smoothed, so the head's walk bob does not shake the view.
            _aimEyeY = _aimEyeYValid
                ? Mathf.Lerp(_aimEyeY, targetY, 1f - Mathf.Exp(-12f * Time.deltaTime))
                : targetY;
            _aimEyeYValid = true;

            var yaw = Quaternion.Euler(0f, _aimYaw, 0f);
            var position = new Vector3(feet.x, _aimEyeY, feet.z) + yaw * (Vector3.forward * 0.12f);
            _aimEyeCamera.transform.SetPositionAndRotation(position, Quaternion.Euler(_aimPitch, _aimYaw, 0f));

            var lens = _aimEyeCamera.Lens;
            float fov = settings.aimViewFieldOfView;
            if (fov <= 0f)
            {
                var fpsCamera = ResolveFpsCamera();
                fov = fpsCamera != null ? fpsCamera.fieldOfView : 60f;
            }
            lens.FieldOfView = fov;
            lens.NearClipPlane = 0.05f;
            _aimEyeCamera.Lens = lens;
        }

        void ApplyAimLook(PlayerTuning.CameraSettings settings)
        {
            var source = LookSource();
            if (source == null)
                return;

            var look = source.look.Value;
            if (look.sqrMagnitude < 0.00001f)
                return;

            // The same arithmetic ThirdPersonFollowTarget uses, so the eyes turn exactly as fast as
            // the shoulder camera did.
            float multiplier = source.UsingMouse.Value ? 1f : Time.deltaTime * source.GamepadMult.Value;
            _aimYaw += look.x * (source.invertX.Value ? -1f : 1f) * source.XMultiplier.Value * multiplier;
            _aimPitch += look.y * (source.invertY.Value ? 1f : -1f) * source.YMultiplier.Value * multiplier;
            _aimPitch = Mathf.Clamp(_aimPitch, settings.aimViewPitchLimits.x, settings.aimViewPitchLimits.y);
        }

        /// <summary>
        /// The rig's look input. It is routed to a single camera's follow target, so whichever one is
        /// currently receiving a value is the one to read.
        /// </summary>
        ThirdPersonFollowTarget LookSource()
        {
            if (camerasCm3 == null)
                return null;
            if (_lookSources == null || _lookSources.Length == 0)
                _lookSources = camerasCm3.GetComponentsInChildren<ThirdPersonFollowTarget>(true);

            ThirdPersonFollowTarget best = null;
            float bestMagnitude = -1f;
            foreach (var source in _lookSources)
            {
                if (source == null)
                    continue;
                float magnitude = source.look.Value.sqrMagnitude;
                if (magnitude > bestMagnitude)
                {
                    bestMagnitude = magnitude;
                    best = source;
                }
            }

            return best;
        }

        /// <summary>
        /// Takes Steve out of the aim view without losing his shadow: renderers switch to shadows
        /// only, the way a first-person body usually works, rather than switching off.
        /// </summary>
        void SetSteveHiddenForAimView(bool hidden)
        {
            if (!hidden)
            {
                for (int i = 0; i < _steveRenderersShadowOnly.Count; i++)
                {
                    if (_steveRenderersShadowOnly[i] != null)
                        _steveRenderersShadowOnly[i].shadowCastingMode = _steveShadowModes[i];
                }
                _steveRenderersShadowOnly.Clear();
                _steveShadowModes.Clear();
                return;
            }

            if (steveRoot == null || _steveRenderersShadowOnly.Count > 0)
                return;

            foreach (var renderer in steveRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled ||
                    renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                    continue;
                _steveRenderersShadowOnly.Add(renderer);
                _steveShadowModes.Add(renderer.shadowCastingMode);
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
        }

        #endregion

        #region Death

        /// <summary>
        /// Parks BOTH controllers for as long as the player is dead: no movement, no input, no
        /// living camera, and no capsule left standing where the corpse lies.
        ///
        /// A death in third person first drops back to (parked) first person, so there is a single
        /// state to come back from. The spectator camera takes over the view.
        /// </summary>
        public void EnterDowned()
        {
            if (_suspended)
                return;

            StopModeTransitions();
            SetCarrying(false, 1f);

            if (_thirdPerson)
            {
                var pos = steveRoot != null ? steveRoot.transform.position : transform.position;
                var yaw = steveRoot != null ? steveRoot.transform.eulerAngles.y : transform.eulerAngles.y;

                if (_steveStarted)
                    EnableMalbers(false);
                if (camerasCm3 != null)
                    camerasCm3.SetActive(false);
                if (steveRoot != null)
                    steveRoot.SetActive(false);

                _thirdPerson = false;
                // Where the player actually died, so anything reading the gameplay position while
                // they are down reads a sensible place rather than the parked FPS body.
                PlaceFps(pos, yaw);
                _onModeChanged?.Invoke(false);
            }

            _suspended = true;
            SetCowsinsInputEnabled(false);
            SetFpsActive(false);
            SetFpsSideRenderersHidden(true);
        }

        /// <summary>
        /// Returns control after a revive, always in first person. The caller has already placed the
        /// player (ReviveAt teleports before clearing IsDead).
        /// </summary>
        public void ExitDowned()
        {
            if (!_suspended)
                return;

            _suspended = false;
            SetFpsSideRenderersHidden(false);
            SetCowsinsInputEnabled(true);
            SetFpsActive(true);
            ApplyCombatInputBlock();

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        bool _menuOpen;
        bool _menuTookControl;
        bool _menuLockedInput;
        bool _menuLockedMovement;
        readonly List<ThirdPersonFollowTarget> _menuHeldCameras = new();

        /// <summary>True while a menu is open over the running game (pause, settings).</summary>
        public bool IsMenuOpen => _menuOpen;

        /// <summary>
        /// A menu over the running game, where nothing actually pauses: the player stands still and
        /// the camera stops following the mouse, in whichever mode is live, and the rest of the
        /// world carries on.
        ///
        /// Safe to call every frame while the menu is up: a revive or a mode switch that hands
        /// control back underneath it has it taken away again on the next call.
        /// </summary>
        public void SetMenuOpen(bool open)
        {
            if (open)
            {
                _menuOpen = true;
                HoldForMenu();
                return;
            }

            if (!_menuOpen)
                return;

            _menuOpen = false;
            ReleaseMenuHold();
        }

        void HoldForMenu()
        {
            // First person: Cowsins' own gate stops movement, look, shooting and interaction. Only
            // control the menu took is given back - a creep's grab or death took theirs for a reason.
            if (_playerControl != null && _playerControl.IsControllable)
            {
                _playerControl.LoseControl();
                _menuTookControl = true;
            }

            if (!_thirdPerson || animal == null)
                return;

            // Third person: Steve stops taking input, and the shoulder camera stops turning. Only
            // the locks this took are given back, so a Malbers state holding its own keeps it.
            if (!animal.LockInput)
            {
                animal.LockInput = true;
                _menuLockedInput = true;
            }
            if (!animal.LockMovement)
            {
                animal.LockMovement = true;
                _menuLockedMovement = true;
            }

            if (_menuHeldCameras.Count == 0 && camerasCm3 != null)
            {
                foreach (var follow in camerasCm3.GetComponentsInChildren<ThirdPersonFollowTarget>(true))
                {
                    if (follow == null || !follow.AllowCameraRotation.Value)
                        continue;
                    follow.AllowCameraRotation.Value = false;
                    _menuHeldCameras.Add(follow);
                }
            }
        }

        void ReleaseMenuHold()
        {
            foreach (var follow in _menuHeldCameras)
            {
                if (follow != null)
                    follow.AllowCameraRotation.Value = true;
            }
            _menuHeldCameras.Clear();

            if (animal != null)
            {
                if (_menuLockedInput)
                    animal.LockInput = false;
                if (_menuLockedMovement)
                    animal.LockMovement = false;
            }
            _menuLockedInput = _menuLockedMovement = false;

            // Dead: control comes back with the revive instead. Held by a creep: when it lets go.
            bool took = _menuTookControl;
            _menuTookControl = false;
            bool grabbed = _networkBridge != null && _networkBridge.IsHeldByCreep;
            if (took && !grabbed && !_suspended && !_thirdPerson && _playerControl != null)
                _playerControl.CheckIfCanGrantControl();
        }

        /// <summary>Abandons any camera transition in flight, putting everything it borrowed back.</summary>
        void StopModeTransitions()
        {
            EndAimViewImmediate();

            if (_enterTppRoutine != null)
            {
                StopCoroutine(_enterTppRoutine);
                _enterTppRoutine = null;
            }

            if (_exitTppRoutine != null)
            {
                StopCoroutine(_exitTppRoutine);
                _exitTppRoutine = null;
                MuteFpsCameras(false);
            }

            FreezeSteveForExit(false);
            ResetCameraHandoff();
            _transitioning = false;
        }

        #endregion

        #region Carrying support

        /// <summary>
        /// The view the telekinesis aims along, in either mode: from the eyes in first person, and
        /// from the chest along the camera's aim in third person (the camera itself is metres behind
        /// the character, which is the wrong place to hold a body from).
        /// </summary>
        public bool TryGetAim(out Vector3 origin, out Vector3 forward)
        {
            origin = default;
            forward = Vector3.forward;

            if (_suspended)
                return false;

            if (_thirdPerson)
            {
                if (steveRoot == null)
                    return false;

                var brain = camerasCm3 != null ? camerasCm3.GetComponentInChildren<CinemachineBrain>(true) : null;
                forward = brain != null ? brain.transform.forward : steveRoot.transform.forward;
                // From the eyes while the throw aim view is up, exactly as in first person, so the
                // body floats where it would in first person and the throw leaves from the view.
                origin = _aimEyeLive && _aimEyeCamera != null
                    ? _aimEyeCamera.transform.position
                    : steveRoot.transform.position + Vector3.up * TpsChestHeight;
                return true;
            }

            var eye = ResolveFpsCameraTransform();
            if (eye == null)
                return false;

            origin = eye.position;
            forward = eye.forward;
            return true;
        }

        /// <summary>How fast the active body is moving, whichever controller is driving it.</summary>
        public Vector3 GetBodyVelocity()
        {
            if (_thirdPerson)
                return SteveVelocity();

            return _fpsRigidbody != null && !_fpsRigidbody.isKinematic
                ? _fpsRigidbody.linearVelocity
                : Vector3.zero;
        }

        /// <summary>Every collider belonging to this player's living bodies, active or not.</summary>
        public void CollectOwnColliders(List<Collider> into)
        {
            if (into == null)
                return;

            if (fpsRoot != null)
                into.AddRange(fpsRoot.GetComponentsInChildren<Collider>(true));
            if (steveRoot != null)
                into.AddRange(steveRoot.GetComponentsInChildren<Collider>(true));
        }

        /// <summary>
        /// Carrying a body: slows the player down and mutes every input that shares a key with the
        /// telekinesis, in whichever mode is active - and in the other one too, so switching modes
        /// mid-carry cannot sneak a punch or a gunshot in.
        /// </summary>
        public void SetCarrying(bool carrying, float speedMultiplier)
        {
            _carrying = carrying;
            _carrySpeedMultiplier = carrying ? Mathf.Clamp(speedMultiplier, 0.05f, 1f) : 1f;
            ApplyCarrySlowdown();
            ApplyCombatInputBlock();
        }

        void ApplyCarrySlowdown()
        {
            bool slow = _carrying && _carrySpeedMultiplier < 0.999f;

            // First person: Cowsins' own weight multiplier, which its movement already scales the
            // player's speed by. A modifier tagged with this component, so it never touches the
            // weapon weight Cowsins keeps in the same stat.
            var multipliers = fpsRoot != null ? fpsRoot.GetComponentInChildren<PlayerMultipliers>(true) : null;
            if (multipliers != null)
            {
                multipliers.WeightMultiplier.RemoveModifierFromSource(this);
                if (slow)
                    multipliers.WeightMultiplier.AddModifier(new cowsins.StatModifier(
                        _carrySpeedMultiplier - 1f, cowsins.StatModifierType.Multiplicative, this));
            }

            // Third person: Malbers' time multiplier, which scales its root-motion movement and the
            // animation driving it together, so the character walks slower rather than sliding.
            if (animal == null)
                return;

            if (slow)
            {
                if (!_timeScaled)
                {
                    _savedTimeMultiplier = animal.TimeMultiplier;
                    _timeScaled = true;
                }
                animal.TimeMultiplier = _savedTimeMultiplier * _carrySpeedMultiplier;
            }
            else if (_timeScaled)
            {
                animal.TimeMultiplier = _savedTimeMultiplier;
                _timeScaled = false;
            }
        }

        /// <summary>
        /// While a body is targeted but not yet held, the interact key belongs to the telekinesis:
        /// otherwise pressing E at a body lying beside a door or a pickup would lift the body AND
        /// use the door.
        /// </summary>
        public void SetInteractClaimed(bool claimed)
        {
            if (_interactClaimed == claimed)
                return;
            _interactClaimed = claimed;
            ApplyCombatInputBlock();
        }

        /// <summary>
        /// Mutes exactly the controller inputs the telekinesis currently needs to itself - everything
        /// that shares a key while a body is held, just interact while one is targeted, nothing
        /// otherwise - and restores the rest. Re-applied after every mode change, because enabling
        /// Cowsins input re-enables all of its actions.
        /// </summary>
        void ApplyCombatInputBlock()
        {
            bool live = !_suspended;
            string[] cowsinsWanted = !live ? System.Array.Empty<string>()
                : _carrying ? CowsinsCarryBlocked
                : _interactClaimed ? CowsinsInteractOnly
                : System.Array.Empty<string>();
            string malbersWanted = !live ? string.Empty
                : _carrying ? MalbersCarryBlocked
                : _interactClaimed ? MalbersInteractOnly
                : string.Empty;

            // Cowsins: let go of whatever is no longer wanted muted...
            for (int i = _mutedCowsinsActions.Count - 1; i >= 0; i--)
            {
                var action = _mutedCowsinsActions[i];
                if (action != null && System.Array.IndexOf(cowsinsWanted, action.name) >= 0)
                    continue;

                // ...but only switch it back on while first person is live. In third person the whole
                // map is off, and it is switched back on in full when first person returns.
                if (action != null && !_thirdPerson && !_suspended)
                    action.Enable();
                _mutedCowsinsActions.RemoveAt(i);
            }

            var actions = InputManager.inputActions;
            var map = actions != null ? actions.asset.FindActionMap("GameControls") : null;
            if (map != null)
            {
                foreach (var name in cowsinsWanted)
                {
                    var action = map.FindAction(name);
                    if (action == null || !action.enabled)
                        continue;
                    action.Disable();
                    if (!_mutedCowsinsActions.Contains(action))
                        _mutedCowsinsActions.Add(action);
                }
            }

            // Malbers. Re-asserted even when unchanged: MInputLink is bounced on every switch into
            // third person, which can bring its buttons back.
            var link = steveRoot != null ? steveRoot.GetComponentInChildren<MInputLink>(true) : null;
            if (link == null)
                return;

            if (_mutedMalbersInputs != malbersWanted && !string.IsNullOrEmpty(_mutedMalbersInputs))
                link.EnableInput(_mutedMalbersInputs, true);
            if (!string.IsNullOrEmpty(malbersWanted))
                link.EnableInput(malbersWanted, false);
            _mutedMalbersInputs = malbersWanted;

            // Escape is the game's own pause menu. Malbers' pause froze time and freed the cursor
            // through its settings canvas, which is gone; its key is taken away as well.
            link.EnableInput("Pause", false);
        }

        /// <summary>
        /// Removes Steve's unarmed punching. The left mouse button is the throw now, and Malbers
        /// drove the punch from it through a combo (Combo Starter on Action1, playing the Attack1
        /// mode). Both are switched off at the source rather than masked, so no route back into a
        /// punch remains - with or without a body in hand.
        /// </summary>
        void RemoveMalbersPunch()
        {
            if (_punchRemoved || animal == null)
                return;
            _punchRemoved = true;

            animal.Mode_Disable("Attack1");

            if (steveRoot == null)
                return;
            foreach (var combo in steveRoot.GetComponentsInChildren<ComboManager>(true))
            {
                if (combo != null)
                    combo.enabled = false;
            }
        }

        #endregion

#if UNITY_EDITOR
        public void EditorWire(
            GameObject fps,
            Transform body,
            GameObject cameraRoot,
            GameObject steve,
            MAnimal manimal,
            GameObject cameras,
            GameObject cowsinsSrc = null,
            GameObject steveSrc = null,
            GameObject camerasSrc = null)
        {
            fpsRoot = fps;
            fpsBody = body;
            fpsCameraRoot = cameraRoot;
            steveRoot = steve;
            animal = manimal;
            camerasCm3 = cameras;
            if (cowsinsSrc != null) cowsinsPrefab = cowsinsSrc;
            if (steveSrc != null) stevePrefab = steveSrc;
            if (camerasSrc != null) camerasPrefab = camerasSrc;
        }
#endif
    }
}
