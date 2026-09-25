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
        [SerializeField] float fpsBodyLocalY = 1f;

        [Header("Mode switch")]
        [Tooltip("Seconds for the camera to pull back from the eyes to over the shoulder when " +
                 "entering third person. 0 restores the old instant cut.")]
        [SerializeField] float tppTransitionSeconds = 0.55f;

        // How much to raise the Malbers jump so the taller Meshy characters clear obstacles like Steve did.
        const float SteveJumpBoost = 1.3f;

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
            if (_thirdPerson)
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
            Debug.Log("[DualPlayer] Switched to Malbers third person.");
        }

        public void ExitToFirstPerson()
        {
            if (!_thirdPerson)
                return;

            var pos = steveRoot != null ? steveRoot.transform.position : transform.position;
            var yaw = steveRoot != null ? steveRoot.transform.eulerAngles.y : transform.eulerAngles.y;
            ApplyMode(thirdPerson: false, teleport: true, pos, yaw);
            _onModeChanged?.Invoke(false);
            Debug.Log("[DualPlayer] Switched to FPS.");
        }

        void ApplyMode(bool thirdPerson, bool teleport, Vector3 pos = default, float yaw = 0f)
        {
            _thirdPerson = thirdPerson;

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
            if (camerasCm3 == null || tppTransitionSeconds <= 0f)
                return;

            AdoptUnderPlayerMain(camerasCm3);
            camerasCm3.SetActive(true);

            var brain = camerasCm3.GetComponentInChildren<CinemachineBrain>(true);
            if (brain == null)
                return;

            if (!_blendSaved)
            {
                _savedBlend = brain.DefaultBlend;
                _blendSaved = true;
            }
            brain.DefaultBlend = new CinemachineBlendDefinition(
                CinemachineBlendDefinition.Styles.EaseInOut, tppTransitionSeconds);

            var eye = ResolveFpsCameraTransform();
            if (eye == null)
                return;

            if (_handoffCamera == null)
            {
                var go = new GameObject("CM FPS Handoff");
                go.transform.SetParent(camerasCm3.transform, false);
                _handoffCamera = go.AddComponent<CinemachineCamera>();
            }

            _handoffCamera.gameObject.SetActive(true);
            _handoffCamera.transform.SetPositionAndRotation(eye.position, eye.rotation);
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
            StartCoroutine(RetireHandoffCamera(tppTransitionSeconds + 0.1f));
        }

        System.Collections.IEnumerator RetireHandoffCamera(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (_handoffCamera != null)
                _handoffCamera.gameObject.SetActive(false);
        }

        void ResetCameraHandoff()
        {
            if (_handoffCamera != null)
                _handoffCamera.gameObject.SetActive(false);

            if (_blendSaved && camerasCm3 != null)
            {
                var brain = camerasCm3.GetComponentInChildren<CinemachineBrain>(true);
                if (brain != null)
                    brain.DefaultBlend = _savedBlend;
                _blendSaved = false;
            }
        }

        /// <summary>The live FPS eye, preferring the tagged main camera over the weapon overlay.</summary>
        Transform ResolveFpsCameraTransform()
        {
            var root = fpsCameraRoot != null ? fpsCameraRoot : fpsRoot;
            if (root == null)
                return null;

            Transform fallback = null;
            foreach (var camera in root.GetComponentsInChildren<Camera>(true))
            {
                if (camera == null)
                    continue;
                if (camera.CompareTag("MainCamera"))
                    return camera.transform;
                if (fallback == null)
                    fallback = camera.transform;
            }

            return fallback;
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

            _fpsRenderersHiddenForTpp.Clear();
            if (fpsRoot == null)
                return;

            foreach (var renderer in fpsRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled)
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
                jumpBasic.Jumps.Value = 2;
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

        void PlaceFps(Vector3 position, float yawDegrees)
        {
            CacheFps();
            if (fpsBody == null)
                return;

            var bodyPos = position + Vector3.up * fpsBodyLocalY;
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

        Vector3 GetFpsWorldPosition()
        {
            if (fpsBody != null)
                return fpsBody.position - Vector3.up * fpsBodyLocalY;
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
