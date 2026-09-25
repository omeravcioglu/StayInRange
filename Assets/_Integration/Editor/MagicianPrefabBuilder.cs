#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds the magician enemy and its projectile prefabs from the Lite Magic Pack (character +
    /// animations) and the Gabriel Aguiar projectile pack.
    ///
    /// The magician reuses the whole zombie combat stack - ZombieHealth, ZombieHealthBar,
    /// ZombieHitboxRelay, ZombieBloodFx - so this builder only has to assemble the rig, wire the
    /// caster brain and ragdoll, and wrap the chosen projectile VFX into damage-dealing prefabs.
    /// </summary>
    public static class MagicianPrefabBuilder
    {
        const string PackRoot = "Assets/Lite Magic Pack/";
        const string CharacterFbx = PackRoot + "Vampire A Lusth.fbx";

        // Animation FBXs
        const string IdleFbx = PackRoot + "standing idle.fbx";
        const string CastFbx = PackRoot + "Standing 1H Magic Attack 01.fbx";
        const string DeathFbx = PackRoot + "Standing React Death Backward.fbx";

        // Projectile pack - the fireball trio the caster fires. Swap these three paths to change
        // the spell; the full set lives under GabrielAguiarProductions/UniqueProjectilesVol_2.
        const string ProjRoot = "Assets/GabrielAguiarProductions/UniqueProjectilesVol_2/Prefabs/";
        const string ProjectileSource = ProjRoot + "Projectiles/vfx_Projectile_Fireball05_Orange.prefab";
        const string MuzzleSource = ProjRoot + "Muzzle/vfx_Muzzle_Fireball05_Orange.prefab";
        const string HitSource = ProjRoot + "Hits/vfx_Hit_Fireball05_Orange.prefab";

        const string OutputRoot = "Assets/_Integration/Resources/";

        // Tuning
        const float MaxHealth = 8f;
        const float HeadshotMultiplier = 2f;
        const float DetectRadius = 40f;
        const float CastCooldown = 3f;
        const float ProjectileDamage = 10f;
        const float ProjectileSpeed = 16f;
        const float ColliderHeight = 1.85f;
        const float ColliderRadius = 0.32f;
        const float HeadHitboxRadius = 0.2f;

        /// <summary>Shrinks the spell VFX. The pack's fireball reads huge at gameplay scale.</summary>
        const float ProjectileScale = 0.5f;

        [MenuItem("Tools/CollarCali/Build Magician")]
        public static void Build()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(CharacterFbx) == null)
            {
                Debug.LogError("[CollarCali] Magician character not found at " + CharacterFbx);
                return;
            }

            if (!ConfigureCharacterRig())
                return;
            ConfigureClips();
            BuildProjectilePrefabs();
            BuildMagicianPrefab();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[CollarCali] Magician built into " + OutputRoot +
                      ". Drop a MagicianPerch on a ledge, or place Magician.prefab directly.");
        }

        #region Rig

        /// <summary>
        /// The character FBX must be Humanoid or nothing works: the humanoid clips will not
        /// retarget, GetBoneTransform returns null so there is no headshot hitbox, and the ragdoll
        /// (which is built from humanoid bones) is skipped entirely. Asset-store rigs frequently
        /// import as Generic by default, so this forces and verifies it rather than assuming.
        /// </summary>
        static bool ConfigureCharacterRig()
        {
            var importer = AssetImporter.GetAtPath(CharacterFbx) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError("[CollarCali] Could not import " + CharacterFbx);
                return false;
            }

            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterFbx);
            var animator = go != null ? go.GetComponent<Animator>() : null;
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            {
                Debug.LogError("[CollarCali] " + CharacterFbx + " did not resolve to a valid Humanoid " +
                               "avatar. Open its Rig tab, set Animation Type = Humanoid, fix the avatar, " +
                               "then run Build Magician again.");
                return false;
            }
            return true;
        }

        #endregion

        #region Clips

        static void ConfigureClips()
        {
            ConfigureClip(IdleFbx, "MagicianIdle", loop: true);
            ConfigureClip(CastFbx, "MagicianCast", loop: false);
            ConfigureClip(DeathFbx, "MagicianDeath", loop: false);
        }

        static void ConfigureClip(string fbxPath, string clipName, bool loop)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning("[CollarCali] Missing magician FBX: " + fbxPath);
                return;
            }

            bool hasTake = importer.importedTakeInfos != null && importer.importedTakeInfos.Length > 0;
            var take = hasTake ? importer.importedTakeInfos[0] : default;
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
                // Root motion locked: the caster is stationary and the death is handed to the
                // ragdoll, so any baked travel would just fight the transform.
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
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                    return clip;
            Debug.LogWarning("[CollarCali] No AnimationClip inside " + fbxPath);
            return null;
        }

        #endregion

        #region Animator

        static AnimatorController BuildController()
        {
            string path = OutputRoot + "MagicianAnimator.controller";
            AssetDatabase.DeleteAsset(path);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter("Aiming", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Cast", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("Dead", AnimatorControllerParameterType.Bool);

            var sm = controller.layers[0].stateMachine;

            var idle = sm.AddState("Idle");
            idle.motion = LoadClip(IdleFbx);

            var cast = sm.AddState("Cast");
            cast.motion = LoadClip(CastFbx);

            var death = sm.AddState("Death");
            death.motion = LoadClip(DeathFbx);

            sm.defaultState = idle;

            // Idle <-> Cast on the trigger; Cast falls back to Idle when the clip finishes.
            var toCast = idle.AddTransition(cast);
            toCast.AddCondition(AnimatorConditionMode.If, 0f, "Cast");
            toCast.hasExitTime = false;
            toCast.duration = 0.1f;

            var castAgain = cast.AddTransition(cast);
            castAgain.AddCondition(AnimatorConditionMode.If, 0f, "Cast");
            castAgain.hasExitTime = false;
            castAgain.duration = 0.1f;
            castAgain.canTransitionToSelf = true;

            var backToIdle = cast.AddTransition(idle);
            backToIdle.hasExitTime = true;
            backToIdle.exitTime = 0.85f;
            backToIdle.duration = 0.2f;

            // Any state -> Death so it dies mid-cast too. The animator is disabled by the ragdoll
            // an instant later, but the death pose gives the ragdoll a natural starting shape.
            var toDeath = sm.AddAnyStateTransition(death);
            toDeath.AddCondition(AnimatorConditionMode.If, 0f, "Dead");
            toDeath.hasExitTime = false;
            toDeath.duration = 0.05f;
            toDeath.canTransitionToSelf = false;

            EditorUtility.SetDirty(controller);
            return controller;
        }

        #endregion

        #region Magician prefab

        static void BuildMagicianPrefab()
        {
            var controller = BuildController();
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterFbx);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(source);
            PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            root.name = "Magician";

            try
            {
                int enemyLayer = LayerMask.NameToLayer("Enemy");
                if (enemyLayer >= 0)
                    SetLayerRecursively(root, enemyLayer);
                root.tag = "Enemy";

                var animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var health = root.AddComponent<ZombieHealth>();
                health.Configure(MaxHealth, HeadshotMultiplier);

                root.AddComponent<ZombieHealthBar>();
                root.AddComponent<MagicianRagdoll>();

                var brain = root.AddComponent<MagicianEnemy>();
                brain.Configure(DetectRadius, CastCooldown, ProjectileDamage, ProjectileSpeed);

                BuildBodyHitbox(root, health, enemyLayer);
                BuildHeadHitbox(root, animator, health, enemyLayer);

                string path = OutputRoot + "Magician.prefab";
                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void BuildBodyHitbox(GameObject root, ZombieHealth health, int enemyLayer)
        {
            var go = new GameObject("BodyHitbox");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(0f, ColliderHeight * 0.5f, 0f);
            go.layer = enemyLayer >= 0 ? enemyLayer : 0;
            go.tag = "BodyShot";

            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;
            capsule.height = ColliderHeight;
            capsule.radius = ColliderRadius;
            capsule.direction = 1;

            var relay = go.AddComponent<ZombieHitboxRelay>();
            relay.Root = health;
        }

        static void BuildHeadHitbox(GameObject root, Animator animator, ZombieHealth health, int enemyLayer)
        {
            var head = animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (head == null)
            {
                Debug.LogWarning("[CollarCali] Magician: no humanoid head bone, skipping headshot hitbox.");
                return;
            }

            var go = new GameObject("HeadHitbox");
            go.transform.SetParent(head, false);
            go.transform.localPosition = new Vector3(0f, 0.1f, 0f);
            go.layer = enemyLayer >= 0 ? enemyLayer : 0;
            go.tag = "Critical";

            var sphere = go.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = HeadHitboxRadius;

            var relay = go.AddComponent<ZombieHitboxRelay>();
            relay.Root = health;
            relay.ForceHeadshot = true;
        }

        #endregion

        #region Projectile prefabs

        /// <summary>
        /// Wraps the pack's pure-VFX projectile, muzzle and hit prefabs into the three Resources
        /// prefabs MagicProjectileFx loads by name. The projectile wrapper nests the VFX under a
        /// mover-less root (MagicBolt is added at runtime), so swapping the source path re-skins
        /// the spell without touching code.
        /// </summary>
        static void BuildProjectilePrefabs()
        {
            WrapVfx(ProjectileSource, "MagicianProjectile", ProjectileScale);
            // Muzzle and hit are scaled to match so the whole spell reads at one size.
            WrapVfx(MuzzleSource, "MagicianMuzzle", ProjectileScale);
            WrapVfx(HitSource, "MagicianHit", ProjectileScale);
        }

        static void WrapVfx(string sourcePath, string outputName, float scale)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null)
            {
                Debug.LogWarning("[CollarCali] Projectile VFX missing: " + sourcePath);
                return;
            }

            var root = new GameObject(outputName);
            try
            {
                var vfx = (GameObject)PrefabUtility.InstantiatePrefab(source);
                vfx.transform.SetParent(root.transform, false);
                vfx.transform.localPosition = Vector3.zero;
                // Scaling the nested VFX (not the root) so MagicBolt's own transform still moves at
                // full speed while the particles just render smaller.
                vfx.transform.localScale = Vector3.one * scale;
                // Most Gabriel Aguiar projectiles are authored travelling down +Z already, which is
                // the forward MagicBolt flies, so no reorientation is needed.

                string path = OutputRoot + outputName + ".prefab";
                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        #endregion

        static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject, layer);
        }
    }
}
#endif
