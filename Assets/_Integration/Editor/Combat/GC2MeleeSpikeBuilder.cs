#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using CollarCali.Combat;
using GameCreator.Runtime.Characters;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds the GC2 Melee spike's player (third-person plan, phase 2):
    /// Assets/_Integration/Spikes/GC2Melee/PlayerCharacterGC2.prefab, a variant of PlayerCharacter
    /// with a Game Creator 2 Character on the root. Its five units are the passive Malbers ones
    /// (Assets/_Integration/Combat/GC2), so Malbers keeps the body and Game Creator only plays melee:
    /// the Character names the root Animator for its gestures and the model as its mannequin, its
    /// footsteps are off (Malbers has its own), and MAnimalGC2Bridge passes melee moves to Malbers.
    ///
    /// PlayerCharacter itself stays free of Game Creator, so every check compares the two. Running
    /// it again changes nothing that is already right.
    /// </summary>
    public static class GC2MeleeSpikeBuilder
    {
        public const string Folder = "Assets/_Integration/Spikes/GC2Melee";
        public const string PlayerPath = Folder + "/PlayerCharacterGC2.prefab";

        [MenuItem(CharacterFitCheck.MenuRoot + "GC2 Melee Spike/Build Spike Player")]
        static void BuildFromMenu()
        {
            string log = Build(out bool ok);
            Debug.Log("[GC2Spike] " + (ok ? "OK" : "FAILED") + "\n" + log);
            EditorUtility.DisplayDialog("GC2 Melee Spike", (ok ? "Built " + PlayerPath : "Failed") + "\n\n" + log, "OK");
        }

        public static void BuildBatch()
        {
            string log = Build(out bool ok);
            Debug.Log("[GC2Spike] " + (ok ? "OK" : "FAILED") + "\n" + log);
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

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath) == null)
            {
                PlayerRigAssets.EnsureFolder(Folder);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                try
                {
                    instance.name = Path.GetFileNameWithoutExtension(PlayerPath);
                    PrefabUtility.SaveAsPrefabAsset(instance, PlayerPath);
                    sb.AppendLine("Created " + PlayerPath + " as a variant of PlayerCharacter.");
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }

            var root = PrefabUtility.LoadPrefabContents(PlayerPath);
            try
            {
                Configure(root, sb);
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPath);
                sb.AppendLine("Saved " + PlayerPath + ".");
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

            return sb.ToString();
        }

        static void Configure(GameObject root, StringBuilder sb)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null)
                throw new InvalidOperationException("The player has no Animator on its root.");

            Transform visual = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == PlayerCharacterBuilder.VisualName)
                {
                    visual = t;
                    break;
                }
            }

            var character = root.GetComponent<Character>();
            if (character == null)
                character = root.AddComponent<Character>();

            var so = new SerializedObject(character);
            so.FindProperty("m_IsPlayer").boolValue = true;
            int units = 0;
            units += Unit<UnitPlayerMAnimal>(so, "m_Kernel.m_Player");
            units += Unit<UnitMotionMAnimal>(so, "m_Kernel.m_Motion");
            units += Unit<UnitDriverMAnimal>(so, "m_Kernel.m_Driver");
            units += Unit<UnitFacingMAnimal>(so, "m_Kernel.m_Facing");
            units += Unit<UnitAnimimMAnimal>(so, "m_Kernel.m_Animim");
            so.ApplyModifiedPropertiesWithoutUndo();

            // Fields inside the new units only exist once they are applied.
            so.Update();
            so.FindProperty("m_Kernel.m_Animim.m_Animator").objectReferenceValue = animator;
            so.FindProperty("m_Kernel.m_Animim.m_Mannequin").objectReferenceValue = visual;
            var feet = so.FindProperty("m_Footsteps.m_Feet");
            if (feet != null)
                feet.arraySize = 0;
            so.ApplyModifiedPropertiesWithoutUndo();

            var bridge = root.GetComponent<MAnimalGC2Bridge>();
            if (bridge == null)
                bridge = root.AddComponent<MAnimalGC2Bridge>();

            // -gc2Wiring Stock|NoIKPass picks the graph wiring for an experiment; otherwise it is kept.
            string wiring = CharacterFitCheck.ArgValue("-gc2Wiring");
            if (!string.IsNullOrEmpty(wiring) && Enum.TryParse(wiring, true, out MAnimalGC2Bridge.GraphWiring chosen))
            {
                var bridgeSo = new SerializedObject(bridge);
                bridgeSo.FindProperty("m_Wiring").enumValueIndex = (int)chosen;
                bridgeSo.ApplyModifiedPropertiesWithoutUndo();
            }

            sb.AppendLine($"  Game Creator Character on the root: player, {units} of 5 units replaced with the passive " +
                          $"Malbers ones; animator {animator.name}, mannequin {(visual != null ? visual.name : "none")}, " +
                          $"footsteps off. MAnimalGC2Bridge, graph wiring {bridge.Wiring}.");

            // Spike input, to try the team sword in play mode: equip at start, swing on the left button.
            var input = root.GetComponent<CollarCali.Spikes.GC2SpikeSwordInput>();
            if (input == null)
                input = root.AddComponent<CollarCali.Spikes.GC2SpikeSwordInput>();
            var sword = AssetDatabase.LoadAssetAtPath<GameCreator.Runtime.Melee.MeleeWeapon>(MeleeContentBuilder.SwordWeaponPath);
            var inputSo = new SerializedObject(input);
            inputSo.FindProperty("m_Weapon").objectReferenceValue = sword;
            inputSo.ApplyModifiedPropertiesWithoutUndo();
            sb.AppendLine("  GC2SpikeSwordInput: " + (sword != null
                ? "equips the team sword at start and swings it on the left mouse button."
                : "no team sword yet - run Build Team Melee Content, then build this again."));
        }

        /// <summary>Puts a <typeparamref name="T"/> in the unit slot unless one is there. 1 if it changed.</summary>
        static int Unit<T>(SerializedObject so, string path) where T : new()
        {
            var slot = so.FindProperty(path);
            if (slot == null)
                throw new InvalidOperationException("Game Creator's Character has no " + path + ".");
            if (slot.managedReferenceValue is T)
                return 0;
            slot.managedReferenceValue = new T();
            return 1;
        }
    }
}
#endif
