using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

namespace CollarCali.Combat
{
    /// <summary>
    /// Game Creator 2 animation unit for a character Malbers animates (see MAnimalGC2Bridge). It
    /// names the Animator, so Game Creator's gestures (melee clips) can play on it, and does nothing
    /// else: it writes no Animator parameters and no update mode, adds no root-motion proxy to the
    /// Animator, and never moves the model - Game Creator's own units place the model below a
    /// character it assumes stands at its capsule's centre, which would drag Malbers' root (on the
    /// feet, and the Animator's own object) around.
    ///
    /// The model-placing calls are switched off through the interface as well, since Game Creator
    /// calls them from elsewhere (changing the model, ragdolls).
    /// </summary>
    [Title("Malbers Animal (passive)")]
    [Category("Malbers Animal (passive)")]
    [Description("Lets Game Creator play gestures on Malbers' Animator; never moves the model")]
    [Serializable]
    public class UnitAnimimMAnimal : TUnitAnimim, IUnitAnimim
    {
        public override void OnUpdate() { }

        void IUnitAnimim.ApplyMannequinPosition() { }

        void IUnitAnimim.ApplyMannequinRotation() { }

        void IUnitAnimim.ApplyMannequinScale() { }
    }
}
