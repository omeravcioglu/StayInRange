using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>Where the local player's body is on its way back.</summary>
    public enum BodyStep
    {
        Down,
        Lifted,
        AtStation,
        Revived,
    }

    /// <summary>What the spectating screen shows (boards 02 and 03).</summary>
    public struct SpectatorData
    {
        public string WatchName;
        public Color WatchColour;
        public int WatchIndex;
        public int WatchCount;
        public bool NobodyStanding;

        public Color SelfColour;
        public BodyStep Step;
        public string DownText;
        public string LiftedText;
        public string StationText;
    }

    /// <summary>
    /// The full-screen moments of the death and revive flow, one at a time: YOU DIED, spectating
    /// with the body tracker, IT'S ALIVE!, a teammate going down, being grabbed, the team wipe.
    ///
    /// Two layers: the backdrops (tints, splats, edge glows) sit under the HUD so the team panel
    /// stays readable on top of them, as on the boards; the words sit over everything.
    /// Positions are the boards', measured from the centre of a 1920x1080 screen.
    /// </summary>
    public class MomentsView : MonoBehaviour
    {
        static readonly Color DeathTint = UiTheme.Rgb(0x6E0806, 0.35f);
        static readonly Color DeathEdges = UiTheme.Rgb(0x6E0402, 0.9f);
        static readonly Color Soft = UiTheme.Rgb(0xD5DAD8);
        static readonly Color StepGrey = UiTheme.Rgb(0x8A9492);

        enum Card
        {
            None,
            Death,
            Spectating,
            Revived,
            TeammateDown,
            Grabbed,
            TeamWipe,
        }

        Card _card = (Card)(-1);

        // Backdrops.
        Image _tint;
        Image _edges;
        Image _splat;
        Image _topSplat;

        // Words.
        CanvasGroup _death;
        TextMeshProUGUI _deathTitle;
        TextMeshProUGUI _deathAside;
        TextMeshProUGUI _deathLine;
        TextMeshProUGUI _deathCountdown;

        CanvasGroup _spectating;
        TrackerRow[] _steps;
        IconStack _selfBlob;
        TextMeshProUGUI _spectatingLine;
        IconStack _watchBlob;
        TextMeshProUGUI _watchCount;
        TextMeshProUGUI _watchName;
        Image _watchUnderline;
        RectTransform _controls;

        CanvasGroup _revived;
        TextMeshProUGUI _revivedLine;

        CanvasGroup _down;
        TextMeshProUGUI _downTitle;
        TextMeshProUGUI _downLine;

        CanvasGroup _grabbed;

        CanvasGroup _wipe;
        TextMeshProUGUI _wipeLine;
        TextMeshProUGUI _wipeCountdown;

        TextMeshProUGUI _toast;
        float _toastUntil;

        int _shownCountdown = -1;
        string _shownWatch;
        int _shownIndex = -1;
        int _shownCount = -1;

        struct TrackerRow
        {
            public IconStack Done;
            public Image Ring;
            public Image Dot;
            public Image Line;
            public TextMeshProUGUI Title;
            public TextMeshProUGUI Detail;
        }

        public static MomentsView Create(Transform backLayer, Transform frontLayer)
        {
            // Backdrops first: when both layers share a canvas (the gallery), sibling order is draw order.
            var backRoot = UiKit.CreateRect("MomentBackdrops", backLayer);
            backRoot.Fill();

            var root = UiKit.CreateRect("Moments", frontLayer);
            root.Fill();
            var view = root.gameObject.AddComponent<MomentsView>();
            view.Build(backRoot, root);
            view.HideAll();
            return view;
        }

        #region Build

        void Build(RectTransform backRoot, RectTransform front)
        {
            var theme = UiTheme.Active;

            _tint = UiKit.CreateImage(backRoot, "Tint", UiSprites.White, Color.clear);
            _tint.rectTransform.Fill();
            _edges = UiKit.CreateImage(backRoot, "Edges", UiSprites.Vignette, Color.clear);
            _edges.rectTransform.Fill();
            _splat = UiKit.CreateImage(backRoot, "Splat", UiSprites.Splat, Color.white);
            _splat.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -2f), new Vector2(1400f, 560f));
            _topSplat = UiKit.CreateImage(backRoot, "TopSplat", UiSprites.Splat, Color.white);
            _topSplat.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0f, -6f), new Vector2(640f, 256f));

            // 01: YOU DIED, then the hand-off to spectating.
            _death = Group(front, "Death");
            _deathTitle = Centred(_death.transform, "Title", "YOU DIED", TextStyle.Title.WithSize(200f), 0f, 31f, -3f);
            _deathAside = Aside(_death.transform, "…for now.", 236f, -131f);
            _deathLine = Centred(_death.transform, "Line", "Hang tight. A teammate can lift your body to a revive station.",
                TextStyle.Body.WithSize(40f), 0f, -324f);
            _deathCountdown = Centred(_death.transform, "Countdown", string.Empty, TextStyle.Body.WithSize(28f), 0f, -467f);
            _deathCountdown.color = Soft;

            BuildSpectating(front, theme);

            // 04: back from the dead.
            _revived = Group(front, "Revived");
            Centred(_revived.transform, "Title", "IT'S ALIVE!", TextStyle.Title.WithSize(190f), 0f, 49f, -3f);
            _revivedLine = Centred(_revived.transform, "Line", string.Empty, TextStyle.Body.WithSize(40f), 0f, -320f);

            // 05: a teammate went down (the survivors' banner).
            _down = Group(front, "TeammateDown");
            _downTitle = TopCentred(_down.transform, "Title", string.Empty, TextStyle.Heading.WithSize(76f), 130f, -3f);
            _downLine = TopCentred(_down.transform, "Line", string.Empty, TextStyle.Body.WithSize(30f), 234f);
            var note = TopCentred(_down.transform, "Note", "Bodies still count for the 30 m collar.", TextStyle.Small.WithSize(24f), 278f);
            note.color = Soft;

            // 07: grabbed by a creep.
            _grabbed = Group(front, "Grabbed");
            Centred(_grabbed.transform, "Title", "YOU ARE GRABBED!", TextStyle.Title.WithSize(150f), 0f, 78f, -4f);
            Centred(_grabbed.transform, "Line", "Your team can shoot it off you!", TextStyle.Body.WithSize(40f), 0f, -204f);
            var drag = Centred(_grabbed.transform, "Warning", "It is dragging you away from your team!",
                TextStyle.Body.WithSize(30f), 0f, -262f);
            drag.color = theme.danger;

            // 08: the whole team is down.
            _wipe = Group(front, "TeamWipe");
            Centred(_wipe.transform, "Title", "YOU DIED", TextStyle.Title.WithSize(210f), 0f, 69f, -3f);
            Aside(_wipe.transform, "…all of you.", 190f, -95f);
            _wipeLine = Centred(_wipe.transform, "Line", string.Empty, TextStyle.Body.WithSize(40f), 0f, -318f);
            _wipeCountdown = Centred(_wipe.transform, "Countdown", string.Empty, TextStyle.Body.WithSize(26f), 0f, -437f);
            _wipeCountdown.color = Soft;

            // "SKIP lifted you!" and friends.
            _toast = Centred(front, "Toast", string.Empty, TextStyle.Heading.WithSize(56f), 0f, 120f, -2f);
            _toast.gameObject.SetActive(false);
        }

        void BuildSpectating(RectTransform front, UiTheme theme)
        {
            _spectating = Group(front, "Spectating");

            // Title block, top centre, over its splat.
            TopCentred(_spectating.transform, "Title", "SPECTATING", TextStyle.Title.WithSize(92f), 116f, -3f);
            _spectatingLine = TopCentred(_spectating.transform, "Line", "You are dead. Watching your teammates…",
                TextStyle.Body.WithSize(28f), 231f);

            // YOUR BODY tracker, top right.
            var tracker = UiKit.CreateColumn(_spectating.transform, "Tracker", 14f, TextAnchor.UpperLeft, fitToContent: false);
            tracker.Place(new Vector2(1f, 1f), new Vector2(-50f, -44f), new Vector2(364f, 340f));

            var header = UiKit.CreateRow(tracker, "Header", 12f, TextAnchor.MiddleLeft, fitToContent: false);
            _selfBlob = UiKit.CreateSwatch(header, theme.dim, 52f);
            var skull = UiKit.CreateImage(_selfBlob.transform, "Down", UiSprites.Skull, Color.white);
            skull.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(32f, 32f));
            skull.preserveAspect = true;
            var title = UiKit.CreateText(header, "Title", "YOUR BODY", TextStyle.Number.WithSize(30f));
            title.characterSpacing = 2f;

            var list = UiKit.CreateColumn(tracker, "Steps", 0f, TextAnchor.UpperLeft, fitToContent: false);
            _steps = new TrackerRow[4];
            string[] names = { "Down", "Lifted", "At a revive station", "Revived!" };
            for (int i = 0; i < _steps.Length; i++)
                _steps[i] = CreateStep(list, names[i], i < _steps.Length - 1);

            // Previous / watching / next, bottom centre.
            _controls = UiKit.CreateRow(_spectating.transform, "Controls", 56f, TextAnchor.MiddleCenter);
            _controls.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -394f), new Vector2(1200f, 110f));

            SwitchHint(_controls, "Previous player", UiSprites.MouseRight, UiSprites.ChevronLeftInk, UiSprites.ChevronLeftLine, chevronFirst: true);

            var watch = UiKit.CreateRow(_controls, "Watching", 18f, TextAnchor.MiddleLeft, fitToContent: false);
            _watchBlob = UiKit.CreateSwatch(watch, Color.white, 84f);
            var names2 = UiKit.CreateColumn(watch, "Names", 0f, TextAnchor.MiddleLeft, fitToContent: false);
            _watchCount = UiKit.CreateText(names2, "Count", string.Empty, TextStyle.Small.WithSize(22f));
            _watchCount.characterSpacing = 4f;
            _watchCount.color = Soft;
            _watchName = UiKit.CreateText(names2, "Name", string.Empty, TextStyle.Title.WithSize(66f));
            _watchUnderline = UiKit.CreateImage(names2, "Underline", UiSprites.Underline, Color.white).Sized(200f, 14f);

            SwitchHint(_controls, "Next player", UiSprites.MouseLeft, UiSprites.ChevronRightInk, UiSprites.ChevronRightLine, chevronFirst: false);
        }

        static TrackerRow CreateStep(Transform list, string title, bool withLine)
        {
            var theme = UiTheme.Active;
            var row = UiKit.CreateRow(list, title, 14f, TextAnchor.UpperLeft, fitToContent: false);

            var marker = UiKit.CreateColumn(row, "Marker", 2f, TextAnchor.UpperCenter, fitToContent: false).Sized(34f, withLine ? 66f : 34f);
            var icon = UiKit.CreateRect("Icon", marker).Sized(34f, 34f);

            var step = new TrackerRow();
            step.Done = UiKit.CreateIcon(icon, "Done", 34f, UiSprites.CheckInk, UiSprites.CheckLine, theme.health);
            ((RectTransform)step.Done.transform).Fill();
            step.Ring = UiKit.CreateImage(icon, "Ring", UiSprites.Ring, theme.warning);
            step.Ring.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(38f, 38f));
            step.Dot = UiKit.CreateImage(icon, "Dot", UiSprites.Circle, theme.warning);
            step.Dot.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(13f, 13f));

            if (withLine)
                step.Line = UiKit.CreateImage(marker, "Line", UiSprites.White, theme.health).Sized(3f, 30f);

            var texts = UiKit.CreateColumn(row, "Texts", 0f, TextAnchor.UpperLeft, fitToContent: false);
            step.Title = UiKit.CreateText(texts, "Title", title, TextStyle.Body.WithSize(26f));
            step.Detail = UiKit.CreateText(texts, "Detail", string.Empty, TextStyle.Small.WithSize(21f).WithTint(ColorRole.Muted));
            return step;
        }

        static void SwitchHint(Transform parent, string label, string mouse, string chevronInk, string chevronLine, bool chevronFirst)
        {
            var column = UiKit.CreateColumn(parent, label, 10f, TextAnchor.MiddleCenter, fitToContent: false);
            UiKit.CreateText(column, "Label", label, TextStyle.Body.WithSize(28f).WithAlign(TextAlignmentOptions.Center));
            var icons = UiKit.CreateRow(column, "Icons", 24f, TextAnchor.MiddleCenter, fitToContent: false);
            if (chevronFirst)
                Chevron(icons, chevronInk, chevronLine);
            UiKit.CreateImage(icons, "Mouse", mouse, Color.white).Sized(40f, 58f).preserveAspect = true;
            if (!chevronFirst)
                Chevron(icons, chevronInk, chevronLine);
        }

        static void Chevron(Transform parent, string ink, string line)
        {
            IconStack.Create(parent, "Chevron", new Vector2(28f, 44f), UiTheme.Active.cream,
                IconStack.PlainLayer(ink), IconStack.TintLayer(line));
        }

        static CanvasGroup Group(Transform parent, string name)
        {
            var rect = UiKit.CreateRect(name, parent);
            rect.Fill();
            return rect.gameObject.AddComponent<CanvasGroup>();
        }

        /// <summary>A line centred on the screen's middle, <paramref name="y"/> up from it.</summary>
        static TextMeshProUGUI Centred(Transform parent, string name, string text, TextStyle style, float x, float y, float tilt = 0f)
        {
            var label = UiKit.CreateText(parent, name, text, style.WithAlign(TextAlignmentOptions.Center));
            label.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(1800f, style.Size * 1.3f));
            label.rectTransform.Tilt(tilt);
            return label;
        }

        /// <summary>A line centred horizontally, <paramref name="fromTop"/> down from the top edge.</summary>
        static TextMeshProUGUI TopCentred(Transform parent, string name, string text, TextStyle style, float fromTop, float tilt = 0f)
        {
            var label = UiKit.CreateText(parent, name, text, style.WithAlign(TextAlignmentOptions.Center));
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            label.rectTransform.anchoredPosition = new Vector2(0f, -fromTop);
            label.rectTransform.sizeDelta = new Vector2(1600f, style.Size * 1.3f);
            label.rectTransform.Tilt(tilt);
            return label;
        }

        /// <summary>The yellow, tilted aside next to a title: "…for now.", "…all of you."</summary>
        static TextMeshProUGUI Aside(Transform parent, string text, float left, float y)
        {
            var label = UiKit.CreateText(parent, "Aside", text,
                TextStyle.Heading.WithSize(64f).WithTint(ColorRole.Warning).WithAlign(TextAlignmentOptions.Left));
            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(left, y);
            rect.sizeDelta = new Vector2(500f, 84f);
            rect.Tilt(-7f);
            return label;
        }

        #endregion

        #region Show

        public void HideAll() => Show(Card.None);

        void Show(Card card)
        {
            if (card == _card)
                return;
            _card = card;
            _shownCountdown = -1;

            SetGroup(_death, card == Card.Death);
            SetGroup(_spectating, card == Card.Spectating);
            SetGroup(_revived, card == Card.Revived);
            SetGroup(_down, card == Card.TeammateDown);
            SetGroup(_grabbed, card == Card.Grabbed);
            SetGroup(_wipe, card == Card.TeamWipe);

            var theme = UiTheme.Active;
            switch (card)
            {
                case Card.Death:
                    Backdrop(DeathTint, DeathEdges, splat: true, topSplat: false);
                    break;
                case Card.TeamWipe:
                    Backdrop(new Color(0f, 0f, 0f, 0.55f), DeathEdges, splat: true, topSplat: false);
                    break;
                case Card.Grabbed:
                    Backdrop(new Color(0f, 0f, 0f, 0.62f), new Color(0.55f, 0.02f, 0.02f, 0.9f), splat: false, topSplat: false);
                    break;
                case Card.Spectating:
                    Backdrop(Color.clear, new Color(0f, 0f, 0f, 0.55f), splat: false, topSplat: true);
                    break;
                case Card.Revived:
                    Backdrop(Color.clear, new Color(theme.revive.r, theme.revive.g, theme.revive.b, 0.5f), splat: false, topSplat: false);
                    break;
                default:
                    Backdrop(Color.clear, Color.clear, splat: false, topSplat: false);
                    break;
            }
        }

        void Backdrop(Color tint, Color edges, bool splat, bool topSplat)
        {
            _tint.color = tint;
            _edges.color = edges;
            _splat.gameObject.SetActive(splat);
            _topSplat.gameObject.SetActive(topSplat);
        }

        static void SetGroup(CanvasGroup group, bool visible)
        {
            if (group.gameObject.activeSelf != visible)
                group.gameObject.SetActive(visible);
        }

        /// <summary>01: YOU DIED, fading in; <paramref name="secondsLeft"/> until spectating starts.</summary>
        public void ShowDeath(float age, int secondsLeft)
        {
            Show(Card.Death);
            _death.alpha = Mathf.Clamp01(age / 0.45f);
            _deathTitle.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.15f, 1f, Mathf.Clamp01(age / 0.45f));
            Countdown(_deathCountdown, secondsLeft, "Spectating your team in {0}…");
        }

        /// <summary>02 / 03: watching a teammate, with where the body is on its way back.</summary>
        public void ShowSpectating(in SpectatorData data)
        {
            Show(Card.Spectating);
            _spectating.alpha = 1f;
            var theme = UiTheme.Active;

            _selfBlob.SetTint(Color.Lerp(data.SelfColour, theme.dim, 0.6f));
            SetStep(0, data.Step, "Down", data.DownText);
            SetStep(1, data.Step, "Lifted", data.LiftedText);
            SetStep(2, data.Step, "At a revive station", data.StationText);
            SetStep(3, data.Step, "Revived!", "You stand up at the station");

            _spectatingLine.text = data.NobodyStanding
                ? "Nobody is standing. Waiting for the round to restart…"
                : "You are dead. Watching your teammates…";

            _controls.gameObject.SetActive(!data.NobodyStanding);
            if (data.NobodyStanding)
                return;

            _watchBlob.SetTint(data.WatchColour);
            _watchUnderline.color = data.WatchColour;
            if (_shownWatch != data.WatchName)
            {
                _shownWatch = data.WatchName;
                _watchName.text = data.WatchName ?? string.Empty;
            }
            if (_shownIndex != data.WatchIndex || _shownCount != data.WatchCount)
            {
                _shownIndex = data.WatchIndex;
                _shownCount = data.WatchCount;
                _watchCount.SetText("WATCHING · {0} OF {1}", data.WatchIndex, data.WatchCount);
            }
        }

        void SetStep(int index, BodyStep current, string title, string detail)
        {
            var theme = UiTheme.Active;
            var step = _steps[index];
            bool done = index < (int)current;
            bool now = index == (int)current;

            step.Done.gameObject.SetActive(done);
            step.Ring.gameObject.SetActive(!done);
            step.Dot.gameObject.SetActive(now);
            step.Ring.color = now ? theme.warning : theme.dim;
            if (step.Line != null)
                step.Line.color = done ? theme.health : theme.dim;

            step.Title.color = done ? theme.cream : now ? theme.warning : StepGrey;
            step.Detail.color = done || now ? theme.muted : StepGrey;
            if (step.Detail.text != (detail ?? string.Empty))
                step.Detail.text = detail ?? string.Empty;
        }

        /// <summary>04: "IT'S ALIVE!", then it fades.</summary>
        public void ShowRevived(string byWho, float age, float seconds)
        {
            Show(Card.Revived);
            _revived.alpha = age < seconds - 0.5f ? 1f : Mathf.Clamp01((seconds - age) / 0.5f);
            var line = string.IsNullOrEmpty(byWho)
                ? "Your team dragged you back from the dead."
                : byWho + " dragged you back from the dead.";
            if (_revivedLine.text != line)
                _revivedLine.text = line;
        }

        /// <summary>05: a teammate is down - tell the living what to do about it.</summary>
        public void ShowTeammateDown(string who)
        {
            Show(Card.TeammateDown);
            var title = (who ?? "SOMEONE").ToUpperInvariant() + " IS DOWN!";
            if (_downTitle.text != title)
            {
                _downTitle.text = title;
                _downLine.text = "Lift " + who + " and get them to a revive station.";
            }
        }

        /// <summary>07: grabbed by a creep.</summary>
        public void ShowGrabbed()
        {
            Show(Card.Grabbed);
            // The edges breathe while it holds on.
            float pulse = 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 6f);
            _edges.color = new Color(0.55f, 0.02f, 0.02f, 0.9f * pulse);
        }

        /// <summary>08: the whole team is down.</summary>
        public void ShowTeamWipe(string line, int secondsLeft)
        {
            Show(Card.TeamWipe);
            if (_wipeLine.text != (line ?? string.Empty))
                _wipeLine.text = line ?? string.Empty;
            Countdown(_wipeCountdown, secondsLeft, "Back to the checkpoint in {0}…");
        }

        void Countdown(TextMeshProUGUI label, int seconds, string format)
        {
            if (seconds == _shownCountdown)
                return;
            _shownCountdown = seconds;
            label.SetText(format, Mathf.Max(0, seconds));
        }

        /// <summary>A short line over the middle: "SKIP lifted you!".</summary>
        public void Toast(string text, Color colour, float seconds = 2.5f)
        {
            _toast.text = text;
            _toast.color = colour;
            _toast.gameObject.SetActive(true);
            _toastUntil = Time.unscaledTime + seconds;
        }

        void Update()
        {
            if (!_toast.gameObject.activeSelf)
                return;

            float left = _toastUntil - Time.unscaledTime;
            if (left <= 0f)
            {
                _toast.gameObject.SetActive(false);
                return;
            }

            var colour = _toast.color;
            colour.a = Mathf.Clamp01(left / 0.4f);
            _toast.color = colour;
        }

        #endregion
    }
}
