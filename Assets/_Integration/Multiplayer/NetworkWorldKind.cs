namespace CollarCali
{
    public enum NetworkWorldKind
    {
        Enemy = 0,
        Creep = 1,
        Platform = 2,
        Trap = 3,

        /// <summary>Scary Zombie Pack melee enemy; brain runs on the master only.</summary>
        Zombie = 4,

        /// <summary>Lite Magic Pack ranged caster; stationary, master-only brain, ragdolls on death.</summary>
        Magician = 5
    }
}
