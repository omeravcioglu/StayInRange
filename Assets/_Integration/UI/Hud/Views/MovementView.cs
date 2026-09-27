using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// Bottom left: the dash charges as three pips that refill with a ring, and a stamina bar for
    /// whichever controller has stamina (third person today; the first-person controller has it
    /// switched off, so the bar simply is not there).
    /// </summary>
    public class MovementView : MonoBehaviour
    {
        const int MaxPips = 5;

        static readonly Color EmptyDisc = new Color(0f, 0f, 0f, 0.55f);
        static readonly Color EmptyGlyph = UiTheme.Rgb(0x8A9492);
        static readonly Color LabelColour = UiTheme.Rgb(0xD5DAD8);

        CanvasGroup _group;

        RectTransform _staminaRow;
        IconStack _bolt;
        SketchBar _stamina;
        TextMeshProUGUI _winded;

        Pip[] _pips;
        TextMeshProUGUI _label;
        int _shownDashes = -1;

        struct Pip
        {
            public RectTransform Root;
            public Image Disc;
            public Image Rim;
            public Image Arc;
            public Image Glyph;
        }

        public static MovementView Create(Transform parent)
        {
            var rect = UiKit.CreateColumn(parent, "Movement", 10f, TextAnchor.LowerLeft, fitToContent: false);
            rect.Place(new Vector2(0f, 0f), new Vector2(20f, 24f), new Vector2(340f, 104f));
            rect.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(8, 8, 8, 8);

            var view = rect.gameObject.AddComponent<MovementView>();
            view._group = rect.gameObject.AddComponent<CanvasGroup>();
            view.Build(rect);
            return view;
        }

        void Build(RectTransform root)
        {
            var theme = UiTheme.Active;

            _staminaRow = UiKit.CreateRow(root, "Stamina", 10f, TextAnchor.MiddleLeft, fitToContent: false);
            _bolt = IconStack.Create(_staminaRow, "Bolt", new Vector2(30f, 30f), theme.cream,
                IconStack.TintLayer(UiSprites.BoltFill), IconStack.PlainLayer(UiSprites.BoltInk));
            _stamina = SketchBar.Create(_staminaRow, "Bar", new Vector2(220f, 18f), theme.cream, shine: false);
            _stamina.SetTrackColor(new Color(0f, 0f, 0f, 0.6f));
            _winded = UiKit.CreateText(_staminaRow, "Winded", "winded!", TextStyle.Small.WithSize(20f).WithTint(ColorRole.Danger));

            var dashRow = UiKit.CreateRow(root, "Dash", 8f, TextAnchor.MiddleLeft, fitToContent: false);
            _pips = new Pip[MaxPips];
            for (int i = 0; i < MaxPips; i++)
                _pips[i] = CreatePip(dashRow, i);
            _label = UiKit.CreateText(dashRow, "Label", "dash", TextStyle.Small);
            _label.color = LabelColour;
        }

        static Pip CreatePip(Transform parent, int index)
        {
            var root = UiKit.CreateRect("Pip " + index, parent).Sized(46f, 46f);
            var pip = new Pip { Root = root };

            // Sizes fit the board's 19 px circle inside the sprites' own margins.
            pip.Disc = Layer(root, "Disc", UiSprites.Circle, 40.5f);
            pip.Rim = Layer(root, "Rim", UiSprites.Ring, 45f);
            pip.Arc = Layer(root, "Recharge", UiSprites.Ring, 45f);
            pip.Arc.type = Image.Type.Filled;
            pip.Arc.fillMethod = Image.FillMethod.Radial360;
            pip.Arc.fillOrigin = (int)Image.Origin360.Top;
            pip.Arc.fillClockwise = true;
            pip.Glyph = Layer(root, "Glyph", UiSprites.DashGlyph, 46f);
            return pip;
        }

        static Image Layer(RectTransform parent, string name, string sprite, float size)
        {
            var image = UiKit.CreateImage(parent, name, sprite, Color.white);
            image.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
            image.preserveAspect = true;
            return image;
        }

        public void Set(in MovementData data)
        {
            _group.alpha = data.Visible ? 1f : 0f;
            if (!data.Visible)
                return;

            var theme = UiTheme.Active;

            bool stamina = data.Stamina01 >= 0f;
            Show(_staminaRow, stamina);
            if (stamina)
            {
                var colour = data.Stamina01 < 0.15f ? theme.danger : data.Stamina01 < 0.35f ? theme.warning : theme.cream;
                _bolt.SetTint(colour);
                _stamina.SetFillColor(colour);
                _stamina.SetValue(data.Stamina01);
                Show(_winded, data.Stamina01 < 0.15f);
            }

            int max = Mathf.Clamp(data.MaxDashes, 0, MaxPips);
            for (int i = 0; i < MaxPips; i++)
            {
                var pip = _pips[i];
                bool used = i < max;
                Show(pip.Root, used);
                if (!used)
                    continue;

                bool ready = i < data.Dashes;
                pip.Disc.color = ready ? theme.cream : EmptyDisc;
                pip.Rim.color = ready ? theme.ink : theme.dim;
                pip.Glyph.color = ready ? theme.ink : EmptyGlyph;

                // The first empty pip is the one coming back; later ones trail behind it.
                float progress = ready ? 0f : Mathf.Clamp01(data.Recharge01 - (i - data.Dashes) * 0.45f);
                Show(pip.Arc, progress > 0f);
                pip.Arc.color = theme.cream;
                pip.Arc.fillAmount = progress;
            }

            if (_shownDashes != data.Dashes)
            {
                _shownDashes = data.Dashes;
                _label.text = data.Dashes <= 0 ? "no dash" : "dash";
            }
        }

        static void Show(Component component, bool visible)
        {
            if (component != null && component.gameObject.activeSelf != visible)
                component.gameObject.SetActive(visible);
        }
    }
}
