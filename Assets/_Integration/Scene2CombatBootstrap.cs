using cowsins;
using EmeraldAI;
using EmeraldAI.Utility;
#if CMPSETUP_COMPLETE
using Fusion;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Scene 2 only: make the existing Cowsins FPS player visible to Emerald.
    /// Does not spawn a player.
    /// </summary>
    public static class Scene2CombatBootstrap
    {
        const string TestPlayerName = "Scene2 Test Player";

        // Fusion loads Game.unity after startup, so RuntimeInitializeOnLoadMethod alone never
        // fires for the Menu -> Game path and enemies are left unconfigured.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "2" || scene.name == "Game")
                Setup(scene);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            var active = SceneManager.GetActiveScene();
            if (active.name == "2" || active.name == "Game")
                Setup(active);
        }

        static void Setup(Scene scene)
        {
            if (Object.FindFirstObjectByType<Scene2CombatRunner>() != null)
                return;

            // #region agent log
            AgentDebugLog.Write("B2", "Scene2CombatBootstrap.Setup", "combat_bootstrap_ran",
                "{\"scene\":\"" + scene.name + "\"}");
            // #endregion

            RuntimeUrpVisualRepair.Repair(scene);
            CowsinsUrpCameraStack.TryAttach(scene);
            RemoveTestPlayer();
            SpawnCombatTextIfNeeded();

            var player = FindExistingFpsPlayer();
            if (player != null)
                EnsureEmeraldTarget(player);

            foreach (var debugger in Object.FindObjectsByType<EmeraldDebugger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (debugger != null)
                    debugger.enabled = false;
            }

            foreach (var ai in Object.FindObjectsByType<EmeraldSystem>(FindObjectsSortMode.None))
            {
                ConfigureDetection(ai.gameObject);
                ConfigureCombat(ai.gameObject);
                EnsureHeldPistol(ai.gameObject);
                EnsureCowsinsCanDamage(ai.gameObject);
            }

            SetupCreeps();

            if (player != null)
                AimCoverNodesAt(player.transform.position);

#if CMPSETUP_COMPLETE
            if (IsFusionProxyClient())
            {
                NetworkWorldActor.DisableAllSceneBrains();
                var runner = new GameObject("Scene2CombatRunner");
                runner.AddComponent<Scene2CombatRunner>();
                return;
            }
#endif

            var runnerGo = new GameObject("Scene2CombatRunner");
            runnerGo.AddComponent<Scene2CombatRunner>();
        }

#if CMPSETUP_COMPLETE
        public static bool IsFusionProxyClient()
        {
            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning)
                    return !runner.IsSharedModeMasterClient;
            }

            return false;
        }
#endif

        static void SpawnCombatTextIfNeeded()
        {
            if (EmeraldAI.CombatTextSystem.Instance != null && EmeraldSystem.CombatTextSystemObject != null)
                return;

            var systemPrefab = Resources.Load<GameObject>("Combat Text System");
            var canvasPrefab = Resources.Load<GameObject>("Combat Text Canvas");
            if (systemPrefab == null || canvasPrefab == null)
                return;

            var system = Object.Instantiate(systemPrefab, Vector3.zero, Quaternion.identity);
            system.name = "Combat Text System";
            var canvas = Object.Instantiate(canvasPrefab, Vector3.zero, Quaternion.identity);
            canvas.name = "Combat Text Canvas";

            EmeraldSystem.CombatTextSystemObject = canvas;
            if (EmeraldAI.CombatTextSystem.Instance != null)
            {
                EmeraldAI.CombatTextSystem.Instance.CombatTextCanvas = canvas;
                EmeraldAI.CombatTextSystem.Instance.Initialize();
            }
        }

        public static void SetupCreeps()
        {
            foreach (var lod in Object.FindObjectsByType<LODGroup>(FindObjectsSortMode.None))
            {
                if (lod != null && lod.name.StartsWith("Creep1"))
                    CreepGrabEnemy.Setup(lod.gameObject);
            }
        }

        public static void RemoveTestPlayer()
        {
            var test = GameObject.Find(TestPlayerName);
            if (test == null)
                return;

            if (Application.isPlaying)
                Object.Destroy(test);
            else
                Object.DestroyImmediate(test);
        }

        public static GameObject FindExistingFpsPlayer()
        {
#if CMPSETUP_COMPLETE
            foreach (var bridge in Object.FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None))
            {
                if (bridge == null || !bridge.IsLocalOwner)
                    continue;
                if (bridge.DualPlayer != null && bridge.DualPlayer.FpsBody != null)
                    return bridge.DualPlayer.FpsBody.gameObject;
            }
#endif

            var deps = Object.FindFirstObjectByType<PlayerDependencies>();
            if (deps != null)
                return deps.gameObject;

            var stats = Object.FindFirstObjectByType<PlayerStats>();
            if (stats != null)
                return stats.gameObject;

            var tagged = GameObject.FindGameObjectWithTag("Player");
            if (tagged != null && tagged.name != TestPlayerName)
                return tagged;

            return null;
        }

        public static void EnsureEmeraldTarget(GameObject player)
        {
            player.tag = "Player";
            int playerLayer = LayerMask.NameToLayer("Player");
            if (playerLayer >= 0)
                player.layer = playerLayer;

            if (player.GetComponent<FactionExtension>() == null)
                player.AddComponent<FactionExtension>();
            player.GetComponent<FactionExtension>().CurrentFaction = 2;

            var cam = player.GetComponentInChildren<Camera>();
            var aim = cam != null ? cam.transform : player.transform;
            var tpm = player.GetComponent<TargetPositionModifier>();
            if (tpm == null)
                tpm = player.AddComponent<TargetPositionModifier>();
            tpm.TransformSource = aim;

            var leftoverBridge = player.GetComponent<EmeraldGeneralTargetBridge>();
            if (leftoverBridge != null)
            {
                if (Application.isPlaying)
                    Object.Destroy(leftoverBridge);
                else
                    Object.DestroyImmediate(leftoverBridge);
            }

            if (player.GetComponent<CowsinsEmeraldPlayerTarget>() == null)
                player.AddComponent<CowsinsEmeraldPlayerTarget>();

            int collidersTagged = 0;
            foreach (var col in player.GetComponentsInChildren<Collider>(true))
            {
                if (col == null)
                    continue;
                if (playerLayer >= 0 && col.gameObject.layer != playerLayer)
                    continue;
                if (col.GetComponent<CowsinsEmeraldPlayerTarget>() != null)
                    continue;
                if (col.GetComponent<EmeraldPlayerTargetProxy>() == null)
                    col.gameObject.AddComponent<EmeraldPlayerTargetProxy>();
                collidersTagged++;
            }

            IncludeEnemyOnCowsinsHitLayer(player);

            // #region agent log
            AgentDebugLog.Write("E1", "Scene2CombatBootstrap.EnsureEmeraldTarget", "wired",
                "{\"player\":\"" + player.name +
                "\",\"tag\":\"" + player.tag +
                "\",\"layer\":" + player.layer +
                ",\"hasTarget\":" + (player.GetComponent<CowsinsEmeraldPlayerTarget>() != null ? "true" : "false") +
                ",\"collidersTagged\":" + collidersTagged + "}");
            // #endregion
        }

        static void RestoreHitMask(cowsins.WeaponController weapons, int enemyLayer)
        {
            if (weapons.settings.hitLayer == 0)
                weapons.settings.hitLayer = 15753;
            weapons.settings.hitLayer |= 1 | (1 << enemyLayer);
        }

        static void IncludeEnemyOnCowsinsHitLayer(GameObject player)
        {
            int enemyLayer = LayerMask.NameToLayer("Enemy");
            if (enemyLayer < 0)
                enemyLayer = 7;

            foreach (var weapons in player.GetComponentsInParent<cowsins.WeaponController>(true))
                RestoreHitMask(weapons, enemyLayer);
            foreach (var weapons in player.GetComponentsInChildren<cowsins.WeaponController>(true))
                RestoreHitMask(weapons, enemyLayer);
        }

        static void ConfigureDetection(GameObject enemy)
        {
            var detection = enemy.GetComponent<EmeraldDetection>();
            if (detection == null)
                return;

            detection.DetectionLayerMask = LayerMask.GetMask("Player");
            // Emerald treats this mask as layers to IGNORE for LOS. Ignore the AI and extra hitboxes,
            // not the world, so they can still shoot when a wall is not in the way.
            detection.ObstructionDetectionLayerMask = LayerMask.GetMask("Enemy", "Ignore Raycast", "TransparentFX");
            detection.PlayerTag = "Player";
            detection.DetectionRadius = 30;
            detection.FieldOfViewAngle = 360;
            detection.CurrentDetectionState = EmeraldDetection.DetectionStates.Unaware;

            var head = FindChild(enemy.transform, "B_Head");
            if (head != null)
                detection.HeadTransform = head;
        }

        static void ConfigureCombat(GameObject enemy)
        {
            var combat = enemy.GetComponent<EmeraldCombat>();
            if (combat != null)
            {
                combat.WeaponTypeAmount = EmeraldCombat.WeaponTypeAmounts.One;
                combat.StartingWeaponType = EmeraldCombat.WeaponTypes.Type1;
                combat.CurrentWeaponType = EmeraldCombat.WeaponTypes.Type1;
                combat.Type1PickTargetType = PickTargetTypes.Closest;

                combat.Type1AttackCooldown = 0.12f;
                combat.Type2AttackCooldown = 0.12f;
                combat.CurrentAttackCooldown = 0.12f;
                combat.Type1Attacks.AttackPickType = AttackPickTypes.Order;
                RemoveGrenadeAttacks(combat.Type1Attacks);
                RemoveGrenadeAttacks(combat.Type2Attacks);

                if (combat.Type1Attacks.AttackDataList.Count == 0 && combat.Type2Attacks.AttackDataList.Count > 0)
                    combat.Type1Attacks.AttackDataList.AddRange(combat.Type2Attacks.AttackDataList);

                foreach (var attack in combat.Type1Attacks.AttackDataList)
                {
                    attack.AttackAnimation = 0;
                    attack.AttackOdds = 100;
                    attack.AttackDistance = 22f;
                    attack.TooCloseDistance = 6f;
                }

                combat.AttackDistance = 22f;
                combat.TooCloseDistance = 6f;
                if (combat.CurrentAttackData != null)
                {
                    combat.CurrentAttackData.AttackDistance = 22f;
                    combat.CurrentAttackData.TooCloseDistance = 6f;
                }
            }

            var agent = enemy.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null)
                agent.stoppingDistance = 22f;

            var cover = enemy.GetComponent<EmeraldCover>();
            if (cover != null)
            {
                cover.MinCoverDistance = 4f;
                cover.MaxTravelDistance = 28f;
                cover.CoverSearchRadius = 35f;
                cover.CoverNodeLayerMask = 1 << 1;
                cover.HideSecondsMin = 0.45f;
                cover.HideSecondsMax = 0.8f;
                cover.AttackSecondsMin = 2.5f;
                cover.AttackSecondsMax = 4f;
            }
        }

        public static void ApplyRangedCombat(EmeraldSystem ai)
        {
            if (ai == null || ai.AnimationComponent != null && ai.AnimationComponent.IsDead)
                return;

            const float range = 22f;
            const float tooClose = 6f;

            var combat = ai.CombatComponent;
            if (combat != null)
            {
                combat.AttackDistance = range;
                combat.TooCloseDistance = tooClose;
                if (combat.Type1Attacks?.AttackDataList != null)
                {
                    foreach (var attack in combat.Type1Attacks.AttackDataList)
                    {
                        attack.AttackDistance = range;
                        attack.TooCloseDistance = tooClose;
                    }
                }

                if (combat.CurrentAttackData != null)
                {
                    combat.CurrentAttackData.AttackDistance = range;
                    combat.CurrentAttackData.TooCloseDistance = tooClose;
                }
            }

            var cover = ai.CoverComponent;
            var agent = ai.m_NavMeshAgent;
            if (agent != null && agent.enabled && combat != null && combat.CombatState)
            {
                if (cover == null || cover.CoverState != EmeraldCover.CoverStates.MovingToCover)
                    agent.stoppingDistance = range;
            }
        }

        static void RemoveGrenadeAttacks(AttackClass attacks)
        {
            if (attacks?.AttackDataList == null)
                return;

            attacks.AttackDataList.RemoveAll(a =>
                a.AbilityObject != null && a.AbilityObject.AbilityName == "Grenade");
        }

        public static void EnsureCowsinsCanDamage(GameObject enemy)
        {
            var health = enemy.GetComponent<EmeraldHealth>();
            if (health != null)
            {
                health.StartingHealth = 30;
                health.CurrentHealth = 30;
                health.Health = 30;
            }

            var box = enemy.GetComponent<BoxCollider>();
            if (box != null)
                box.enabled = true;

            var hitbox = enemy.transform.Find("DamageHitbox");
            if (hitbox == null)
            {
                var go = new GameObject("DamageHitbox");
                hitbox = go.transform;
                hitbox.SetParent(enemy.transform, false);
            }

            int enemyLayer = LayerMask.NameToLayer("Enemy");
            hitbox.gameObject.layer = enemyLayer >= 0 ? enemyLayer : 7;
            hitbox.gameObject.tag = "BodyShot";
            var capsule = hitbox.GetComponent<CapsuleCollider>();
            if (capsule == null)
                capsule = hitbox.gameObject.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;
            capsule.height = 2f;
            capsule.radius = 0.45f;
            capsule.center = Vector3.zero;
            hitbox.localPosition = new Vector3(0f, 1f, 0f);

            if (enemy.GetComponent<Scene2EnemyDeathFall>() == null)
                enemy.AddComponent<Scene2EnemyDeathFall>();
            if (enemy.GetComponent<Scene2AllyCombatAlert>() == null)
                enemy.AddComponent<Scene2AllyCombatAlert>();

            if (enemy.GetComponent<CowsinsHitsEmeraldHealth>() == null)
                enemy.AddComponent<CowsinsHitsEmeraldHealth>();
            if (hitbox.GetComponent<CowsinsHitsEmeraldHealth>() == null)
                hitbox.gameObject.AddComponent<CowsinsHitsEmeraldHealth>();

            foreach (var col in enemy.GetComponentsInChildren<Collider>(true))
            {
                if (col.GetComponent<CowsinsHitsEmeraldHealth>() == null)
                    col.gameObject.AddComponent<CowsinsHitsEmeraldHealth>();
            }
        }

        public static void EnsureHeldPistol(GameObject enemy)
        {
            var hand = FindChild(enemy.transform, "B_R_Hand");
            if (hand == null)
                return;

            var pistol = hand.Find("Pistol (Held)");
            if (pistol == null)
            {
                var existing = GameObject.Find("Pistol (Held)");
                if (existing != null && existing.transform.IsChildOf(enemy.transform))
                    pistol = existing.transform;
                else
                    pistol = CreatePistol(hand);
            }

            if (pistol == null)
                return;

            pistol.SetParent(hand, false);
            pistol.localPosition = new Vector3(-0.116f, 0.041f, 0.164f);
            pistol.localRotation = new Quaternion(-0.28377095f, -0.32938722f, -0.90053725f, 0.0032882984f);
            pistol.localScale = new Vector3(2.14f, 2.14f, 2.14f);
            pistol.gameObject.SetActive(true);

            var items = enemy.GetComponent<EmeraldItems>();
            if (items != null)
            {
                if (items.Type1EquippableWeapons.Count == 0)
                    items.Type1EquippableWeapons.Add(new EmeraldItems.EquippableWeapons());

                var slot = items.Type1EquippableWeapons[0];
                slot.HeldObject = pistol.gameObject;
                slot.HeldToggle = true;
                slot.HolsteredObject = null;
                slot.HolsteredToggle = false;
                items.Type1EquippableWeapons[0] = slot;
            }

            var firePoint = pistol.Find("FirePoint");
            if (firePoint == null)
            {
                var go = new GameObject("FirePoint");
                firePoint = go.transform;
                firePoint.SetParent(pistol, false);
                firePoint.localPosition = new Vector3(0f, 0.02f, 0.12f);
            }

            var combat = enemy.GetComponent<EmeraldCombat>();
            if (combat != null)
            {
                combat.WeaponType1AttackTransforms.Clear();
                combat.WeaponType1AttackTransforms.Add(firePoint);
                combat.WeaponType2AttackTransforms.Clear();
                combat.WeaponType2AttackTransforms.Add(firePoint);
            }
        }

        static Transform CreatePistol(Transform hand)
        {
            GameObject source = null;
#if UNITY_EDITOR
            source = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Emerald AI/Demo/Demo Source/Models/Demo Models/Mesh/Pistol.fbx");
#endif
            GameObject pistol;
            if (source != null)
            {
                pistol = Object.Instantiate(source, hand);
                pistol.name = "Pistol (Held)";
            }
            else
            {
                var existing = Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
                MeshFilter template = null;
                foreach (var filter in existing)
                {
                    if (filter.sharedMesh != null && filter.sharedMesh.name.Contains("Pistol"))
                    {
                        template = filter;
                        break;
                    }
                }

                pistol = new GameObject("Pistol (Held)");
                pistol.transform.SetParent(hand, false);
                if (template != null)
                {
                    pistol.AddComponent<MeshFilter>().sharedMesh = template.sharedMesh;
                    var srcRenderer = template.GetComponent<MeshRenderer>();
                    var dst = pistol.AddComponent<MeshRenderer>();
                    if (srcRenderer != null)
                        dst.sharedMaterials = srcRenderer.sharedMaterials;
                }
            }

            return pistol.transform;
        }

        static void AimCoverNodesAt(Vector3 playerPosition)
        {
            var root = GameObject.Find("Cover Nodes");
            if (root == null)
                return;

            foreach (var node in root.GetComponentsInChildren<CoverNode>())
            {
                var flat = playerPosition - node.transform.position;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f)
                    node.transform.rotation = Quaternion.LookRotation(flat);
            }
        }

        static Transform FindChild(Transform root, string name)
        {
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChild(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }

    /// <summary>
    /// Emerald ResetSettings holsters the gun on Start. Keep range and death drop applied after that.
    /// </summary>
    public class Scene2CombatRunner : MonoBehaviour
    {
        GameObject _player;
        Collider[] _playerColliders = System.Array.Empty<Collider>();
        EmeraldSystem[] _ais;
        bool _wired;
        bool _secondPass = true;
        float _nextRefresh;
#if CMPSETUP_COMPLETE
        bool _proxyClient;
#endif

        void Start()
        {
#if CMPSETUP_COMPLETE
            _proxyClient = Scene2CombatBootstrap.IsFusionProxyClient();
#endif
            Scene2CombatBootstrap.SetupCreeps();
#if CMPSETUP_COMPLETE
            if (_proxyClient)
                NetworkWorldActor.DisableAllSceneBrains();
#endif
            Refresh(true);
            _nextRefresh = Time.unscaledTime + 0.5f;
        }

        void Update()
        {
            if (Time.unscaledTime < _nextRefresh)
                return;

            if (_secondPass)
            {
                _secondPass = false;
                Refresh(true);
                _nextRefresh = Time.unscaledTime + 2f;
                return;
            }

            _nextRefresh = Time.unscaledTime + 2f;
            Refresh(false);
        }

        void Refresh(bool first)
        {
            if (_player == null)
                _player = Scene2CombatBootstrap.FindExistingFpsPlayer();
            if (_player != null && !_wired)
            {
                Scene2CombatBootstrap.EnsureEmeraldTarget(_player);
                _playerColliders = _player.GetComponentsInChildren<Collider>(true);
                _wired = true;
                first = true;
            }

#if CMPSETUP_COMPLETE
            // The local-owner wire above never sees a joining client. Every networked
            // player needs its own Player-layer faction target on this machine.
            foreach (var bridge in FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None))
            {
                if (bridge == null || bridge.Object == null || !bridge.Object.IsValid)
                    continue;
                bridge.EnsureAiVisible();
            }
#endif

            if (first || _ais == null)
                _ais = FindObjectsByType<EmeraldSystem>(FindObjectsSortMode.None);

            // #region agent log
            if (first)
            {
                float nearest = -1f;
                int enabledAi = 0;
                int detectionOn = 0;
                for (int i = 0; i < _ais.Length; i++)
                {
                    var ai = _ais[i];
                    if (ai == null) continue;
                    if (ai.enabled) enabledAi++;
                    if (ai.DetectionComponent != null && ai.DetectionComponent.enabled) detectionOn++;
                    if (_player != null)
                    {
                        float d = Vector3.Distance(_player.transform.position, ai.transform.position);
                        if (nearest < 0f || d < nearest) nearest = d;
                    }
                }

                AgentDebugLog.Write("E2", "Scene2CombatRunner.Refresh", "enemy_scan",
                    "{\"wired\":" + (_wired ? "true" : "false") +
                    ",\"player\":\"" + (_player != null ? _player.name : "null") +
                    "\",\"playerPos\":\"" + (_player != null ? _player.transform.position.ToString() : "") +
                    "\",\"aiCount\":" + _ais.Length +
                    ",\"enabledAi\":" + enabledAi +
                    ",\"detectionOn\":" + detectionOn +
                    ",\"nearestAi\":" + nearest.ToString("F1") +
#if CMPSETUP_COMPLETE
                    ",\"proxyClient\":" + (_proxyClient ? "true" : "false") +
#endif
                    "}");
            }
            // #endregion

            for (int i = 0; i < _ais.Length; i++)
            {
                var ai = _ais[i];
                if (ai == null)
                    continue;

                if (first)
                {
#if CMPSETUP_COMPLETE
                    if (!_proxyClient)
#endif
                    {
                        Scene2CombatBootstrap.EnsureHeldPistol(ai.gameObject);
                        Scene2CombatBootstrap.EnsureCowsinsCanDamage(ai.gameObject);
                        Scene2CombatBootstrap.ApplyRangedCombat(ai);
                    }

                    var detection = ai.DetectionComponent;
                    if (detection != null)
                    {
                        for (int c = 0; c < _playerColliders.Length; c++)
                        {
                            var col = _playerColliders[c];
                            if (col != null && !detection.IgnoredColliders.Contains(col))
                                detection.IgnoredColliders.Add(col);
                        }
                    }
                }

#if CMPSETUP_COMPLETE
                if (!_proxyClient && ai.AnimationComponent != null && ai.AnimationComponent.IsDead)
#else
                if (ai.AnimationComponent != null && ai.AnimationComponent.IsDead)
#endif
                {
                    var fall = ai.GetComponent<Scene2EnemyDeathFall>()
                        ?? ai.gameObject.AddComponent<Scene2EnemyDeathFall>();
                    fall.Drop();
                }
            }
        }
    }
}
