#if UNITY_EDITOR
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CollarCali.UI.EditorTools
{
    /// <summary>
    /// The UI gallery from the editor:
    /// - <b>Capture Gallery</b> renders every page of <see cref="UiScenarios"/> at 1920x1080 and
    ///   1280x720 into Builds/UiCaptures, without entering play mode or touching the open scenes, for
    ///   side-by-side review against the design canvas;
    /// - <b>Open Gallery Scene</b> opens a scene that shows the pages live in play mode.
    /// </summary>
    public static class UiGalleryTools
    {
        const string ScenePath = "Assets/_Integration/UI/Gallery/UiGallery.unity";
        const string CaptureFolder = "Builds/UiCaptures";

        static readonly Vector2Int[] Resolutions =
        {
            new Vector2Int(1920, 1080),
            // The brief's floor: everything must still read at 720p.
            new Vector2Int(1280, 720),
            // Off 16:9 both ways - an ultrawide, and a squarer screen (or a docked Game view).
            new Vector2Int(2560, 1080),
            new Vector2Int(1440, 1080),
        };

        [MenuItem("Tools/CollarCali/UI/Capture Gallery")]
        public static void CaptureAll()
        {
            Directory.CreateDirectory(CaptureFolder);

            // A preview scene is invisible to the rest of the editor and never saved, so capturing
            // cannot dirty or disturb whatever scene is open.
            var scene = EditorSceneManager.NewPreviewScene();
            int written = 0;
            try
            {
                foreach (var scenario in UiScenarios.All)
                {
                    foreach (var resolution in Resolutions)
                    {
                        Capture(scene, scenario, resolution);
                        written++;
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }

            Debug.Log($"[UiGallery] {written} capture(s) written to {Path.GetFullPath(CaptureFolder)}");
        }

        static void Capture(Scene scene, UiScenarios.Scenario scenario, Vector2Int size)
        {
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0)
                uiLayer = 5;

            var cameraObject = new GameObject("GalleryCamera");
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            var camera = cameraObject.AddComponent<Camera>();
            camera.scene = scene;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = UiTheme.Rgb(0x050707);
            camera.cullingMask = 1 << uiLayer;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 10f;

            var target = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;

            var canvasObject = new GameObject("GalleryCanvas", typeof(RectTransform));
            SceneManager.MoveGameObjectToScene(canvasObject, scene);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1f;

            // The same scale the game's canvases pick (ScaleWithScreenSize, Expand), set directly so
            // the capture does not depend on how the canvas reads a render texture's size.
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = ScaleFor(size);

            try
            {
                var page = UiScenarios.CreatePage(canvasObject.transform);
                scenario.Build(page);
                SetLayer(canvasObject.transform, uiLayer);

                Canvas.ForceUpdateCanvases();
                foreach (var text in canvasObject.GetComponentsInChildren<TMP_Text>(true))
                    text.ForceMeshUpdate(true, true);
                Canvas.ForceUpdateCanvases();

                camera.Render();

                var previous = RenderTexture.active;
                RenderTexture.active = target;
                var pixels = new Texture2D(size.x, size.y, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0f, 0f, size.x, size.y), 0, 0);
                pixels.Apply();
                RenderTexture.active = previous;

                var file = Path.Combine(CaptureFolder, $"{scenario.Name}_{size.x}x{size.y}.png");
                File.WriteAllBytes(file, pixels.EncodeToPNG());
                Object.DestroyImmediate(pixels);
            }
            finally
            {
                camera.targetTexture = null;
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(canvasObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        /// <summary>CanvasScaler's ScaleWithScreenSize maths in Expand mode: the reference always fits.</summary>
        static float ScaleFor(Vector2Int size)
        {
            return Mathf.Min(size.x / UiKit.ReferenceResolution.x, size.y / UiKit.ReferenceResolution.y);
        }

        static void SetLayer(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            foreach (Transform child in root)
                SetLayer(child, layer);
        }

        [MenuItem("Tools/CollarCali/UI/Open Gallery Scene")]
        public static void OpenGalleryScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            if (File.Exists(ScenePath))
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = UiTheme.Rgb(0x050707);
            camera.cullingMask = 0;

            new GameObject("UiGallery").AddComponent<UiGalleryDriver>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath) ?? "Assets");
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[UiGallery] Created " + ScenePath + ". Press Play, then use the arrow keys.");
        }
    }
}
#endif
