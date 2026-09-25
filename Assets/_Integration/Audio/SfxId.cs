namespace CollarCali
{
    /// <summary>
    /// Every gameplay sound the game can ask for.
    ///
    /// The numbers are written out and MUST NOT be reused or renumbered: they travel over the wire
    /// in NetworkVfxRelay.RPC_Sfx, so a client running an older build would otherwise play a
    /// different sound than the one the master fired. Add new ids at the end of their block.
    /// </summary>
    public enum SfxId
    {
        None = 0,

        #region Zombie (walker)

        /// <summary>First moment a zombie notices a player. Deliberately short - it does not stall the chase.</summary>
        ZombieAlert = 100,

        /// <summary>The swing itself, fired at the start of the wind-up.</summary>
        ZombieAttackSwing = 101,

        /// <summary>Only when the claw actually connects with a player.</summary>
        ZombieAttackHit = 102,

        ZombieHurt = 103,
        ZombieDeath = 104,

        /// <summary>Idle burble while standing around. Ambient, played per-client.</summary>
        ZombieIdleGroan = 105,

        /// <summary>Louder groan while actively shambling at someone. Ambient, played per-client.</summary>
        ZombieChaseGroan = 106,

        /// <summary>Footfall, driven by replicated movement rather than animation events.</summary>
        ZombieFootstep = 107,

        #endregion

        #region Zombie (crawler variant)

        CrawlerAlert = 130,
        CrawlerIdleGroan = 131,

        /// <summary>Wet drag of a crawler pulling itself along. Replaces the walker footstep.</summary>
        CrawlerScrape = 132,

        #endregion

        #region Magician

        /// <summary>Muttering while it has line of sight on someone. Ambient.</summary>
        MagicianIdleChant = 200,

        /// <summary>The caster's own vocal effort as the cast animation starts.</summary>
        MagicianCast = 201,

        MagicianHurt = 202,
        MagicianDeath = 203,

        #endregion

        #region Magic bolt

        /// <summary>Bolt leaving the hand. Fires on every client, at the muzzle.</summary>
        MagicCast = 220,

        /// <summary>Looped whistle that travels with the bolt.</summary>
        MagicTravelLoop = 221,

        /// <summary>Bolt popping on geometry or a player.</summary>
        MagicImpact = 222,

        #endregion

        #region Gore

        // 240 was a per-hit blood squelch, cut because the generated clips were not good enough to
        // keep. The number stays retired rather than being handed to something else.

        /// <summary>
        /// The killing blow, played wherever the death blood spawns. Rides the existing blood relay
        /// rather than sending its own, so it is always in step with the splash on every client.
        /// </summary>
        BloodBurstDeath = 241,

        #endregion

        #region Creep

        /// <summary>The wind-up roar before it charges.</summary>
        CreepRoar = 300,

        /// <summary>Breath and footfalls while sprinting. Looped.</summary>
        CreepChaseLoop = 301,

        /// <summary>The instant it takes hold of a player.</summary>
        CreepGrab = 302,

        /// <summary>Looped while dragging a victim away.</summary>
        CreepDragLoop = 303,

        CreepRelease = 304,
        CreepHurt = 305,
        CreepDeath = 306,

        /// <summary>2D sting on the grabbed player's own screen, paired with the overlay.</summary>
        CreepGrabbedSting = 307,

        #endregion

        #region Blob (generated ahead of the enemy existing - nothing fires these yet)

        BlobAttack = 400,
        BlobMove = 401,
        BlobHurt = 402,
        BlobDeath = 403,
        BlobIdle = 404,

        #endregion

        #region Player

        /// <summary>Taking damage. Cowsins has no slot for this at all.</summary>
        PlayerHurt = 500,
        PlayerDeath = 501,
        PlayerRespawn = 502,
        PlayerHeal = 503,

        /// <summary>Picking a weapon up off the floor.</summary>
        WeaponPickup = 504,

        /// <summary>Flashlight toggle click.</summary>
        FlashlightToggle = 505,

        ModeSwitchToThirdPerson = 506,
        ModeSwitchToFirstPerson = 507,

        #endregion

        #region Team / progression

        /// <summary>Every living player has stood on the pad and the checkpoint advanced.</summary>
        CheckpointSaved = 520,

        /// <summary>One player has entered the pad; the team is still waiting on the others.</summary>
        CheckpointPartial = 521,

        /// <summary>Rising tension while the team is too far apart and the grace timer is running.</summary>
        TeamSeparationWarning = 522,

        /// <summary>The separation rule fired and the team got yanked back.</summary>
        TeamFail = 523,

        #endregion

        #region UI

        UiHover = 600,
        UiClick = 601,
        UiBack = 602,
        UiReady = 603,
        UiUnready = 604,
        UiMatchStart = 605,
        UiPlayerJoined = 606,
        UiError = 607,

        #endregion

        #region World hazards

        /// <summary>Continuous hum from a sweeping laser.</summary>
        LaserHum = 700,

        /// <summary>Laser burning a player.</summary>
        LaserZap = 701,

        PlatformBlinkOn = 702,
        PlatformBlinkOff = 703,

        #endregion
    }
}
