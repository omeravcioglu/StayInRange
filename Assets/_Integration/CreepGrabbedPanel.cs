using UnityEngine;
using UnityEngine.UI;

namespace CollarCali
{
    /// <summary>
    /// Full-screen overlay while a creep is dragging a player.
    /// JumpScareRoot is an empty stretch-full slot for images / video later.
    /// </summary>
    public class CreepGrabbedPanel : MonoBehaviour
    {
        public static Transform JumpScareRoot { get; private set; }

        static CreepGrabbedPanel _instance;
        CanvasGroup _group;

        public static void Show()
        {
            Ensure().SetVisible(true);
        }

        public static void Hide()
        {
            if (_instance != null)
                _instance.SetVisible(false);
        }

        static CreepGrabbedPanel Ensure()
        {
            if (_instance != null)
                return _instance;

            var existing = FindFirstObjectByType<CreepGrabbedPanel>(FindObjectsInactive.Include);
            if (existing != null)
            {
                _instance = existing;
                _instance.CacheRefs();
                return _instance;
            }

            var root = new GameObject("CreepGrabbedPanel");
            _instance = root.AddComponent<CreepGrabbedPanel>();
            return _instance;
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            CacheRefs();
            if (_group == null)
                BuildUi();
            SetVisible(false);
        }

        void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
                JumpScareRoot = null;
            }
        }

        void SetVisible(bool visible)
        {
            if (_group == null)
                CacheRefs();
            if (_group == null)
                return;

            _group.alpha = visible ? 1f : 0f;
            _group.blocksRaycasts = visible;
            _group.interactable = false;
            gameObject.SetActive(true);
        }

        void CacheRefs()
        {
            _group = GetComponent<CanvasGroup>();
            var scare = transform.Find("JumpScareRoot");
            if (scare != null)
                JumpScareRoot = scare;
        }

        void BuildUi()
        {
            var canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null)
                canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;

            var scaler = gameObject.GetComponent<CanvasScaler>();
            if (scaler == null)
                scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
                gameObject.AddComponent<GraphicRaycaster>();

            _group = gameObject.GetComponent<CanvasGroup>();
            if (_group == null)
                _group = gameObject.AddComponent<CanvasGroup>();

            var dimmer = CreateChild("Dimmer", transform);
            var dimmerImage = dimmer.gameObject.AddComponent<Image>();
            dimmerImage.color = new Color(0f, 0f, 0f, 0.62f);
            Stretch(dimmer);

            var scare = CreateChild("JumpScareRoot", transform);
            Stretch(scare);
            JumpScareRoot = scare;

            var label = CreateChild("GrabbedLabel", transform);
            var text = label.gameObject.AddComponent<Text>();
            text.text = "YOU ARE GRABBED";
            text.alignment = TextAnchor.LowerCenter;
            text.fontSize = 64;
            text.fontStyle = FontStyle.Bold;
            text.color = new Color(0.92f, 0.12f, 0.12f, 1f);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.font = BuiltinFont();
            Stretch(label);
            label.offsetMin = new Vector2(40f, 48f);
            label.offsetMax = new Vector2(-40f, -40f);
        }

        static Font BuiltinFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        static RectTransform CreateChild(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
        }
    }
}
