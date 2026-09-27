using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The weapon block, bottom right: the magazine count big in Knewave, the weapon's name and
    /// silhouette above it, and one state line - LOW AMMO, reloading, RELOAD!, OVERHEATED! - that
    /// only appears when there is something to say. The slot strip shows briefly after a swap.
    ///
    /// Only drawn while a gun can be used, so it disappears in third person and while dead.
    /// </summary>
    public class WeaponHudView : MonoBehaviour
    {
        static readonly Color SlotNumberColour = Color.white;
        static readonly Color SubtleGrey = UiTheme.Rgb(0x8A9492);
        static readonly Color OverheatOrange = UiTheme.Rgb(0xFF6A3D);

        CanvasGroup _group;

        RectTransform _slots;
        CanvasGroup[] _slotGroups;
        Image[] _slotGlyphs;
        TextMeshProUGUI[] _slotNames;

        TextMeshProUGUI _name;
        Image _glyph;

        RectTransform _lowRow;
        RectTransform _reloadRow;
        SketchBar _reloadBar;
        RectTransform _emptyRow;
        RectTransform _overheatRow;

        TextMeshProUGUI _magazine;
        TextMeshProUGUI _magazineSize;
        IconStack _infinity;
        TextMeshProUGUI _reserve;

        RectTransform _heatRow;
        IconStack _heatIcon;
        SketchBar _heatBar;

        int _shownMagazine = -1;
        int _shownSize = -1;
        int _shownReserve = -1;
        string _shownName;
        WeaponGlyph _shownGlyph = (WeaponGlyph)(-1);

        const int MaxSlots = 4;

        public static WeaponHudView Create(Transform parent)
        {
            var rect = UiKit.CreateColumn(parent, "Weapon", 6f, TextAnchor.LowerRight, fitToContent: false);
            // 20 px in from the bottom-right corner, as on the HUD boards.
            rect.Place(new Vector2(1f, 0f), new Vector2(-20f, 20f), new Vector2(560f, 280f));
            var layout = rect.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 10);

            var view = rect.gameObject.AddComponent<WeaponHudView>();
            view._group = rect.gameObject.AddComponent<CanvasGroup>();
            view.Build(rect);
            return view;
        }

        void Build(RectTransform root)
        {
            var theme = UiTheme.Active;

            // Slot strip: "1 [pistol]  2 [mp7]", the one in hand bright.
            _slots = UiKit.CreateRow(root, "Slots", 24f, TextAnchor.LowerRight, fitToContent: false);
            _slotGroups = new CanvasGroup[MaxSlots];
            _slotGlyphs = new Image[MaxSlots];
            _slotNames = new TextMeshProUGUI[MaxSlots];
            for (int i = 0; i < MaxSlots; i++)
            {
                var slot = UiKit.CreateRow(_slots, "Slot " + (i + 1), 6f, TextAnchor.LowerLeft, fitToContent: false);
                _slotGroups[i] = slot.gameObject.AddComponent<CanvasGroup>();
                var number = UiKit.CreateText(slot, "Number", (i + 1).ToString(), TextStyle.Number.WithSize(26f));
                number.color = SlotNumberColour;
                _slotGlyphs[i] = UiKit.CreateImage(slot, "Glyph", UiSprites.Pistol, Color.white).Sized(70f, 42f);
                _slotGlyphs[i].preserveAspect = true;
                _slotNames[i] = UiKit.CreateText(slot, "Name", string.Empty, TextStyle.Small.WithSize(22f).WithTint(ColorRole.White));
            }

            // Name and silhouette.
            var nameRow = UiKit.CreateRow(root, "NameRow", 14f, TextAnchor.MiddleRight, fitToContent: false);
            _name = UiKit.CreateText(nameRow, "Name", string.Empty, TextStyle.Number.WithSize(32f));
            _glyph = UiKit.CreateImage(nameRow, "Glyph", UiSprites.Pistol, Color.white).Sized(110f, 66f);
            _glyph.preserveAspect = true;

            // State lines; at most one shows.
            _lowRow = UiKit.CreateRow(root, "Low", 0f, TextAnchor.MiddleRight, fitToContent: false);
            var low = UiKit.CreateText(_lowRow, "Text", "LOW AMMO", TextStyle.Small.WithSize(26f).WithTint(ColorRole.Warning));
            low.characterSpacing = 2f;

            _reloadRow = UiKit.CreateRow(root, "Reloading", 12f, TextAnchor.MiddleRight, fitToContent: false);
            UiKit.CreateText(_reloadRow, "Text", "reloading…", TextStyle.Small.WithSize(26f).WithTint(ColorRole.Cream));
            _reloadBar = SketchBar.Create(_reloadRow, "Bar", new Vector2(170f, 16f), theme.cream, shine: false);
            _reloadBar.SetTrackColor(new Color(0f, 0f, 0f, 0.6f));

            _emptyRow = UiKit.CreateRow(root, "Empty", 10f, TextAnchor.MiddleRight, fitToContent: false);
            KeyCap.Create(_emptyRow, "R", 40f);
            UiKit.CreateText(_emptyRow, "Text", "RELOAD!", TextStyle.Number.WithSize(32f).WithTint(ColorRole.Danger));

            _overheatRow = UiKit.CreateRow(root, "Overheated", 8f, TextAnchor.MiddleRight, fitToContent: false);
            IconStack.Create(_overheatRow, "Flame", new Vector2(30f, 30f), OverheatOrange,
                IconStack.TintLayer(UiSprites.FlameFill), IconStack.PlainLayer(UiSprites.FlameInk));
            var overheat = UiKit.CreateText(_overheatRow, "Text", "OVERHEATED!", TextStyle.Number.WithSize(32f));
            overheat.color = OverheatOrange;

            // The magazine: "17 / 17 ∞".
            var magRow = UiKit.CreateRow(root, "Magazine", 10f, TextAnchor.LowerRight, fitToContent: false);
            _magazine = UiKit.CreateText(magRow, "Count", string.Empty, TextStyle.BigNumber.WithSize(96f));
            // Knewave's line box is far taller than its digits; without a fixed height the count
            // opens a gap above itself.
            _magazine.alignment = TextAlignmentOptions.BottomRight;
            _magazine.gameObject.AddComponent<LayoutElement>().preferredHeight = 122f;
            _magazineSize = UiKit.CreateText(magRow, "Size", string.Empty, TextStyle.Number.WithSize(36f).WithTint(ColorRole.Faint));
            _infinity = UiKit.CreateIcon(magRow, "Unlimited", 34f, UiSprites.InfinityInk, UiSprites.InfinityLine, SubtleGrey);
            _reserve = UiKit.CreateText(magRow, "Reserve", string.Empty, TextStyle.Number.WithSize(34f).WithTint(ColorRole.Faint));

            // Heat, for weapons that overheat.
            _heatRow = UiKit.CreateRow(root, "Heat", 10f, TextAnchor.MiddleRight, fitToContent: false);
            _heatIcon = IconStack.Create(_heatRow, "Flame", new Vector2(24f, 24f), OverheatOrange,
                IconStack.TintLayer(UiSprites.FlameFill), IconStack.PlainLayer(UiSprites.FlameInk));
            _heatBar = SketchBar.Create(_heatRow, "Bar", new Vector2(240f, 18f), OverheatOrange, shine: false);
            _heatBar.SetTrackColor(new Color(0f, 0f, 0f, 0.6f));
        }

        public void Set(in WeaponData data)
        {
            _group.alpha = data.Visible ? 1f : 0f;
            if (!data.Visible)
                return;

            var theme = UiTheme.Active;

            // Slots.
            Show(_slots, data.ShowSlots && data.SlotCount > 1);
            if (data.ShowSlots)
            {
                for (int i = 0; i < MaxSlots; i++)
                {
                    bool used = i < data.SlotCount;
                    Show(_slotGroups[i], used);
                    if (!used)
                        continue;
                    _slotGroups[i].alpha = i + 1 == data.ActiveSlot ? 1f : 0.3f;
                    var glyph = data.SlotGlyphs != null && i < data.SlotGlyphs.Length ? data.SlotGlyphs[i] : WeaponGlyph.None;
                    SetGlyph(_slotGlyphs[i], glyph, small: true);
                    Show(_slotNames[i], glyph == WeaponGlyph.None);
                }
            }

            // Name and silhouette.
            if (_shownName != data.Name)
            {
                _shownName = data.Name;
                _name.text = string.IsNullOrEmpty(data.Name) ? string.Empty : data.Name.ToUpperInvariant();
            }
            if (_shownGlyph != data.Glyph)
            {
                _shownGlyph = data.Glyph;
                SetGlyph(_glyph, data.Glyph, small: false);
            }

            // One state line, most urgent first.
            bool overheated = data.Overheated;
            bool reloading = !overheated && data.Reloading;
            bool empty = !overheated && !reloading && data.IsEmpty;
            bool low = !overheated && !reloading && !empty && data.IsLow;
            Show(_overheatRow, overheated);
            Show(_reloadRow, reloading);
            Show(_emptyRow, empty);
            Show(_lowRow, low);
            if (reloading)
                _reloadBar.SetValue(data.Reload01);

            // Magazine.
            _magazine.color = empty ? theme.danger : low ? theme.warning : theme.white;
            if (_shownMagazine != data.Magazine)
            {
                _shownMagazine = data.Magazine;
                _magazine.SetText("{0}", data.Magazine);
            }
            if (_shownSize != data.MagazineSize)
            {
                _shownSize = data.MagazineSize;
                _magazineSize.SetText("/ {0}", data.MagazineSize);
            }

            Show(_infinity, data.UnlimitedReserve);
            Show(_reserve, !data.UnlimitedReserve);
            if (!data.UnlimitedReserve && _shownReserve != data.Reserve)
            {
                _shownReserve = data.Reserve;
                _reserve.SetText("{0}", data.Reserve);
            }

            // Heat.
            bool heat = data.Heat01 >= 0f;
            Show(_heatRow, heat);
            if (heat)
            {
                var colour = data.Heat01 >= 0.8f ? theme.danger : UiTheme.Rgb(0xFF8A3D);
                _heatIcon.SetTint(colour);
                _heatBar.SetFillColor(colour);
                _heatBar.SetValue(data.Heat01);
            }
        }

        static void SetGlyph(Image image, WeaponGlyph glyph, bool small)
        {
            string sprite = glyph == WeaponGlyph.Mp7 ? UiSprites.Mp7 : glyph == WeaponGlyph.Pistol ? UiSprites.Pistol : null;
            Show(image, sprite != null);
            if (sprite == null)
                return;

            image.sprite = UiKit.GetSprite(sprite);
            // The board draws the MP7 wider than the pistol; heights stay close so the row keeps its line.
            if (glyph == WeaponGlyph.Mp7)
                image.Sized(small ? 92f : 150f, small ? 46f : 74f);
            else
                image.Sized(small ? 70f : 110f, small ? 42f : 66f);
        }

        static void Show(Component component, bool visible)
        {
            if (component != null && component.gameObject.activeSelf != visible)
                component.gameObject.SetActive(visible);
        }
    }
}
