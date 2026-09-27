using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace CollarCali.UI
{
    /// <summary>
    /// The gallery's pages: every state the design draws, built with mock data so it can be checked
    /// without a session, two players or a level. Each phase of the migration adds its boards here,
    /// and Tools/CollarCali/UI/Capture Gallery renders every page to a PNG for side-by-side review
    /// against the canvas.
    /// </summary>
    public static class UiScenarios
    {
        public sealed class Scenario
        {
            public readonly string Name;
            public readonly Action<RectTransform> Build;

            public Scenario(string name, Action<RectTransform> build)
            {
                Name = name;
                Build = build;
            }
        }

        public static readonly List<Scenario> All = BuildList();

        static List<Scenario> BuildList()
        {
            var list = new List<Scenario> { new Scenario("00-kit", KitSheet.Build) };
            HudScenarios.Register(list);
            MomentScenarios.Register(list);
            SessionScenarios.Register(list);
            ScreenScenarios.Register(list);
            return list;
        }

        /// <summary>
        /// A page covering the whole canvas, as the game's screens do, on the dark ground the
        /// design's sheets use - so a capture off 16:9 shows what really happens at the edges.
        /// </summary>
        public static RectTransform CreatePage(Transform parent)
        {
            var page = UiKit.CreateRect("Page", parent);
            page.Fill();

            var ground = UiKit.CreateImage(page, "Ground", UiSprites.White, UiTheme.Rgb(0x0E1213));
            ground.rectTransform.Fill();
            return page;
        }

        /// <summary>Pins a rect by its top-left corner, measured from the page's top-left like the boards.</summary>
        public static RectTransform At(RectTransform rect, float x, float y)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            return rect;
        }

        /// <summary>A label pinned by its top-left corner and sized to its text.</summary>
        public static TextMeshProUGUI Label(RectTransform page, string text, TextStyle style, float x, float y)
        {
            var label = UiKit.CreateText(page, text, text, style);
            var size = label.GetPreferredValues(text);
            label.rectTransform.sizeDelta = new Vector2(size.x, Mathf.Max(size.y, style.Size * 1.2f));
            At(label.rectTransform, x, y);
            return label;
        }
    }

    /// <summary>
    /// Page 00: the kit on its own - both faces in every text style, key caps, bars, player colours
    /// and the icon set - so fonts, outline presets and sprites can be judged before any screen uses
    /// them.
    /// </summary>
    static class KitSheet
    {
        public static void Build(RectTransform page)
        {
            var theme = UiTheme.Active;

            UiScenarios.Label(page, "UI KIT", TextStyle.Heading.WithSize(60f), 72f, 48f);
            UiScenarios.Label(page, "Fonts, ink, keys, bars, colours and icons, built by UiKit from the UiTheme asset.",
                TextStyle.Small, 72f, 124f);

            // Text styles.
            UiScenarios.Label(page, "TEXT", TextStyle.Section, 72f, 180f);
            UiScenarios.Label(page, "YOU DIED", TextStyle.Title, 72f, 216f);
            UiScenarios.Label(page, "DOT IS DOWN!", TextStyle.Heading, 760f, 250f);
            UiScenarios.Label(page, "…for now.", TextStyle.Body.WithSize(40f), 1260f, 262f);

            UiScenarios.Label(page, "NIBS", TextStyle.Name, 72f, 380f);
            UiScenarios.Label(page, "you", TextStyle.Small, 176f, 388f);
            UiScenarios.Label(page, "12m", TextStyle.Number.WithTint(ColorRole.Safe), 260f, 378f);
            UiScenarios.Label(page, "23m", TextStyle.Number.WithTint(ColorRole.Warning), 350f, 378f);
            UiScenarios.Label(page, "31m", TextStyle.Number.WithTint(ColorRole.Danger), 440f, 378f);
            UiScenarios.Label(page, "Lift DOT and get them to a revive station.", TextStyle.Body, 560f, 382f);
            UiScenarios.Label(page, "YOUR NAME", TextStyle.Caption, 1320f, 386f);
            UiScenarios.Label(page, "17", TextStyle.BigNumber, 1580f, 360f);

            // Keys and prompts.
            UiScenarios.Label(page, "KEYS & PROMPTS", TextStyle.Section, 72f, 460f);
            var keys = UiScenarios.At(UiKit.CreateRow(page, "Keys", 14f), 72f, 516f);
            KeyCap.Create(keys, "E", 54f);
            KeyCap.Create(keys, "Q", 54f);
            KeyCap.Create(keys, "R", 54f, enabled: false);
            KeyCap.Create(keys, "SPACE", 54f);
            KeyCap.Create(keys, "MOUSE 1", 54f);

            var prompts = UiScenarios.At(UiKit.CreateRow(page, "Prompts", 56f), 700f, 516f);
            UiKit.CreateAction(prompts, "E", "Drop", TextStyle.Prompt);
            UiKit.CreateAction(prompts, "Q", "Throw", TextStyle.Prompt);
            var catchPrompt = UiScenarios.At(UiKit.CreateRow(page, "Catch", 12f), 1240f, 516f);
            UiKit.CreateAction(catchPrompt, "E", "CATCH DOT!", TextStyle.Prompt.WithTint(ColorRole.Warning).WithSize(46f));

            // Bars.
            UiScenarios.Label(page, "BARS", TextStyle.Section, 72f, 620f);
            var healthy = SketchBar.Create(page, "Health", new Vector2(236f, 30f), theme.health);
            healthy.SetValue(0.85f);
            UiScenarios.At((RectTransform)healthy.transform, 72f, 676f);

            var hurt = SketchBar.Create(page, "Hurt", new Vector2(236f, 30f), theme.danger);
            hurt.SetValue(0.22f);
            UiScenarios.At((RectTransform)hurt.transform, 340f, 676f);

            var charge = SketchBar.Create(page, "Charge", new Vector2(260f, 22f), UiKit.ChargeColor(0.6f), shine: false);
            charge.SetValue(0.6f);
            charge.SetTrackColor(new Color(0f, 0f, 0f, 0.6f));
            UiScenarios.At((RectTransform)charge.transform, 620f, 680f);
            UiScenarios.Label(page, "10 m/s", TextStyle.Small.WithTint(ColorRole.White).WithSize(24f), 710f, 706f)
                .color = UiKit.ChargeColor(0.6f);

            var empty = SketchBar.Create(page, "Empty", new Vector2(236f, 30f), theme.health);
            empty.SetValue(0f);
            UiScenarios.At((RectTransform)empty.transform, 920f, 676f);

            var slider = SketchBar.Create(page, "Slider", new Vector2(300f, 24f), theme.cream, shine: false);
            slider.SetValue(0.8f);
            slider.SetTrackColor(new Color(0f, 0f, 0f, 0.6f));
            UiScenarios.At((RectTransform)slider.transform, 1200f, 679f);
            var handle = UiKit.CreateImage(page, "Handle", UiSprites.SkullHandle, Color.white);
            handle.rectTransform.sizeDelta = new Vector2(26f, 30f);
            handle.preserveAspect = true;
            UiScenarios.At(handle.rectTransform, 1200f + 4f + 0.8f * 292f - 12f, 676f);

            // Colours and icons.
            UiScenarios.Label(page, "COLOURS & ICONS", TextStyle.Section, 72f, 780f);
            var swatches = UiScenarios.At(UiKit.CreateRow(page, "Swatches", 18f), 72f, 836f);
            for (int i = 0; i < PlayerColorPalette.Count; i++)
                UiKit.CreateSwatch(swatches, PlayerColorPalette.Get(i), 52f);
            UiKit.CreateSwatch(swatches, theme.dim, 52f);

            var icons = UiScenarios.At(UiKit.CreateRow(page, "Icons", 22f), 520f, 846f);
            Icon(icons, UiSprites.Skull, 34f);
            UiKit.CreateIcon(icons, "Hand", 34f, UiSprites.HandInk, UiSprites.HandLine, theme.warning);
            Icon(icons, UiSprites.Eye, 26f);
            UiKit.CreateIcon(icons, "ChainSafe", 24f, UiSprites.ChainInk, UiSprites.ChainLine, theme.safe);
            UiKit.CreateIcon(icons, "ChainWarning", 24f, UiSprites.ChainInk, UiSprites.ChainLine, theme.warning);
            UiKit.CreateIcon(icons, "ChainDanger", 24f, UiSprites.ChainInk, UiSprites.ChainLine, theme.danger);
            Heart(icons, theme.heart);
            Heart(icons, theme.heartLow);
            Icon(icons, UiSprites.Cross, 30f);
            Voice(icons, theme.cream * new Color(1f, 1f, 1f, 0.4f), muted: false);
            Voice(icons, theme.safe, muted: false);
            Voice(icons, new Color(1f, 1f, 1f, 0.55f), muted: true);
            UiKit.CreateIcon(icons, "Infinity", 30f, UiSprites.InfinityInk, UiSprites.InfinityLine, theme.cream);

            // Menu focus: the pointer and the brush stroke under the focused item.
            var focus = UiScenarios.At(UiKit.CreateRow(page, "Focus", 10f), 72f, 940f);
            IconStack.Create(focus, "Arrow", new Vector2(34f, 34f), theme.white,
                IconStack.PlainLayer(UiSprites.ArrowInk), IconStack.TintLayer(UiSprites.ArrowFill));
            UiKit.CreateText(focus, "Item", "SETTINGS", TextStyle.Prompt.WithSize(60f));
            var brush = UiKit.CreateImage(page, "Brush", UiSprites.Brush, theme.brush, sliced: true);
            brush.rectTransform.sizeDelta = new Vector2(250f, 18f);
            UiScenarios.At(brush.rectTransform, 112f, 1012f);
        }

        static void Icon(Transform parent, string sprite, float size)
        {
            IconStack.Create(parent, sprite, new Vector2(size, size), Color.white, IconStack.PlainLayer(sprite));
        }

        static void Heart(Transform parent, Color colour)
        {
            IconStack.Create(parent, "Heart", new Vector2(28f, 28f), colour,
                IconStack.TintLayer(UiSprites.HeartFill), IconStack.PlainLayer(UiSprites.HeartInk));
        }

        static void Voice(Transform parent, Color tint, bool muted)
        {
            if (muted)
                IconStack.Create(parent, "Muted", new Vector2(34f, 32f), tint,
                    IconStack.TintLayer(UiSprites.Speaker), IconStack.PlainLayer(UiSprites.SpeakerMute));
            else
                IconStack.Create(parent, "Voice", new Vector2(34f, 32f), tint,
                    IconStack.PlainLayer(UiSprites.Speaker), IconStack.TintLayer(UiSprites.SpeakerWaves));
        }
    }
}
