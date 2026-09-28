#if CMPSETUP_COMPLETE
using cowsins;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The floating label over a pickup (world sheet, PICKUPS): the gun's silhouette, its name, and
    /// the little pointer down to it. Aimed at, the name brightens and gets the underline. It stands
    /// in for Cowsins' own icon ring, whose canvas is switched off.
    ///
    /// A world-space canvas of its own that follows the pickup (not a child of it, so the pickup's
    /// spin and scale never reach it) and faces the camera; walls hide it like anything else.
    /// </summary>
    public class PickupLabel : MonoBehaviour
    {
        const float Height = 0.55f;
        const float Scale = 0.0028f;
        const float FadeFrom = 5f;
        const float HideBeyond = 10f;

        static readonly Color Idle = UiTheme.Rgb(0xD5DAD8);

        Pickeable _target;
        Canvas _canvas;
        CanvasGroup _group;
        TextMeshProUGUI _name;
        Image _underline;

        public Pickeable Target => _target;

        public static PickupLabel Create(Pickeable target, string name, WeaponGlyph glyph)
        {
            var go = new GameObject("PickupLabel " + name, typeof(RectTransform));
            var label = go.AddComponent<PickupLabel>();
            label._target = target;
            label.Build((RectTransform)go.transform, name, glyph);
            return label;
        }

        void Build(RectTransform root, string name, WeaponGlyph glyph)
        {
            var theme = UiTheme.Active;
            root.sizeDelta = new Vector2(320f, 220f);
            root.localScale = Vector3.one * Scale;

            _canvas = root.gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 4;
            _group = root.gameObject.AddComponent<CanvasGroup>();

            var column = UiKit.CreateColumn(root, "Stack", 2f, TextAnchor.LowerCenter, fitToContent: false);
            column.Fill();

            if (glyph != WeaponGlyph.None)
            {
                string sprite = glyph == WeaponGlyph.Mp7 ? UiSprites.Mp7 : UiSprites.Pistol;
                float width = glyph == WeaponGlyph.Mp7 ? 150f : 110f;
                UiKit.CreateImage(column, "Gun", sprite, theme.cream).Sized(width, width * 0.45f);
            }

            _name = UiKit.CreateText(column, "Name", name, TextStyle.Prompt.WithSize(34f).WithAlign(TextAlignmentOptions.Center));
            _name.color = Idle;
            _name.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;

            _underline = UiKit.CreateImage(column, "Underline", UiSprites.Brush, theme.cream, sliced: true);
            _underline.Sized(120f, 12f);
            _underline.enabled = false;

            UiKit.CreateImage(column, "Tick", UiSprites.Tick, theme.cream).Sized(24f, 15f);
        }

        /// <summary>Follows and faces; false once the pickup is gone, so the director can drop it.</summary>
        public bool Tick(Transform camera, bool aimed)
        {
            if (_target == null)
                return false;

            var anchor = _target.transform.position + Vector3.up * Height;
            transform.position = anchor;

            bool show = camera != null && _target.gameObject.activeInHierarchy;
            float distance = show ? Vector3.Distance(camera.position, anchor) : float.MaxValue;
            show &= distance <= HideBeyond;
            if (_canvas.enabled != show)
                _canvas.enabled = show;
            if (!show)
                return true;

            // Parallel to the view rather than looking at it, so a row of labels does not fan out.
            transform.rotation = Quaternion.LookRotation(camera.forward, Vector3.up);
            _group.alpha = Mathf.InverseLerp(HideBeyond, FadeFrom, distance);

            _name.color = aimed ? Color.white : Idle;
            _name.fontSize = aimed ? 40f : 34f;
            _underline.enabled = aimed;
            return true;
        }
    }
}
#endif
