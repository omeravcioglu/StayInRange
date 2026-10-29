#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Anything a player can lift, carry, throw and catch with telekinesis: a dead teammate's body
    /// (FpsNetworkBridge) or a prop such as a ladder (TelekineticProp).
    ///
    /// Every implementation follows the same rules, which is what lets BodyTelekinesis treat them
    /// alike: one machine decides who holds it (the dead player's own machine for a body, the host
    /// for a prop), every change is a request carrying the epoch the sender saw so stale or
    /// simultaneous requests lose, and whoever is handling it simulates it while everyone else
    /// follows.
    /// </summary>
    public interface ITelekineticTarget
    {
        /// <summary>Exists in the session and can be targeted at all right now.</summary>
        bool IsValidTarget { get; }

        /// <summary>Shown in the prompt: "Lift Omer", "Lift Ladder".</summary>
        string DisplayName { get; }

        BodyPhase Phase { get; }

        /// <summary>Bumped on every change of who holds or simulates it. Requests carry the one the sender saw.</summary>
        int Epoch { get; }

        /// <summary>Who holds it in a grip, or None.</summary>
        PlayerRef Holder { get; }

        /// <summary>The point targeting, prompts and the throw arc measure from.</summary>
        Vector3 Position { get; }

        Quaternion Rotation { get; }

        /// <summary>Its solid colliders: what the crosshair hits and what the carrier must not collide with.</summary>
        IReadOnlyList<Collider> Colliders { get; }

        /// <summary>Can be turned and tilted while held. Bodies hang as they hang; props are aimed.</summary>
        bool SupportsRotation { get; }

        bool Throwable { get; }

        /// <summary>The prompt word for letting go with the interact key: "Drop", or "Place" for a prop that stays put.</summary>
        string DropVerb { get; }

        /// <summary>
        /// How far it reaches out from its centre. A long object is held at least that far out, so
        /// turning it never sweeps through the carrier. 0 for a body.
        /// </summary>
        float HoldReach { get; }

        /// <summary>True on the machine that is currently simulating it.</summary>
        bool IsLocallySimulated { get; }

        void RequestHold(int epoch, bool catching);

        /// <summary>The holder lets go: thrown, or let go with the interact key (a drop, or a placement).</summary>
        void RequestRelease(int epoch, bool thrown);

        /// <summary>The simulating machine reports a flight has ended.</summary>
        void RequestRest(int epoch);

        /// <summary>Launches it, on the machine simulating it. False anywhere else.</summary>
        bool LaunchLocal(Vector3 velocity, Vector3 spin);

        void IgnoreCollisionsWith(IReadOnlyList<Collider> others, bool ignore);
    }

    /// <summary>
    /// Something this machine is simulating and publishing through the local player's bridge
    /// (FpsNetworkBridge.Driven*): a body or prop held or thrown by the local player, or the local
    /// player's own corpse while it lies loose.
    /// </summary>
    public interface ITelekineticSimulation
    {
        /// <summary>The network object other machines match against FpsNetworkBridge.DrivenBody.</summary>
        NetworkId PublishedId { get; }

        bool IsSimulatedHere { get; }

        ITelekineticTarget Target { get; }

        void ReadPublishedPose(out Vector3 position, out Quaternion rotation,
            out Vector3 secondaryPosition, out Quaternion secondaryRotation, out Vector3 velocity);
    }

    /// <summary>
    /// The single thing this machine publishes. One at a time by design: picking something up hands
    /// whatever was still in flight back to its owner first.
    /// </summary>
    public static class TelekineticSimulation
    {
        public static ITelekineticSimulation Local { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => Local = null;
    }
}
#endif
