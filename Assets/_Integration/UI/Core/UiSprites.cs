namespace CollarCali.UI
{
    /// <summary>
    /// Names of the hand-drawn sprites in Assets/_Integration/UI/Sprites, as <see cref="UiTheme"/>
    /// lists them (the file name without its extension).
    ///
    /// They are exported from the design canvas's own SVG shapes at three times the design's pixel
    /// size. Pieces that take a colour at runtime are drawn white and paired with a separate black
    /// "ink" piece, so tinting never turns the outline coloured.
    /// </summary>
    public static class UiSprites
    {
        /// <summary>Sketchy key cap with a thick bottom stroke. 9-sliced so long names stretch it.</summary>
        public const string KeyCap = "KeyCap";

        /// <summary>The same cap in grey: a fixed key, or an action that cannot happen right now.</summary>
        public const string KeyCapOff = "KeyCapOff";

        /// <summary>The wobbly bar outline drawn over every bar. 9-sliced; tint it cream.</summary>
        public const string BarFrame = "BarFrame";

        /// <summary>A player's colour blob (tint it), and its ink rim with a highlight on top.</summary>
        public const string BlobFill = "BlobFill";
        public const string BlobInk = "BlobInk";

        /// <summary>DOWN: a cream skull with red eyes.</summary>
        public const string Skull = "Skull";

        /// <summary>The slider handle: a cream skull with black eyes.</summary>
        public const string SkullHandle = "SkullHandle";

        /// <summary>Carried / carrying: the grip hand's ink and its line (tint it yellow).</summary>
        public const string HandInk = "HandInk";
        public const string HandLine = "HandLine";

        /// <summary>The spectator is watching this player.</summary>
        public const string Eye = "Eye";

        /// <summary>Collar strain: the chain link's ink and its line (tint it with the tether colour).</summary>
        public const string ChainInk = "ChainInk";
        public const string ChainLine = "ChainLine";

        /// <summary>Your own health: a heart to tint and its ink outline.</summary>
        public const string HeartFill = "HeartFill";
        public const string HeartInk = "HeartInk";

        /// <summary>The green revive cross used on stations and their markers.</summary>
        public const string Cross = "Cross";

        /// <summary>Voice: the speaker, its waves (tint: faint idle, green talking) and the muted cross.</summary>
        public const string Speaker = "Speaker";
        public const string SpeakerWaves = "SpeakerWaves";
        public const string SpeakerMute = "SpeakerMute";

        /// <summary>Menu focus: the hand-drawn pointer's ink and fill, and the brush stroke (tint it red).</summary>
        public const string ArrowInk = "ArrowInk";
        public const string ArrowFill = "ArrowFill";
        public const string Brush = "Brush";

        /// <summary>Unlimited ammo. No game font has the glyph, so it is drawn.</summary>
        public const string InfinityInk = "InfinityInk";
        public const string InfinityLine = "InfinityLine";

        /// <summary>Plain shapes: a disc, a ring and a white square for fills.</summary>
        public const string Circle = "Circle";
        public const string Ring = "Ring";
        public const string White = "White";

        /// <summary>Weapon silhouettes, cream with an ink edge.</summary>
        public const string Pistol = "Pistol";
        public const string Mp7 = "MP7";

        /// <summary>The arrow inside a dash charge (tint it).</summary>
        public const string DashGlyph = "DashGlyph";

        /// <summary>Stamina bolt and weapon-heat flame: a fill to tint under an ink edge.</summary>
        public const string BoltFill = "BoltFill";
        public const string BoltInk = "BoltInk";
        public const string FlameFill = "FlameFill";
        public const string FlameInk = "FlameInk";

        /// <summary>A jammed weapon: ink and line (tint it red).</summary>
        public const string JamInk = "JamInk";
        public const string JamLine = "JamLine";

        /// <summary>The checkpoint flag, full colour.</summary>
        public const string Flag = "Flag";

        /// <summary>Mouse buttons for prompts, the pressed half in red.</summary>
        public const string MouseLeft = "MouseLeft";
        public const string MouseRight = "MouseRight";

        /// <summary>Crosshair arms at rest and opened over an enemy; its centre dot; the hit cross.</summary>
        public const string CrossArmsInk = "CrossArmsInk";
        public const string CrossArmsLine = "CrossArmsLine";
        public const string CrossArmsWideInk = "CrossArmsWideInk";
        public const string CrossArmsWideLine = "CrossArmsWideLine";
        public const string CrossDotFill = "CrossDotFill";
        public const string CrossDotInk = "CrossDotInk";
        public const string HitXInk = "HitXInk";
        public const string HitXLine = "HitXLine";

        /// <summary>The red splat that creeps in from the screen edges when you are hurt.</summary>
        public const string Splat = "Splat";

        /// <summary>A white screen-edge falloff, tinted for hurt, heal and critical.</summary>
        public const string Vignette = "Vignette";

        /// <summary>The body tracker's done tick: ink and line (tint it green).</summary>
        public const string CheckInk = "CheckInk";
        public const string CheckLine = "CheckLine";

        /// <summary>Previous / next player chevrons.</summary>
        public const string ChevronLeftInk = "ChevronLeftInk";
        public const string ChevronLeftLine = "ChevronLeftLine";
        public const string ChevronRightInk = "ChevronRightInk";
        public const string ChevronRightLine = "ChevronRightLine";

        /// <summary>A hand-drawn underline (tint it), under the watched player's name.</summary>
        public const string Underline = "Underline";

        /// <summary>The loading spinner: chain links fading round a collar ring, turned in steps.</summary>
        public const string Spinner = "Spinner";

        /// <summary>The pointer under a world marker (white with an ink edge; tint it).</summary>
        public const string Tick = "Tick";

        /// <summary>The sketchy line under fields, dropdowns and list rows; stretch it to width, tint it.</summary>
        public const string SketchLine = "SketchLine";

        /// <summary>Room and roster glyphs: ink with a white body, tinted by the Image colour.</summary>
        public const string LockGlyph = "LockGlyph";
        public const string CrownGlyph = "CrownGlyph";
        public const string MinusGlyph = "MinusGlyph";

        /// <summary>A room's occupancy dots: a filled seat (fill + ink) or an empty one (line).</summary>
        public const string DotFill = "DotFill";
        public const string DotInk = "DotInk";
        public const string DotLine = "DotLine";

        /// <summary>A left-to-right fade that darkens the menu column without drawing an edge.</summary>
        public const string Scrim = "Scrim";

        /// <summary>Full-screen menu art (UI/Art): the title screen's key art and the bare corridor.</summary>
        public const string MenuKeyArt = "MenuKeyArt";
        public const string MenuCorridor = "MenuCorridor";

        /// <summary>The lobby's colour preview: the critter facing you, one per player colour, and its turntable.</summary>
        public static readonly string[] Figures = { "FigureOrange", "FigureBlue", "FigurePink", "FigureLime" };
        public const string Turntable = "Turntable";
    }
}
