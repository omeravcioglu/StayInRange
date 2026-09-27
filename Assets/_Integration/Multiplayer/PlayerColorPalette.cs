using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// The player colours - the only thing that tells players apart, since everyone shares one body.
    /// The same colour tints a teammate's body, their swatch in the team panel, their world marker
    /// and their roster row, so it is defined once, here.
    ///
    /// Orange, blue, pink and lime, from the UI design. None of them is the green, yellow or red the
    /// HUD reserves for the collar's safe, warning and danger states, so a colour can never be misread
    /// as a distance warning. Four colours for four players; a fifth player would wrap to orange.
    /// </summary>
    public static class PlayerColorPalette
    {
        static readonly Color[] Colors =
        {
            Rgb(0xFF9A3C), // orange
            Rgb(0x3FA9FF), // blue
            Rgb(0xFF6FC8), // pink
            Rgb(0xB8E356), // lime
        };

        /// <summary>A darker shade of each colour, for the shaded side of a figure or a pressed state.</summary>
        static readonly Color[] Shades =
        {
            Rgb(0xC46F22),
            Rgb(0x2A78C0),
            Rgb(0xC24C98),
            Rgb(0x86A93A),
        };

        static readonly string[] Names = { "ORANGE", "BLUE", "PINK", "LIME" };

        public static int Count => Colors.Length;

        const string ChoiceKey = "CollarCali.PlayerColor";

        static int _sessionChoice = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => _sessionChoice = -1;

        /// <summary>
        /// The colour this player picked in the lobby, -1 for none yet. It carries from the lobby
        /// into the match (the player spawns long after the lobby record is gone): this run's pick
        /// first - two test copies of the game on one machine share PlayerPrefs, and the last to
        /// write would dress both - then the saved one, offered again next session.
        /// </summary>
        public static int SavedChoice
        {
            get
            {
                if (_sessionChoice >= 0)
                    return _sessionChoice;
                int saved = PlayerPrefs.GetInt(ChoiceKey, -1);
                return saved >= 0 && saved < Colors.Length ? saved : -1;
            }
            set
            {
                int clamped = value >= 0 && value < Colors.Length ? value : -1;
                _sessionChoice = clamped;
                PlayerPrefs.SetInt(ChoiceKey, clamped);
                PlayerPrefs.Save();
            }
        }

        public static Color Get(int index) => Colors[Wrap(index)];

        public static Color GetShade(int index) => Shades[Wrap(index)];

        /// <summary>The colour's name as the colour picker shows it.</summary>
        public static string GetName(int index) => Names[Wrap(index)];

        static int Wrap(int index)
        {
            if (index < 0)
                index = 0;
            return index % Colors.Length;
        }

        static Color Rgb(int hex)
        {
            return new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, 1f);
        }
    }
}
