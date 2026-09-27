using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// An icon made of stacked sprites, some of them tinted: a white blob under a black rim for a
    /// player swatch, a black stroke under a coloured line for the chain or the grip hand.
    ///
    /// Tinting multiplies, so a single sprite with a black outline cannot take a colour without the
    /// outline going muddy. Keeping the ink and the colour on separate layers is what lets one sprite
    /// set serve every player colour and every collar state.
    /// </summary>
    public class IconStack : MonoBehaviour
    {
        public readonly struct Layer
        {
            public readonly string Sprite;
            public readonly bool IsTinted;

            public Layer(string sprite, bool isTinted)
            {
                Sprite = sprite;
                IsTinted = isTinted;
            }
        }

        readonly List<Image> _tinted = new List<Image>();

        public static Layer PlainLayer(string sprite) => new Layer(sprite, false);
        public static Layer TintLayer(string sprite) => new Layer(sprite, true);

        /// <summary>Builds the stack, bottom layer first. Tinted layers start in <paramref name="tint"/>.</summary>
        public static IconStack Create(Transform parent, string name, Vector2 size, Color tint, params Layer[] layers)
        {
            var rect = UiKit.CreateRect(name, parent);
            rect.sizeDelta = size;

            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = size.x;
            element.minHeight = element.preferredHeight = size.y;

            var stack = rect.gameObject.AddComponent<IconStack>();
            foreach (var layer in layers)
            {
                var image = UiKit.CreateImage(rect, layer.Sprite, layer.Sprite, layer.IsTinted ? tint : Color.white);
                image.rectTransform.Fill();
                image.preserveAspect = true;
                if (layer.IsTinted)
                    stack._tinted.Add(image);
            }

            return stack;
        }

        public void SetTint(Color colour)
        {
            foreach (var image in _tinted)
            {
                if (image != null)
                    image.color = colour;
            }
        }
    }
}
