#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds the zombie animator controllers and prefabs from the Scary Zombie Pack.
    ///
    /// Generated rather than hand-authored because the pack ships Mixamo FBXs whose clips are all
    /// named "mixamo.com" and default to non-looping with root motion left in - none of which is
    /// usable until the importers are reconfigured. Doing that in code keeps the setup repeatable
    /// if the pack is ever reimported.
    /// </summary>
    public static class ZombiePrefabBuilder
    {
        const string PackRoot = "Assets/Scary Zombie Pack/";
        const string OutputRoot = "Assets/_Integration/Resources/";
        const string CharacterFbx = PackRoot + "Mremireh O Desbiens.fbx";

        // Source FBX -> the clip name we want inside it.
        const string IdleFbx = PackRoot + "zombie idle.fbx";
        const string WalkFbx = PackRoot + "zombie walk.fbx";
        const string RunFbx = PackRoot + "zombie run.fbx";
        const string AttackFbx = PackRoot + "zombie attack.fbx";
        const string ScreamFbx = PackRoot + "zombie scream.fbx";
        const string DeathFbx = PackRoot + "zombie death.fbx";
        const string CrawlFbx = PackRoot + "zombie crawl.fbx";
        const string RunCrawlFbx = PackRoot + "running crawl.fbx";
        const string BiteFbx = PackRoot + "zombie biting.fbx";

        struct Variant
        {
            public string Name;
            public string IdleClip, RunClip, AttackClip, ScreamClip, DeathClip;
            public float MaxHealth;
            public float RunSpeed, AttackRange, AttackDamage, DetectRadius;
            public float ColliderHeight, ColliderRadius;
            public float HeadHitboxRadius;
        }

        [MenuItem("Tools/CollarCali/Build Zombie Prefabs")]
        public static void Build()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(CharacterFbx) == null)
            {
                Debug.LogError("[CollarCali] Zombie character not found at " + CharacterFbx);
                return;
            }

            ConfigureAllClips();

            var walker = new Variant
            {
                Name = "Zombie",
                IdleClip = IdleFbx, RunClip = RunFbx,
                AttackClip = AttackFbx, ScreamClip = ScreamFbx, DeathClip = DeathFbx,
                // Tuned against the actual Cowsins weapon damage in this project: SMG 1.5,
                // Pistol 2, Rifle 2.8, Revolver 5, Shotgun 4x5, Rocket 20, all with a 1.5x
                // critical multiplier. At the old 100 HP a pistol needed fifty shots. At 18 it is
                // nine body shots, three headshots, or one point-blank shotgun blast.
                MaxHealth = 10f,
                // Player walk is 4.3 and sprint is 10.06. At the old 3.6 a zombie could not catch
                // someone merely strolling away, which made relentless chasing pointless. 5 means
                // you have to actually sprint to break contact, and sprint still doubles them.
                RunSpeed = 5f,
                AttackRange = 2.1f, AttackDamage = 12f, DetectRadius = 22f,
                ColliderHeight = 1.9f, ColliderRadius = 0.4f,
                HeadHitboxRadius = 0.22f,
            };

            var crawler = new Variant
            {
                Name = "ZombieCrawler",
                IdleClip = CrawlFbx, RunClip = RunCrawlFbx,
                AttackClip = BiteFbx, ScreamClip = ScreamFbx, DeathClip = DeathFbx,
                // Lower profile and harder to hit, so it dies faster to compensate.
                MaxHealth = 6f,
                // Kept below the walker so crawlers are the ones you can leave behind.
                RunSpeed = 4.2f,
                AttackRange = 1.8f, AttackDamage = 9f, DetectRadius = 18f,
                ColliderHeight = 0.8f, ColliderRadius = 0.35f,
                HeadHitboxRadius = 0.2f,
            };

            BuildVariant(walker);
            BuildVariant(crawler);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[CollarCali] Zombie prefabs rebuilt into " + OutputRoot);
        }

        #region Clip import settings

        static void ConfigureAllClips()
        {
            ConfigureClip(IdleFbx, "ZombieIdle", loop: true);
            ConfigureClip(RunFbx, "ZombieRun", loop: true);
            ConfigureClip(CrawlFbx, "ZombieCrawl", loop: true);
            ConfigureClip(RunCrawlFbx, "ZombieRunCrawl", loop: true);
            ConfigureClip(AttackFbx, "ZombieAttack", loop: false);
            ConfigureClip(BiteFbx, "ZombieBite", loop: false);
            ConfigureClip(ScreamFbx, "ZombieScream", loop: false);
            ConfigureClip(DeathFbx, "ZombieDeath", loop: false);
        }

        /// <summary>
        /// Names the take and bakes root motion into the pose. Without the XZ lock the clips carry
        /// Mixamo's forward travel, which fights the NavMeshAgent and makes zombies drift away from
        /// where the agent thinks they are.
        /// </summary>
        static void ConfigureClip(string fbxPath, string clipName, bool loop)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning("[CollarCali] Missing animation FBX: " + fbxPath);
                return;
            }

            bool hasTake = importer.importedTakeInfos != null && importer.importedTakeInfos.Length > 0;
            var take = hasTake ? importer.importedTakeInfos[0] : default;

            // Every Mixamo export names its take "mixamo.com", which is why clips are loaded by
            // asset path rather than by name everywhere else in this file.
            string takeName = hasTake ? take.name : "mixamo.com";
            float sampleRate = hasTake && take.sampleRate > 0f ? take.sampleRate : 30f;

            var clip = new ModelImporterClipAnimation
            {
                name = clipName,
                takeName = takeName,
                firstFrame = hasTake ? take.bakeStartTime * sampleRate : 0f,
                lastFrame = hasTake ? take.bakeStopTime * sampleRate : 0f,
                loopTime = loop,
                loopPose = loop,
                lockRootRotation = true,
                keepOriginalOrientation = true,
                lockRootHeightY = true,
                keepOriginalPositionY = true,
                lockRootPositionXZ = true,
                keepOriginalPositionXZ = true,
            };

            importer.clipAnimations = new[] { clip };
            importer.animationType = ModelImporterAnimationType.Human;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        static AnimationClip LoadClip(string fbxPath)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath))
            {
                // Unity keeps a hidden __preview__ clip alongside the real one.
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                    return clip;
            }

            Debug.LogWarning("[CollarCali] No AnimationClip inside " + fbxPath);
            return null;
        }

        #endregion

        #region Animator controller

        static AnimatorController BuildController(Variant v)
        {
            string path = OutputRoot + v.Name + "Animator.controller";
            AssetDatabase.DeleteAsset(path);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Attack", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("Scream", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("Dead", AnimatorControllerParameterType.Bool);

            var sm = controller.layers[0].stateMachine;

            // Discrete states, NOT a blend tree. NetworkWorldActor replicates only the animator
            // state hash and normalized time, so a blend tree would arrive on remote clients as
            // one "Locomotion" hash whose pose depends on a Speed float that is never sent - every
            // remote zombie would slide around playing the idle clip. The creep controller this
            // pattern comes from uses discrete states for the same reason.
            var idle = sm.AddState("Idle");
            idle.motion = LoadClip(v.IdleClip);

            var run = sm.AddState("Run");
            run.motion = LoadClip(v.RunClip);

            var attack = sm.AddState("Attack");
            attack.motion = LoadClip(v.AttackClip);

            var scream = sm.AddState("Scream");
            scream.motion = LoadClip(v.ScreamClip);

            var death = sm.AddState("Death");
            death.motion = LoadClip(v.DeathClip);

            sm.defaultState = idle;

            // Two locomotion states only: zombies idle or run, never walk. The brain writes
            // Speed as 0 or 1, so one threshold in the middle covers both directions.
            AddFloat(idle, run, "Speed", AnimatorConditionMode.Greater, 0.25f);
            AddFloat(run, idle, "Speed", AnimatorConditionMode.Less, 0.25f);

            foreach (var from in new[] { idle, run })
            {
                AddTrigger(from, attack, "Attack");
                AddTrigger(from, scream, "Scream");
            }

            // Lets a scream be cut short when the player is already in reach.
            AddTrigger(scream, attack, "Attack");
            AddExit(attack, idle, 0.85f);
            AddExit(scream, idle, 0.9f);

            // Attack exits at 85% of a ~2s clip, which is later than the 1.6s attack cooldown - so
            // the next swing's trigger arrived while still inside Attack, where nothing consumed
            // it. The swing then dealt damage with no animation. This lets it restart itself.
            var again = attack.AddTransition(attack);
            again.AddCondition(AnimatorConditionMode.If, 0f, "Attack");
            again.hasExitTime = false;
            again.duration = 0.1f;
            again.canTransitionToSelf = true;

            // Any state so a zombie dies mid-swing or mid-scream, not just while walking.
            var toDeath = sm.AddAnyStateTransition(death);
            toDeath.AddCondition(AnimatorConditionMode.If, 0f, "Dead");
            toDeath.hasExitTime = false;
            toDeath.duration = 0.1f;
            toDeath.canTransitionToSelf = false;

            EditorUtility.SetDirty(controller);
            return controller;
        }

        static void AddTrigger(AnimatorState from, AnimatorState to, string trigger)
        {
            var t = from.AddTransition(to);
            t.AddCondition(AnimatorConditionMode.If, 0f, trigger);
            t.hasExitTime = false;
            t.duration = 0.1f;
        }

        static void AddFloat(AnimatorState from, AnimatorState to, string parameter,
            AnimatorConditionMode mode, float threshold)
        {
            var t = from.AddTransition(to);
            t.AddCondition(mode, threshold, parameter);
            t.hasExitTime = false;
            t.duration = 0.15f;
        }

        static void AddExit(AnimatorState from, AnimatorState to, float exitTime)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = true;
            t.exitTime = exitTime;
            t.duration = 0.15f;
        }

        #endregion

        #region Prefab

        static void BuildVariant(Variant v)
        {
            var controller = BuildController(v);

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterFbx);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(source);
            // Unpacked so the zombie prefab owns its own components instead of being a variant of
            // the raw FBX, which cannot carry added colliders cleanly.
            PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            root.name = v.Name;

            try
            {
                int enemyLayer = LayerMask.NameToLayer("Enemy");
                if (enemyLayer >= 0)
                    SetLayerRecursively(root, enemyLayer);
                root.tag = "Enemy";

                var animator = root.GetComponent<Animator>();
                if (animator == null)
                    animator = root.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                // The NavMeshAgent drives position; root motion would fight it.
                animator.applyRootMotion = false;
                // AlwaysAnimate, not CullUpdateTransforms: the head hitbox is parented to the Head
            // bone, and culling bone updates off-screen would freeze it away from the head.
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var agent = root.GetComponent<NavMeshAgent>();
                if (agent == null)
                    agent = root.AddComponent<NavMeshAgent>();
                ZombieEnemy.ConfigureAgent(agent, v.RunSpeed, v.AttackRange);
                agent.radius = v.ColliderRadius;
                agent.height = v.ColliderHeight;

                var health = root.AddComponent<ZombieHealth>();
                // 2x on top of the weapon's own 1.5x critical, so a headshot is 3x a body shot.
                // Zombies are the one enemy where going for the head should obviously be right,
                // and 1.5x alone barely changed the shots-to-kill.
                health.Configure(v.MaxHealth, 2f);

                var brain = root.AddComponent<ZombieEnemy>();
                brain.Configure(v.RunSpeed, v.AttackRange, v.AttackDamage, v.DetectRadius);

                root.AddComponent<ZombieHealthBar>();

                BuildBodyHitbox(root, health, v, enemyLayer);
                BuildHeadHitbox(root, animator, health, v, enemyLayer);

                string path = OutputRoot + v.Name + ".prefab";
                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void BuildBodyHitbox(GameObject root, ZombieHealth health, Variant v, int enemyLayer)
        {
            var go = new GameObject("BodyHitbox");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(0f, v.ColliderHeight * 0.5f, 0f);
            go.layer = enemyLayer >= 0 ? enemyLayer : 0;
            // Cowsins reads these tags to decide body vs critical damage.
            go.tag = "BodyShot";

            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;
            capsule.height = v.ColliderHeight;
            capsule.radius = v.ColliderRadius;
            capsule.center = Vector3.zero;
            capsule.direction = 1;

            var relay = go.AddComponent<ZombieHitboxRelay>();
            relay.Root = health;
        }

        /// <summary>
        /// Parented to the humanoid head bone so headshots track the animation rather than a fixed
        /// point above the origin - it matters most exactly when the zombie is lunging.
        /// </summary>
        static void BuildHeadHitbox(GameObject root, Animator animator, ZombieHealth health,
            Variant v, int enemyLayer)
        {
            var head = animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (head == null)
            {
                Debug.LogWarning("[CollarCali] " + v.Name +
                                 ": no humanoid head bone, skipping the headshot hitbox.");
                return;
            }

            var go = new GameObject("HeadHitbox");
            go.transform.SetParent(head, false);
            go.transform.localPosition = new Vector3(0f, 0.1f, 0f);
            go.layer = enemyLayer >= 0 ? enemyLayer : 0;
            go.tag = "Critical";

            var sphere = go.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = v.HeadHitboxRadius;

            var relay = go.AddComponent<ZombieHitboxRelay>();
            relay.Root = health;
            // Guarantees a head hit counts even if the shot is reported as a body hit.
            relay.ForceHeadshot = true;
        }

        static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        #endregion
    }
}
#endif
