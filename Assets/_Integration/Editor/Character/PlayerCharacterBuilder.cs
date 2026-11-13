#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MalbersAnimations.Controller;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds the third-person player rig, Assets/_Integration/Characters/Player/PlayerCharacter.prefab,
    /// from the body profile. It is a variant of Malbers' Human Player (which carries no body of its
    /// own), so Malbers' updates still reach it, and it replaces the transitional Steve Player once the
    /// controller moves over (phases 3-4 of the third-person plan). Running it again rewrites the same
    /// overrides; nothing else in the project changes.
    ///
    /// What it does:
    ///   1. Carries over the tuning the team gave Steve Player (animator speed, gravity, slide, combo,
    ///      input and mode settings), but nothing tied to Steve's own skeleton.
    ///   2. Nests the profile's model, as a prefab instance, under Rotator/ModelOffset and names
    ///      ModelOffset MAnimal's root bone, so Malbers never re-parents the body at runtime. The root
    ///      Animator gets the model's avatar and always animates.
    ///   3. Adds the profile's sockets and HB_ hitboxes (triggers on the Animal layer) to the real bones.
    ///   4. Fits Malbers to the body: height, chest pivot, Rotator, the main, crouch and against-wall
    ///      capsules, and the ground layers (Malbers ships Default only).
    ///   5. Points the weapon equip points, the glide and arrow effects and the Player Head hook at the
    ///      sockets, and moves Malbers' bone attachments (attack triggers, holsters, footstep triggers,
    ///      carry point) from where they sat on Steve to the same place on this body.
    ///   6. Fits Malbers' look-at IK (the head follows the camera) to this model's neck and head bones.
    ///   7. Puts the camera target at the profile's height, standing and crouched.
    ///   8. Adds ThirdPersonJumpTuning, CharacterBodyCalibration, CharacterStateCalibration and
    ///      CharacterVisualInterpolator to the root.
    /// </summary>
    public static class PlayerCharacterBuilder
    {
        const string HumanPlayerGuid = "51089675ba972e5448cb7e8b015fc52c";
        const string StevePlayerPath = "Assets/Malbers Animations/Animal Controller/Human/Steve Player.prefab";
        const string SteveModelPath = "Assets/Malbers Animations/Animal Controller/Human/Model/Steve_v2.fbx";

        public const string OffsetName = "ModelOffset";
        public const string VisualName = "CharacterVisual";
        const string SocketPrefix = "Socket ";
        const int AnimalLayer = 20;

        [MenuItem(CharacterFitCheck.MenuRoot + "Build Player Character (and camera rig)")]
        static void BuildFromMenu()
        {
            string log = BuildAll(out bool ok);
            Debug.Log(log);
            EditorUtility.DisplayDialog("Build Player Character", (ok ? "Done.\n\n" : "Did not finish.\n\n") + log, "OK");
        }

        /// <summary>Batch entry point: -executeMethod CollarCali.EditorTools.PlayerCharacterBuilder.BuildBatch</summary>
        public static void BuildBatch()
        {
            string log = BuildAll(out bool ok);
            Debug.Log("[PlayerCharacter] " + (ok ? "OK" : "FAILED") + "\n" + log);
        }

        static string BuildAll(out bool ok)
        {
            string character = Build(out bool characterOk);
            string camera = PlayerCameraRigBuilder.Build(out bool cameraOk);
            ok = characterOk && cameraOk;
            return character + "Camera rig:\n" + camera;
        }

        public static string Build(out bool ok)
        {
            ok = false;
            var sb = new StringBuilder();
            var profile = PlayerRigAssets.LoadOrCreateProfile(sb);
            var model = profile.model;
            string modelPath = model != null ? AssetDatabase.GetAssetPath(model) : null;
            var avatar = string.IsNullOrEmpty(modelPath) ? null : AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                sb.AppendLine("The profile's model has no valid humanoid avatar.");
                return sb.ToString();
            }

            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(HumanPlayerGuid));
            if (basePrefab == null)
            {
                sb.AppendLine("Malbers' Human Player was not found (guid " + HumanPlayerGuid + ").");
                return sb.ToString();
            }

            string path = PlayerRigAssets.CharacterPath;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                PlayerRigAssets.EnsureFolder(PlayerRigAssets.Folder);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                try
                {
                    instance.name = Path.GetFileNameWithoutExtension(path);
                    PrefabUtility.SaveAsPrefabAsset(instance, path);
                    sb.AppendLine("Created " + path + " as a variant of Malbers' Human Player.");
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                CopySteveTuning(root, sb);  // first: it re-merges the instance

                var visual = Body(root, profile, model, avatar, sb);
                var bones = BonesOf(visual.transform, avatar);
                MeasureClimbing(root.transform, profile, bones, sb);
                var sockets = Sockets(root.transform, profile, bones, sb);
                Hitboxes(root.transform, profile, bones, sb);
                Calibrate(root, profile, visual.transform.parent, sb);
                Repoint(root, profile, sockets, sb);
                RetargetAttachments(root.transform, bones, sb);
                LookAt(root, avatar, bones, sb);
                CameraTarget(root.transform, profile, sb);
                Components(root, profile, visual.transform, sb);

                PrefabUtility.SaveAsPrefabAsset(root, path);
                sb.AppendLine("Saved " + path + ".");
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

            AssetDatabase.SaveAssets();
            return sb.ToString();
        }

        // ---- 1. Steve's tuning ---------------------------------------------------------------------

        /// <summary>
        /// Copies Steve Player's overrides of Human Player onto this variant (both are variants of it, so
        /// the same targets apply), except anything placed on, or pointing at, Steve's own objects: his
        /// skeleton, colliders, hooks and equip points, which this builder makes for the new body.
        /// Overrides this variant already has win.
        /// </summary>
        static void CopySteveTuning(GameObject root, StringBuilder sb)
        {
            string stevePath = File.Exists(StevePlayerPath) ? StevePlayerPath : ProjectPaths.Asset("Steve Player", "Prefab");
            if (string.IsNullOrEmpty(stevePath))
            {
                sb.AppendLine("  ! Steve Player not found: its tuning was not carried over.");
                return;
            }

            var steve = PrefabUtility.LoadPrefabContents(stevePath);
            try
            {
                var theirs = PrefabUtility.GetPropertyModifications(steve);
                if (theirs == null || theirs.Length == 0)
                    return;

                var ourBySource = new Dictionary<Object, Object>();
                foreach (var o in AllObjects(root))
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(o);
                    if (source != null && !ourBySource.ContainsKey(source))
                        ourBySource[source] = o;
                }

                var ours = (PrefabUtility.GetPropertyModifications(root) ?? Array.Empty<PropertyModification>()).ToList();
                var have = new HashSet<(Object, string)>(ours.Select(m => (m.target, m.propertyPath)));
                var dropped = new HashSet<(Object, string)>();
                var kept = new List<PropertyModification>();
                foreach (var m in theirs)
                {
                    if (m.target == null || BelongsToBody(m))
                    {
                        NoteArray(m, dropped);
                        continue;
                    }

                    var reference = m.objectReference;
                    if (reference != null && (!EditorUtility.IsPersistent(reference) || AssetDatabase.GetAssetPath(reference) == stevePath))
                    {
                        // One of Steve Player's own objects: fine if it is one of Human Player's, which we have too.
                        var source = PrefabUtility.GetCorrespondingObjectFromSource(reference);
                        if (source == null || !ourBySource.TryGetValue(source, out var mine))
                        {
                            NoteArray(m, dropped);
                            continue;
                        }

                        m.objectReference = mine;
                    }

                    kept.Add(m);
                }

                // An array that lost an element keeps its own length.
                kept.RemoveAll(m => m.propertyPath.EndsWith(".Array.size", StringComparison.Ordinal) &&
                                    dropped.Contains((m.target, m.propertyPath.Substring(0, m.propertyPath.Length - ".Array.size".Length))));

                int added = 0;
                foreach (var m in kept)
                {
                    if (have.Add((m.target, m.propertyPath)))
                    {
                        ours.Add(m);
                        added++;
                    }
                }

                if (added > 0)
                    PrefabUtility.SetPropertyModifications(root, ours.ToArray());
                sb.AppendLine($"  Steve Player's tuning: {added} new of {theirs.Length} overrides carried over " +
                              $"({theirs.Length - kept.Count} left behind with his skeleton, colliders and hooks).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(steve);
            }
        }

        /// <summary>Overrides that place, name or wire Steve's body, which the builder sets for this one.</summary>
        static bool BelongsToBody(PropertyModification m)
        {
            if (m.target is Transform || m.target is GameObject)
                return true;

            string p = m.propertyPath;
            return p == "m_Avatar" || p == "RootBone" || p == "Reference" || p == "LeftHandEquipPoint" || p == "RightHandEquipPoint" ||
                   p.StartsWith("colliders", StringComparison.Ordinal) ||
                   p.StartsWith("internalComponents", StringComparison.Ordinal) ||
                   p.StartsWith("GlobalTargets", StringComparison.Ordinal) ||
                   p.StartsWith("sets", StringComparison.Ordinal) ||
                   p.StartsWith("LocalPos", StringComparison.Ordinal) || p.StartsWith("LocalRot", StringComparison.Ordinal) ||
                   (p.StartsWith("Effects.Array.data[", StringComparison.Ordinal) && p.EndsWith("].root", StringComparison.Ordinal));
        }

        static void NoteArray(PropertyModification m, HashSet<(Object, string)> arrays)
        {
            int i = m.propertyPath.IndexOf(".Array.data[", StringComparison.Ordinal);
            if (i > 0)
                arrays.Add((m.target, m.propertyPath.Substring(0, i)));
        }

        static IEnumerable<Object> AllObjects(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                yield return t.gameObject;
                foreach (var c in t.GetComponents<Component>())
                    if (c != null)
                        yield return c;
            }
        }

        // ---- 2. The body -----------------------------------------------------------------------------

        static GameObject Body(GameObject root, CharacterBodyProfile profile, GameObject model, Avatar avatar, StringBuilder sb)
        {
            var animal = root.GetComponent<MAnimal>();
            var rotator = animal != null && animal.Rotator != null ? animal.Rotator : root.transform.Find("Rotator");
            if (rotator == null)
                throw new InvalidOperationException("Human Player has no Rotator.");

            rotator.localPosition = new Vector3(0f, profile.rotatorHeight, 0f);
            PlayerRigAssets.Record(rotator);

            var offset = rotator.Find(OffsetName);
            if (offset == null)
            {
                offset = new GameObject(OffsetName).transform;
                offset.SetParent(rotator, false);
            }

            offset.gameObject.layer = rotator.gameObject.layer;
            offset.SetLocalPositionAndRotation(new Vector3(0f, -profile.rotatorHeight, 0f), Quaternion.identity);
            offset.localScale = Vector3.one;

            var existing = offset.Find(VisualName);
            if (existing != null && PrefabUtility.GetCorrespondingObjectFromSource(existing.gameObject) != model)
            {
                Object.DestroyImmediate(existing.gameObject);
                existing = null;
            }

            GameObject visual;
            if (existing == null)
            {
                visual = (GameObject)PrefabUtility.InstantiatePrefab(model, offset);
                visual.name = VisualName;
                sb.AppendLine($"  Nested {model.name} under Rotator/{OffsetName}/{VisualName}.");
            }
            else
            {
                visual = existing.gameObject;
            }

            visual.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            visual.transform.localScale = Vector3.one;

            // The model's own Animator would fight the rig's.
            var own = visual.GetComponent<Animator>();
            if (own != null)
                Object.DestroyImmediate(own);

            var animator = root.GetComponent<Animator>();
            animator.avatar = avatar;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            PlayerRigAssets.Record(animator);
            return visual;
        }

        /// <summary>The model's humanoid bones, found by the avatar's bone names under <paramref name="modelRoot"/> only.</summary>
        static Dictionary<HumanBodyBones, Transform> BonesOf(Transform modelRoot, Avatar avatar)
        {
            var byName = new Dictionary<string, Transform>();
            foreach (var t in modelRoot.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name))
                    byName[t.name] = t;

            var names = HumanTrait.BoneName;
            var bones = new Dictionary<HumanBodyBones, Transform>();
            foreach (var human in avatar.humanDescription.human)
            {
                int index = Array.IndexOf(names, human.humanName);
                if (index >= 0 && index < (int)HumanBodyBones.LastBone && byName.TryGetValue(human.boneName, out var t))
                    bones[(HumanBodyBones)index] = t;
            }

            return bones;
        }

        // ---- 3. Sockets and hitboxes ----------------------------------------------------------------

        static Dictionary<string, Transform> Sockets(Transform root, CharacterBodyProfile profile,
            Dictionary<HumanBodyBones, Transform> bones, StringBuilder sb)
        {
            var wanted = new HashSet<string>(profile.sockets.Where(s => s != null && !string.IsNullOrEmpty(s.name)).Select(s => s.name));
            RemoveStale(bones.Values, n => n.StartsWith(SocketPrefix, StringComparison.Ordinal) && !wanted.Contains(n));

            var made = new Dictionary<string, Transform>();
            foreach (var s in profile.sockets)
            {
                if (s == null || string.IsNullOrEmpty(s.name))
                    continue;
                if (!bones.TryGetValue(s.bone, out var bone))
                {
                    sb.AppendLine($"  ! Socket {s.name}: the model has no {s.bone}.");
                    continue;
                }

                var position = bone.position;
                if (s.toward != HumanBodyBones.LastBone && bones.TryGetValue(s.toward, out var other))
                    position = Vector3.Lerp(bone.position, other.position, s.along);
                position += root.TransformVector(s.offset);

                var socket = Child(bone, s.name);
                socket.SetPositionAndRotation(position, root.rotation);
                socket.gameObject.layer = AnimalLayer;
                made[s.name] = socket;
            }

            sb.AppendLine($"  Sockets: {string.Join(", ", made.Keys)}.");
            return made;
        }

        static void Hitboxes(Transform root, CharacterBodyProfile profile, Dictionary<HumanBodyBones, Transform> bones, StringBuilder sb)
        {
            var wanted = new HashSet<string>(profile.hitboxes.Where(h => h != null && !string.IsNullOrEmpty(h.name)).Select(h => h.name));
            RemoveStale(bones.Values, n => n.StartsWith(CharacterFitCheck.HitboxPrefix, StringComparison.Ordinal) && !wanted.Contains(n));

            var made = new List<string>();
            foreach (var h in profile.hitboxes)
            {
                if (h == null || string.IsNullOrEmpty(h.name))
                    continue;
                if (!bones.TryGetValue(h.from, out var from) || !bones.TryGetValue(h.to, out var to))
                {
                    sb.AppendLine($"  ! Hitbox {h.name}: the model lacks {h.from} or {h.to}.");
                    continue;
                }

                var box = Child(from, h.name);
                box.gameObject.layer = AnimalLayer;
                var shift = root.TransformVector(h.offset);
                if (from == to)
                {
                    box.SetPositionAndRotation(from.position + shift, root.rotation);
                    Remove<CapsuleCollider>(box);
                    var sphere = GetOrAdd<SphereCollider>(box);
                    sphere.isTrigger = true;
                    sphere.center = Vector3.zero;
                    sphere.radius = h.radius / Scale(box);
                }
                else
                {
                    var axis = to.position - from.position;
                    float length = axis.magnitude;
                    axis /= Mathf.Max(length, 1e-5f);
                    var start = from.position - axis * h.extend;
                    var end = to.position + axis * h.extend;
                    var forward = Vector3.ProjectOnPlane(root.forward, axis);
                    if (forward.sqrMagnitude < 1e-6f)
                        forward = Vector3.ProjectOnPlane(root.up, axis);
                    box.SetPositionAndRotation((start + end) * 0.5f + shift, Quaternion.LookRotation(forward.normalized, axis));
                    Remove<SphereCollider>(box);
                    var capsule = GetOrAdd<CapsuleCollider>(box);
                    capsule.isTrigger = true;
                    capsule.direction = 1;
                    capsule.center = Vector3.zero;
                    float s = Scale(box);
                    capsule.radius = h.radius / s;
                    capsule.height = (Vector3.Distance(start, end) + 2f * h.radius) / s;
                }

                made.Add(h.name);
            }

            sb.AppendLine($"  Hitboxes (triggers, Animal layer): {string.Join(", ", made)}.");
        }

        static float Scale(Transform t) => Mathf.Max(Mathf.Abs(t.lossyScale.x), 1e-5f);

        // ---- 4. Malbers fitted to the body ----------------------------------------------------------

        static void Calibrate(GameObject root, CharacterBodyProfile profile, Transform modelOffset, StringBuilder sb)
        {
            var animal = root.GetComponent<MAnimal>();
            var so = new SerializedObject(animal);
            so.FindProperty("RootBone").objectReferenceValue = modelOffset;
            so.FindProperty("height").floatValue = profile.height;

            var pivots = so.FindProperty("pivots");
            for (int i = 0; pivots != null && i < pivots.arraySize; i++)
            {
                var pivot = pivots.GetArrayElementAtIndex(i);
                string name = pivot.FindPropertyRelative("name").stringValue;
                if (name == "Chest" || name == "Water")
                    pivot.FindPropertyRelative("position").vector3Value = new Vector3(0f, profile.chestPivot, 0f);
            }

            SetLayers(so, "groundLayer", profile.surfaces);

            var stances = so.FindProperty("Stances");
            var fitted = new List<string>();
            for (int i = 0; stances != null && i < stances.arraySize; i++)
            {
                var stance = stances.GetArrayElementAtIndex(i);
                var id = stance.FindPropertyRelative("ID").objectReferenceValue;
                string name = id != null ? id.name : string.Empty;
                if (name.Contains("Crouch"))
                    SetStanceCapsule(stance, profile.crouchCapsule);
                else if (name.Contains("Against Wall"))
                    SetStanceCapsule(stance, profile.againstWallCapsule);
                else
                    continue;
                fitted.Add(name);
            }

            so.ApplyModifiedPropertiesWithoutUndo();

            var capsule = animal.MainCollider != null ? animal.MainCollider : root.GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                capsule.direction = 1;
                capsule.radius = profile.capsule.radius;
                capsule.height = profile.capsule.height;
                capsule.center = new Vector3(0f, profile.capsule.centre, 0f);
                PlayerRigAssets.Record(capsule);
            }

            var steps = FindComponent(root, "StepsManager");
            if (steps != null)
            {
                var stepsSo = new SerializedObject(steps);
                SetLayers(stepsSo, "GroundLayer", profile.surfaces);
                stepsSo.ApplyModifiedPropertiesWithoutUndo();
            }

            sb.AppendLine($"  Malbers: height {profile.height}, chest pivot {profile.chestPivot}, Rotator {profile.rotatorHeight}, " +
                          $"capsule {profile.capsule.Bottom:0.00}..{profile.capsule.Top:0.00} m r {profile.capsule.radius}; " +
                          $"stance capsules: {string.Join(", ", fitted)}; ground and steps on {PlayerCameraRigBuilder.LayerNames(profile.surfaces)}.");
        }

        static void SetStanceCapsule(SerializedProperty stance, CharacterBodyProfile.Capsule shape)
        {
            stance.FindPropertyRelative("OverrideCapsule").boolValue = true;
            var capsule = stance.FindPropertyRelative("newCapsule");
            capsule.FindPropertyRelative("center").vector3Value = new Vector3(0f, shape.centre, 0f);
            capsule.FindPropertyRelative("height").floatValue = shape.height;
            capsule.FindPropertyRelative("radius").floatValue = shape.radius;
            var modify = capsule.FindPropertyRelative("modify");
            if (modify != null)
                modify.intValue |= 28; // centre, height and radius
        }

        static void SetLayers(SerializedObject so, string reference, LayerMask mask)
        {
            var useConstant = so.FindProperty(reference + ".UseConstant");
            var constant = so.FindProperty(reference + ".ConstantValue");
            if (useConstant == null || constant == null)
                return;
            useConstant.boolValue = true;
            constant.intValue = mask.value;
        }

        // ---- 5. Malbers' links into the body -------------------------------------------------------

        static void Repoint(GameObject root, CharacterBodyProfile profile, Dictionary<string, Transform> sockets, StringBuilder sb)
        {
            Transform Socket(string name) => sockets.TryGetValue(name, out var t) ? t : null;
            var done = new List<string>();

            var weapons = FindComponent(root, "MWeaponManager");
            if (weapons != null)
            {
                var so = new SerializedObject(weapons);
                so.FindProperty("LeftHandEquipPoint").objectReferenceValue = Socket("Socket Hand L");
                so.FindProperty("RightHandEquipPoint").objectReferenceValue = Socket("Socket Hand R");
                so.ApplyModifiedPropertiesWithoutUndo();
                done.Add("hand equip points");
            }

            var effects = FindComponent(root, "EffectManager");
            if (effects != null)
            {
                var so = new SerializedObject(effects);
                var list = so.FindProperty("Effects");
                for (int i = 0; list != null && i < list.arraySize; i++)
                {
                    var effect = list.GetArrayElementAtIndex(i);
                    string name = effect.FindPropertyRelative("Name").stringValue;
                    var socket = name == "Glide Wings" ? Socket("Socket Chest") : name == "Arrow In Hand" ? Socket("Socket Hand R") : null;
                    if (socket == null)
                        continue;
                    effect.FindPropertyRelative("root").objectReferenceValue = socket;
                    done.Add(name);
                }

                so.ApplyModifiedPropertiesWithoutUndo();
            }

            foreach (var hook in FindComponents(root, "TransformHook"))
            {
                var so = new SerializedObject(hook);
                var asset = so.FindProperty("Hook")?.objectReferenceValue;
                if (asset == null)
                    continue;
                if (asset.name == "Player Head")
                {
                    so.FindProperty("Reference").objectReferenceValue = Socket("Socket Head");
                    so.ApplyModifiedPropertiesWithoutUndo();
                    done.Add("Player Head hook");
                }
            }

            // Malbers' own spelling. The Player hook marks the chest, where enemies aim.
            var hooks = FindDeep(root.transform, "Trasform Hooks");
            if (hooks != null)
            {
                hooks.localPosition = new Vector3(hooks.localPosition.x, profile.chestPivot, hooks.localPosition.z);
                PlayerRigAssets.Record(hooks);
                done.Add("Player hook at the chest");
            }

            sb.AppendLine("  Pointed at the sockets: " + string.Join(", ", done) + ".");
        }

        /// <summary>
        /// Malbers' HumanoidParent objects (attack triggers, holsters, footstep triggers, carry point)
        /// attach themselves to a bone when they wake, keeping where they stand. They stand where Malbers
        /// put them for Steve; this moves each by the difference between Steve's bone and the same bone
        /// on this body, so it keeps its place relative to the bone. Footstep triggers keep their height,
        /// at the soles.
        /// </summary>
        static void RetargetAttachments(Transform root, Dictionary<HumanBodyBones, Transform> ours, StringBuilder sb)
        {
            var steve = SteveBones();
            if (steve == null)
            {
                sb.AppendLine("  ! Steve's model not found: Malbers' bone attachments were left where they were.");
                return;
            }

            int moved = 0;
            foreach (var component in FindComponents(root.gameObject, "HumanoidParent"))
            {
                var so = new SerializedObject(component);
                var localUse = so.FindProperty("LocalPos.UseConstant");
                var localValue = so.FindProperty("LocalPos.ConstantValue");
                if (localUse != null && localValue != null && localUse.boolValue && localValue.boolValue)
                    continue; // placed by its own offset from the bone

                var bone = (HumanBodyBones)so.FindProperty("parent").intValue;
                if (!ours.TryGetValue(bone, out var mine) || !steve.TryGetValue(bone, out var his))
                    continue;

                var t = component.transform;
                var source = PrefabUtility.GetCorrespondingObjectFromSource(t);
                var authored = source != null ? root.TransformPoint(source.root.InverseTransformPoint(source.position)) : t.position;
                var position = authored + (mine.position - root.TransformPoint(his));
                if (t.name.StartsWith("Track Trigger", StringComparison.Ordinal))
                    position.y = authored.y;
                t.position = position;
                PlayerRigAssets.Record(t);
                moved++;
            }

            sb.AppendLine($"  Moved {moved} of Malbers' bone attachments from Steve's bones to this body's.");
        }

        /// <summary>Steve's bones in his model's bind pose, relative to his feet; null if his model is missing.</summary>
        static Dictionary<HumanBodyBones, Vector3> SteveBones()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(SteveModelPath);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(SteveModelPath).OfType<Avatar>().FirstOrDefault();
            if (model == null || avatar == null)
                return null;

            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var steve = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                steve.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                return BonesOf(steve.transform, avatar).ToDictionary(p => p.Key, p => p.Value.position);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>
        /// Fills in the profile's climbing scales that are still 0, from this body against Steve's:
        /// chest and hip heights, and the reach of a raised arm (shoulder height plus arm length).
        /// </summary>
        static void MeasureClimbing(Transform root, CharacterBodyProfile profile, Dictionary<HumanBodyBones, Transform> ours, StringBuilder sb)
        {
            var climbing = profile.climbing;
            if (climbing.chestScale > 0f && climbing.hipScale > 0f && climbing.reachScale > 0f)
                return;

            var steve = SteveBones();
            if (steve == null)
            {
                sb.AppendLine("  ! Steve's model not found: the climbing scales stay unmeasured (1).");
                return;
            }

            var mine = ours.ToDictionary(p => p.Key, p => root.InverseTransformPoint(p.Value.position));
            var a = Heights(mine);
            var b = Heights(steve);
            if (climbing.chestScale <= 0f)
                climbing.chestScale = Round(a.chest / b.chest);
            if (climbing.hipScale <= 0f)
                climbing.hipScale = Round(a.hip / b.hip);
            if (climbing.reachScale <= 0f)
                climbing.reachScale = Round(a.reach / b.reach);
            EditorUtility.SetDirty(profile);
            sb.AppendLine($"  Body against Steve: chest {a.chest:0.00}/{b.chest:0.00} m, hips {a.hip:0.00}/{b.hip:0.00} m, " +
                          $"raised reach {a.reach:0.00}/{b.reach:0.00} m -> climbing scales chest {climbing.chestScale}, " +
                          $"hip {climbing.hipScale}, reach {climbing.reachScale}.");
        }

        static (float chest, float hip, float reach) Heights(Dictionary<HumanBodyBones, Vector3> bones)
        {
            Vector3 P(HumanBodyBones b) => bones.TryGetValue(b, out var p) ? p : Vector3.zero;
            float Length(HumanBodyBones x, HumanBodyBones y) =>
                bones.ContainsKey(x) && bones.ContainsKey(y) ? Vector3.Distance(P(x), P(y)) : 0f;

            float chest = bones.ContainsKey(HumanBodyBones.UpperChest) ? P(HumanBodyBones.UpperChest).y : P(HumanBodyBones.Chest).y;
            float hand = bones.ContainsKey(HumanBodyBones.RightMiddleProximal) ? Length(HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal) : 0.08f;
            float arm = Length(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm) +
                        Length(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand) + hand;
            return (chest, P(HumanBodyBones.Hips).y, P(HumanBodyBones.RightUpperArm).y + arm);
        }

        static float Round(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 1f : Mathf.Round(value * 1000f) / 1000f;

        // ---- 6. Look-at IK ---------------------------------------------------------------------------

        /// <summary>
        /// Malbers' IK manager turns the neck and head toward where the camera looks (set "Look At")
        /// and, with the camera in front, toward the camera (set "Look At Camera"). Each look-at turns
        /// its bone to LookRotation(aim) x Offset, where Offset is the turn from the bone's own axes to
        /// the way the face points - and Malbers' offsets are for Steve's bones.
        ///
        /// Here each one gets that turn for this model's bones, from the avatar's T-pose, in which the
        /// head looks straight ahead: the bone's rotation against the character there. The camera set's
        /// is the same turned half round, as Malbers' is. Its Neck and Head targets are set to the
        /// avatar's own Neck and Head bones: Malbers fills empty ones by name whenever the prefab is
        /// saved, and had put this model's bones under Steve's offsets, the head turned on its side.
        /// </summary>
        static void LookAt(GameObject root, Avatar avatar, Dictionary<HumanBodyBones, Transform> bones, StringBuilder sb)
        {
            var ik = FindComponent(root, "IKManager");
            if (ik == null)
            {
                sb.AppendLine("  ! No IK manager: the look-at was left as it is.");
                return;
            }

            bones.TryGetValue(HumanBodyBones.Neck, out var neck);
            bones.TryGetValue(HumanBodyBones.Head, out var head);
            Transform BoneFor(Object tag)
            {
                string name = tag != null ? tag.name.ToLowerInvariant() : "";
                return name.Contains("head") ? head : name.Contains("neck") ? neck : null;
            }

            var so = new SerializedObject(ik);
            int targets = 0, offsets = 0;

            var globals = so.FindProperty("GlobalTargets");
            for (int i = 0; globals != null && i < globals.arraySize; i++)
            {
                var entry = globals.GetArrayElementAtIndex(i);
                var bone = BoneFor(entry.FindPropertyRelative("ID")?.objectReferenceValue);
                var useConstant = entry.FindPropertyRelative("Value.UseConstant");
                var constant = entry.FindPropertyRelative("Value.ConstantValue");
                if (bone == null || useConstant == null || constant == null)
                    continue;
                useConstant.boolValue = true;
                constant.objectReferenceValue = bone;
                targets++;
            }

            var turnRound = Quaternion.Euler(0f, 180f, 0f);
            var sets = so.FindProperty("sets");
            for (int s = 0; sets != null && s < sets.arraySize; s++)
            {
                var set = sets.GetArrayElementAtIndex(s);
                string setName = set.FindPropertyRelative("name.ConstantValue")?.stringValue ?? "";
                bool facesCamera = setName.IndexOf("camera", StringComparison.OrdinalIgnoreCase) >= 0;
                var processors = set.FindPropertyRelative("IKProcesors");
                for (int p = 0; processors != null && p < processors.arraySize; p++)
                {
                    var processor = processors.GetArrayElementAtIndex(p);
                    if (!processor.managedReferenceFullTypename.EndsWith("IKGenericLookAt", StringComparison.Ordinal))
                        continue;

                    var bone = BoneFor(processor.FindPropertyRelative("Tag")?.objectReferenceValue);
                    var offset = processor.FindPropertyRelative("Offset");
                    if (bone == null || offset == null)
                        continue;

                    var straight = TPoseRotation(root.transform, bone, avatar);
                    offset.vector3Value = Rounded((facesCamera ? turnRound * straight : straight).eulerAngles);
                    offsets++;
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            sb.AppendLine($"  Look-at IK: {targets} targets on the neck and head, {offsets} offsets for this model's bones.");
        }

        /// <summary>
        /// The bone's rotation against the character root in the avatar's T-pose. Bones take their
        /// T-pose rotations; the objects above the model (Rotator, ModelOffset, the model's root) keep
        /// their own.
        /// </summary>
        public static Quaternion TPoseRotation(Transform root, Transform bone, Avatar avatar)
        {
            var tpose = new Dictionary<string, Quaternion>();
            foreach (var b in avatar.humanDescription.skeleton)
                tpose.TryAdd(b.name, b.rotation);

            var chain = new List<Transform>();
            for (var t = bone; t != null && t != root; t = t.parent)
                chain.Add(t);
            chain.Reverse();

            var rotation = Quaternion.identity;
            foreach (var t in chain)
                rotation *= tpose.TryGetValue(t.name, out var r) ? r : t.localRotation;
            return rotation;
        }

        /// <summary>Angles to a thousandth of a degree, so a rebuild writes the same numbers.</summary>
        static Vector3 Rounded(Vector3 angles)
        {
            static float R(float a)
            {
                a = Mathf.Round(a * 1000f) / 1000f;
                return a >= 360f ? a - 360f : a;
            }

            return new Vector3(R(angles.x), R(angles.y), R(angles.z));
        }

        // ---- 7. Camera target -----------------------------------------------------------------------

        /// <summary>
        /// Malbers' AnimalTracker eases the camera target (CM Main Target) to a height per stance,
        /// 1.5 m standing and 0.5 m crouched for Steve; both are scaled to the profile's target height,
        /// from Human Player's values so a rebuild never compounds.
        /// </summary>
        static void CameraTarget(Transform root, CharacterBodyProfile profile, StringBuilder sb)
        {
            var target = FindDeep(root, "CM Main Target");
            if (target == null)
            {
                sb.AppendLine("  ! No CM Main Target: the camera target height was not set.");
                return;
            }

            var shipped = PrefabUtility.GetCorrespondingObjectFromSource(target);
            float shippedHeight = shipped != null ? shipped.localPosition.y : 1.5f;
            float factor = profile.cameraRig.targetHeight / Mathf.Max(shippedHeight, 0.01f);
            target.localPosition = new Vector3(target.localPosition.x, profile.cameraRig.targetHeight, target.localPosition.z);
            PlayerRigAssets.Record(target);

            var heights = new List<string>();
            foreach (var tracker in FindComponents(root.gameObject, "AnimalTracker"))
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(tracker);
                var so = new SerializedObject(tracker);
                var shippedSo = source != null ? new SerializedObject(source) : null;
                var list = so.FindProperty("Trackers");
                var shippedList = shippedSo?.FindProperty("Trackers");
                for (int i = 0; list != null && i < list.arraySize; i++)
                {
                    var position = list.GetArrayElementAtIndex(i).FindPropertyRelative("Position");
                    var original = shippedList != null && i < shippedList.arraySize
                        ? shippedList.GetArrayElementAtIndex(i).FindPropertyRelative("Position").vector3Value
                        : position.vector3Value;
                    position.vector3Value = new Vector3(original.x, original.y * factor, original.z);
                    heights.Add($"{list.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue.Trim()} {original.y * factor:0.00} m");
                }

                so.ApplyModifiedPropertiesWithoutUndo();
            }

            sb.AppendLine($"  Camera target {profile.cameraRig.targetHeight} m; tracked heights: {string.Join(", ", heights)}.");
        }

        // ---- 8. Team components ---------------------------------------------------------------------

        static void Components(GameObject root, CharacterBodyProfile profile, Transform visual, StringBuilder sb)
        {
            GetOrAdd<ThirdPersonJumpTuning>(root.transform);
            GetOrAdd<CharacterBodyCalibration>(root.transform);

            var states = GetOrAdd<CharacterStateCalibration>(root.transform);
            states.profile = profile;
            PlayerRigAssets.Record(states);

            var smoother = GetOrAdd<CharacterVisualInterpolator>(root.transform);
            smoother.visual = visual;
            var core = root.transform.Find("Player Core");
            smoother.followers = core != null ? new[] { core } : Array.Empty<Transform>();
            PlayerRigAssets.Record(smoother);

            sb.AppendLine("  Root: ThirdPersonJumpTuning, CharacterBodyCalibration, CharacterStateCalibration, " +
                          "CharacterVisualInterpolator (smooths the body" + (core != null ? " and Player Core, the camera target's parent" : "") + ").");
        }

        // ---- helpers ----------------------------------------------------------------------------------

        static Transform Child(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name == name)
                    return parent.GetChild(i);

            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        static void RemoveStale(IEnumerable<Transform> parents, Func<string, bool> stale)
        {
            foreach (var parent in parents.Distinct())
            {
                for (int i = parent.childCount - 1; i >= 0; i--)
                {
                    var child = parent.GetChild(i);
                    if (!stale(child.name))
                        continue;
                    // Only what this builder added; never part of the model itself.
                    if (PrefabUtility.IsPartOfPrefabInstance(child.gameObject) && !PrefabUtility.IsAddedGameObjectOverride(child.gameObject))
                        continue;
                    Object.DestroyImmediate(child.gameObject);
                }
            }
        }

        static T GetOrAdd<T>(Transform t) where T : Component
        {
            var c = t.GetComponent<T>();
            return c != null ? c : t.gameObject.AddComponent<T>();
        }

        static void Remove<T>(Transform t) where T : Component
        {
            var c = t.GetComponent<T>();
            if (c != null)
                Object.DestroyImmediate(c);
        }

        static Component FindComponent(GameObject root, string typeName) => FindComponents(root, typeName).FirstOrDefault();

        static IEnumerable<Component> FindComponents(GameObject root, string typeName) =>
            root.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == typeName);

        static Transform FindDeep(Transform root, string name) =>
            root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
    }
}
#endif
