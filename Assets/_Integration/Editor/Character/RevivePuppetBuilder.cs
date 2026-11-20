#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Sets up the revive puppet: the player's body cut into six pieces (head, torso, two arms, two
    /// legs) with the one clip "Revive" - the dead player floats up, is pulled apart, snaps back
    /// together, comes alive and lands. It is made in Blender; the source and the scripts that
    /// generate it are in Characters/Player/Revive/Source~ (Unity skips folders ending in ~).
    ///
    /// The puppet is a stand-in, not the player: the real body is one welded skin and cannot come
    /// apart. Whoever plays the revive hides the real body, shows this prefab on the spot for the
    /// length of the clip, then swaps back. The clip starts limp, hanging a little above the ground,
    /// and ends in a standing idle with the feet on the puppet's own origin.
    ///
    /// What this does, and does again safely every time it runs:
    ///   - import settings of RevivePuppet.fbx: the player model's own scale, generic rig, one clip
    ///     that does not loop, no compression (the pop and the snaps are two frames long), and the
    ///     beats from RevivePuppet.beats.json as animation events (OnReviveBeat(name); nothing has
    ///     to listen);
    ///   - the body uses the player's own material; the cut faces get ReviveFlesh and ReviveBone,
    ///     made once and then left alone so they can be tuned by hand;
    ///   - RevivePuppet.controller (one state) and RevivePuppet.prefab (a variant of the model).
    /// The extra bones (master, tear.*, ghost.*, stay.*) are what lets a piece leave without its
    /// skin stretching; nothing in the game needs to touch them.
    /// </summary>
    public static class RevivePuppetBuilder
    {
        public const string Folder = PlayerRigAssets.Folder + "/Revive";
        public const string ModelPath = Folder + "/RevivePuppet.fbx";
        public const string BeatsPath = Folder + "/RevivePuppet.beats.json";
        public const string ControllerPath = Folder + "/RevivePuppet.controller";
        public const string PrefabPath = Folder + "/RevivePuppet.prefab";
        public const string FleshPath = Folder + "/ReviveFlesh.mat";
        public const string BonePath = Folder + "/ReviveBone.mat";
        public const string ClipName = "Revive";
        public const string BeatMethod = "OnReviveBeat";

        const string BodyMaterialName = "Cube.001";
        const string FleshName = "ReviveFlesh";
        const string BoneName = "ReviveBone";

        [Serializable]
        class Beat
        {
            public string name;
            public float time;
        }

        [Serializable]
        class BeatFile
        {
            public float fps = 30f;
            public float length = 8f;
            public Beat[] beats = new Beat[0];
        }

        [MenuItem(CharacterFitCheck.MenuRoot + "Build Revive Puppet")]
        static void BuildFromMenu()
        {
            string log = Build(out bool ok);
            Debug.Log(log);
            EditorUtility.DisplayDialog("Build Revive Puppet", (ok ? "Done.\n\n" : "Did not finish.\n\n") + log, "OK");
        }

        [MenuItem(CharacterFitCheck.MenuRoot + "Revive Puppet Pictures")]
        static void PicturesFromMenu()
        {
            string folder = Pictures(out string log);
            Debug.Log(log);
            if (folder != null)
                EditorUtility.RevealInFinder(Path.Combine(folder, "revive.png"));
        }

        /// <summary>
        /// Batch entry: -executeMethod CollarCali.EditorTools.RevivePuppetBuilder.RunBatch (no -quit, no
        /// -nographics: it draws pictures). Exit code 0 = built and drawn, 1 = not.
        /// </summary>
        public static void RunBatch()
        {
            int code = 1;
            try
            {
                string log = Build(out bool ok);
                Debug.Log("[RevivePuppet] " + (ok ? "OK" : "FAILED") + "\n" + log);
                if (ok)
                {
                    string folder = Pictures(out string pictureLog);
                    Debug.Log("[RevivePuppet] pictures: " + (folder ?? "none") + "\n" + pictureLog);
                    code = folder != null ? 0 : 1;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            EditorApplication.Exit(code);
        }

        public static string Build(out bool ok)
        {
            ok = false;
            var sb = new StringBuilder();
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null)
            {
                sb.AppendLine("No model at " + ModelPath + ". Export it from Blender first (Revive/Source~/README.md).");
                return sb.ToString();
            }

            // ---- materials
            var playerImporter = AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(CharacterAvatarFixer.ModelGuid)) as ModelImporter;
            Material bodyMaterial = null;
            if (playerImporter != null)
            {
                foreach (var pair in playerImporter.GetExternalObjectMap())
                {
                    if (pair.Key.type == typeof(Material) && pair.Value is Material material)
                        bodyMaterial = material;
                }
            }

            if (bodyMaterial == null)
                sb.AppendLine("! The player model names no material; the puppet's body keeps the one from its own file.");

            var flesh = LoadOrCreateMaterial(FleshPath, new Color(0.86f, 0.22f, 0.27f), 0.45f, sb);
            var bone = LoadOrCreateMaterial(BonePath, new Color(0.94f, 0.90f, 0.78f), 0.55f, sb);

            // ---- import settings
            importer.globalScale = playerImporter != null ? playerImporter.globalScale : 0.75f;
            importer.useFileScale = true;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.motionNodeName = string.Empty;
            importer.optimizeGameObjects = false;
            importer.importAnimation = true;
            importer.animationCompression = ModelImporterAnimationCompression.Off;
            importer.resampleCurves = true;
            importer.importBlendShapes = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.importConstraints = false;
            importer.isReadable = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            if (bodyMaterial != null)
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), BodyMaterialName), bodyMaterial);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), FleshName), flesh);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), BoneName), bone);

            var takes = importer.defaultClipAnimations;
            if (takes == null || takes.Length == 0)
            {
                sb.AppendLine("The model holds no animation.");
                return sb.ToString();
            }

            var take = takes[0];
            take.name = ClipName;
            take.loopTime = false;
            take.loopPose = false;
            take.wrapMode = WrapMode.ClampForever;
            float seconds = (take.lastFrame - take.firstFrame) / 30f;      // the Blender scene runs at 30 fps
            var beats = ReadBeats(sb);
            if (beats != null && beats.length > 0f)
                seconds = beats.length;
            take.events = beats == null
                ? new AnimationEvent[0]
                : beats.beats.Select(b => new AnimationEvent
                {
                    functionName = BeatMethod,
                    stringParameter = b.name,
                    // The importer keeps event times as a fraction of the clip. Just short of the end,
                    // so the last beat is not lost to rounding.
                    time = Mathf.Clamp(b.time / seconds, 0f, 0.995f),
                    messageOptions = SendMessageOptions.DontRequireReceiver,
                }).ToArray();
            importer.clipAnimations = new[] { take };
            importer.SaveAndReimport();

            // ---- what came in
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var clip = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<AnimationClip>()
                .FirstOrDefault(c => c.name == ClipName && (c.hideFlags & HideFlags.HideInHierarchy) == 0);
            if (model == null || clip == null)
            {
                sb.AppendLine("The model came in without its clip \"" + ClipName + "\".");
                return sb.ToString();
            }

            var skins = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            sb.AppendLine("Model: scale " + importer.globalScale.ToString("0.###") + ", " + skins.Length + " skinned meshes, "
                          + skins.Sum(s => s.sharedMesh != null ? s.sharedMesh.vertexCount : 0) + " vertices, "
                          + skins.Max(s => s.bones.Length) + " bones, root turned "
                          + Quaternion.Angle(Quaternion.identity, model.transform.rotation).ToString("0.#") + " deg.");
            foreach (var skin in skins)
                sb.AppendLine("  " + skin.name + ": " + string.Join(", ", skin.sharedMaterials.Select(m => m != null ? m.name : "(none)")));
            sb.AppendLine("  under the root: " + string.Join(", ", model.transform.Cast<Transform>().Select(t => t.name)) + ".");
            sb.AppendLine("Clip \"" + clip.name + "\": " + clip.length.ToString("0.###") + " s at " + clip.frameRate.ToString("0.#")
                          + " fps, " + AnimationUtility.GetAnimationEvents(clip).Length + " beats, loops: " + clip.isLooping + ".");

            // ---- controller: one state
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
                sb.AppendLine("Created " + ControllerPath + ".");
            }

            var machine = controller.layers[0].stateMachine;
            var state = machine.states.Select(s => s.state).FirstOrDefault(s => s.name == ClipName);
            if (state == null)
                state = machine.AddState(ClipName);
            state.motion = clip;
            state.writeDefaultValues = true;
            machine.defaultState = state;
            EditorUtility.SetDirty(controller);

            // ---- prefab: a variant of the model, so a new export flows straight through
            GameObject root;
            bool existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null;
            if (existing)
                root = PrefabUtility.LoadPrefabContents(PrefabPath);
            else
                root = (GameObject)PrefabUtility.InstantiatePrefab(model);

            try
            {
                root.name = Path.GetFileNameWithoutExtension(PrefabPath);
                var animator = root.GetComponent<Animator>();
                if (animator == null)
                    animator = root.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                // Off screen it must still reach its beats and its last pose.
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    // The pieces fly well outside the bounds of the standing body.
                    skin.updateWhenOffscreen = true;
                }

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                sb.AppendLine((existing ? "Updated " : "Created ") + PrefabPath + ".");
            }
            finally
            {
                if (existing)
                    PrefabUtility.UnloadPrefabContents(root);
                else
                    Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            ok = true;
            return sb.ToString();
        }

        static BeatFile ReadBeats(StringBuilder sb)
        {
            if (!File.Exists(BeatsPath))
            {
                sb.AppendLine("! No " + BeatsPath + ": the clip gets no beat events.");
                return null;
            }

            var file = JsonUtility.FromJson<BeatFile>(File.ReadAllText(BeatsPath));
            if (file == null || file.beats == null)
                return null;
            return file;
        }

        static Material LoadOrCreateMaterial(string path, Color colour, float smoothness, StringBuilder sb)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
                return material;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");
            material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
            if (material.HasProperty("_Color")) material.SetColor("_Color", colour);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            PlayerRigAssets.EnsureFolder(Folder);
            AssetDatabase.CreateAsset(material, path);
            sb.AppendLine("Created " + path + ".");
            return material;
        }

        // ------------------------------------------------------------------ pictures

        /// <summary>
        /// Draws the prefab at a row of moments of the clip into Builds/CharacterFit/&lt;time&gt;-revive-puppet/revive.png,
        /// to check by eye that what Unity plays is what Blender made. Returns the folder, or null.
        /// </summary>
        public static string Pictures(out string log)
        {
            var sb = new StringBuilder();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var clip = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<AnimationClip>()
                .FirstOrDefault(c => c.name == ClipName && (c.hideFlags & HideFlags.HideInHierarchy) == 0);
            if (prefab == null || clip == null)
            {
                log = "Build the revive puppet first.";
                return null;
            }

            int[] frames = { 0, 24, 45, 72, 95, 98, 112, 130, 146, 166, 171, 174, 180, 197, 208, 240 };
            const int Width = 300, Height = 400, Columns = 8;
            string folder = CharacterFitCheck.NewReportFolder("revive-puppet");
            var scene = EditorSceneManager.NewPreviewScene();
            var made = new List<Object>();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                var lightGo = new GameObject("Revive Light");
                SceneManager.MoveGameObjectToScene(lightGo, scene);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.3f;
                lightGo.transform.rotation = Quaternion.Euler(38f, -150f, 0f);

                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                SceneManager.MoveGameObjectToScene(floor, scene);
                floor.transform.localScale = Vector3.one * 1.2f;

                var camGo = new GameObject("Revive Camera");
                SceneManager.MoveGameObjectToScene(camGo, scene);
                var camera = camGo.AddComponent<Camera>();
                camera.scene = scene;
                camera.fieldOfView = 39.6f;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 40f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.33f, 0.36f, 0.42f);
                // The same three-quarter view as the Blender preview (it faces +Z here, its left is -X).
                var focus = new Vector3(0f, 1.39f, 0f);
                var from = focus + new Vector3(-0.55f, 0.14f, 1f).normalized * 4.72f;
                camera.transform.SetPositionAndRotation(from, Quaternion.LookRotation(focus - from));

                var skins = instance.GetComponentsInChildren<SkinnedMeshRenderer>();
                var stands = new List<(SkinnedMeshRenderer skin, Mesh mesh, GameObject stand)>();
                foreach (var skin in skins)
                {
                    var mesh = new Mesh();
                    var stand = new GameObject("Baked " + skin.name);
                    SceneManager.MoveGameObjectToScene(stand, scene);
                    stand.AddComponent<MeshFilter>().sharedMesh = mesh;
                    stand.AddComponent<MeshRenderer>().sharedMaterials = skin.sharedMaterials;
                    skin.enabled = false;
                    stands.Add((skin, mesh, stand));
                    made.Add(mesh);
                }

                var sheet = new Texture2D(Width * Columns, Height * ((frames.Length + Columns - 1) / Columns), TextureFormat.RGB24, false);
                made.Add(sheet);
                var hips = FindDeep(instance.transform, "root");
                var head = FindDeep(instance.transform, "tear.head");
                var footL = FindDeep(instance.transform, "foot.L");
                for (int i = 0; i < frames.Length; i++)
                {
                    float time = Mathf.Min(frames[i] / 30f, clip.length);
                    clip.SampleAnimation(instance, time);
                    // A skinned mesh is only re-skinned once a frame; every moment here is one frame.
                    foreach (var (skin, mesh, stand) in stands)
                    {
                        skin.BakeMesh(mesh);
                        stand.transform.SetPositionAndRotation(skin.transform.position, skin.transform.rotation);
                    }

                    var rt = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32);
                    var previous = RenderTexture.active;
                    try
                    {
                        camera.targetTexture = rt;
                        camera.Render();
                        RenderTexture.active = rt;
                        int x = (i % Columns) * Width;
                        int y = sheet.height - Height * (i / Columns + 1);
                        sheet.ReadPixels(new Rect(0, 0, Width, Height), x, y);
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                        camera.targetTexture = null;
                        RenderTexture.ReleaseTemporary(rt);
                    }

                    sb.AppendLine("f" + frames[i] + ": hips " + Point(hips) + ", head " + Point(head) + ", left foot " + Point(footL));
                }

                sheet.Apply();
                File.WriteAllBytes(Path.Combine(folder, "revive.png"), sheet.EncodeToPNG());
                File.WriteAllText(Path.Combine(folder, "revive.txt"), sb.ToString());
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (var o in made)
                    Object.DestroyImmediate(o);
            }

            log = sb.ToString();
            return folder;
        }

        static string Point(Transform t)
        {
            if (t == null)
                return "?";
            var p = t.position;
            return "(" + p.x.ToString("0.000") + ", " + p.y.ToString("0.000") + ", " + p.z.ToString("0.000") + ")";
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                    return t;
            }

            return null;
        }
    }
}
#endif
