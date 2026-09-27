#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// One shared body in the four player colours. Run <b>Tools ▸ CollarCali ▸ Build Shared Body</b>.
    ///
    /// Everyone wears the same Meshy body; the only difference between players is their colour.
    /// The four textures in Characters/SharedBody are one skin (the purple one) with its main colour
    /// moved to orange, blue, pink and lime - the rest of the paint (face, eyes, details) as it was -
    /// baked by the scratch tool shared_body.py. This builds a material for each and rewrites
    /// Resources/CharacterSkins.asset as those four, in PlayerColorPalette order, so a player's skin
    /// is simply their colour index (FpsNetworkBridge.ApplyIdentity).
    ///
    /// Builds assets only - no prefab is touched; the skin is put on at runtime.
    /// </summary>
    public static class SharedBodyBuilder
    {
        const string Folder = "Assets/_Integration/Characters/SharedBody";
        const string SkinLibraryPath = "Assets/_Integration/Resources/CharacterSkins.asset";
        const string PreviewPrefabPath = "Assets/_Integration/Resources/CharacterPreview.prefab";
        const string PreviewFile = "Builds/UiCaptures/_shared_body.png";

        /// <summary>The purple Meshy skin the colours are baked from ("Material.001 4").</summary>
        const string BaseMaterialGuid = "6545073b4dba0c94f8cad5886fdc7be3";

        static readonly string[] Colours = { "Orange", "Blue", "Pink", "Lime" };

        [MenuItem("Tools/CollarCali/Build Shared Body")]
        public static void Build()
        {
            var log = new StringBuilder("[SharedBody]\n");
            AssetDatabase.Refresh();

            var basePath = AssetDatabase.GUIDToAssetPath(BaseMaterialGuid);
            var baseMaterial = AssetDatabase.LoadAssetAtPath<Material>(basePath);
            if (baseMaterial == null)
            {
                Debug.LogError("[SharedBody] The base skin material is missing (guid " + BaseMaterialGuid + ").");
                return;
            }

            var baseTexture = baseMaterial.GetTexture("_BaseMap") as Texture2D;
            var baseImporter = baseTexture != null
                ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(baseTexture)) as TextureImporter
                : null;

            var library = AssetDatabase.LoadAssetAtPath<CharacterSkinLibrary>(SkinLibraryPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<CharacterSkinLibrary>();
                AssetDatabase.CreateAsset(library, SkinLibraryPath);
                log.AppendLine("  Created " + SkinLibraryPath);
            }

            library.skins.Clear();
            for (int i = 0; i < Colours.Length; i++)
            {
                string texturePath = $"{Folder}/SharedBody_{Colours[i]}.png";
                MatchImport(texturePath, baseImporter);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (texture == null)
                {
                    log.AppendLine("  ! Missing " + texturePath);
                    continue;
                }

                string materialPath = $"{Folder}/SharedBody {Colours[i]}.mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    material = new Material(baseMaterial);
                    AssetDatabase.CreateAsset(material, materialPath);
                }
                else
                {
                    material.shader = baseMaterial.shader;
                    material.CopyPropertiesFromMaterial(baseMaterial);
                }

                // The colour is in the texture now, so nothing tints over it.
                SetTexture(material, "_BaseMap", texture);
                SetTexture(material, "_MainTex", texture);
                SetColour(material, "_BaseColor", Color.white);
                SetColour(material, "_Color", Color.white);
                EditorUtility.SetDirty(material);

                library.skins.Add(new CharacterSkinLibrary.Skin
                {
                    displayName = PlayerColorPalette.GetName(i),
                    material = material,
                    uiTint = PlayerColorPalette.Get(i),
                });
                log.AppendLine("  " + Colours[i] + ": " + materialPath);
            }

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            log.AppendLine($"  Skin library: {library.Count} skin(s), one per player colour.");
            Debug.Log(log.ToString());
        }

        /// <summary>Imports a baked texture exactly as its source skin is imported.</summary>
        static void MatchImport(string path, TextureImporter source)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                return;

            if (source != null)
            {
                var settings = new TextureImporterSettings();
                source.ReadTextureSettings(settings);
                importer.SetTextureSettings(settings);
                importer.maxTextureSize = source.maxTextureSize;
                importer.textureCompression = source.textureCompression;
            }
            else
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.mipmapEnabled = true;
            }

            importer.SaveAndReimport();
        }

        static void SetTexture(Material material, string property, Texture texture)
        {
            if (material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        static void SetColour(Material material, string property, Color colour)
        {
            if (material.HasProperty(property))
                material.SetColor(property, colour);
        }

        /// <summary>
        /// Renders the lobby's preview body in each colour, side by side, to Builds/UiCaptures, so
        /// the four looks can be checked without entering play mode.
        /// </summary>
        [MenuItem("Tools/CollarCali/Render Shared Body Preview")]
        public static void RenderPreview()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PreviewPrefabPath);
            var library = AssetDatabase.LoadAssetAtPath<CharacterSkinLibrary>(SkinLibraryPath);
            if (prefab == null || library == null || library.Count == 0)
            {
                Debug.LogError("[SharedBody] Nothing to render: preview prefab or skin library missing.");
                return;
            }

            const int width = 480;
            const int height = 720;
            var scene = EditorSceneManager.NewPreviewScene();
            var sheet = new Texture2D(width * library.Count, height, TextureFormat.RGB24, false);
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                antiAliasing = 4,
            };

            GameObject body = null;
            GameObject lightObject = null;
            GameObject cameraObject = null;
            try
            {
                body = Object.Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(body, scene);
                body.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 180f, 0f));

                lightObject = new GameObject("Key Light");
                SceneManager.MoveGameObjectToScene(lightObject, scene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.2f;
                lightObject.transform.rotation = Quaternion.Euler(35f, 160f, 0f);

                var bounds = new Bounds(body.transform.position + Vector3.up, Vector3.one);
                foreach (var renderer in body.GetComponentsInChildren<Renderer>(true))
                    bounds.Encapsulate(renderer.bounds);

                cameraObject = new GameObject("Preview Camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.scene = scene;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.06f, 0.07f, 0.07f, 1f);
                camera.fieldOfView = 30f;
                camera.targetTexture = target;
                float distance = bounds.extents.y / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;
                cameraObject.transform.position = bounds.center + Vector3.back * -distance;
                cameraObject.transform.LookAt(bounds.center);
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = distance * 4f;

                for (int i = 0; i < library.Count; i++)
                {
                    CharacterSelection.Apply(body, i);
                    camera.Render();

                    var previous = RenderTexture.active;
                    RenderTexture.active = target;
                    var shot = new Texture2D(width, height, TextureFormat.RGB24, false);
                    shot.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                    shot.Apply();
                    RenderTexture.active = previous;

                    sheet.SetPixels(width * i, 0, width, height, shot.GetPixels());
                    Object.DestroyImmediate(shot);
                }

                sheet.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(PreviewFile));
                File.WriteAllBytes(PreviewFile, sheet.EncodeToPNG());
                Debug.Log("[SharedBody] Preview written to " + Path.GetFullPath(PreviewFile));
            }
            finally
            {
                if (cameraObject != null)
                    cameraObject.GetComponent<Camera>().targetTexture = null;
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(sheet);
                if (body != null)
                    Object.DestroyImmediate(body);
                if (lightObject != null)
                    Object.DestroyImmediate(lightObject);
                if (cameraObject != null)
                    Object.DestroyImmediate(cameraObject);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>Batch entry: build, then render the check image.</summary>
        public static void BuildAndPreview()
        {
            Build();
            RenderPreview();
        }
    }
}
#endif
