using TMPro;

namespace CollarCali.UI
{
    /// <summary>
    /// One text look from the design: face, size at the 1920x1080 reference, colour and ink weight.
    ///
    /// The presets are the handful of looks the 38 boards actually use, so a new screen picks one
    /// instead of inventing a size. Colours are roles, resolved through <see cref="UiTheme"/>.
    /// </summary>
    public readonly struct TextStyle
    {
        public readonly FontRole Face;
        public readonly float Size;
        public readonly ColorRole Tint;
        public readonly Ink Weight;
        public readonly float Spacing;
        public readonly TextAlignmentOptions Align;

        public TextStyle(FontRole face, float size, ColorRole tint, Ink weight, float spacing = 0f,
            TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            Face = face;
            Size = size;
            Tint = tint;
            Weight = weight;
            Spacing = spacing;
            Align = align;
        }

        public TextStyle WithSize(float size) => new TextStyle(Face, size, Tint, Weight, Spacing, Align);
        public TextStyle WithTint(ColorRole tint) => new TextStyle(Face, Size, tint, Weight, Spacing, Align);
        public TextStyle WithAlign(TextAlignmentOptions align) => new TextStyle(Face, Size, Tint, Weight, Spacing, align);

        /// <summary>YOU DIED, STAY IN RANGE, IT'S ALIVE!</summary>
        public static readonly TextStyle Title = new TextStyle(FontRole.Display, 120f, ColorRole.White, Ink.Heavy);

        /// <summary>DOT IS DOWN!, PAUSED, SPECTATING, screen headings.</summary>
        public static readonly TextStyle Heading = new TextStyle(FontRole.Display, 64f, ColorRole.White, Ink.Heavy);

        /// <summary>Section headings and tabs inside a screen.</summary>
        public static readonly TextStyle Section = new TextStyle(FontRole.Display, 34f, ColorRole.White, Ink.Heavy);

        /// <summary>Player names in the team panel and roster.</summary>
        public static readonly TextStyle Name = new TextStyle(FontRole.Display, 30f, ColorRole.White, Ink.Heavy);

        /// <summary>The magazine count and other large numbers.</summary>
        public static readonly TextStyle BigNumber = new TextStyle(FontRole.Display, 56f, ColorRole.White, Ink.Heavy);

        /// <summary>Interaction prompts: "Lift DOT", "Throw", "CATCH DOT!".</summary>
        public static readonly TextStyle Prompt = new TextStyle(FontRole.Body, 38f, ColorRole.White, Ink.Heavy);

        /// <summary>Distances and small readouts: "12m", "85".</summary>
        public static readonly TextStyle Number = new TextStyle(FontRole.Body, 34f, ColorRole.White, Ink.Heavy);

        /// <summary>Instructions and messages: "Lift DOT and get them to a revive station."</summary>
        public static readonly TextStyle Body = new TextStyle(FontRole.Body, 28f, ColorRole.Cream, Ink.Heavy);

        /// <summary>Hints and asides: "you", "hold E", "hands full".</summary>
        public static readonly TextStyle Small = new TextStyle(FontRole.Body, 22f, ColorRole.Faint, Ink.Soft);

        /// <summary>Menu labels in spaced capitals: YOUR NAME, REGION, KEY REBINDING.</summary>
        public static readonly TextStyle Caption = new TextStyle(FontRole.Body, 24f, ColorRole.Muted, Ink.Soft, 3f);
    }
}
