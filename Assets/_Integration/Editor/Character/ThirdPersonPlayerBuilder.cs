#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using GameCreator.Runtime.Melee;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds the third-person-only player (phase 3 of the third-person plan):
    /// Characters/Player/ThirdPersonPlayer.prefab, a variant of PlayerCharacter with Game Creator 2
    /// melee on it (the spike's passive Malbers units and MAnimalGC2Bridge) and ThirdPersonPlayer on
    /// its root. ThirdPersonPlayer makes its camera rig from PlayerCameraRig (CM3) when play starts and
    /// equips the team sword. Drop the prefab into a scene with a floor to play it offline.
    ///
    /// Also makes Resources/PlayerRigRecipe, which decides whether networked players use it. It starts
    /// switched off; tick Use Third Person Player on the asset to try it in a session. Running this
    /// again changes nothing that is already right, and never touches that switch.
    /// </summary>
    public static class ThirdPersonPlayerBuilder
    {
        public const string PrefabPath = PlayerRigAssets.Folder + "/ThirdPersonPlayer.prefab";
        public const string RecipePath = "Assets/_Integration/Resources/PlayerRigRecipe.asset";

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
                GC2MeleeSpikeBuilder.ConfigureGameCreator(root, sb);
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

        static void ConfigurePlayer(GameObject root, StringBuilder sb)
        {
            // The spike's test input never belongs on the real player.
            var spikeInput = root.GetComponent<CollarCali.Spikes.GC2SpikeSwordInput>();
            if (spikeInput != null)
                Object.DestroyImmediate(spikeInput, true);

            var player = root.GetComponent<ThirdPersonPlayer>();
            if (player == null)
                player = root.AddComponent<ThirdPersonPlayer>();

            var rig = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerRigAssets.CameraRigPath);
            var sword = AssetDatabase.LoadAssetAtPath<MeleeWeapon>(MeleeContentBuilder.SwordWeaponPath);
            var so = new SerializedObject(player);
            so.FindProperty("cameraRigPrefab").objectReferenceValue = rig;
            so.FindProperty("startingWeapon").objectReferenceValue = sword;
            so.ApplyModifiedPropertiesWithoutUndo();

            sb.AppendLine("  ThirdPersonPlayer: camera rig " + (rig != null ? rig.name : "MISSING - build the player camera rig") +
                          ", starting weapon " + (sword != null ? sword.name : "none - run Build Team Melee Content, then build this again") + ".");
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

            AssetDatabase.SaveAssets();
            sb.AppendLine("  Networked players use " + (recipe.useThirdPersonPlayer
                ? "ThirdPersonPlayer (Use Third Person Player is ticked on " + RecipePath + ")."
                : "DualPlayerController. Tick Use Third Person Player on " + RecipePath + " to try the new player in a session."));
            return true;
        }
    }
}
#endif
