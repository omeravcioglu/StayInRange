using UnityEngine;
using UnityEngine.UI;

namespace CollarCali
{
    /// <summary>
    /// The full-screen "YOU DIED" card shown when the whole team is down, before everyone is put
    /// back at the last checkpoint.
    ///
    /// Built in code and summoned statically, the same way CreepGrabbedPanel is, so it needs no
    /// prefab and no wiring in any scene - which matters because it has to appear in whatever scene
    /// the team happened to wipe in.
    ///
    /// This is separate from Cowsins' own death screen, which is a per-player thing that appears the
    /// moment YOU die. This one is about the team, and only appears when there is nobody left to
    /// carry anybody to a station.
    /// </summary>
    public class TeamWipePanel : MonoBehaviour
    {
        static TeamWipePanel _instance;

        CanvasGroup _group;
        Text _title;
        Text _subtitle;
        float _hideAt;
        bool _visible;

        /// <summary>Shows the card for a while. Calling it again just extends the time.</summary>
        public static void Show(string subtitle, float seconds)
        {
            var panel = Ensure();
            panel._subtitle.text = subtitle;
            panel._hideAt = Time.unscaledTime + Mathf.Max(0.1f, seconds);
            panel.SetVisible(true);
        }

        public static void Hide()
        {
            if (_instance != null)
                _instance.SetVisible(false);
        }

        static TeamWipePanel Ensure()
        {
            if (_instance != null)
                return _instance;

            var existing = FindFirstObjectByType<TeamWipePanel>(FindObjectsInactive.Include);
            if (existing != null)
            {
                _instance = existing;
                return _instance;
            }

            var root = new GameObject("TeamWipePanel");
            _instance = root.AddComponent<TeamWipePanel>();
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
            // Survives the scene reload a checkpoint restart could involve, so the card does not
            // vanish halfway through being read.
            DontDestroyOnLoad(gameObject);
            BuildUi();
            SetVisible(false);
        }

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        void Update()
        {
            if (!_visible)
                return;

            // Unscaled, so a pause or a time-scale effect cannot strand the card on screen.
            if (Time.unscaledTime >= _hideAt)
                SetVisible(false);
        }

        void SetVisible(bool visible)
        {
            _visible = visible;
            if (_group == null)
                return;

            _group.alpha = visible ? 1f : 0f;
            _group.blocksRaycasts = visible;
            _group.interactable = false;
        }

        void BuildUi()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above the spectator label and the grabbed overlay: a wipe outranks both.
            canvas.sortingOrder = 600;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            gameObject.AddComponent<GraphicRaycaster>();
            _group = gameObject.AddComponent<CanvasGroup>();

            var dimmer = CreateChild("Dimmer", transform);
            var dimmerImage = dimmer.gameObject.AddComponent<Image>();
            dimmerImage.color = new Color(0f, 0f, 0f, 0.78f);
            Stretch(dimmer);

            var titleRect = CreateChild("Title", transform);
            Stretch(titleRect);
            titleRect.offsetMin = new Vector2(40f, 0f);
            titleRect.offsetMax = new Vector2(-40f, 0f);
            _title = titleRect.gameObject.AddComponent<Text>();
            _title.text = "YOU DIED";
            _title.alignment = TextAnchor.MiddleCenter;
            _title.fontSize = 110;
            _title.fontStyle = FontStyle.Bold;
            _title.color = new Color(0.85f, 0.09f, 0.09f, 1f);
            _title.horizontalOverflow = HorizontalWrapMode.Overflow;
            _title.verticalOverflow = VerticalWrapMode.Overflow;
            _title.raycastTarget = false;
            _title.font = BuiltinFont();

            var subtitleRect = CreateChild("Subtitle", transform);
            Stretch(subtitleRect);
            subtitleRect.offsetMin = new Vector2(40f, -150f);
            subtitleRect.offsetMax = new Vector2(-40f, -150f);
            _subtitle = subtitleRect.gameObject.AddComponent<Text>();
            _subtitle.text = string.Empty;
            _subtitle.alignment = TextAnchor.MiddleCenter;
            _subtitle.fontSize = 34;
            _subtitle.color = new Color(0.88f, 0.88f, 0.88f, 0.92f);
            _subtitle.horizontalOverflow = HorizontalWrapMode.Overflow;
            _subtitle.verticalOverflow = VerticalWrapMode.Overflow;
            _subtitle.raycastTarget = false;
            _subtitle.font = BuiltinFont();
        }

        static Font BuiltinFont()
        {
            // YOU DIED is the loudest text in the game, so it gets the display face.
            // Falls back to Unity's built-in font when the font set has not been built,
            // so a missing set costs styling rather than legibility.
            return GameFontSet.LegacyDisplayOrDefault();
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
