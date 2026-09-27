namespace CollarCali.UI
{
    /// <summary>
    /// The one sorting ladder for every canvas the game draws.
    ///
    /// Before the redesign each overlay picked its own number (2, 25, 100, 300, 350, 400, 500, 600),
    /// so whether a prompt landed above or below a death card depended on which file you read last.
    /// Everything new sorts by these, and a layer is chosen by what must win when two of them are
    /// on screen at once: a loading screen covers everything, a popup covers menus, a full-screen
    /// moment (YOU DIED, grabbed) covers the HUD.
    /// </summary>
    public static class UiLayers
    {
        /// <summary>Markers, enemy bars and damage numbers projected from the world.</summary>
        public const int World = 100;

        /// <summary>Hurt splats, the heal flush and the critical grey-out - under the HUD's text.</summary>
        public const int HudFx = 150;

        /// <summary>Team panel, weapon, movement, compass, collar warning, killfeed.</summary>
        public const int Hud = 200;

        /// <summary>Interaction prompts and the crosshair: never hidden by the HUD.</summary>
        public const int Prompts = 250;

        /// <summary>Full-screen cards: YOU DIED, spectating, grabbed, team wipe, revived.</summary>
        public const int Moments = 400;

        /// <summary>Menus, lobby, pause and settings.</summary>
        public const int Screens = 500;

        /// <summary>Error and confirm popups, above whichever screen raised them.</summary>
        public const int Popups = 700;

        /// <summary>The loading overlay, which must cover scene swaps and everything else.</summary>
        public const int Loading = 900;

        /// <summary>The gallery and other developer tools.</summary>
        public const int Dev = 990;
    }
}
