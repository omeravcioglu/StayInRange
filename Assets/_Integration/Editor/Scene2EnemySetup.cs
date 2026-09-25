#if UNITY_EDITOR
using EmeraldAI;
using EmeraldAI.Utility;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace CollarCali.Editor
{
    /// <summary>
    /// Wires the scene 2 RPG-Character so it wanders, takes cover, and shoots.
    /// </summary>
    public static class Scene2EnemySetup
    {
        const string ScenePath = "Assets/Scenes/2.unity";
        const string NavMeshPath = "Assets/Scenes/2/NavMesh-Plane.asset";
        const string MarkerName = "Scene2AISetup";
        const string TestPlayerName = "Scene2 Test Player";

        [MenuItem("Tools/CollarCali/Setup Scene 2 Enemy")]
        public static void SetupFromMenu()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Setup(scene, forceSave: true);
        }

        [InitializeOnLoadMethod]
        static void AutoSetupIfNeeded()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;

                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || scene.path != ScenePath)
                    return;

                if (GameObject.Find("RPG-Character") == null)
                    return;

                var testPlayer = GameObject.Find(TestPlayerName);
                if (testPlayer != null)
                {
                    Object.DestroyImmediate(testPlayer);
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
            };
        }

        static void Setup(Scene scene, bool forceSave)
        {
            var enemy = GameObject.Find("RPG-Character");
            if (enemy == null)
            {
                Debug.LogWarning("[Scene2Enemy] RPG-Character not found.");
                return;
            }

            Scene2CombatBootstrap.RemoveTestPlayer();
            HookNavMesh();
            ConfigureEnemy(enemy);
            CreateCoverNodes();
            var fps = Scene2CombatBootstrap.FindExistingFpsPlayer();
            if (fps != null)
                Scene2CombatBootstrap.EnsureEmeraldTarget(fps);
            MarkSetup();

            if (forceSave)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log("[Scene2Enemy] RPG-Character configured: wander, cover, Type 1 shooting. Uses the existing FPS player.");
        }

        static void HookNavMesh()
        {
            var plane = GameObject.Find("Plane");
            if (plane == null)
                return;

            var surface = plane.GetComponent<NavMeshSurface>();
            if (surface == null)
                surface = plane.AddComponent<NavMeshSurface>();

            var data = AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshPath);
            if (data != null)
            {
                var so = new SerializedObject(surface);
                so.FindProperty("m_NavMeshData").objectReferenceValue = data;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            surface.agentTypeID = 0;
            surface.collectObjects = CollectObjects.All;
        }

        static void ConfigureEnemy(GameObject enemy)
        {
            enemy.layer = LayerMask.NameToLayer("Enemy");
            enemy.tag = "Enemy";

            var movement = enemy.GetComponent<EmeraldMovement>();
            if (movement != null)
            {
                movement.MovementType = EmeraldMovement.MovementTypes.NavMeshDriven;
                movement.WanderType = EmeraldMovement.WanderTypes.Dynamic;
                movement.CurrentMovementState = EmeraldMovement.MovementStates.Walk;
                movement.StartingMovementState = EmeraldMovement.MovementStates.Walk;
                movement.WalkSpeed = 2f;
                movement.RunSpeed = 5f;
                movement.WanderRadius = 18;
                movement.StoppingDistance = 1.25f;
                EditorUtility.SetDirty(movement);
            }

            var agent = enemy.GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                agent.speed = 2f;
                agent.acceleration = 75f;
                agent.stoppingDistance = 1.25f;
                agent.baseOffset = 0f;
            }

            var animator = enemy.GetComponent<Animator>();
            if (animator != null)
                animator.applyRootMotion = false;

            var detection = enemy.GetComponent<EmeraldDetection>();
            if (detection != null)
            {
                detection.DetectionLayerMask = LayerMask.GetMask("Player");
                detection.ObstructionDetectionLayerMask = LayerMask.GetMask("Default", "Ground", "Enemy", "Ignore Raycast");
                detection.PlayerTag = "Player";
                detection.DetectionRadius = 30;
                detection.FieldOfViewAngle = 360;
                var head = enemy.transform.Find("B_Head") ?? FindChild(enemy.transform, "B_Head");
                if (head != null)
                    detection.HeadTransform = head;
                EditorUtility.SetDirty(detection);
            }

            var combat = enemy.GetComponent<EmeraldCombat>();
            if (combat != null)
            {
                combat.WeaponTypeAmount = EmeraldCombat.WeaponTypeAmounts.One;
                combat.StartingWeaponType = EmeraldCombat.WeaponTypes.Type1;
                combat.CurrentWeaponType = EmeraldCombat.WeaponTypes.Type1;
                combat.Type1PickTargetType = PickTargetTypes.Closest;
                combat.Type1AttackCooldown = 0.12f;
                combat.Type2AttackCooldown = 0.12f;
                combat.Type1Attacks.AttackPickType = AttackPickTypes.Order;
                combat.Type1Attacks.AttackDataList.RemoveAll(a =>
                    a.AbilityObject != null && a.AbilityObject.AbilityName == "Grenade");
                combat.Type2Attacks.AttackDataList.RemoveAll(a =>
                    a.AbilityObject != null && a.AbilityObject.AbilityName == "Grenade");
                if (combat.Type1Attacks.AttackDataList.Count == 0 && combat.Type2Attacks.AttackDataList.Count > 0)
                    combat.Type1Attacks.AttackDataList.AddRange(combat.Type2Attacks.AttackDataList);

                foreach (var attack in combat.Type1Attacks.AttackDataList)
                {
                    attack.AttackAnimation = 0;
                    attack.AttackOdds = 100;
                    if (attack.AttackDistance < 12f)
                        attack.AttackDistance = 25f;
                    if (attack.TooCloseDistance <= 0f)
                        attack.TooCloseDistance = 3f;
                }

                Scene2CombatBootstrap.EnsureHeldPistol(enemy);

                var firePoint = enemy.transform.Find("FirePoint");
                if (firePoint == null)
                {
                    var go = new GameObject("FirePoint");
                    firePoint = go.transform;
                    firePoint.SetParent(enemy.transform, false);
                    firePoint.localPosition = new Vector3(0.25f, 1.4f, 0.45f);
                }

                combat.WeaponType1AttackTransforms.Clear();
                combat.WeaponType1AttackTransforms.Add(firePoint);
                combat.WeaponType2AttackTransforms.Clear();
                combat.WeaponType2AttackTransforms.Add(firePoint);
                EditorUtility.SetDirty(combat);
            }

            var cover = enemy.GetComponent<EmeraldCover>();
            if (cover == null)
                cover = enemy.AddComponent<EmeraldCover>();
            cover.MinCoverDistance = 4f;
            cover.MaxTravelDistance = 28f;
            cover.CoverSearchRadius = 35f;
            cover.CoverNodeLayerMask = 1 << 1;

            if (enemy.GetComponent<EmeraldDebugger>() == null)
                enemy.AddComponent<EmeraldDebugger>();
        }

        static void CreateCoverNodes()
        {
            if (GameObject.Find("Cover Nodes") != null)
                return;

            var root = new GameObject("Cover Nodes");
            var aim = new Vector3(33f, 0f, 80f);
            Vector3[] spots =
            {
                new(22.6f, 0.05f, 82.1f),
                new(22.6f, 0.05f, 91.8f),
                new(22.6f, 0.05f, 97.0f),
                new(15.2f, 0.05f, 96.2f),
                new(18.5f, 0.05f, 80.4f),
                new(16.8f, 0.05f, 88.0f),
                new(24.0f, 0.05f, 74.0f),
            };

            foreach (var spot in spots)
            {
                var go = new GameObject("Emerald Cover Node");
                go.transform.SetParent(root.transform, false);
                go.transform.position = spot;
                var look = aim - spot;
                look.y = 0f;
                go.transform.rotation = Quaternion.LookRotation(look);
                go.layer = 1;
                go.AddComponent<CoverNode>();
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

        static void MarkSetup()
        {
            if (GameObject.Find(MarkerName) != null)
                return;

            var marker = new GameObject(MarkerName);
            marker.hideFlags = HideFlags.HideInHierarchy;
        }
    }
}
#endif
