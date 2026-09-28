using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CollarCali.UI
{
    public enum LobbySlot
    {
        Empty,
        Host,
        Ready,
        NotReady,
    }

    /// <summary>One line of the lobby roster.</summary>
    public struct LobbyRowData
    {
        public string Name;
        public int Colour;
        public bool IsYou;
        public LobbySlot Status;
    }

    /// <summary>
    /// The lobby (boards 19 and 20): who is in, who is ready, the one button that matters to you -
    /// START GAME for the host, READY / CANCEL READY for everyone else - and your colour, picked on
    /// the right. Colours someone else has are struck out with their name under them.
    /// </summary>
    public class LobbyView : MonoBehaviour
    {
        public const int Slots = 4;
        public static readonly string[] ColourNames = { "ORANGE", "BLUE", "PINK", "LIME" };

        static readonly Vector2 Left = new Vector2(0f, 0.5f);
        static readonly Vector2 Right = new Vector2(1f, 0.5f);
        static readonly Color Subtle = UiTheme.Rgb(0xD5DAD8);
        static readonly Color Muted = UiTheme.Rgb(0xB9C0BE);
        static readonly Color Note = UiTheme.Rgb(0x8A9492);

        TextMeshProUGUI _subtitle;
        TextMeshProUGUI _hint;
        readonly LobbyRowView[] _rows = new LobbyRowView[Slots];
        MenuButton _primary;
        TextMeshProUGUI _primaryNote;
        MenuButton _leave;
        Button _nextColour;
        Image _figure;
        RawImage _liveFigure;
        CharacterPreviewStage _stage;
        TextMeshProUGUI _colourName;
        readonly ColourDot[] _dots = new ColourDot[Slots];

        public event Action Primary;
        public event Action Leave;
        public event Action<int> ColourStep;
        public event Action<int> ColourPick;

        public Button PrimaryButton => _primary.Button;

        public static LobbyView Create(Transform parent)
        {
            var root = UiKit.CreateRect("Lobby", parent);
            root.Fill();
            var view = root.gameObject.AddComponent<LobbyView>();
            view.Build(root);
            return view;
        }

        void Build(RectTransform root)
        {
            UiKit.CreateBackdrop(root, UiSprites.MenuCorridor, Color.white);
            var dim = UiKit.CreateImage(root, "Dim", UiSprites.White, new Color(0f, 0f, 0f, 0.5f));
            dim.rectTransform.Fill();
            var scrim = UiKit.CreateImage(root, "Scrim", UiSprites.Scrim, Color.white);
            scrim.rectTransform.anchorMin = new Vector2(0f, 0f);
            scrim.rectTransform.anchorMax = new Vector2(0f, 1f);
            scrim.rectTransform.pivot = new Vector2(0f, 0.5f);
            scrim.rectTransform.sizeDelta = new Vector2(1100f, 0f);

            var title = UiKit.CreateText(root, "Title", "LOBBY", TextStyle.Title.WithSize(110f));
            title.rectTransform.AtBoard(Left, 110f, 36f, 700f, 116f).Tilt(-3f);

            _subtitle = UiKit.CreateText(root, "Subtitle", string.Empty, TextStyle.Body.WithSize(34f));
            _subtitle.color = Subtle;
            _subtitle.rectTransform.AtBoard(Left, 120f, 172f, 900f, 44f);

            var roster = UiKit.CreateColumn(root, "Roster", 0f, TextAnchor.UpperLeft, fitToContent: false);
            roster.AtBoard(Left, 120f, 250f, 820f, 4f * 80f, fromTop: true);
            roster.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            for (int i = 0; i < Slots; i++)
                _rows[i] = LobbyRowView.Create(roster);

            var actions = UiKit.CreateRow(root, "Actions", 70f, TextAnchor.UpperLeft);
            actions.AtBoard(Left, 120f, 700f, 900f, 0f, fromTop: true);
            var primary = UiKit.CreateColumn(actions, "Primary", 0f, TextAnchor.UpperLeft, fitToContent: false);
            _primary = MenuButton.Create(primary, "READY", 56f, () => Primary?.Invoke());
            _primaryNote = UiKit.CreateText(primary, "Note", string.Empty, TextStyle.Small.WithSize(22f));
            _primaryNote.color = Note;
            _primaryNote.margin = new Vector4(44f, 0f, 0f, 0f);
            _leave = MenuButton.Create(actions, "LEAVE", 46f, () => Leave?.Invoke());

            _hint = UiKit.CreateText(root, "Hint", string.Empty, TextStyle.Body.WithSize(26f));
            _hint.color = Muted;
            _hint.rectTransform.AtBoard(Left, 120f, 1000f, 1000f, 34f);

            BuildColour(root);
        }

        void BuildColour(RectTransform root)
        {
            var label = UiKit.CreateText(root, "Your Colour", "YOUR COLOR", TextStyle.Caption.WithSize(24f));
            label.rectTransform.AtBoard(Right, 1240f, 196f, 400f, 30f);

            var turntable = UiKit.CreateImage(root, "Turntable", UiSprites.Turntable, new Color(1f, 1f, 1f, 0.7f));
            turntable.rectTransform.AtBoard(Right, 1485f - 330f, 612f - 170f, 660f, 340f, fromTop: true);

            _figure = UiKit.CreateImage(root, "Figure", UiSprites.Figures[0], Color.white);
            _figure.rectTransform.AtBoard(Right, 1320f, 220f, 330f, 440f, fromTop: true);

            // The character every player actually wears, live on the turntable and painted in the
            // chosen colour, in place of the drawn figure. The drawn figure stays as the fallback
            // when no preview model has been built (Tools > CollarCali > Build Character Visuals).
            _stage = CharacterPreviewStage.Create(660, 880);
            if (_stage != null)
            {
                var live = UiKit.CreateRect("Live Figure", root);
                live.AtBoard(Right, 1320f, 220f, 330f, 440f, fromTop: true);
                _liveFigure = live.gameObject.AddComponent<RawImage>();
                _liveFigure.texture = _stage.Texture;
                _liveFigure.raycastTarget = false;
                _figure.gameObject.SetActive(false);
            }

            Chevron(root, "Previous colour", UiSprites.ChevronLeftInk, UiSprites.ChevronLeftLine, 1210f, -1);
            _nextColour = Chevron(root, "Next colour", UiSprites.ChevronRightInk, UiSprites.ChevronRightLine, 1690f, 1);

            _colourName = UiKit.CreateText(root, "Colour", string.Empty,
                TextStyle.Heading.WithSize(72f).WithAlign(TextAlignmentOptions.Center));
            _colourName.rectTransform.AtBoard(Right, 1185f, 650f, 600f, 80f);

            var picker = UiKit.CreateRow(root, "Picker", 22f, TextAnchor.UpperCenter);
            picker.AtBoard(Right, 1250f, 760f, 400f, 0f, fromTop: true);
            for (int i = 0; i < Slots; i++)
            {
                int index = i;
                _dots[i] = ColourDot.Create(picker, i, () => ColourPick?.Invoke(index));
            }
        }

        Button Chevron(RectTransform root, string name, string ink, string line, float left, int step)
        {
            var holder = UiKit.CreateRect(name, root);
            holder.AtBoard(Right, left, 390f, 76f, 76f, fromTop: true);
            var hit = holder.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            var button = holder.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hit;
            button.onClick.AddListener(() => ColourStep?.Invoke(step));

            var icon = UiKit.CreateIcon(holder, "Icon", 40f, ink, line, UiTheme.Active.cream);
            var rect = (RectTransform)icon.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(32f, 50f);
            return button;
        }

        public void SetSubtitle(string text) => _subtitle.text = text ?? string.Empty;
        public void SetHint(string text) => _hint.text = text ?? string.Empty;

        /// <summary>The roster in join order; the remaining slots up to <paramref name="capacity"/> wait for a player.</summary>
        public void SetRoster(IReadOnlyList<LobbyRowData> rows, int capacity)
        {
            int count = rows != null ? rows.Count : 0;
            int shown = Mathf.Clamp(Mathf.Max(capacity, count), 1, Slots);
            for (int i = 0; i < Slots; i++)
            {
                var row = _rows[i];
                row.gameObject.SetActive(i < shown);
                if (i < count)
                    row.Set(rows[i]);
                else
                    row.Set(new LobbyRowData { Status = LobbySlot.Empty });
            }
        }

        /// <summary>The host's START GAME or a guest's READY / CANCEL READY, with an optional reason under it.</summary>
        public void SetPrimary(string label, bool visible, bool enabled, string note)
        {
            _primary.transform.parent.gameObject.SetActive(visible);
            _primary.SetLabel(label);
            _primary.Interactable = enabled;
            _primaryNote.text = note ?? string.Empty;
            _primaryNote.gameObject.SetActive(!string.IsNullOrEmpty(note));
        }

        /// <summary>Your colour, and who holds each of the others (null for free).</summary>
        public void SetColour(int chosen, IReadOnlyList<string> takenBy)
        {
            int colour = Mathf.Clamp(chosen, 0, Slots - 1);
            _figure.sprite = UiKit.GetSprite(UiSprites.Figures[colour]);
            if (_stage != null)
                _stage.SetColour(colour);
            _colourName.text = ColourNames[colour];
            for (int i = 0; i < Slots; i++)
            {
                string owner = takenBy != null && i < takenBy.Count ? takenBy[i] : null;
                _dots[i].Set(i == colour, owner);
            }
        }

        /// <summary>
        /// The primary button once it can be pressed, the colour picker until then. Never LEAVE:
        /// Enter on a pre-selected LEAVE would throw a player out of the room.
        /// </summary>
        public bool FocusDefault()
        {
            bool primary = _primary.Interactable && _primary.gameObject.activeInHierarchy;
            var events = EventSystem.current;
            if (events == null)
            {
                if (primary)
                    _primary.ForceFocus(true);
                return primary;
            }

            events.SetSelectedGameObject(primary ? _primary.gameObject : _nextColour.gameObject);
            return true;
        }

        /// <summary>For gallery pages: the focused look on the primary button.</summary>
        public void ShowPrimaryFocused(bool focused) => _primary.ForceFocus(focused);

        void OnDestroy()
        {
            // The stage is its own object away from the UI, with a render texture to release.
            if (_stage != null)
                Destroy(_stage.gameObject);
        }
    }

    /// <summary>A roster line: colour blob, name, (you), and HOST / READY / NOT READY - or an empty seat.</summary>
    public class LobbyRowView : MonoBehaviour
    {
        static readonly Color Gold = UiTheme.Rgb(0xFFC940);
        static readonly Color Green = UiTheme.Rgb(0x7CE36A);
        static readonly Color Grey = UiTheme.Rgb(0x8A9492);
        static readonly Color EmptyInk = UiTheme.Rgb(0x6E7876);
        static readonly Color RowLine = UiTheme.Rgb(0x3F4846);

        IconStack _swatch;
        Image _empty;
        TextMeshProUGUI _name;
        TextMeshProUGUI _you;
        Image _crown;
        IconStack _check;
        Image _minus;
        TextMeshProUGUI _status;

        public static LobbyRowView Create(Transform parent)
        {
            var root = UiKit.CreateColumn(parent, "Player", 0f, TextAnchor.UpperLeft, fitToContent: false);
            root.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            var view = root.gameObject.AddComponent<LobbyRowView>();
            view.Build(root);
            return view;
        }

        void Build(RectTransform root)
        {
            var line = UiKit.CreateRect("Line", root);
            var element = line.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = 78f;

            var left = UiKit.CreateRow(line, "Who", 18f);
            left.anchorMin = left.anchorMax = left.pivot = new Vector2(0f, 0.5f);
            left.anchoredPosition = Vector2.zero;

            var badge = UiKit.CreateRect("Badge", left);
            badge.Sized(52f, 52f);
            _swatch = UiKit.CreateSwatch(badge, Color.white, 52f);
            ((RectTransform)_swatch.transform).Fill();
            _empty = UiKit.CreateImage(badge, "Empty", UiSprites.Ring, EmptyInk);
            _empty.rectTransform.Fill(4f);

            _name = UiKit.CreateText(left, "Name", string.Empty, TextStyle.Prompt.WithSize(40f));
            _you = UiKit.CreateText(left, "You", "(you)", TextStyle.Small.WithSize(24f));

            var right = UiKit.CreateRow(line, "Status", 10f);
            right.anchorMin = right.anchorMax = right.pivot = new Vector2(1f, 0.5f);
            right.anchoredPosition = Vector2.zero;
            var glyph = UiKit.CreateRect("Glyph", right);
            glyph.Sized(30f, 30f);
            _crown = UiKit.CreateImage(glyph, "Crown", UiSprites.CrownGlyph, Gold);
            _crown.rectTransform.Fill();
            _check = UiKit.CreateIcon(glyph, "Check", 30f, UiSprites.CheckInk, UiSprites.CheckLine, Green);
            ((RectTransform)_check.transform).Fill();
            _minus = UiKit.CreateImage(glyph, "Minus", UiSprites.MinusGlyph, Grey);
            _minus.rectTransform.Fill();
            _status = UiKit.CreateText(right, "Text", string.Empty, TextStyle.Number.WithSize(30f));
            _status.characterSpacing = 4f;

            var rule = UiKit.CreateImage(root, "Rule", UiSprites.SketchLine, RowLine);
            var ruleElement = rule.gameObject.AddComponent<LayoutElement>();
            ruleElement.minHeight = ruleElement.preferredHeight = 6f;
        }

        public void Set(LobbyRowData data)
        {
            bool empty = data.Status == LobbySlot.Empty;
            _swatch.gameObject.SetActive(!empty);
            _empty.gameObject.SetActive(empty);
            if (!empty)
                _swatch.SetTint(PlayerColorPalette.Get(data.Colour));

            _name.text = empty ? "waiting for a player…" : data.Name;
            _name.fontSize = empty ? 34f : 40f;
            _name.color = empty ? EmptyInk : Color.white;
            _you.gameObject.SetActive(!empty && data.IsYou);

            _crown.gameObject.SetActive(data.Status == LobbySlot.Host);
            _check.gameObject.SetActive(data.Status == LobbySlot.Ready);
            _minus.gameObject.SetActive(data.Status == LobbySlot.NotReady);
            _status.transform.parent.gameObject.SetActive(!empty);
            switch (data.Status)
            {
                case LobbySlot.Host:
                    _status.text = "HOST";
                    _status.color = Gold;
                    break;
                case LobbySlot.Ready:
                    _status.text = "READY";
                    _status.color = Green;
                    break;
                case LobbySlot.NotReady:
                    _status.text = "NOT READY";
                    _status.color = Grey;
                    break;
            }
        }
    }

    /// <summary>One colour in the picker: chosen is bigger and underlined; someone else's is struck out and named.</summary>
    public class ColourDot : MonoBehaviour
    {
        const float Dot = 26f;
        static readonly Color StrikeRed = UiTheme.Rgb(0xFF5A52);
        static readonly Color OwnerInk = UiTheme.Rgb(0x8A9492);

        RectTransform _disc;
        Image _fill;
        CanvasGroup _group;
        GameObject _strike;
        Image _underline;
        TextMeshProUGUI _owner;
        Button _button;

        public static ColourDot Create(Transform parent, int colour, UnityEngine.Events.UnityAction onPick)
        {
            var root = UiKit.CreateColumn(parent, "Colour " + colour, 4f, TextAnchor.UpperCenter, fitToContent: false);
            var element = root.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = 70f;
            element.minHeight = element.preferredHeight = 70f;
            var dot = root.gameObject.AddComponent<ColourDot>();
            dot.Build(root, colour, onPick);
            return dot;
        }

        void Build(RectTransform root, int colour, UnityEngine.Events.UnityAction onPick)
        {
            var holder = UiKit.CreateRect("Holder", root);
            holder.Sized(40f, 40f);
            var hit = holder.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            _button = holder.gameObject.AddComponent<Button>();
            _button.transition = Selectable.Transition.None;
            _button.targetGraphic = hit;
            _button.onClick.AddListener(onPick);

            _disc = UiKit.CreateRect("Disc", holder);
            _disc.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(Dot, Dot));
            _group = _disc.gameObject.AddComponent<CanvasGroup>();
            var ink = UiKit.CreateImage(_disc, "Ink", UiSprites.Circle, UiTheme.Active.ink);
            ink.rectTransform.Fill();
            _fill = UiKit.CreateImage(_disc, "Fill", UiSprites.Circle, PlayerColorPalette.Get(colour));
            _fill.rectTransform.Fill(3f);

            // A red slash with an ink edge over a colour someone else has - at full strength, over
            // the faded dot.
            _strike = UiKit.CreateRect("Strike", holder).gameObject;
            var strike = (RectTransform)_strike.transform;
            strike.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(Dot, Dot));
            var edge = UiKit.CreateImage(strike, "Edge", UiSprites.White, UiTheme.Active.ink);
            edge.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(Dot * 1.25f, 7f));
            edge.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            var slash = UiKit.CreateImage(strike, "Slash", UiSprites.White, StrikeRed);
            slash.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(Dot * 1.2f, 3.5f));
            slash.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);

            _underline = UiKit.CreateImage(root, "Underline", UiSprites.Underline, UiTheme.Active.cream);
            _underline.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _underline.rectTransform.anchorMin = _underline.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            _underline.rectTransform.pivot = new Vector2(0.5f, 1f);
            _underline.rectTransform.anchoredPosition = new Vector2(0f, -44f);
            _underline.rectTransform.sizeDelta = new Vector2(56f, 10f);

            _owner = UiKit.CreateText(root, "Owner", string.Empty,
                TextStyle.Small.WithSize(18f).WithAlign(TextAlignmentOptions.Center));
            _owner.color = OwnerInk;
        }

        public void Set(bool chosen, string takenBy)
        {
            bool taken = !chosen && !string.IsNullOrEmpty(takenBy);
            float size = chosen ? Dot + 12f : Dot;
            _disc.sizeDelta = new Vector2(size, size);
            _group.alpha = taken ? 0.35f : 1f;
            _strike.SetActive(taken);
            _underline.gameObject.SetActive(chosen);
            _owner.text = taken ? takenBy : string.Empty;
            _button.interactable = !taken;
        }
    }
}
