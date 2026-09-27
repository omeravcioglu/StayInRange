namespace CollarCali.UI
{
    /// <summary>
    /// The three build scenes, by name. Several older hooks key on these strings, so new code uses
    /// the constants rather than adding more literals - and nothing renames the scenes.
    /// </summary>
    public static class SceneNames
    {
        public const string Menu = "Menu";
        public const string Lobby = "Lobby";
        public const string Game = "Game";
    }
}
