#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using cowsins;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Resolves Weapon_SO assets by name for networked weapon sync.
    /// </summary>
    public static class WeaponCatalog
    {
        static Dictionary<string, Weapon_SO> _byName;

        public static void EnsureLoaded()
        {
            if (_byName != null)
                return;

            _byName = new Dictionary<string, Weapon_SO>();
            foreach (var weapon in Resources.FindObjectsOfTypeAll<Weapon_SO>())
            {
                if (weapon == null || string.IsNullOrEmpty(weapon.name))
                    continue;
                if (!_byName.ContainsKey(weapon.name))
                    _byName[weapon.name] = weapon;
            }
        }

        public static Weapon_SO FindByName(string weaponName)
        {
            if (string.IsNullOrEmpty(weaponName))
                return null;

            // Fusion NetworkString.ToString() can include trailing null padding.
            weaponName = weaponName.Trim('\0', ' ', '\r', '\n');
            if (string.IsNullOrEmpty(weaponName))
                return null;

            EnsureLoaded();
            return _byName.TryGetValue(weaponName, out var weapon) ? weapon : null;
        }

        public static string GetName(Weapon_SO weapon)
        {
            return weapon != null ? weapon.name : string.Empty;
        }
    }
}
#endif
