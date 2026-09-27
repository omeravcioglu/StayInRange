using UnityEngine;

namespace CollarCali.UI
{
    /// <summary>
    /// Whether a menu is open over the running game. The pause menu holds the player still through
    /// the controllers themselves; this is for the team's own raw key and mouse reads outside them -
    /// the spectator's Previous / Next, the flashlight - so a click on a menu button does not also
    /// flip the spectated teammate.
    /// </summary>
    public static class UiInput
    {
        static Object _menuOwner;

        /// <summary>True while a menu is open; false again the moment its owner is destroyed.</summary>
        public static bool MenuOpen => _menuOwner != null;

        public static void SetMenuOpen(Object owner, bool open)
        {
            if (open)
                _menuOwner = owner;
            else if (_menuOwner == owner)
                _menuOwner = null;
        }
    }
}
