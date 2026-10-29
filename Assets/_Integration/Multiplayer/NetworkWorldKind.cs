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
        Magician = 5,

        /// <summary>
        /// A TelekineticProp - a ladder, a plank - that players lift, place, throw and catch. Not
        /// master-simulated like the others: whoever is handling it simulates it (see TelekineticProp).
        /// </summary>
        Prop = 6
    }
}
