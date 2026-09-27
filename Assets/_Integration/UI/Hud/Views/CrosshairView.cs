using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The crosshair, redrawn in the design's ink style. It replaces Cowsins' IMGUI crosshair, which
    /// drew in raw pixels above every canvas and only existed in first person.
    ///
    /// Idle: four short arms and a dot. Over an enemy the arms open up and turn red. A hit flashes a
    /// white cross, a headshot a red one, and a kill puts a skull in the middle for a moment.
    /// </summary>
    public class CrosshairView : MonoBehaviour
    {
        static readonly Color EnemyTint = UiTheme.Rgb(0xFF8A80);
        static readonly Color HeadshotTint = UiTheme.Rgb(0xFF3B30);

        const float HitSeconds = 0.25f;
        const float KillSeconds = 0.55f;

        CanvasGroup _group;
        Image _armsInk;
        Image _armsLine;
        Image _wideInk;
        Image _wideLine;
        Image _dotFill;
        Image _dotInk;
        Image _xInk;
        Image _xLine;
        Image _skull;

        float _hitUntil;
        float _killUntil;
        bool _hitHeadshot;

        public static CrosshairView Create(Transform parent)
        {
            var rect = UiKit.CreateRect("Crosshair", parent);
            rect.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(96f, 96f));
            var view = rect.gameObject.AddComponent<CrosshairView>();
            view._group = rect.gameObject.AddComponent<CanvasGroup>();
            view.Build(rect);
            return view;
        }

        void Build(RectTransform root)
        {
            _armsInk = Layer(root, UiSprites.CrossArmsInk);
            _armsLine = Layer(root, UiSprites.CrossArmsLine);
            _wideInk = Layer(root, UiSprites.CrossArmsWideInk);
            _wideLine = Layer(root, UiSprites.CrossArmsWideLine);
            _dotFill = Layer(root, UiSprites.CrossDotFill);
            _dotInk = Layer(root, UiSprites.CrossDotInk);
            _xInk = Layer(root, UiSprites.HitXInk);
            _xLine = Layer(root, UiSprites.HitXLine);

            // The skull is 24 units in a 96 box on the board, scaled to 0.85.
            _skull = UiKit.CreateImage(root, "Kill", UiSprites.Skull, Color.white);
            _skull.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(21f, 21f));
            _skull.preserveAspect = true;
        }

        static Image Layer(RectTransform root, string sprite)
        {
            var image = UiKit.CreateImage(root, sprite, sprite, Color.white);
            image.rectTransform.Fill();
            return image;
        }

        /// <summary>Shows the crosshair, opened up over an enemy or at rest.</summary>
        public void Set(bool visible, bool onEnemy)
        {
            _group.alpha = visible ? 1f : 0f;
            if (!visible)
                return;

            var cream = UiTheme.Active.cream;
            Show(_armsInk, !onEnemy);
            Show(_armsLine, !onEnemy);
            Show(_wideInk, onEnemy);
            Show(_wideLine, onEnemy);
            _armsLine.color = cream;
            _wideLine.color = EnemyTint;

            float now = Time.unscaledTime;
            bool killing = now < _killUntil;
            bool hitting = now < _hitUntil || killing;

            Show(_dotFill, !killing);
            Show(_dotInk, !killing);
            _dotFill.color = onEnemy ? EnemyTint : cream;
            Show(_skull, killing);

            Show(_xInk, hitting);
            Show(_xLine, hitting);
            if (hitting)
            {
                float remaining = killing ? (_killUntil - now) / KillSeconds : (_hitUntil - now) / HitSeconds;
                float alpha = Mathf.Clamp01(remaining * 1.6f);
                var colour = killing || _hitHeadshot ? HeadshotTint : cream;
                colour.a = alpha;
                _xLine.color = colour;
                _xInk.color = new Color(1f, 1f, 1f, alpha);
            }
        }

        /// <summary>Flashes the hit cross; a kill also shows the skull.</summary>
        public void Flash(bool headshot, bool kill)
        {
            float now = Time.unscaledTime;
            _hitHeadshot = headshot;
            _hitUntil = now + HitSeconds;
            if (kill)
                _killUntil = now + KillSeconds;
        }

        static void Show(Component component, bool visible)
        {
            if (component != null && component.gameObject.activeSelf != visible)
                component.gameObject.SetActive(visible);
        }
    }
}
