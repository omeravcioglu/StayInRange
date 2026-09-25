using UnityEngine;

namespace CollarCali
{
    public static class PlayerColorPalette
    {
        static readonly Color[] Colors =
        {
            new Color(0.86f, 0.18f, 0.18f),
            new Color(0.95f, 0.82f, 0.12f),
            new Color(0.18f, 0.72f, 0.28f),
            new Color(0.15f, 0.72f, 0.92f),
            new Color(0.78f, 0.28f, 0.86f),
            new Color(0.95f, 0.48f, 0.12f),
        };

        public static int Count => Colors.Length;

        public static Color Get(int index)
        {
            if (Colors.Length == 0)
                return Color.white;
            if (index < 0)
                index = 0;
            return Colors[index % Colors.Length];
        }
    }
}
