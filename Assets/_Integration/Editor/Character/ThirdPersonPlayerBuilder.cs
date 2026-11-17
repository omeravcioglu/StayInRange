#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds the third-person-only player (phase 3 of the third-person plan):
    /// Characters/Player/ThirdPersonPlayer.prefab, a variant of PlayerCharacter - Malbers' own human
    /// controller with the team's body - and ThirdPersonPlayer on its root. It fights with Malbers'
    /// own weapons and combos; Game Creator 2 melee is not on it (two animation systems on one body did
    /// not work out), and a build removes it from a prefab made before. ThirdPersonPlayer makes its
    /// camera rig from PlayerCameraRig (CM3) when play starts and holsters Malbers' sword. Drop the
    /// prefab into a scene with a floor to play it offline.
    ///
    /// Also makes Resources/PlayerRigRecipe, which decides whether networked players use it. It starts
    /// switched off; tick Use Third Person Player on the asset to try it in a session. Running this
    /// again changes nothing that is already right, and never touches that switch.
    /// </summary>
    public static class ThirdPersonPlayerBuilder
    {
        public const string PrefabPath = PlayerRigAssets.Folder + "/ThirdPersonPlayer.prefab";
        public const string RecipePath = "Assets/_Integration/Resources/PlayerRigRecipe.asset";
        public const string RemoteBodyControllerPath = PlayerRigAssets.Folder + "/Remote Body.controller";
        const string MalbersWeaponsFolder = "Assets/Malbers Animations/Common/Prefabs/Weapons";

        [MenuItem(CharacterFitCheck.MenuRoot + "Build Third Person Player")]
        static void BuildFromMenu()
        {
            string log = Build(out bool ok);
            Debug.Log("[ThirdPersonPlayer] " + (ok ? "OK" : "FAILED") + "\n" + log);
            EditorUtility.DisplayDialog("Build Third Person Player", (ok ? "Built " + PrefabPath : "Failed") + "\n\n" + log, "OK");
        }

        /// <summary>Batch entry point: -executeMethod CollarCali.EditorTools.ThirdPersonPlayerBuilder.BuildBatch</summary>
        public static void BuildBatch()
        {
            string log = Build(out bool ok);
            Debug.Log("[ThirdPersonPlayer] " + (ok ? "OK" : "FAILED") + "\n" + log);
        }

        public static string Build(out bool ok)
        {
            ok = false;
            var sb = new StringBuilder();
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerRigAssets.CharacterPath);
            if (basePrefab == null)
            {
                sb.AppendLine("Build the player first: " + PlayerRigAssets.CharacterPath + " is missing.");
                return sb.ToString();
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                try
                {
                    instance.name = Path.GetFileNameWithoutExtension(PrefabPath);
                    PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                    sb.AppendLine("Created " + PrefabPath + " as a variant of PlayerCharacter.");
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                RemoveGameCreator(root, sb);
                ConfigurePlayer(root, sb);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                sb.AppendLine("Saved " + PrefabPath + ".");
                ok = true;
            }
            catch (Exception e)
            {
                sb.AppendLine("! " + e.GetType().Name + ": " + e.Message);
                Debug.LogException(e);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            if (ok)
                ok = BuildRecipe(sb);
            return sb.ToString();
        }

        /// <summary>
        /// Game Creator 2 melee came off the player (the spike's Character, MAnimalGC2Bridge and its
        /// test input). Found by type name, so this builds with or without Game Creator installed.
        /// </summary>
        static void RemoveGameCreator(GameObject root, StringBuilder sb)
        {
            string[] names =
            {
                "CollarCali.Spikes.GC2SpikeSwordInput",
                "CollarCali.Combat.MAnimalGC2Bridge",
                "GameCreator.Runtime.Characters.Character",
            };

            var removed = new System.Collections.Generic.List<string>();
            foreach (string name in names)
            {
                foreach (var component in root.GetComponents<Component>())
                {
                    if (component == null || component.GetType().FullName != name)
                        continue;
                    removed.Add(component.GetType().Name);
                    Object.DestroyImmediate(component, true);
                }
            }

            if (removed.Count > 0)
                sb.AppendLine("  Removed Game Creator melee: " + string.Join(", ", removed) + ".");
        }

        static void ConfigurePlayer(GameObject root, StringBuilder sb)
        {
            var player = root.GetComponent<ThirdPersonPlayer>();
            if (player == null)
                player = root.AddComponent<ThirdPersonPlayer>();

            var rig = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerRigAssets.CameraRigPath);
            var sword = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(ThirdPersonPlayer.MalbersSwordGuid));
            int enemy = LayerMask.NameToLayer("Enemy");
            var so = new SerializedObject(player);
            so.FindProperty("cameraRigPrefab").objectReferenceValue = rig;
            so.FindProperty("startingWeapon").objectReferenceValue = sword;
            so.FindProperty("meleeHitLayers").intValue = enemy >= 0 ? 1 << enemy : 0;
            so.ApplyModifiedPropertiesWithoutUndo();

            sb.AppendLine("  ThirdPersonPlayer: camera rig " + (rig != null ? rig.name : "MISSING - build the player camera rig") +
                          ", starting weapon " + (sword != null ? "Malbers' " + sword.name : "none - Malbers' Sword prefab is missing") +
                          ", melee hits " + (enemy >= 0 ? "the Enemy layer" : "nothing yet - the project has no Enemy layer") + ".");
        }

        static bool BuildRecipe(StringBuilder sb)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var player = prefab != null ? prefab.GetComponent<ThirdPersonPlayer>() : null;
            if (player == null)
            {
                sb.AppendLine("! " + PrefabPath + " has no ThirdPersonPlayer on its root.");
                return false;
            }

            var recipe = AssetDatabase.LoadAssetAtPath<PlayerRigRecipe>(RecipePath);
            if (recipe == null)
            {
                PlayerRigAssets.EnsureFolder(Path.GetDirectoryName(RecipePath)?.Replace('\\', '/'));
                recipe = ScriptableObject.CreateInstance<PlayerRigRecipe>();
                recipe.thirdPersonPlayer = player;
                AssetDatabase.CreateAsset(recipe, RecipePath);
                sb.AppendLine("Created " + RecipePath + ".");
            }
            else if (recipe.thirdPersonPlayer != player)
            {
                recipe.thirdPersonPlayer = player;
                EditorUtility.SetDirty(recipe);
            }

            var remote = RemoteBodyController(prefab, sb);
            if (remote != null && recipe.remoteBodyController != remote)
            {
                recipe.remoteBodyController = remote;
                EditorUtility.SetDirty(recipe);
            }

            FillRemoteWeapons(recipe, sb);
            CopyCollar(prefab, recipe, sb);

            AssetDatabase.SaveAssets();
            sb.AppendLine("  Networked players use " + (recipe.useThirdPersonPlayer
                ? "ThirdPersonPlayer (Use Third Person Player is ticked on " + RecipePath + ")."
                : "DualPlayerController. Tick Use Third Person Player on " + RecipePath + " to try the new player in a session."));
            return true;
        }

        /// <summary>
        /// What the other players' copies of this player animate with: the character's own Malbers
        /// controller, copied and stripped of every StateMachineBehaviour, so a copy plays the animation
        /// its owner publishes and nothing else - attacks, effects and state logic belong to the owner's
        /// character (PlayerNetworkBridge.RemoteBody). Copied again only when the source changes; the
        /// copy keeps its GUID.
        /// </summary>
        static RuntimeAnimatorController RemoteBodyController(GameObject prefab, StringBuilder sb)
        {
            var animator = prefab.GetComponent<Animator>();
            var source = animator != null ? animator.runtimeAnimatorController : null;
            if (source is AnimatorOverrideController overrides)
                source = overrides.runtimeAnimatorController;
            string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            if (string.IsNullOrEmpty(sourcePath) || !sourcePath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("  ! The character has no Animator controller asset: other players' copies keep the old locomotion animation.");
                return null;
            }

            string stamp = Hash128.Compute(File.ReadAllBytes(sourcePath)) + "|" + sourcePath;
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(RemoteBodyControllerPath);
            var importer = existing != null ? AssetImporter.GetAtPath(RemoteBodyControllerPath) : null;
            if (existing != null && importer != null && importer.userData == stamp)
            {
                sb.AppendLine($"  Remote body: {RemoteBodyControllerPath} is up to date with {source.name}.");
                return existing;
            }

            // Over the old copy's file, so its .meta - and the GUID the recipe points at - stay.
            File.Copy(sourcePath, RemoteBodyControllerPath, true);
            AssetDatabase.ImportAsset(RemoteBodyControllerPath, ImportAssetOptions.ForceUpdate);
            var copy = AssetDatabase.LoadAssetAtPath<AnimatorController>(RemoteBodyControllerPath);
            if (copy == null)
            {
                sb.AppendLine("  ! Could not copy " + sourcePath + " for the other players' copies.");
                return null;
            }

            int stripped = 0;
            foreach (var layer in copy.layers)
                stripped += StripBehaviours(layer.stateMachine);
            EditorUtility.SetDirty(copy);
            AssetDatabase.SaveAssets();

            importer = AssetImporter.GetAtPath(RemoteBodyControllerPath);
            importer.userData = stamp;
            importer.SaveAndReimport();
            copy = AssetDatabase.LoadAssetAtPath<AnimatorController>(RemoteBodyControllerPath);
            sb.AppendLine($"  Remote body: {RemoteBodyControllerPath}, {source.name} without its {stripped} behaviours " +
                          $"({copy.parameters.Length} parameters, {copy.layers.Length} layers).");
            return copy;
        }

        /// <summary>
        /// The weapons the other players can see on a third-person player (PlayerNetworkBridge.RemoteWeapons):
        /// every MWeapon prefab under Malbers' Common/Prefabs/Weapons, after any already listed. The
        /// order only ever grows at the end, because every machine looks weapons up by their place in it.
        /// </summary>
        static void FillRemoteWeapons(PlayerRigRecipe recipe, StringBuilder sb)
        {
            var list = new List<GameObject>();
            if (recipe.remoteWeapons != null)
            {
                foreach (var weapon in recipe.remoteWeapons)
                {
                    if (weapon != null && !list.Contains(weapon))
                        list.Add(weapon);
                }
            }

            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { MalbersWeaponsFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal);
            int added = 0;
            foreach (var path in paths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponent<MalbersAnimations.Weapons.MWeapon>() == null || list.Contains(prefab))
                    continue;
                list.Add(prefab);
                added++;
            }

            if (recipe.remoteWeapons == null || !recipe.remoteWeapons.SequenceEqual(list))
            {
                recipe.remoteWeapons = list.ToArray();
                EditorUtility.SetDirty(recipe);
            }

            sb.AppendLine($"  Weapons the other players can see: {list.Count} ({added} new): {string.Join(", ", list.Select(w => w.name))}.");
        }

        /// <summary>
        /// The collar PlayerCharacterBuilder fitted on the character's neck, into the recipe: the model and
        /// where it sits against the neck bone. The body the other players see is the same model, so the
        /// network bridge puts the same collar at the same place on it.
        /// </summary>
        static void CopyCollar(GameObject prefab, PlayerRigRecipe recipe, StringBuilder sb)
        {
            var animator = prefab.GetComponent<Animator>();
            var neck = animator != null && animator.avatar != null && animator.avatar.isHuman ? FindNeck(prefab.transform, animator.avatar) : null;
            var collar = neck != null ? neck.Find(PlayerCharacterBuilder.CollarName) : null;
            // The collar model itself (the FBX), not the copy of it inside PlayerCharacter.prefab.
            var model = collar != null ? PrefabUtility.GetCorrespondingObjectFromOriginalSource(collar.gameObject) : null;
            if (collar == null || model == null)
            {
                if (recipe.collarPrefab != null)
                {
                    recipe.collarPrefab = null;
                    EditorUtility.SetDirty(recipe);
                }

                sb.AppendLine("  No collar on the character's neck: the body the other players see has none either.");
                return;
            }

            bool changed = recipe.collarPrefab != model || recipe.collarPosition != collar.localPosition ||
                           recipe.collarRotation != collar.localRotation || !Mathf.Approximately(recipe.collarScale, collar.localScale.x);
            if (changed)
            {
                recipe.collarPrefab = model;
                recipe.collarPosition = collar.localPosition;
                recipe.collarRotation = collar.localRotation;
                recipe.collarScale = collar.localScale.x;
                EditorUtility.SetDirty(recipe);
            }

            sb.AppendLine($"  Collar for the other players' view: {model.name} on the neck bone, scale {collar.localScale.x:0.000}.");
        }

        static Transform FindNeck(Transform root, Avatar avatar)
        {
            string bone = null;
            foreach (var human in avatar.humanDescription.human)
            {
                if (human.humanName == "Neck")
                    bone = human.boneName;
            }

            if (string.IsNullOrEmpty(bone))
                return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == bone)
                    return t;
            }

            return null;
        }

        static int StripBehaviours(AnimatorStateMachine machine)
        {
            if (machine == null)
                return 0;

            int count = 0;
            var own = machine.behaviours;
            machine.behaviours = Array.Empty<StateMachineBehaviour>();
            foreach (var behaviour in own)
            {
                if (behaviour == null)
                    continue;
                Object.DestroyImmediate(behaviour, true);
                count++;
            }

            foreach (var child in machine.states)
            {
                var state = child.state;
                if (state == null)
                    continue;
                var behaviours = state.behaviours;
                state.behaviours = Array.Empty<StateMachineBehaviour>();
                foreach (var behaviour in behaviours)
                {
                    if (behaviour == null)
                        continue;
                    Object.DestroyImmediate(behaviour, true);
                    count++;
                }
            }

            foreach (var child in machine.stateMachines)
                count += StripBehaviours(child.stateMachine);
            return count;
        }
    }
}
#endif
