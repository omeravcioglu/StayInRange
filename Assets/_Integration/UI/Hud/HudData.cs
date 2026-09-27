using UnityEngine;

namespace CollarCali.UI
{
    // What the HUD widgets draw, as plain data. The live game fills these every frame (HudRoot);
    // the gallery fills them with mock states, so every board can be checked without a session.

    public enum RowStatus
    {
        Alive,

        /// <summary>Dead, body on the floor.</summary>
        Down,

        /// <summary>Dead, body held up by a teammate.</summary>
        Carried,
    }

    public enum VoiceState
    {
        /// <summary>No voice chat in this build: the column stays empty rather than lying.</summary>
        Hidden,
        Idle,
        Talking,
        Muted,
    }

    /// <summary>One player's row in the team panel.</summary>
    public struct TeamRowData
    {
        public string Name;
        public Color Colour;
        public bool IsSelf;
        public RowStatus Status;
        public float Health;
        public float MaxHealth;
        public float Shield01;

        /// <summary>From you, or from your body while you are dead. Unused on your own row.</summary>
        public float Metres;

        /// <summary>The teammate this player is holding up, or null.</summary>
        public string Carrying;

        /// <summary>Who holds this player's body up ("YOU" when it is you), or null.</summary>
        public string CarriedBy;

        /// <summary>The spectator is watching this player.</summary>
        public bool Watched;

        public VoiceState Voice;

        public float Health01 => MaxHealth > 0f ? Mathf.Clamp01(Health / MaxHealth) : 0f;
    }

    public enum WeaponGlyph
    {
        None,
        Pistol,
        Mp7,
    }

    /// <summary>The weapon block, bottom right.</summary>
    public struct WeaponData
    {
        public bool Visible;
        public string Name;
        public WeaponGlyph Glyph;
        public int Magazine;
        public int MagazineSize;

        /// <summary>Spare bullets; ignored when <see cref="UnlimitedReserve"/>.</summary>
        public int Reserve;
        public bool UnlimitedReserve;

        public bool Reloading;
        public float Reload01;

        /// <summary>0-1 heat, or negative for a weapon that does not overheat.</summary>
        public float Heat01;
        public bool Overheated;

        /// <summary>1-based slot of the weapon in hand, and how many slots hold a weapon.</summary>
        public int ActiveSlot;
        public int SlotCount;
        public bool ShowSlots;
        public WeaponGlyph[] SlotGlyphs;

        public bool IsLow => Magazine > 0 && MagazineSize > 0 && Magazine <= Mathf.FloorToInt(MagazineSize / 3f);
        public bool IsEmpty => Magazine <= 0 && !Reloading;
    }

    /// <summary>Dash charges and stamina, bottom left.</summary>
    public struct MovementData
    {
        public bool Visible;
        public int Dashes;
        public int MaxDashes;

        /// <summary>Progress of the next charge coming back.</summary>
        public float Recharge01;

        /// <summary>0-1, or negative when the controller in use has no stamina.</summary>
        public float Stamina01;
    }

    /// <summary>The interaction prompt for whatever is under the crosshair.</summary>
    public struct PromptData
    {
        public bool Visible;
        public string Key;
        public string Text;

        /// <summary>The action needs the key held; shows the hint and, while held, the fill.</summary>
        public bool Hold;
        public float Progress01;

        /// <summary>The action exists but cannot happen right now: grey, slashed key.</summary>
        public bool Blocked;

        /// <summary>A small line under the prompt ("hold E", "bring a body here to revive").</summary>
        public string Hint;
    }

    public enum CollarStage
    {
        None,

        /// <summary>Someone is 20 m or more away.</summary>
        Warning,

        /// <summary>Someone is 25 m or more away.</summary>
        Danger,

        /// <summary>Past the collar's length: the grace timer is running.</summary>
        Breach,

        /// <summary>The timer ran out and the collar yanked everyone back.</summary>
        Failed,
    }

    public struct CollarData
    {
        public CollarStage Stage;
        public string Who;
        public float Metres;
        public float SecondsLeft;
        public float Left01;
    }

    /// <summary>A save pad waiting for the team, or a save that just happened.</summary>
    public struct CheckpointData
    {
        public bool Pending;
        public bool JustSaved;
        public int Id;
        public int Here;
        public int Needed;
        public Color[] Colours;
        public bool[] Arrived;
        public string WaitingFor;
    }
}
