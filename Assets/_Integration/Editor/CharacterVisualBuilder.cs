#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// One-shot editor wiring for the game's character visuals. Run
    /// <b>Tools ▸ CollarCali ▸ Build Character Visuals</b> after changing the character model.
    ///
    /// It does, in order and each step guarded on its own so one failure never aborts the rest:
    ///   1. Re-imports the character model as Humanoid and grabs the generated Avatar.
    ///   2. Builds Resources/CharacterSkins.asset.
    ///   3. Builds Resources/CharacterPreview.prefab for the lobby's rotating 3D preview.
    ///   4. Swaps the networked player's "Player Render" body (what other players see).
    ///   5. Swaps the Malbers Steve TPS mesh (visuals only; controls untouched).
    ///
    /// Everything that touches assets happens here in the editor with full Undo/import handling, so
    /// the risky rig + prefab work is verifiable in Unity rather than edited blind.
    ///
    /// NOW POINTED AT "omerfeozs - 3D Low Poly", which replaced the four Meshy characters. That model
    /// is a single mesh with a single material of its own, rather than one mesh with four skin
    /// variants, so the skin library is built from the model's own material and players are told apart
    /// by the per-player colour tint that FpsNetworkBridge already applies. The old Meshy model and
    /// the Character 1-4 prefabs are left on disk, unreferenced, so this is reversible by putting the
    /// old guid back.
    /// </summary>
    public static class CharacterVisualBuilder
    {
        // The character model every visual is built from.
        const string CharacterModelGuid = "342451a4fcae76e4b963e07b2a6f9794"; // omerfeozs - 3D Low Poly

        /// <summary>
        /// The model's own material. Used for the skin library so the character keeps its authored
        /// textures - without this the builder would paint a leftover Meshy skin over the new mesh.
        /// </summary>
        const string CharacterMaterialGuid = "29bca3b60d41e04469cb130ff1971ed3"; // Cube.001.mat


        // StarterAssetsThirdPerson.controller — used so the lobby preview plays an idle instead of a T-pose.
        const string ThirdPersonControllerGuid = "40db3173a05ae3242b1c182a09b0a183";

        const string NetworkedPlayerPath = "Assets/_Integration/Prefabs/NetworkedFPSPlayer.prefab";
        const string LocalDualPlayerResource = "Assets/_Integration/Resources/LocalDualPlayer.prefab";
        const string SkinLibraryPath = "Assets/_Integration/Resources/CharacterSkins.asset";
        const string PreviewPrefabPath = "Assets/_Integration/Resources/CharacterPreview.prefab";
        const string RemoteBodyChildName = "Player Render";

        static readonly string[] CharacterPrefabPaths =
        {
            "Assets/Characters/Character 1.prefab",
            "Assets/Characters/Character 2.prefab",
            "Assets/Characters/Character 3.prefab",
            "Assets/Characters/Character 4.prefab",
        };

        [MenuItem("Tools/CollarCali/Build Character Visuals")]
        public static void Build()
        {
            var log = new System.Text.StringBuilder();
            log.AppendLine("[CharacterVisualBuilder] Build started.");

            var (fbxPath, avatar) = EnsureHumanoid(log);
            if (string.IsNullOrEmpty(fbxPath))
            {
                Debug.LogError(log.ToString());
                EditorUtility.DisplayDialog("Build Character Visuals",
                    "Could not find the character model. Nothing was changed.\n\nSee the Console.",
                    "OK");
                return;
            }

            var skins = BuildSkinLibrary(log);
            SafeStep(log, "Character preview prefab", () => BuildPreviewPrefab(fbxPath, avatar, skins, log));
            SafeStep(log, "Networked player body", () => SwapRemoteBody(fbxPath, avatar, skins, log));
            SafeStep(log, "Malbers Steve mesh", () => SwapSteveMesh(fbxPath, avatar, log));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            log.AppendLine("[CharacterVisualBuilder] Build finished.");
            Debug.Log(log.ToString());
            EditorUtility.DisplayDialog("Build Character Visuals",
                "Done. Check the Console for details.\n\n" +
                "Next: open the character model's Rig tab, confirm the Avatar mapped cleanly (green), " +
                "then play the Lobby and Game scenes to verify.",
                "OK");
        }

        static void SafeStep(System.Text.StringBuilder log, string label, System.Action step)
        {
            try
            {
                step();
            }
            catch (System.Exception e)
            {
                log.AppendLine($"  ! {label} FAILED: {e.Message}");
                Debug.LogException(e);
            }
        }

        // ---- 1. Humanoid conversion -------------------------------------------------------------

        static (string fbxPath, Avatar avatar) EnsureHumanoid(System.Text.StringBuilder log)
        {
            var fbxPath = AssetDatabase.GUIDToAssetPath(CharacterModelGuid);
            if (string.IsNullOrEmpty(fbxPath))
            {
                log.AppendLine("  ! Character model guid not found in project.");
                return (null, null);
            }

            log.AppendLine($"  Character model: {fbxPath}");
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
            {
                log.AppendLine("  ! Model importer missing.");
                return (fbxPath, null);
            }

            bool needsReimport = false;
            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                needsReimport = true;
                log.AppendLine("  Set rig to Humanoid (Create From This Model).");
            }

            if (importer.optimizeGameObjects)
            {
                // Keep bones as real transforms so skinning + any bone lookups resolve.
                importer.optimizeGameObjects = false;
                needsReimport = true;
                log.AppendLine("  Disabled Optimize Game Objects (bones kept exposed).");
            }

            if (needsReimport)
                importer.SaveAndReimport();

            var avatar = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null)
                log.AppendLine("  ! No Avatar generated. Open the model's Rig tab and configure it.");
            else if (!avatar.isValid)
                log.AppendLine("  ! Avatar generated but is NOT valid - configure it in the Rig tab.");
            else
                log.AppendLine("  Avatar OK: " + avatar.name);

            return (fbxPath, avatar);
        }

        // ---- 2. Skin library --------------------------------------------------------------------

        static CharacterSkinLibrary BuildSkinLibrary(System.Text.StringBuilder log)
        {
            var lib = AssetDatabase.LoadAssetAtPath<CharacterSkinLibrary>(SkinLibraryPath);
            if (lib == null)
            {
                lib = ScriptableObject.CreateInstance<CharacterSkinLibrary>();
                System.IO.Directory.CreateDirectory("Assets/_Integration/Resources");
                AssetDatabase.CreateAsset(lib, SkinLibraryPath);
                log.AppendLine("  Created " + SkinLibraryPath);
            }

            lib.skins.Clear();

            var fallbackTints = new[]
            {
                new Color(0.85f, 0.85f, 0.88f),
                new Color(0.90f, 0.45f, 0.35f),
                new Color(0.40f, 0.70f, 0.95f),
                new Color(0.55f, 0.85f, 0.50f),
            };

            // One character now, with its own material, so the library holds a single skin. Players
            // are still told apart: FpsNetworkBridge tints each body by its networked colour index.
            //
            // This runs before the legacy scan below and returns early, so the old four skins are
            // only consulted if this material has gone missing.
            var ownMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                AssetDatabase.GUIDToAssetPath(CharacterMaterialGuid));
            if (ownMaterial != null)
            {
                var tint = fallbackTints[0];
                if (ownMaterial.HasProperty("_BaseColor"))
                    tint = ownMaterial.GetColor("_BaseColor");
                else if (ownMaterial.HasProperty("_Color"))
                    tint = ownMaterial.GetColor("_Color");

                lib.skins.Add(new CharacterSkinLibrary.Skin
                {
                    displayName = "omerfeozs",
                    material = ownMaterial,
                    uiTint = tint,
                });

                log.AppendLine("  Skin: the character's own material (" + ownMaterial.name + ").");
                EditorUtility.SetDirty(lib);
                return lib;
            }

            log.AppendLine("  ! The character material was not found; falling back to the old " +
                           "Character prefabs. Check CharacterMaterialGuid.");

            for (int i = 0; i < CharacterPrefabPaths.Length; i++)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPrefabPaths[i]);
                if (go == null)
                    continue;

                var smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
                var mat = smr != null ? smr.sharedMaterial : null;
                if (mat == null)
                    continue;

                var tint = fallbackTints[i % fallbackTints.Length];
                if (mat.HasProperty("_BaseColor"))
                    tint = mat.GetColor("_BaseColor");
                else if (mat.HasProperty("_Color"))
                    tint = mat.GetColor("_Color");

                lib.skins.Add(new CharacterSkinLibrary.Skin
                {
                    displayName = "Character " + (i + 1),
                    material = mat,
                    uiTint = tint,
                });
            }

            if (lib.skins.Count == 0)
                log.AppendLine("  ! No skins found on the Character prefabs.");
            else
                log.AppendLine("  Skins: " + string.Join(", ", lib.skins.Select(s => s.displayName)));

            EditorUtility.SetDirty(lib);
            return lib;
        }

        // ---- 3. Lobby preview prefab ------------------------------------------------------------

        static void BuildPreviewPrefab(string fbxPath, Avatar avatar, CharacterSkinLibrary skins,
            System.Text.StringBuilder log)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null)
                return;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            try
            {
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
                instance.name = "CharacterPreview";
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;

                // Idle animation for the preview: reuse the third-person controller + character avatar.
                var anim = instance.GetComponent<Animator>();
                if (anim == null)
                    anim = instance.AddComponent<Animator>();
                anim.applyRootMotion = false;
                if (avatar != null)
                    anim.avatar = avatar;
                var controllerPath = AssetDatabase.GUIDToAssetPath(ThirdPersonControllerGuid);
                if (!string.IsNullOrEmpty(controllerPath))
                    anim.runtimeAnimatorController =
                        AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);

                if (skins != null && skins.Count > 0 && skins.Get(0).material != null)
                    ApplySkinMaterial(instance, skins.Get(0).material);

                PrefabUtility.SaveAsPrefabAsset(instance, PreviewPrefabPath);
                log.AppendLine("  Wrote " + PreviewPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        // ---- 4. Networked remote body -----------------------------------------------------------

        static void SwapRemoteBody(string fbxPath, Avatar avatar, CharacterSkinLibrary skins,
            System.Text.StringBuilder log)
        {
            var root = PrefabUtility.LoadPrefabContents(NetworkedPlayerPath);
            try
            {
                var playerRender = FindDeep(root.transform, RemoteBodyChildName);
                if (playerRender == null)
                {
                    log.AppendLine("  ! '" + RemoteBodyChildName + "' not found on networked player.");
                    return;
                }

                // Remote body carries no logic - only the MaleModel mesh/armature - so clearing it is safe.
                for (int i = playerRender.childCount - 1; i >= 0; i--)
                    Object.DestroyImmediate(playerRender.GetChild(i).gameObject);

                var visual = InstantiateCharacterVisual(fbxPath, playerRender, skins, avatar);

                // The imported pivot is not at the soles, so parenting at the root's feet buries the body
                // to the waist (only the jump lifted it into view). Drop the visual so its lowest point
                // sits exactly on the root origin - i.e. the player's feet.
                float lift = AlignBottomToParentY(visual, playerRender);
                log.AppendLine("  Remote body foot-aligned (lifted " + lift.ToString("0.###") + "m).");

                // The root Animator drives the body via humanoid retargeting, so it must point at the
                // character avatar for the new bones to be found.
                var rootAnimator = root.GetComponent<Animator>();
                if (rootAnimator != null && avatar != null)
                    rootAnimator.avatar = avatar;

                PrefabUtility.SaveAsPrefabAsset(root, NetworkedPlayerPath);
                log.AppendLine("  Networked player body swapped to the new character (" +
                    (visual != null ? visual.name : "null") + "); root Animator avatar set.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ---- 5. Malbers Steve TPS mesh ----------------------------------------------------------

        static void SwapSteveMesh(string fbxPath, Avatar avatar, System.Text.StringBuilder log)
        {
            var stevePath = ResolveStevePrefabPath(log);
            if (string.IsNullOrEmpty(stevePath))
                return;

            var root = PrefabUtility.LoadPrefabContents(stevePath);
            try
            {
                var anim = root.GetComponentInChildren<Animator>(true);
                if (anim == null)
                {
                    log.AppendLine("  ! Steve has no Animator - skipped.");
                    return;
                }

                // Additive-and-hide (rather than delete) so Malbers' component/bone references on the
                // original rig never dangle. The Animator retargets onto the new skeleton once its
                // avatar is set; the old mesh is just switched off.
                foreach (var oldSmr in anim.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (oldSmr != null)
                        oldSmr.enabled = false;

                var visual = InstantiateCharacterVisual(fbxPath, anim.transform, skins: null, avatar: null);
                visual.name = "CharacterVisual";

                if (avatar != null)
                    anim.avatar = avatar;

                PrefabUtility.SaveAsPrefabAsset(root, stevePath);
                log.AppendLine("  Steve mesh swapped to the new character (old renderers hidden); avatar set. Path: " +
                    stevePath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static string ResolveStevePrefabPath(System.Text.StringBuilder log)
        {
            var local = AssetDatabase.LoadAssetAtPath<GameObject>(LocalDualPlayerResource);
            if (local == null)
            {
                log.AppendLine("  ! LocalDualPlayer resource not found; cannot resolve Steve.");
                return null;
            }

            var dual = local.GetComponent("DualPlayerController");
            if (dual == null)
            {
                log.AppendLine("  ! DualPlayerController missing on LocalDualPlayer.");
                return null;
            }

            var so = new SerializedObject(dual);
            var prop = so.FindProperty("stevePrefab");
            var steve = prop != null ? prop.objectReferenceValue as GameObject : null;
            if (steve == null)
            {
                log.AppendLine("  ! stevePrefab not assigned on LocalDualPlayer.");
                return null;
            }

            return AssetDatabase.GetAssetPath(steve);
        }

        // ---- shared helpers ---------------------------------------------------------------------

        /// <summary>
        /// Instantiates the character model, strips its own Animator (so only the parent rig drives it),
        /// zeroes its local transform, matches the parent's layer and optionally applies a skin.
        /// </summary>
        static GameObject InstantiateCharacterVisual(string fbxPath, Transform parent,
            CharacterSkinLibrary skins, Avatar avatar)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(fbx, parent);
            PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.Completely,
                InteractionMode.AutomatedAction);

            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;

            // The model FBX carries its own Animator; a nested Animator would fight the parent rig.
            var ownAnimator = visual.GetComponent<Animator>();
            if (ownAnimator != null)
                Object.DestroyImmediate(ownAnimator);

            // Deliberately leave the imported layer (Default) alone: the remote body must render on
            // every other player's camera, and forcing it onto the Player layer risks being culled.

            if (skins != null && skins.Count > 0 && skins.Get(0).material != null)
                ApplySkinMaterial(visual, skins.Get(0).material);

            return visual;
        }

        /// <summary>
        /// Shifts <paramref name="visual"/> vertically so the bottom of its combined renderer bounds
        /// rests on the parent's origin height. Returns the lift applied (metres). No-op if the bounds
        /// come back empty.
        /// </summary>
        static float AlignBottomToParentY(GameObject visual, Transform parent)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return 0f;

            bool has = false;
            Bounds bounds = default;
            foreach (var r in renderers)
            {
                if (r == null || r is TrailRenderer || r is LineRenderer)
                    continue;
                if (!has)
                {
                    bounds = r.bounds;
                    has = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            if (!has || bounds.size.y <= 0.0001f)
                return 0f;

            float delta = parent.position.y - bounds.min.y;
            visual.transform.position += new Vector3(0f, delta, 0f);
            return delta;
        }

        static void ApplySkinMaterial(GameObject root, Material material)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null)
                    continue;
                var mats = smr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    mats[i] = material;
                smr.sharedMaterials = mats;
            }
        }

        static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i), layer);
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
#endif
