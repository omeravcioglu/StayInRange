namespace CollarCali
{
    /// <summary>
    /// Optional hook so FPS Engine hit detection can forward damage into Fusion
    /// without cowsins depending on a concrete network type.
    /// Return true when the call was consumed (including RPC'd to the owner).
    /// </summary>
    public interface INetworkedDamageRouter
    {
        bool TryRouteDamage(float damage, bool isHeadshot);
    }
}
