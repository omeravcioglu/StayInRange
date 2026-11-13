#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CollarCali.Combat;
using GameCreator.Runtime.Melee;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds the team's copies of Game Creator's melee content (third-person plan, phase 2), under
    /// Assets/_Integration/Combat/GC2/Content. Game Creator's own assets stay as installed; each build
    /// starts again from them, keeping the copies' GUIDs.
    ///
    /// The sword (the game's main weapon):
    ///   - TeamSword.prefab: Game Creator's sword with its blade striker finding trigger hitboxes
    ///     (StrikerCapsuleTeam), and skipping the players' layers.
    ///   - Team skills: every skill of the sword's combos, with the hit pause (a global time-scale
    ///     change, which would slow every player in a co-op game) and the camera shake (Game Creator's
    ///     own camera, not our Cinemachine rig) taken out, and the team's damage added to On Hit
    ///     (InstructionTeamDealDamage).
    ///   - TeamSword_Combos: the sword's combos, played with the team skills.
    ///   - TeamSword_Weapon: the sword weapon with its own ID, no weapon state (Game Creator's sword
    ///     stance would take over the legs from Malbers' locomotion), the team combos, no slow-motion
    ///     dodge, and the prop held in this model's right hand the way Game Creator's mannequin holds
    ///     it: the grip offset is carried from the mannequin's hand to ours through both T-poses.
    /// </summary>
    public static class MeleeContentBuilder
    {
        public const string Folder = "Assets/_Integration/Combat/GC2/Content";
        public const string SwordFolder = Folder + "/Sword";
        public const string SwordWeaponPath = SwordFolder + "/TeamSword_Weapon.asset";
        const string SwordPropPath = SwordFolder + "/TeamSword.prefab";
        const string SwordCombosPath = SwordFolder + "/TeamSword_Combos.asset";
        const string SwordSkillsFolder = SwordFolder + "/Skills";

        /// <summary>Damage of one sword hit, and how much more a head takes. A zombie has 100 health.</summary>
        const float SwordDamage = 25f;
        const float CriticalMultiplier = 2f;

        /// <summary>Instructions taken out of every team skill and weapon.</summary>
        static readonly string[] Unwanted = { "InstructionCommonTimeScale", "InstructionCameraShakeBurst" };

        /// <summary>Layers the team's strikers skip: the players' bodies.</summary>
        static readonly string[] SkippedLayers = { "Animal", "Player", "Ignore Raycast", "UI" };

        [MenuItem(CharacterFitCheck.MenuRoot + "GC2 Melee Spike/Build Team Melee Content")]
        static void BuildFromMenu()
        {
            string log = Build(out bool ok);
            Debug.Log("[MeleeContent] " + (ok ? "OK" : "FAILED") + "\n" + log);
            EditorUtility.DisplayDialog("Team melee content", (ok ? "Built." : "Failed.") + "\n\n" + log, "OK");
        }

        public static void BuildBatch()
        {
            string log = Build(out bool ok);
            Debug.Log("[MeleeContent] " + (ok ? "OK" : "FAILED") + "\n" + log);
        }

        public static string Build(out bool ok)
        {
            ok = false;
            var sb = new StringBuilder();
            try
            {
                ok = BuildSword(sb);
            }
            catch (Exception e)
            {
                sb.AppendLine("! " + e.GetType().Name + ": " + e.Message);
                Debug.LogException(e);
            }

            AssetDatabase.SaveAssets();
            return sb.ToString();
        }

        // ---- The sword ------------------------------------------------------------------------------

        static bool BuildSword(StringBuilder sb)
        {
            var sourceWeapon = FindAsset<MeleeWeapon>("Sword_Weapon");
            if (sourceWeapon == null)
            {
                sb.AppendLine("Game Creator's Sword_Weapon is not installed (Melee.Sword).");
                return false;
            }

            PlayerRigAssets.EnsureFolder(SwordFolder);
            PlayerRigAssets.EnsureFolder(SwordSkillsFolder);

            var source = new SerializedObject(sourceWeapon);
            var sourceCombos = source.FindProperty("m_Combos.m_CombosAsset").objectReferenceValue;
            var equip = FindInstruction(source.FindProperty("m_OnEquip"), "InstructionCharacterAttachProp");
            var sourceProp = equip?.FindPropertyRelative("m_Prop.m_Property.m_GameObject")?.objectReferenceValue as GameObject;
            var sourceHandle = equip?.FindPropertyRelative("m_Handle.m_Handle")?.objectReferenceValue;
            if (sourceCombos == null || sourceProp == null)
            {
                sb.AppendLine("Sword_Weapon has no combos or no prop to copy.");
                return false;
            }

            // 1. The prop, its striker seeing hitboxes.
            var prop = CopyFresh<GameObject>(AssetDatabase.GetAssetPath(sourceProp), SwordPropPath);
            int strikers = PatchProp(SwordPropPath);
            prop = AssetDatabase.LoadAssetAtPath<GameObject>(SwordPropPath);
            sb.AppendLine($"  {Path.GetFileName(SwordPropPath)}: {strikers} striker(s) now see trigger hitboxes and skip " +
                          string.Join(", ", SkippedLayers) + ".");

            // 2. The skills of its combos.
            var skills = new Dictionary<Object, Object>();
            var comboSo = new SerializedObject(sourceCombos);
            foreach (var skill in References<Skill>(comboSo))
            {
                string path = SwordSkillsFolder + "/Team " + skill.name + ".asset";
                var copy = CopyFresh<Skill>(AssetDatabase.GetAssetPath(skill), path);
                PatchSkill(copy);
                skills[skill] = copy;
            }

            sb.AppendLine($"  {skills.Count} skills copied: hit pause and camera shake out, {SwordDamage:0} damage " +
                          $"(x{CriticalMultiplier:0} on the head) on hit.");

            // 3. The combos, playing the team skills.
            var combos = CopyFresh<Object>(AssetDatabase.GetAssetPath(sourceCombos), SwordCombosPath);
            int remapped = Remap(new SerializedObject(combos), skills);
            int targets = TargetOrSelf(new SerializedObject(combos));
            foreach (var copy in skills.Values)
                targets += TargetOrSelf(new SerializedObject(copy));
            sb.AppendLine($"  {Path.GetFileName(SwordCombosPath)}: {remapped} skill references point at the team skills; " +
                          $"{targets} target positions fall back to the character itself, not the world origin.");

            // 4. The weapon.
            var existing = AssetDatabase.LoadAssetAtPath<MeleeWeapon>(SwordWeaponPath);
            string id = existing != null ? new SerializedObject(existing).FindProperty("m_Id.m_SerializedID.m_String")?.stringValue : null;
            var weapon = CopyFresh<MeleeWeapon>(AssetDatabase.GetAssetPath(sourceWeapon), SwordWeaponPath);
            var so = new SerializedObject(weapon);
            so.FindProperty("m_Id.m_SerializedID.m_String").stringValue = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString() : id;
            var title = so.FindProperty("m_Title.m_Property.m_Value");
            if (title != null)
                title.stringValue = "Team Sword";

            so.FindProperty("m_State.m_StateType").enumValueIndex = 0; // AnimationClip, none: no weapon state
            so.FindProperty("m_State.m_AnimationClip").objectReferenceValue = null;
            so.FindProperty("m_State.m_State").objectReferenceValue = null;
            so.FindProperty("m_Combos.m_CombosAsset").objectReferenceValue = combos;

            var attach = FindInstruction(so.FindProperty("m_OnEquip"), "InstructionCharacterAttachProp");
            attach.FindPropertyRelative("m_Prop.m_Property.m_GameObject").objectReferenceValue = prop;
            string grip = HoldInRightHand(attach.FindPropertyRelative("m_Handle"), sourceHandle);
            var remove = FindInstruction(so.FindProperty("m_OnUnequip"), "InstructionCharacterRemoveProp");
            var removeProp = remove?.FindPropertyRelative("m_Prop.m_Property.m_GameObject");
            if (removeProp != null)
                removeProp.objectReferenceValue = prop;
            int dropped = RemoveUnwanted(so);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(weapon);

            sb.AppendLine($"  {Path.GetFileName(SwordWeaponPath)}: own ID, no weapon state, team combos, {dropped} " +
                          $"time-scale or camera-shake instruction(s) out; held {grip}.");
            return true;
        }

        /// <summary>The blade's striker shape swapped for the team's, which also finds trigger colliders.</summary>
        static int PatchProp(string path)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            int count = 0;
            try
            {
                root.name = Path.GetFileNameWithoutExtension(path);
                int skip = 0;
                foreach (string layer in SkippedLayers)
                {
                    int index = LayerMask.NameToLayer(layer);
                    if (index >= 0)
                        skip |= 1 << index;
                }

                foreach (var striker in root.GetComponentsInChildren<Striker>(true))
                {
                    var so = new SerializedObject(striker);
                    var shape = so.FindProperty("m_Shape");
                    string type = shape.managedReferenceFullTypename;
                    if (type.EndsWith("StrikerCapsule", StringComparison.Ordinal))
                    {
                        var position = shape.FindPropertyRelative("m_Position").vector3Value;
                        float height = shape.FindPropertyRelative("m_Height").floatValue;
                        float radius = shape.FindPropertyRelative("m_Radius").floatValue;
                        int direction = shape.FindPropertyRelative("m_Direction").enumValueIndex;
                        shape.managedReferenceValue = new StrikerCapsuleTeam(position, height, radius,
                            (StrikerCapsuleTeam.Direction)Mathf.Clamp(direction, 0, 2));
                    }
                    else if (type.EndsWith("StrikerSphere", StringComparison.Ordinal))
                    {
                        var position = shape.FindPropertyRelative("m_Position").vector3Value;
                        float radius = shape.FindPropertyRelative("m_Radius").floatValue;
                        shape.managedReferenceValue = new StrikerSphereTeam(position, radius);
                    }
                    else if (!type.EndsWith("Team", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    so.FindProperty("m_LayerMask").intValue = ~skip;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    count++;
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return count;
        }

        static void PatchSkill(Skill skill)
        {
            var so = new SerializedObject(skill);
            var hitPause = so.FindProperty("m_Effects.m_HitPause");
            if (hitPause != null)
                hitPause.boolValue = false;
            RemoveUnwanted(so);

            var onHit = so.FindProperty("m_OnHit.m_Instructions.m_Instructions");
            int at = onHit.arraySize;
            onHit.InsertArrayElementAtIndex(at);
            onHit.GetArrayElementAtIndex(at).managedReferenceValue = new InstructionTeamDealDamage(SwordDamage, CriticalMultiplier);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(skill);
        }

        /// <summary>
        /// Holds the prop in this model's right hand as Game Creator's mannequin holds it with
        /// <paramref name="sourceHandle"/>: the grip's turn and offset against the hand, carried from
        /// the mannequin's hand to ours through both avatars' T-poses, and the offset scaled by hand size.
        /// </summary>
        static string HoldInRightHand(SerializedProperty handle, Object sourceHandle)
        {
            var gripPosition = Vector3.zero;
            var gripAngles = Vector3.zero;
            if (sourceHandle != null)
            {
                var item = new SerializedObject(sourceHandle).FindProperty("m_Handles.m_Handles").GetArrayElementAtIndex(0);
                gripPosition = item.FindPropertyRelative("m_LocalPosition.m_Property.m_Position")?.vector3Value ?? Vector3.zero;
                gripAngles = item.FindPropertyRelative("m_LocalRotation.m_Property.m_Angles")?.vector3Value ?? Vector3.zero;
            }

            var mannequin = Hand(FindModel("Mannequin", "GameCreator"));
            var profile = PlayerRigAssets.LoadOrCreateProfile(new StringBuilder());
            var ours = Hand(profile != null ? profile.model : null);
            var position = gripPosition;
            var rotation = Quaternion.Euler(gripAngles);
            string how = "as Game Creator's mannequin holds it (no avatar to carry it over)";
            if (mannequin.ok && ours.ok)
            {
                var turn = Quaternion.Inverse(ours.rotation) * mannequin.rotation;
                float scale = mannequin.length > 1e-4f ? ours.length / mannequin.length : 1f;
                position = turn * gripPosition * scale;
                rotation = turn * rotation;
                how = $"in the right hand, the mannequin's grip carried over (hand {scale:0.00}x the size)";
            }

            handle.FindPropertyRelative("m_Type").enumValueIndex = 0; // a value, not Game Creator's handle asset
            handle.FindPropertyRelative("m_Bone.m_Type").enumValueIndex = 1; // a human bone
            handle.FindPropertyRelative("m_Bone.m_Human").intValue = (int)HumanBodyBones.RightHand;
            handle.FindPropertyRelative("m_LocalPosition").vector3Value = Round(position, 10000f);
            handle.FindPropertyRelative("m_LocalRotation").vector3Value = Round(rotation.eulerAngles, 1000f);
            handle.FindPropertyRelative("m_Handle").objectReferenceValue = null;
            return how;
        }

        /// <summary>A model's right hand in its avatar's T-pose: its rotation against the model, and its length.</summary>
        static (bool ok, Quaternion rotation, float length) Hand(GameObject model)
        {
            if (model == null)
                return (false, Quaternion.identity, 0f);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(model)).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isHuman)
                return (false, Quaternion.identity, 0f);

            Transform Bone(string humanName)
            {
                string boneName = avatar.humanDescription.human.FirstOrDefault(h => h.humanName == humanName).boneName;
                return string.IsNullOrEmpty(boneName)
                    ? null
                    : model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == boneName);
            }

            var hand = Bone("RightHand");
            var finger = Bone("Right Middle Proximal") ?? Bone("Right Index Proximal");
            if (hand == null)
                return (false, Quaternion.identity, 0f);

            var rotation = PlayerCharacterBuilder.TPoseRotation(null, hand, avatar);
            float length = finger != null ? Vector3.Distance(hand.position, finger.position) : 0f;
            return (true, rotation, length);
        }

        // ---- Helpers --------------------------------------------------------------------------------

        /// <summary>
        /// <paramref name="target"/> made a fresh copy of <paramref name="source"/>: a new asset the
        /// first time, then the source's content again on every build, keeping the copy's GUID.
        /// </summary>
        static T CopyFresh<T>(string source, string target) where T : Object
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(target) == null)
            {
                if (!AssetDatabase.CopyAsset(source, target))
                    throw new IOException("Could not copy " + source + " to " + target);
            }
            else
            {
                File.Copy(source, target, true);
                AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceUpdate);
            }

            var asset = AssetDatabase.LoadAssetAtPath<T>(target);
            if (asset == null)
                throw new IOException("Copied " + target + " but could not load it.");
            if (!(asset is GameObject))
                asset.name = Path.GetFileNameWithoutExtension(target);
            return asset;
        }

        static IEnumerable<T> References<T>(SerializedObject so) where T : Object
        {
            var seen = new HashSet<T>();
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue is T asset && seen.Add(asset))
                    yield return asset;
            }
        }

        static int Remap(SerializedObject so, Dictionary<Object, Object> map)
        {
            int count = 0;
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference || it.objectReferenceValue == null)
                    continue;
                if (map.TryGetValue(it.objectReferenceValue, out var copy))
                {
                    it.objectReferenceValue = copy;
                    count++;
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(so.targetObject);
            return count;
        }

        /// <summary>
        /// Game Creator's "Target Position" swapped for GetPositionTargetOrSelf everywhere in
        /// <paramref name="so"/>: with no target, Game Creator measures from the world origin.
        /// </summary>
        static int TargetOrSelf(SerializedObject so)
        {
            var paths = new List<string>();
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType == SerializedPropertyType.ManagedReference &&
                    (it.managedReferenceFullTypename ?? "").EndsWith(" GameCreator.Runtime.Common.GetPositionTarget", StringComparison.Ordinal))
                    paths.Add(it.propertyPath);
            }

            foreach (string path in paths)
                so.FindProperty(path).managedReferenceValue = new GetPositionTargetOrSelf();
            so.ApplyModifiedPropertiesWithoutUndo();
            if (paths.Count > 0)
                EditorUtility.SetDirty(so.targetObject);
            return paths.Count;
        }

        /// <summary>Takes the unwanted instructions out of every instruction list of <paramref name="so"/>.</summary>
        static int RemoveUnwanted(SerializedObject so)
        {
            var lists = new List<string>();
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.isArray && it.name == "m_Instructions" && it.propertyType == SerializedPropertyType.Generic)
                    lists.Add(it.propertyPath);
            }

            int removed = 0;
            foreach (string path in lists)
            {
                var list = so.FindProperty(path);
                for (int i = list.arraySize - 1; i >= 0; i--)
                {
                    string type = list.GetArrayElementAtIndex(i).managedReferenceFullTypename ?? "";
                    if (Unwanted.Any(u => type.EndsWith(u, StringComparison.Ordinal)))
                    {
                        list.DeleteArrayElementAtIndex(i);
                        removed++;
                    }
                }
            }

            return removed;
        }

        /// <summary>The first instruction of <paramref name="type"/> in an instruction list (…m_Instructions.m_Instructions).</summary>
        static SerializedProperty FindInstruction(SerializedProperty owner, string type)
        {
            var list = owner?.FindPropertyRelative("m_Instructions.m_Instructions");
            for (int i = 0; list != null && i < list.arraySize; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                if ((element.managedReferenceFullTypename ?? "").EndsWith(type, StringComparison.Ordinal))
                    return element;
            }

            return null;
        }

        static T FindAsset<T>(string name) where T : Object
        {
            foreach (string guid in AssetDatabase.FindAssets(name + " t:" + typeof(T).Name))
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null && asset.name == name)
                    return asset;
            }

            return null;
        }

        static GameObject FindModel(string name, string pathPart)
        {
            foreach (string guid in AssetDatabase.FindAssets(name + " t:Model"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains(pathPart) && Path.GetFileNameWithoutExtension(path) == name)
                    return AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }

            return null;
        }

        static Vector3 Round(Vector3 v, float precision) =>
            new Vector3(Mathf.Round(v.x * precision) / precision, Mathf.Round(v.y * precision) / precision,
                Mathf.Round(v.z * precision) / precision);
    }
}
#endif
