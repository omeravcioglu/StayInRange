#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using AvocadoShark;
using cowsins;
using Fusion;
using StarterAssets;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Fusion adapter between Clean Multiplayer Pro (ownership, sync, lobby spawn)
    /// and FPS Engine (local first-person gameplay). Runs before other Awakes so
    /// remote clones never enable local-only Cowsins systems.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class FpsNetworkBridge : NetworkBehaviour, INetworkedDamageRouter, IDamageable
    {
        [Header("FPS Engine")]
        [SerializeField] GameObject fpsRoot;
        [SerializeField] Transform fpsBody;
        [SerializeField] GameObject fpsCameraRoot;
        [SerializeField] GameObject fpsInputRoot;
        [SerializeField] GameObject fpsUiRoot;
        [SerializeField] GameObject fpsManagersRoot;

        [Header("Remote representation")]
        [SerializeField] Transform remoteBody;
        [SerializeField] Transform interpolationTarget;
        [SerializeField] RemoteWeaponVisual remoteWeaponVisual;

        [Header("Canonical position")]
        [SerializeField] Transform distanceOrigin;

        [Header("CMP")]
        [SerializeField] StarterAssetsInputs starterAssetsInputs;

        [Header("Runtime spawn")]
        [SerializeField] GameObject cowsinsPrefab;

        [Networked] public float SyncedHealth { get; set; }
        [Networked] public float SyncedShield { get; set; }
        [Networked] public int WeaponIndex { get; set; }
        [Networked] public NetworkString<_32> WeaponName { get; set; }
        [Networked] public NetworkBool IsAiming { get; set; }
        [Networked] public NetworkBool IsFiring { get; set; }
        [Networked] public NetworkBool IsDead { get; set; }
        [Networked] public int FireSeq { get; set; }
        [Networked] public NetworkBool IsThirdPerson { get; set; }

        /// <summary>
        /// Who is holding this player's body in a telekinetic grip, or None.
        ///
        /// Lives on the DEAD player rather than on the carrier, which is what makes two survivors
        /// grabbing (or catching) the same body resolve cleanly: every request arrives at one
        /// authority - the dead player's own machine - and only the first finds the slot free.
        /// </summary>
        [Networked] public PlayerRef CarriedBy { get; set; }

        /// <summary>
        /// Whose machine is simulating this body right now, or None for the dead player's own.
        /// The carrier while it is held; the thrower while it flies; None once it lies still.
        /// </summary>
        [Networked] public PlayerRef BodyDriver { get; set; }

        /// <summary>The body's <see cref="BodyPhase"/>, as a byte.</summary>
        [Networked] public byte BodyPhaseValue { get; set; }

        /// <summary>
        /// Bumped on every change of who holds or simulates the body. Requests carry the epoch the
        /// sender saw, and anything stale is refused - two players catching the same throw both send
        /// the same epoch, and only the first one to arrive still matches.
        /// </summary>
        [Networked] public int BodyEpoch { get; set; }

        // The pose of the body THIS player's machine is simulating, if any - their own corpse while
        // it lies loose, or a teammate's while they carry or throw it. Written by this player only,
        // on their own object, which is how a carrier can publish a body they have no authority over.
        [Networked] public NetworkId DrivenBody { get; set; }
        [Networked] public Vector3 DrivenHipsPosition { get; set; }
        [Networked] public Quaternion DrivenHipsRotation { get; set; }
        [Networked] public Vector3 DrivenChestPosition { get; set; }
        [Networked] public Quaternion DrivenChestRotation { get; set; }
        [Networked] public Vector3 DrivenVelocity { get; set; }
        [Networked] public int ColorIndex { get; set; }

        // Which character skin this player chose in the lobby. Replicated so every machine dresses
        // this player's third-person body (and, for the owner, the Malbers TPS mesh) in the same skin.
        [Networked] public int CharacterIndex { get; set; }

        // Animation state is replicated as discrete parameters instead of syncing the whole Animator,
        // so proxies drive the same controller inputs the owner does.
        [Networked] public float MoveSpeed { get; set; }
        [Networked] public NetworkBool IsGrounded { get; set; }
        [Networked] public NetworkBool IsJumping { get; set; }
        [Networked] public NetworkBool IsFreeFall { get; set; }
        [Networked] public NetworkBool IsReloading { get; set; }
        [Networked] public NetworkBool IsFlashlightOn { get; set; }

        PlayerMovement _movement;
        PlayerControl _playerControl;
        cowsins.PlayerStats _cowsinsStats;
        WeaponController _weapon;
        Rigidbody _fpsRigidbody;
        Animator _remoteAnimator;
        float _bodyLocalY = 1f;

        /// <summary>
        /// Pivot-to-feet distance of the FPS body, read from its capsule. The prefab's 1 m body
        /// offset (_bodyLocalY) is NOT where the feet are - the capsule is raised off the pivot - and
        /// using it put every first-person player half a metre into the floor on other screens.
        /// </summary>
        float FeetOffset() => DualPlayerController.CapsuleFeetOffset(fpsBody, _bodyLocalY);
        bool _applyingLocalDamage;
        bool _fpsDetached;
        bool _wasFiring;
        int _lastSeenFireSeq;
        string _lastAppliedWeaponName = string.Empty;
        readonly List<UnityEngine.Behaviour> _localBehaviours = new List<UnityEngine.Behaviour>();
        readonly List<GameObject> _localObjects = new List<GameObject>();
        Renderer[] _remoteRenderers;
        bool _malbersThirdPerson;
        Transform _malbersDrive;
        DualPlayerController _dual;
        WeaponFlashlight _flashlight;
        NetworkTransform _networkTransform;
        ChangeDetector _identityChanges;
        int _appliedColorIndex = int.MinValue;
        int _appliedSteveColorIndex = int.MinValue;
        int _appliedCharacterIndex = int.MinValue;
        Transform _creepHold;
        Transform _creepHoldOldParent;
        bool _creepGrabLocked;

        /// <summary>A creep holds this player: control stays off until it lets go.</summary>
        public bool IsHeldByCreep => _creepGrabLocked;
        bool _animParamsProbed;
        bool _animHasAim;
        bool _animHasReload;
        bool _animHasShoot;

        static readonly int AnimIdSpeed = Animator.StringToHash("Speed");
        static readonly int AnimIdGrounded = Animator.StringToHash("Grounded");
        static readonly int AnimIdJump = Animator.StringToHash("Jump");
        static readonly int AnimIdFreeFall = Animator.StringToHash("FreeFall");
        static readonly int AnimIdMotionSpeed = Animator.StringToHash("MotionSpeed");
        static readonly int AnimIdAim = Animator.StringToHash("Aim");
        static readonly int AnimIdReload = Animator.StringToHash("Reload");
        static readonly int AnimIdShoot = Animator.StringToHash("Shoot");

        public bool IsLocalOwner => Object != null && HasStateAuthority;
        public bool IsMalbersThirdPerson => _malbersThirdPerson;
        public DualPlayerController DualPlayer => _dual;

        /// <summary>
        /// The player this object belongs to. State authority, not input authority: players are
        /// spawned in shared mode without an input authority, so that one reads None for everybody.
        /// </summary>
        public PlayerRef Owner => Object != null ? Object.StateAuthority : PlayerRef.None;

        /// <summary>Every spawned player, living or dead. Kept here so per-frame lookups never scan the scene.</summary>
        public static readonly List<FpsNetworkBridge> All = new List<FpsNetworkBridge>();

        public static FpsNetworkBridge FindByPlayer(PlayerRef player)
        {
            if (player == PlayerRef.None)
                return null;

            for (int i = 0; i < All.Count; i++)
            {
                var bridge = All[i];
                if (bridge != null && bridge.Object != null && bridge.Object.IsValid &&
                    bridge.Object.StateAuthority == player)
                    return bridge;
            }

            return null;
        }

        /// <summary>The name to show for this player, falling back to their slot when they have none.</summary>
        public string DisplayName
        {
            get
            {
                var stats = GetComponent<AvocadoShark.PlayerStats>();
                var name = stats != null ? stats.PlayerName.ToString().Trim('\0', ' ', '\r', '\n') : string.Empty;
                if (!string.IsNullOrEmpty(name))
                    return name;
                return Object != null ? "Player " + Object.StateAuthority.PlayerId : "Player";
            }
        }

        void Awake()
        {
            SetRemoteBodyVisible(true);
        }

        public override void Spawned()
        {
            if (!All.Contains(this))
                All.Add(this);

            // Only the owner needs the local Cowsins FPS stack (camera, input, UI, manager singletons).
            // Proxies used to instantiate it too and then disable it - but its Awake had already run,
            // so the proxy's UIController/PoolManager singletons clobbered the owner's, leaving the
            // joining client with no crosshair or health HUD. Proxies are drawn by the networked
            // "Player Render" body instead, so they never need this stack.
            if (HasStateAuthority)
                EnsureFpsInstance();
            CacheReferences();
            CollectLocalOnly();
            SetLocalOnlyEnabled(false);

            _networkTransform = GetComponent<NetworkTransform>();
            _identityChanges = GetChangeDetector(ChangeDetector.Source.SimulationState);

            if (HasStateAuthority)
            {
                ColorIndex = ReadMenuColorIndex();
                // One shared body for everyone: the skin follows the colour.
                CharacterIndex = ColorIndex;
                EnableLocalGameplay();
                HookShootEvents(true);
                EnsureDualPlayer();
                EnsureLocalFlashlight();
                EnsureTelekinesis();
                // Steve exists now (EnsureDualPlayer builds it), so dress both the body others see
                // and the owner's own third-person mesh in this player's colour.
                ApplyIdentity();
                if (_cowsinsStats != null)
                {
                    SyncedHealth = _cowsinsStats.Health;
                    SyncedShield = _cowsinsStats.Shield;
                }
                // #region agent log
                AgentDebugLog.Write("F", "FpsNetworkBridge.Spawned", "local_owner",
                    "{\"colorIndex\":" + ColorIndex +
                    ",\"hasDual\":" + (_dual != null ? "true" : "false") +
                    ",\"fpsRoot\":\"" + (fpsRoot != null ? fpsRoot.name : "null") +
                    "\",\"pos\":\"" + transform.position.ToString() + "\"}");
                AgentDebugLog.LogPlayerSetup(
                    "H1",
                    "FpsNetworkBridge.Spawned",
                    "network_menu_join",
                    fpsRoot,
                    _dual != null ? _dual.SteveRoot : null,
                    networkMode: true);
                // #endregion
            }
            else
            {
                ConfigureProxy();
                _lastSeenFireSeq = FireSeq;
                ApplyIdentity();
                // #region agent log
                AgentDebugLog.Write("F", "FpsNetworkBridge.Spawned", "remote_proxy",
                    "{\"colorIndex\":" + ColorIndex + "}");
                // #endregion
            }

            // Owner and proxy both need a Player-layer collider on this replicated root.
            // The real FPS capsule only exists on the owning machine, so without this the
            // host's creep / Emerald never see a joining client.
            EnsureAiVisible();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            All.Remove(this);
            HookShootEvents(false);
            CleanupDetachedFps();
        }

        void OnDestroy()
        {
            All.Remove(this);
            HookShootEvents(false);
            CleanupDetachedFps();
        }

        /// <summary>The local player's lift / throw / catch controller. Only the local player gets one.</summary>
        void EnsureTelekinesis()
        {
            var telekinesis = GetComponent<BodyTelekinesis>();
            if (telekinesis == null)
                telekinesis = gameObject.AddComponent<BodyTelekinesis>();
            telekinesis.Bind(this);
        }

        void EnsureFpsInstance()
        {
            if (fpsRoot != null)
                return;

            if (cowsinsPrefab == null)
            {
                Debug.LogError("[FpsNetworkBridge] Cowsins FPS prefab is not assigned.");
                return;
            }

            fpsRoot = Instantiate(cowsinsPrefab, transform);
            fpsRoot.name = "CowsinsFPSController";
            fpsRoot.transform.localPosition = Vector3.zero;
            fpsRoot.transform.localRotation = Quaternion.identity;
            fpsRoot.transform.localScale = Vector3.one;
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority)
                return;

            CaptureAnimationState();
            PublishDrivenBody();
        }

        /// <summary>
        /// Publishes the pose of whichever body this machine is simulating, on this player's own
        /// object. That is how a carrier or thrower - who has no authority over the dead player's
        /// object - still gets to tell everyone where the body is.
        /// </summary>
        void PublishDrivenBody()
        {
            var simulated = PlayerDownState.LocallySimulated;
            if (simulated == null || !simulated.IsSimulatedHere || simulated.Bridge == null ||
                simulated.Bridge.Object == null || !simulated.Bridge.Object.IsValid)
            {
                if (DrivenBody != default)
                    DrivenBody = default;
                return;
            }

            simulated.ReadPublishedPose(out var hips, out var hipsRotation, out var chest,
                out var chestRotation, out var velocity);

            DrivenBody = simulated.Bridge.Object.Id;
            DrivenHipsPosition = hips;
            DrivenHipsRotation = hipsRotation;
            DrivenChestPosition = chest;
            DrivenChestRotation = chestRotation;
            DrivenVelocity = velocity;
        }

        public override void Render()
        {
            if (HasStateAuthority)
                SyncAuthorityState();
            else
                ApplyProxyVisualState();

            // Before the animation is applied: a downed player's Animator is switched off by the
            // ragdoll, and driving parameters into it first would be wasted work on a corpse.
            ApplyDownedState();

            // Every machine, owner included, drives the mesh from the same replicated values.
            ApplyNetworkedAnimation();
            DetectIdentityChanges();
        }

        /// <summary>
        /// Latches the networked IsDead onto the local body, on every machine.
        ///
        /// Same idiom NetworkWorldActor uses for enemy corpses: watch the replicated flag, apply the
        /// local consequence once per change. It has to run on the owner too, because the owner is
        /// the one who loses their camera and gains a spectator view.
        /// </summary>
        void ApplyDownedState()
        {
            var down = EnsureDownState();
            if (down == null)
                return;

            bool dead = IsDead;
            if (dead == _appliedDown)
                return;

            _appliedDown = dead;

            if (dead)
            {
                // Taken before the controllers are parked, which zeroes it.
                var velocity = _dual != null ? _dual.GetBodyVelocity()
                    : _fpsRigidbody != null ? _fpsRigidbody.linearVelocity : Vector3.zero;

                if (HasStateAuthority)
                {
                    // The body starts life loose and simulated here, whatever was true of the last one.
                    ResetBodyAuthority();
                    EnterDownedLocal();
                }

                SetRemoteBodyVisible(true);
                down.EnterDown(HasStateAuthority, velocity);
            }
            else
            {
                down.ExitDown(HasStateAuthority);
                if (HasStateAuthority)
                    ExitDownedLocal();
                else
                    // A living player is drawn with this body on every other machine in BOTH modes:
                    // the Malbers character exists only on its owner's machine.
                    SetRemoteBodyVisible(true);
            }
        }

        void LateUpdate()
        {
            if (!Object)
                return;

            if (!HasStateAuthority)
            {
                // Every machine decides for itself whether it is the one simulating this body or one
                // following the machine that is.
                if (_appliedDown)
                    _downState?.Tick();
                return;
            }

            if (_appliedDown && _downState != null)
            {
                MaintainBodyAuthority();
                _downState.Tick();
                // The root - and the collar anchor on it - follows the body wherever it is carried,
                // thrown or dropped. The body is detached from the root while dead, so moving the
                // root no longer drags the corpse along with it.
                transform.position = _downState.BodyPosition;
                return;
            }

            if (_creepGrabLocked && _creepHold != null)
            {
                var holdPos = _creepHold.position;
                transform.SetPositionAndRotation(holdPos, _creepHold.rotation);
                _dual?.TeleportGameplay(holdPos, _creepHold.eulerAngles.y);
                return;
            }

            var gameplayPos = GetGameplayWorldPosition();
            var gameplayYaw = Quaternion.Euler(0f, GetGameplayYaw(), 0f);

            // The root is the canonical network position and sits at the player's feet, which is
            // also where the remote body's pivot is. GetGameplayWorldPosition already reports feet
            // level for every mode, so subtracting the body height again here buried the remote
            // mesh one metre below the ground.
            transform.SetPositionAndRotation(gameplayPos, gameplayYaw);

            if (_malbersThirdPerson || fpsBody == null)
                return;

            if (fpsBody.IsChildOf(transform))
            {
                fpsBody.localPosition = new Vector3(0f, FeetOffset(), 0f);
                fpsBody.localRotation = Quaternion.identity;
                if (_fpsRigidbody != null)
                    _fpsRigidbody.position = fpsBody.position;
            }
        }

        public void SetGameplayInputEnabled(bool enabled)
        {
            if (!HasStateAuthority || _playerControl == null)
                return;

            if (enabled)
                _playerControl.CheckIfCanGrantControl();
            else
                _playerControl.LoseControl();
        }

        /// <summary>
        /// Local-only: park FPS Engine and drive the network root from Malbers Steve.
        /// Remotes keep seeing the CMP third-person mesh following this transform.
        /// </summary>
        public void EnterMalbersThirdPerson(Transform malbersRoot)
        {
            if (!HasStateAuthority || malbersRoot == null)
                return;

            _malbersThirdPerson = true;
            _malbersDrive = malbersRoot;

            SetGameplayInputEnabled(false);
            SetLocalOnlyEnabled(false);
            SetRemoteBodyVisible(false);

            if (_fpsRigidbody != null)
            {
                _fpsRigidbody.isKinematic = true;
                _fpsRigidbody.useGravity = false;
                _fpsRigidbody.linearVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Local-only: leave Malbers Steve and restore FPS Engine at the given pose.
        /// </summary>
        public void ExitMalbersThirdPerson(Vector3 worldPosition, float yawDegrees)
        {
            if (!HasStateAuthority)
                return;

            _malbersThirdPerson = false;
            _malbersDrive = null;

            transform.SetPositionAndRotation(
                worldPosition,
                Quaternion.Euler(0f, yawDegrees, 0f));

            if (fpsBody != null)
            {
                fpsBody.SetPositionAndRotation(
                    worldPosition + Vector3.up * FeetOffset(),
                    Quaternion.Euler(0f, yawDegrees, 0f));
                if (_fpsRigidbody != null)
                    _fpsRigidbody.position = fpsBody.position;
            }

            EnableLocalGameplay();
        }

        /// <summary>
        /// Feet-level gameplay position in every mode. The branches used to disagree - the dual
        /// path already subtracted the body height while the bare FPS path returned capsule centre -
        /// so owners and proxies measured different points on the same player.
        /// </summary>
        public Vector3 GetGameplayWorldPosition()
        {
            if (_dual != null)
                return _dual.GetGameplayWorldPosition();
            if (_malbersThirdPerson && _malbersDrive != null)
                return _malbersDrive.position;
            if (fpsBody != null)
                return fpsBody.position - Vector3.up * FeetOffset();
            return transform.position;
        }

        public const string DistanceOriginName = "PlayerDistanceOrigin";

        /// <summary>
        /// The one position every machine must agree on for this player. It hangs off the replicated
        /// root at local zero, so the owner and every proxy read the same value. Never resolve this
        /// from cameras, weapons, visual meshes or the detached local FPS hierarchy: those exist on
        /// only one machine and made the two clients disagree about the same pair of players.
        /// </summary>
        public Vector3 GetNetworkAnchorPosition()
        {
            if (distanceOrigin != null)
                return distanceOrigin.position;
            return transform.position;
        }

        public Transform NetworkAnchor => distanceOrigin != null ? distanceOrigin : transform;

        public const string AiTargetName = "PlayerAiTarget";

        /// <summary>
        /// Puts a Player-tagged, Player-layer capsule on the replicated root so host-side AI
        /// (creep overlap and Emerald faction checks) can see every connected player.
        /// </summary>
        public void EnsureAiVisible()
        {
            var target = transform.Find(AiTargetName);
            if (target == null)
            {
                var go = new GameObject(AiTargetName);
                target = go.transform;
                target.SetParent(transform, false);
            }

            target.localPosition = new Vector3(0f, 1f, 0f);
            target.localRotation = Quaternion.identity;
            target.localScale = Vector3.one;

            int playerLayer = LayerMask.NameToLayer("Player");
            if (playerLayer >= 0)
                target.gameObject.layer = playerLayer;
            target.gameObject.tag = "Player";

            var capsule = target.GetComponent<CapsuleCollider>();
            if (capsule == null)
                capsule = target.gameObject.AddComponent<CapsuleCollider>();
            capsule.center = Vector3.zero;
            capsule.radius = 0.4f;
            capsule.height = 2f;
            capsule.direction = 1;
            capsule.isTrigger = true;

            // Trigger-vs-trigger (creep activate radius) only fires if one side has a rigidbody.
            var rb = target.GetComponent<Rigidbody>();
            if (rb == null)
                rb = target.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.detectCollisions = true;

            var faction = target.GetComponent<EmeraldAI.FactionExtension>();
            if (faction == null)
                faction = target.gameObject.AddComponent<EmeraldAI.FactionExtension>();
            faction.CurrentFaction = 2;

            var tpm = target.GetComponent<EmeraldAI.Utility.TargetPositionModifier>();
            if (tpm == null)
                tpm = target.gameObject.AddComponent<EmeraldAI.Utility.TargetPositionModifier>();
            tpm.TransformSource = target;
            tpm.PositionModifier = 0.6f;

            var leftover = target.GetComponent<EmeraldAI.EmeraldGeneralTargetBridge>();
            if (leftover != null)
                Destroy(leftover);

            var aiTarget = target.GetComponent<NetworkedPlayerAiTarget>();
            if (aiTarget == null)
                aiTarget = target.gameObject.AddComponent<NetworkedPlayerAiTarget>();
            aiTarget.Initialize(this);
        }

        void EnsureDistanceOrigin()
        {
            if (distanceOrigin != null)
                return;

            var existing = transform.Find(DistanceOriginName);
            if (existing == null)
            {
                // Self-heal prefab instances authored before the anchor existed.
                existing = new GameObject(DistanceOriginName).transform;
                existing.SetParent(transform, false);
            }

            existing.localPosition = Vector3.zero;
            existing.localRotation = Quaternion.identity;
            existing.localScale = Vector3.one;
            distanceOrigin = existing;
        }

        public float GetGameplayYaw()
        {
            if (_dual != null)
                return _dual.GetGameplayYaw();
            if (_malbersThirdPerson && _malbersDrive != null)
                return _malbersDrive.eulerAngles.y;
            if (_movement != null)
                return _movement.Orientation.Rotation.eulerAngles.y;
            if (fpsBody != null)
                return fpsBody.eulerAngles.y;
            return transform.eulerAngles.y;
        }

        public void AuthorityTeleport(Vector3 position, float yawDegrees)
        {
            if (!HasStateAuthority)
                return;

            var rot = Quaternion.Euler(0f, yawDegrees, 0f);
            if (_networkTransform != null)
                _networkTransform.Teleport(position);
            transform.SetPositionAndRotation(position, rot);

            if (_dual != null)
            {
                _dual.TeleportGameplay(position, yawDegrees);
                return;
            }

            if (_malbersThirdPerson && _malbersDrive != null)
            {
                _malbersDrive.SetPositionAndRotation(position, rot);
                return;
            }

            if (fpsBody == null)
                return;

            var bodyPos = position + Vector3.up * FeetOffset();
            fpsBody.SetPositionAndRotation(bodyPos, rot);
            if (_fpsRigidbody != null)
            {
                _fpsRigidbody.linearVelocity = Vector3.zero;
                _fpsRigidbody.angularVelocity = Vector3.zero;
                _fpsRigidbody.position = bodyPos;
            }

            if (_movement != null)
                _movement.TeleportPlayer(bodyPos, rot, true, true);
        }

        /// <summary>
        /// Kill local player for a team-distance failure, then respawn at the given spawn.
        /// </summary>
        public void AuthorityTeamFailAndRespawn(Vector3 position, float yawDegrees)
        {
            if (!HasStateAuthority)
                return;

            // A team failure does not resurrect anybody. A dead player's body is dragged back to the
            // checkpoint along with the living, so the collar is satisfied - otherwise separation
            // would be a free revive. Whoever was holding it lets go, and the body comes back under
            // this machine's simulation so the teleport is the one everybody follows.
            if (IsDead)
            {
                ResetBodyAuthority();
                AuthorityTeleport(position, yawDegrees);
                _downState?.TeleportBody(position + Vector3.up * 0.4f);
                return;
            }

            if (_teamFailRoutine != null)
                StopCoroutine(_teamFailRoutine);
            _teamFailRoutine = StartCoroutine(TeamFailAndRespawnRoutine(position, yawDegrees));
        }

        Coroutine _teamFailRoutine;

        System.Collections.IEnumerator TeamFailAndRespawnRoutine(Vector3 position, float yawDegrees)
        {
            // Stop the fall immediately at the spawn — do not wait while dead mid-air.
            if (_fpsRigidbody != null)
            {
                _fpsRigidbody.linearVelocity = Vector3.zero;
                _fpsRigidbody.angularVelocity = Vector3.zero;
            }

            AuthorityTeleport(position, yawDegrees);

            // #region agent log
            AgentDebugLog.Write("E", "FpsNetworkBridge.TeamFailAndRespawnRoutine", "after_first_teleport",
                "{\"requested\":\"" + position.ToString() +
                "\",\"root\":\"" + transform.position.ToString() +
                "\",\"gameplay\":\"" + GetGameplayWorldPosition().ToString() +
                "\",\"hasDual\":" + (_dual != null ? "true" : "false") + "}");
            // #endregion

            if (_cowsinsStats != null && !_cowsinsStats.IsDead)
                ApplyDamageLocal(99999f, false);

            yield return new WaitForSecondsRealtime(0.35f);

            if (_cowsinsStats != null)
                _cowsinsStats.Respawn(position + Vector3.up * FeetOffset());

            AuthorityTeleport(position, yawDegrees);

            // #region agent log
            AgentDebugLog.Write("E", "FpsNetworkBridge.TeamFailAndRespawnRoutine", "after_respawn_teleport",
                "{\"requested\":\"" + position.ToString() +
                "\",\"root\":\"" + transform.position.ToString() +
                "\",\"gameplay\":\"" + GetGameplayWorldPosition().ToString() + "\"}");
            // #endregion

            if (_cowsinsStats != null)
            {
                SyncedHealth = _cowsinsStats.Health;
                SyncedShield = _cowsinsStats.Shield;
                IsDead = _cowsinsStats.IsDead;
            }
            else
            {
                IsDead = false;
            }

            _teamFailRoutine = null;
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RPC_AssignColor(int index)
        {
            ColorIndex = Mathf.Clamp(index, 0, PlayerColorPalette.Count - 1);
            CharacterIndex = ColorIndex;
            ApplyIdentity();
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RPC_SetCreepGrab(NetworkId actorId, NetworkBool grabbed)
        {
            if (!grabbed)
            {
                ReleaseCreepGrab();
                // This RPC runs on the grabbed player's own client, so this is where the overlay
                // belongs - not on the master where the creep brain lives.
                CreepGrabbedPanel.Hide();
                return;
            }

            var actorObj = Runner.FindObject(actorId);
            var actor = actorObj != null ? actorObj.GetComponent<NetworkWorldActor>() : null;
            var hold = actor != null ? actor.GetLocalHoldPoint() : null;
            if (hold == null)
                return;

            _creepHold = hold;
            _creepGrabLocked = true;
            // Grabbed overlay shows on the victim's screen (this machine), the one actually caught.
            CreepGrabbedPanel.Show();
            // Audio for the same moment, and only here: the sting belongs to the player who was
            // caught, not to the master where the creep's brain happens to live.
            GameSfx.Play2D(SfxId.CreepGrabbedSting);
            _creepDragLoop = GameSfx.PlayLoop(SfxId.CreepDragLoop, hold);
            if (_dual != null && _dual.IsThirdPerson && _dual.SteveRoot != null)
            {
                _creepHoldOldParent = _dual.SteveRoot.transform.parent;
                _dual.SteveRoot.transform.SetParent(hold, true);
                _dual.SteveRoot.transform.localPosition = Vector3.zero;
            }
            else if (fpsBody != null)
            {
                _creepHoldOldParent = fpsBody.parent;
                fpsBody.SetParent(hold, true);
                fpsBody.localPosition = Vector3.zero;
                SetGameplayInputEnabled(false);
                if (_fpsRigidbody != null)
                {
                    _fpsRigidbody.isKinematic = true;
                    _fpsRigidbody.linearVelocity = Vector3.zero;
                }
            }
        }

        SfxLoop _creepDragLoop;

        void ReleaseCreepGrab()
        {
            if (!_creepGrabLocked)
                return;

            _creepGrabLocked = false;
            _creepDragLoop.Stop();
            if (_dual != null && _dual.IsThirdPerson && _dual.SteveRoot != null)
                _dual.SteveRoot.transform.SetParent(_creepHoldOldParent, true);
            else if (fpsBody != null)
            {
                fpsBody.SetParent(_creepHoldOldParent, true);
                if (_fpsRigidbody != null)
                    _fpsRigidbody.isKinematic = false;
                SetGameplayInputEnabled(true);
            }

            _creepHold = null;
            _creepHoldOldParent = null;
        }

        void EnsureDualPlayer()
        {
            if (!HasStateAuthority)
                return;

            _dual = GetComponent<DualPlayerController>();
            if (_dual == null)
                _dual = gameObject.AddComponent<DualPlayerController>();

            var source = Resources.Load<DualPlayerController>("LocalDualPlayer");
            if (source != null)
                _dual.CopySourcePrefabsFrom(source);

            // #region agent log
            {
                string cowsinsSrc = "null";
                string steveSrc = "null";
                string camerasSrc = "null";
                if (source != null)
                {
                    var soType = typeof(DualPlayerController);
                    var cowsinsField = soType.GetField("cowsinsPrefab",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var steveField = soType.GetField("stevePrefab",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var camerasField = soType.GetField("camerasPrefab",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var c = cowsinsField?.GetValue(source) as GameObject;
                    var s = steveField?.GetValue(source) as GameObject;
                    var cam = camerasField?.GetValue(source) as GameObject;
                    cowsinsSrc = c != null ? c.name : "null";
                    steveSrc = s != null ? s.name : "null";
                    camerasSrc = cam != null ? cam.name : "null";
                }

                AgentDebugLog.Write("H1", "FpsNetworkBridge.EnsureDualPlayer", "source_prefabs",
                    "{\"cowsinsSrc\":\"" + cowsinsSrc +
                    "\",\"steveSrc\":\"" + steveSrc +
                    "\",\"camerasSrc\":\"" + camerasSrc +
                    "\",\"nestedFps\":\"" + (fpsRoot != null ? fpsRoot.name : "null") + "\"}");
            }
            // #endregion

            _dual.BindNetworkOwner(this, OnDualModeChanged);
            _dual.WireExisting(fpsRoot, null, null);

            // The redesigned HUD: one for both controllers, replacing Cowsins' and the old
            // distance list.
            if (GetComponent<UI.HudRoot>() == null)
                gameObject.AddComponent<UI.HudRoot>();
        }

        void OnDualModeChanged(bool thirdPerson)
        {
            IsThirdPerson = thirdPerson;
            if (thirdPerson && _dual != null && _dual.SteveRoot != null)
            {
                // Steve only wakes up on the first switch to third person; make sure its mesh is
                // in this player's colour the moment the owner can see it.
                _appliedCharacterIndex = int.MinValue;
                ApplyIdentity();
                EnterMalbersThirdPerson(_dual.SteveRoot.transform);
            }
            else
            {
                ExitMalbersThirdPerson(GetGameplayWorldPosition(), GetGameplayYaw());
            }
        }

        static int ReadMenuColorIndex()
        {
            // The colour picked in the lobby; the master still separates any two that match
            // (TeamDistanceManager.EnsureUniqueColors).
            int picked = PlayerColorPalette.SavedChoice;
            if (picked >= 0)
                return picked;

            if (FusionConnection.Instance != null &&
                FusionConnection.Instance.characterScriptableObject != null)
            {
                return Mathf.Clamp(
                    FusionConnection.Instance.characterScriptableObject.GetSelectedCharacterIndex,
                    0,
                    PlayerColorPalette.Count - 1);
            }

            return 0;
        }

        /// <summary>
        /// Everyone wears the one shared body in their own colour. The skin library holds that body
        /// once per player colour (Tools/CollarCali/Build Shared Body), so the skin is simply the
        /// colour index. With an older library of fewer skins, the first is worn and painted with
        /// the colour instead.
        /// </summary>
        void ApplyIdentity()
        {
            var lib = CharacterSkinLibrary.Load();
            bool perColour = lib != null && lib.Count >= PlayerColorPalette.Count;
            ApplyCharacterSkin(perColour ? ColorIndex : 0);
            if (!perColour)
                ApplyColorTint(ColorIndex);
        }

        /// <summary>
        /// Dresses this player in a skin on whatever visuals exist on this machine: always
        /// the third-person "Player Render" body every other player sees, plus the owner's Malbers
        /// Steve mesh so first- and third-person stay the same character. Idempotent - skipped when
        /// the skin has not changed - so it is safe to call every time the networked index replicates.
        /// </summary>
        void ApplyCharacterSkin(int index)
        {
            if (index == _appliedCharacterIndex)
                return;
            _appliedCharacterIndex = index;

            if (remoteBody != null)
                CharacterSelection.Apply(remoteBody.gameObject, index);

            if (_dual != null && _dual.SteveRoot != null)
                CharacterSelection.Apply(_dual.SteveRoot, index);

            // A skin swaps the materials the colour was painted on; paint the new ones.
            _appliedColorIndex = int.MinValue;
            _appliedSteveColorIndex = int.MinValue;
        }

        /// <summary>
        /// Keeps every machine's copy of this player in their current colour - the master can move
        /// a colour after spawn (TeamDistanceManager.EnsureUniqueColors). Compared against what is
        /// applied, so it returns at once when nothing moved.
        /// </summary>
        void DetectIdentityChanges()
        {
            if (_identityChanges == null)
                return;

            ApplyIdentity();
        }

        /// <summary>
        /// Everyone shares one body; the colour is who they are. Paints the body the others see and,
        /// on the owner's machine, their own third-person mesh.
        /// </summary>
        void ApplyColorTint(int index)
        {
            if (index != _appliedColorIndex && remoteBody != null)
            {
                _appliedColorIndex = index;
                if (_remoteRenderers == null)
                    _remoteRenderers = remoteBody.GetComponentsInChildren<Renderer>(true);
                Paint(_remoteRenderers, index);
            }

            var steve = _dual != null ? _dual.SteveRoot : null;
            if (steve != null && index != _appliedSteveColorIndex)
            {
                _appliedSteveColorIndex = index;
                Paint(steve.GetComponentsInChildren<SkinnedMeshRenderer>(true), index);
            }
        }

        static void Paint(Renderer[] renderers, int index)
        {
            var color = PlayerColorPalette.Get(index);
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;
                var mats = renderer.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null)
                        continue;
                    if (mats[i].HasProperty("_BaseColor"))
                        mats[i].SetColor("_BaseColor", color);
                    if (mats[i].HasProperty("_Color"))
                        mats[i].SetColor("_Color", color);
                }
            }
        }

        #region Downed, carried and revived

        PlayerDownState _downState;
        bool _appliedDown;

        public bool IsCarried => CarriedBy != PlayerRef.None;
        public BodyPhase Phase => (BodyPhase)BodyPhaseValue;

        /// <summary>The machine simulating this body: the driver when there is one, otherwise the owner.</summary>
        public PlayerRef BodySimulator => BodyDriver != PlayerRef.None ? BodyDriver : Owner;

        /// <summary>This player's ragdoll and downed state. Exists on every machine once the player has been rendered.</summary>
        public PlayerDownState DownState => _downState;

        /// <summary>
        /// Adds the ragdoll and downed-state components on first use rather than requiring them on
        /// the prefab, so an existing player prefab keeps working without being rebuilt.
        /// </summary>
        PlayerDownState EnsureDownState()
        {
            if (_downState != null)
                return _downState;

            if (remoteBody == null)
                return null;

            _downState = GetComponent<PlayerDownState>();
            if (_downState == null)
            {
                if (GetComponent<PlayerRagdoll>() == null)
                    gameObject.AddComponent<PlayerRagdoll>();
                _downState = gameObject.AddComponent<PlayerDownState>();
            }

            _downState.Bind(this, remoteBody);
            return _downState;
        }

        /// <summary>
        /// Hands the local player over to the spectator camera: the living controllers are parked -
        /// whichever of first or third person was active - and their input, camera and HUD go with
        /// them. Nothing of the living character is left to collide with the corpse.
        /// </summary>
        void EnterDownedLocal()
        {
            if (_dual != null)
                _dual.EnterDowned();

            // Takes the camera, the input and the HUD in one move - the same switch that is used to
            // decide what belongs to the local player in the first place.
            SetLocalOnlyEnabled(false);
        }

        /// <summary>Gives the local player their body back after a revive, in first person.</summary>
        void ExitDownedLocal()
        {
            SetLocalOnlyEnabled(true);
            // Hidden again because they are back behind their own eyes; the third-person mesh is for
            // everybody else to look at.
            SetRemoteBodyVisible(false);

            if (_dual != null)
                _dual.ExitDowned();

            if (_fpsRigidbody != null)
            {
                _fpsRigidbody.isKinematic = false;
                _fpsRigidbody.useGravity = true;
            }

            if (UIController.Instance != null)
                UIController.Instance.LockMouse();
        }

        /// <summary>
        /// Puts the body back under this machine's own simulation with nobody holding it. Owner only.
        /// </summary>
        void ResetBodyAuthority()
        {
            if (!HasStateAuthority)
                return;

            CarriedBy = PlayerRef.None;
            BodyDriver = PlayerRef.None;
            BodyPhaseValue = (byte)BodyPhase.Loose;
            BodyEpoch++;
        }

        /// <summary>
        /// The owner's standing duty while dead: if whoever is simulating the body has gone - left
        /// the session, or died themselves - take it back, so a body can never be stranded with a
        /// simulator that no longer exists.
        /// </summary>
        void MaintainBodyAuthority()
        {
            if (!HasStateAuthority || BodyDriver == PlayerRef.None)
                return;

            var driver = FindByPlayer(BodyDriver);
            if (driver == null || driver.IsDead)
                ResetBodyAuthority();
        }

        /// <summary>Asks this body's owner to put it in the requesting player's grip.</summary>
        public void RequestHold(int epoch, bool catching)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_RequestHold(epoch, catching);
        }

        /// <summary>Tells this body's owner that its holder let go - dropped, or threw it.</summary>
        public void RequestRelease(int epoch, bool thrown)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_RequestRelease(epoch, thrown);
        }

        /// <summary>Tells this body's owner that its flight is over and it can take the simulation back.</summary>
        public void RequestBodyRest(int epoch)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_RequestBodyRest(epoch);
        }

        /// <summary>Asks this body's owner to be revived. Kept for the revival mechanic.</summary>
        public void RequestRevive(Vector3 position, float yawDegrees)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_RequestRevive(position, yawDegrees);
        }

        /// <summary>
        /// Lifting and catching are the same request: put the body in my grip. Granted only while
        /// nobody holds it, only if the sender saw the current epoch, and only to a living player -
        /// so of two players catching the same throw, exactly one succeeds.
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        void RPC_RequestHold(int epoch, NetworkBool catching, RpcInfo info = default)
        {
            if (!IsDead || CarriedBy != PlayerRef.None || epoch != BodyEpoch)
                return;

            var requester = FindByPlayer(info.Source);
            if (requester == null || requester == this || requester.IsDead)
                return;

            // A catch is only valid while the body is actually in flight; a lift works either way.
            if (catching && Phase != BodyPhase.Thrown)
                return;

            CarriedBy = info.Source;
            BodyDriver = info.Source;
            BodyPhaseValue = (byte)BodyPhase.Held;
            BodyEpoch++;
        }

        /// <summary>
        /// The holder let go. The body goes into flight - a drop is just a throw with no speed - and
        /// stays simulated by the same machine until it lands or somebody catches it.
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        void RPC_RequestRelease(int epoch, NetworkBool thrown, RpcInfo info = default)
        {
            if (!IsDead || CarriedBy != info.Source || epoch != BodyEpoch)
                return;

            CarriedBy = PlayerRef.None;
            BodyPhaseValue = (byte)BodyPhase.Thrown;
            BodyEpoch++;
        }

        /// <summary>A flight has ended. The owner's machine takes the simulation back from the thrower.</summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        void RPC_RequestBodyRest(int epoch, RpcInfo info = default)
        {
            if (!IsDead || BodyDriver != info.Source || CarriedBy != PlayerRef.None || epoch != BodyEpoch)
                return;

            BodyDriver = PlayerRef.None;
            BodyPhaseValue = (byte)BodyPhase.Loose;
            BodyEpoch++;
        }

        /// <summary>
        /// The IsDead check is the duplicate-revive guard: two survivors reviving the same body on
        /// the same frame both arrive here, and the second finds the player already alive.
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        void RPC_RequestRevive(Vector3 position, float yawDegrees)
        {
            if (!IsDead)
                return;

            ReviveAt(position, yawDegrees);
        }

        /// <summary>
        /// Brings this player back at a position, whatever put them down.
        ///
        /// THE REVIVAL HOOK. The dead player keeps their network identity - this object, its state
        /// authority, their name and colour - for the whole time they are dead, and the ragdoll is
        /// their own "Player Render" body rather than a spawned copy, so reviving is purely a change
        /// of state on this object: whoever held the body lets go, the body is reassembled where it
        /// is told to stand, and control comes back. Shared by the team wipe and by any future
        /// revive mechanic so there is one definition of coming back.
        ///
        /// Only meaningful on the state authority, which is the machine actually playing this
        /// character; everyone else finds out through IsDead.
        /// </summary>
        public void ReviveAt(Vector3 position, float yawDegrees)
        {
            if (!HasStateAuthority)
                return;

            ResetBodyAuthority();

            // Teleported before and after the respawn, the same as the team-failure routine: Cowsins
            // moves the body itself during Respawn, so the second call is what makes the position
            // stick.
            AuthorityTeleport(position, yawDegrees);

            if (_cowsinsStats != null)
                _cowsinsStats.Respawn(position + Vector3.up * FeetOffset());

            AuthorityTeleport(position, yawDegrees);

            if (_cowsinsStats != null)
            {
                SyncedHealth = _cowsinsStats.Health;
                SyncedShield = _cowsinsStats.Shield;
            }

            // Last, because it is what every machine watches to put the body back together.
            IsDead = false;
        }

        #endregion

        public void NotifyShot()
        {
            if (!HasStateAuthority)
                return;
            FireSeq++;
            IsFiring = true;
        }

        public void NotifyExplosion(Vector3 position, GameObject explosionVfx)
        {
            if (!Object || !Object.IsValid)
                return;
            var key = explosionVfx != null ? explosionVfx.name : string.Empty;
            RPC_ExplosionFx(position, key);
        }

        public void NotifyBarrelExplode(Vector3 position)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_BarrelExplode(position);
        }

        public bool TryRouteDamage(float damage, bool isHeadshot)
        {
            if (_applyingLocalDamage)
                return false;

            if (!Object)
                return false;

            if (HasStateAuthority)
            {
                ApplyDamageLocal(damage, isHeadshot);
                return true;
            }

            RPC_RequestDamage(damage, isHeadshot);
            return true;
        }

        public void Damage(float damage, bool isHeadshot)
        {
            TryRouteDamage(Mathf.Abs(damage), isHeadshot);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        void RPC_RequestDamage(float damage, bool isHeadshot)
        {
            ApplyDamageLocal(damage, isHeadshot);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
        void RPC_ExplosionFx(Vector3 position, NetworkString<_32> vfxName)
        {
            PlayFallbackExplosion(position);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
        void RPC_BarrelExplode(Vector3 position)
        {
            PlayFallbackExplosion(position);
            foreach (var barrel in FindObjectsByType<ExplosiveBarrel>(FindObjectsSortMode.None))
            {
                if (barrel != null && Vector3.Distance(barrel.transform.position, position) <= 0.75f)
                    Destroy(barrel.gameObject);
            }
        }

        static void PlayFallbackExplosion(Vector3 position)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "NetExplosionFlash";
            sphere.transform.position = position;
            sphere.transform.localScale = Vector3.one * 2.2f;
            var col = sphere.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
            var rend = sphere.GetComponent<Renderer>();
            if (rend != null)
                rend.material.color = new Color(1f, 0.55f, 0.1f, 0.85f);
            Destroy(sphere, 0.4f);
        }

        void ApplyDamageLocal(float damage, bool isHeadshot)
        {
            if (_cowsinsStats == null)
                return;

            _applyingLocalDamage = true;
            try
            {
                _cowsinsStats.Damage(damage, isHeadshot);
                SyncedHealth = _cowsinsStats.Health;
                SyncedShield = _cowsinsStats.Shield;
                IsDead = _cowsinsStats.IsDead;
            }
            finally
            {
                _applyingLocalDamage = false;
            }
        }

        void HookShootEvents(bool subscribe)
        {
            if (_weapon == null)
                return;

            if (subscribe)
            {
                _weapon.settings.userEvents.OnShoot.AddListener(OnLocalShoot);
            }
            else
            {
                _weapon.settings.userEvents.OnShoot.RemoveListener(OnLocalShoot);
            }
        }

        void OnLocalShoot()
        {
            NotifyShot();
        }

        void EnableLocalGameplay()
        {
            SetLocalOnlyEnabled(true);
            SetRemoteBodyVisible(false);
            DisableSceneThirdPersonCamera();
            DisableCowsinsPauseMenu();
            DetachLocalFpsFromNetworkRoot();

            if (_fpsRigidbody != null)
            {
                _fpsRigidbody.isKinematic = false;
                _fpsRigidbody.useGravity = true;
            }

            if (starterAssetsInputs != null)
                starterAssetsInputs.cursorLocked = true;

            if (UIController.Instance != null)
                UIController.Instance.LockMouse();
        }

        void ConfigureProxy()
        {
            SetLocalOnlyEnabled(false);
            SetRemoteBodyVisible(true);

            if (_fpsRigidbody != null)
            {
                _fpsRigidbody.isKinematic = true;
                _fpsRigidbody.useGravity = false;
                _fpsRigidbody.linearVelocity = Vector3.zero;
            }

            // Deliberately NOT reparenting remoteBody under interpolationTarget. The humanoid Animator
            // lives on this root and resolves its bones by path, so moving the skeleton one level
            // deeper silently broke every proxy animation. The root NetworkTransform has no
            // interpolation target configured, so Fusion interpolates this transform directly and the
            // reparent bought nothing.

            EnsureRemoteWeaponVisual();
            remoteWeaponVisual?.SetWeapon(WeaponName.ToString());
            remoteWeaponVisual?.SetFlashlight(IsFlashlightOn);
            _lastAppliedWeaponName = WeaponName.ToString();
        }

        void SyncAuthorityState()
        {
            if (_cowsinsStats != null)
            {
                SyncedHealth = _cowsinsStats.Health;
                SyncedShield = _cowsinsStats.Shield;
                IsDead = _cowsinsStats.IsDead;
            }

            if (_weapon != null)
            {
                WeaponIndex = _weapon.CurrentWeaponIndex;
                WeaponName = WeaponCatalog.GetName(_weapon.Weapon);
                IsAiming = _weapon.IsAiming;
                IsFiring = _weapon.IsShooting;
            }

            EnsureLocalFlashlight();
            if (_flashlight != null)
                IsFlashlightOn = _flashlight.IsOn;

            EnsureNetworkedPlayerName();
        }

        void EnsureNetworkedPlayerName()
        {
            var stats = GetComponent<AvocadoShark.PlayerStats>();
            if (stats == null)
                return;

            var current = stats.PlayerName.ToString().Trim('\0', ' ', '\r', '\n');
            if (!string.IsNullOrEmpty(current))
                return;

            var name = FusionConnection.LastPlayerName;
            if (string.IsNullOrWhiteSpace(name) && FusionConnection.Instance != null)
                name = FusionConnection.Instance._playerName;
            if (!string.IsNullOrWhiteSpace(name))
                stats.PlayerName = name;
        }

        /// <summary>
        /// Authority-side: derive the animation inputs from whichever controller is actually driving
        /// this player and publish them as networked values.
        /// </summary>
        void CaptureAnimationState()
        {
            Vector3 velocity;
            float maxSpeed;
            bool grounded;

            if (_malbersThirdPerson && _malbersDrive != null)
            {
                var rb = _malbersDrive.GetComponentInChildren<Rigidbody>();
                velocity = rb != null ? rb.linearVelocity : Vector3.zero;
                maxSpeed = 6f;
                float malbersHorizontal = new Vector3(velocity.x, 0f, velocity.z).magnitude;
                grounded = malbersHorizontal < 0.05f || Mathf.Abs(velocity.y) < 0.35f;
            }
            else if (_movement != null)
            {
                velocity = _fpsRigidbody != null ? _fpsRigidbody.linearVelocity : Vector3.zero;
                maxSpeed = _movement.RunSpeed;
                grounded = _movement.Grounded;
            }
            else
            {
                return;
            }

            float horizontal = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            bool jumping = !grounded && velocity.y > 0.5f;

            MoveSpeed = Mathf.Clamp(horizontal, 0f, Mathf.Max(0.01f, maxSpeed));
            IsGrounded = grounded;
            IsJumping = jumping;
            IsFreeFall = !grounded && !jumping;
            IsReloading = _weapon != null && _weapon.IsReloading;
        }

        /// <summary>
        /// Runs on every machine from the replicated parameters, so each client sees the same
        /// animation state for a given player.
        /// </summary>
        void ApplyNetworkedAnimation()
        {
            if (_remoteAnimator == null)
                return;

            ProbeAnimatorParameters();

            _remoteAnimator.SetFloat(AnimIdSpeed, MoveSpeed);
            _remoteAnimator.SetFloat(AnimIdMotionSpeed, MoveSpeed > 0.1f ? 1f : 0f);
            _remoteAnimator.SetBool(AnimIdGrounded, IsGrounded);
            _remoteAnimator.SetBool(AnimIdJump, IsJumping);
            _remoteAnimator.SetBool(AnimIdFreeFall, IsFreeFall);

            if (_animHasAim)
                _remoteAnimator.SetBool(AnimIdAim, IsAiming);
            if (_animHasReload)
                _remoteAnimator.SetBool(AnimIdReload, IsReloading);
        }

        /// <summary>
        /// StarterAssetsThirdPerson only declares the five locomotion parameters. Probing once keeps
        /// the weapon-state writes free until those parameters are added to the controller, without
        /// allocating an Animator.parameters array every frame.
        /// </summary>
        void ProbeAnimatorParameters()
        {
            if (_animParamsProbed)
                return;
            _animParamsProbed = true;

            foreach (var p in _remoteAnimator.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Bool && p.nameHash == AnimIdAim)
                    _animHasAim = true;
                else if (p.type == AnimatorControllerParameterType.Bool && p.nameHash == AnimIdReload)
                    _animHasReload = true;
                else if (p.type == AnimatorControllerParameterType.Trigger && p.nameHash == AnimIdShoot)
                    _animHasShoot = true;
            }
        }

        void ApplyProxyVisualState()
        {
            // The corpse stays on screen. It is not a hidden player, it is an object teammates have
            // to find, lift and carry.
            if (IsDead)
            {
                SetRemoteBodyVisible(true);
                return;
            }

            // Drawn in BOTH modes. This used to be hidden while the player was in third person, on
            // the assumption that their Malbers character represented them - but that character
            // exists only on its owner's machine and is not networked, so everyone else saw nothing
            // at all. This body is the one networked representation of the player: its root follows
            // whichever controller is active and its animation comes from the same replicated
            // parameters in both modes, so it is the same character whichever mode they are in.
            SetRemoteBodyVisible(true);
            EnsureRemoteWeaponVisual();

            var weaponName = WeaponName.ToString();
            if (weaponName != _lastAppliedWeaponName)
            {
                remoteWeaponVisual?.SetWeapon(weaponName);
                _lastAppliedWeaponName = weaponName;
            }

            remoteWeaponVisual?.SetFlashlight(IsFlashlightOn);

            if (FireSeq != _lastSeenFireSeq)
            {
                _lastSeenFireSeq = FireSeq;
                remoteWeaponVisual?.PlayMuzzleFlash();
                if (_animHasShoot && _remoteAnimator != null)
                    _remoteAnimator.SetTrigger(AnimIdShoot);
            }
            else if (IsFiring && !_wasFiring)
            {
                remoteWeaponVisual?.PlayMuzzleFlash();
            }

            _wasFiring = IsFiring;
        }

        void EnsureRemoteWeaponVisual()
        {
            if (remoteWeaponVisual != null)
                return;

            remoteWeaponVisual = GetComponent<RemoteWeaponVisual>();
            if (remoteWeaponVisual == null)
                remoteWeaponVisual = gameObject.AddComponent<RemoteWeaponVisual>();
            remoteWeaponVisual.Initialize(remoteBody);
        }

        void CacheReferences()
        {
            if (fpsRoot == null)
                fpsRoot = transform.Find("CowsinsFPSController")?.gameObject;

            if (fpsBody == null && fpsRoot != null)
            {
                var movement = fpsRoot.GetComponentInChildren<PlayerMovement>(true);
                if (movement != null)
                    fpsBody = movement.transform;
            }

            if (fpsBody != null)
            {
                _bodyLocalY = fpsBody.localPosition.y;
                _movement = fpsBody.GetComponent<PlayerMovement>();
                _playerControl = fpsBody.GetComponent<PlayerControl>();
                _cowsinsStats = fpsBody.GetComponent<cowsins.PlayerStats>();
                _weapon = fpsBody.GetComponent<WeaponController>();
                _fpsRigidbody = fpsBody.GetComponent<Rigidbody>();
            }

            if (fpsCameraRoot == null && fpsRoot != null)
                fpsCameraRoot = FindChild(fpsRoot.transform, "Camera");

            if (fpsInputRoot == null && fpsRoot != null)
            {
                var input = fpsRoot.GetComponentInChildren<InputManager>(true);
                if (input != null)
                    fpsInputRoot = input.gameObject;
            }

            if (fpsUiRoot == null && fpsRoot != null)
                fpsUiRoot = FindChild(fpsRoot.transform, "PlayerUI");

            if (fpsManagersRoot == null && fpsRoot != null)
                fpsManagersRoot = FindChild(fpsRoot.transform, "GeneralManagers");

            if (remoteBody == null)
            {
                var render = transform.Find("Player Render");
                if (render != null)
                    remoteBody = render;
            }

            if (interpolationTarget == null)
            {
                var interp = transform.Find("Interpolation target");
                if (interp != null)
                    interpolationTarget = interp;
            }

            if (starterAssetsInputs == null)
                starterAssetsInputs = GetComponent<StarterAssetsInputs>();

            EnsureDistanceOrigin();

            _remoteAnimator = GetComponent<Animator>();
            if (_remoteAnimator != null)
                ProbeAnimatorParameters();

            EnsureRemoteWeaponVisual();
        }

        void EnsureLocalFlashlight()
        {
            if (_flashlight == null)
            {
                _flashlight = GetComponent<WeaponFlashlight>();
                if (_flashlight == null)
                    _flashlight = gameObject.AddComponent<WeaponFlashlight>();
            }

            _flashlight.Bind(_weapon, _dual);
            IsFlashlightOn = _flashlight.IsOn;
        }

        void CollectLocalOnly()
        {
            _localObjects.Clear();
            _localBehaviours.Clear();

            AddLocalObject(fpsCameraRoot);
            AddLocalObject(fpsInputRoot);
            // Managers before UI: UIController.Start reads PoolManager/CoinManager/ExperienceManager
            // singletons that GeneralManagers owns.
            AddLocalObject(fpsManagersRoot);
            AddLocalObject(fpsUiRoot);

            if (fpsBody != null)
            {
                AddLocalBehaviour(fpsBody.GetComponent<PlayerMovement>());
                AddLocalBehaviour(fpsBody.GetComponent<PlayerStates>());
                AddLocalBehaviour(fpsBody.GetComponent<WeaponController>());
                AddLocalBehaviour(fpsBody.GetComponent<WeaponStates>());
                AddLocalBehaviour(fpsBody.GetComponent<InteractManager>());
                AddLocalBehaviour(fpsBody.GetComponent<CameraEffects>());
                AddLocalBehaviour(fpsBody.GetComponent<WeaponEffects>());
            }

            if (fpsCameraRoot != null)
            {
                foreach (var listener in fpsCameraRoot.GetComponentsInChildren<AudioListener>(true))
                    AddLocalBehaviour(listener);
                foreach (var cam in fpsCameraRoot.GetComponentsInChildren<Camera>(true))
                    AddLocalBehaviour(cam);
            }
        }

        void AddLocalObject(GameObject go)
        {
            if (go != null && !_localObjects.Contains(go))
                _localObjects.Add(go);
        }

        void AddLocalBehaviour(UnityEngine.Behaviour behaviour)
        {
            if (behaviour != null && !_localBehaviours.Contains(behaviour))
                _localBehaviours.Add(behaviour);
        }

        void SetLocalOnlyEnabled(bool enabled)
        {
            foreach (var go in _localObjects)
            {
                if (go != null)
                    go.SetActive(enabled);
            }

            foreach (var behaviour in _localBehaviours)
            {
                if (behaviour != null)
                    behaviour.enabled = enabled;
            }
        }

        /// <summary>
        /// Shows or hides the third-person character mesh on this machine.
        ///
        /// Public so DualPlayerController can hide it on a mode switch without going through
        /// EnterMalbersThirdPerson, which is gated on state authority and therefore never runs in
        /// offline play.
        /// </summary>
        public void SetRemoteBodyVisible(bool visible)
        {
            if (remoteBody == null)
                return;

            // Re-queried every call rather than cached once. RemoteWeaponVisual instantiates the held
            // weapon under a BONE inside this hierarchy, long after any cache was taken, so a cached
            // list hid the body and left the gun hanging in the air.
            _remoteRenderers = remoteBody.GetComponentsInChildren<Renderer>(true);

            foreach (var renderer in _remoteRenderers)
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }
        }

        void DetachLocalFpsFromNetworkRoot()
        {
            if (fpsRoot == null || _fpsDetached)
                return;

            fpsRoot.transform.SetParent(null, true);
            _fpsDetached = true;
        }

        void CleanupDetachedFps()
        {
            if (!_fpsDetached || fpsRoot == null)
                return;

            Destroy(fpsRoot);
            fpsRoot = null;
            _fpsDetached = false;
        }

        void DisableCowsinsPauseMenu()
        {
            if (fpsRoot == null)
                return;

            var pause = fpsRoot.GetComponentInChildren<PauseMenu>(true);
            if (pause == null)
                return;

            foreach (Transform child in pause.transform)
            {
                if (child.GetComponent<CanvasGroup>() == null && !child.name.Contains("Pause"))
                    continue;

                child.gameObject.SetActive(false);
                var canvasGroup = child.GetComponent<CanvasGroup>();
                if (canvasGroup != null)
                {
                    canvasGroup.alpha = 0f;
                    canvasGroup.interactable = false;
                    canvasGroup.blocksRaycasts = false;
                }
            }

            pause.enabled = false;
        }

        void DisableSceneThirdPersonCamera()
        {
            var follow = GameObject.Find("Player Follow Camera");
            if (follow != null)
                follow.SetActive(false);

            foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (fpsCameraRoot != null && cam.transform.IsChildOf(fpsCameraRoot.transform))
                    continue;
                if (cam.CompareTag("MainCamera") || cam.name.Contains("Main Camera"))
                {
                    cam.enabled = false;
                    var listener = cam.GetComponent<AudioListener>();
                    if (listener != null)
                        listener.enabled = false;
                }
            }
        }

        static GameObject FindChild(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null)
                return t.gameObject;

            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child != parent && child.name == name)
                    return child.gameObject;
            }

            return null;
        }
    }
}
#endif
