namespace CollarCali
{
    /// <summary>
    /// Anything an attack can hurt: an enemy's health, one of its hitboxes, a player.
    ///
    /// The method is the same as Cowsins' IDamageable, so while Cowsins is still in the project one
    /// Damage implements both. The name differs on purpose: Cowsins and Emerald AI each have their
    /// own IDamageable, and a CollarCali one would silently take their place in files that import
    /// those namespaces.
    ///
    /// Callers find it on the struck collider's object or its parents (DamageRouting). A target owned
    /// by another machine routes the damage there itself, so callers never need to know who owns it.
    /// </summary>
    public interface IDamageTarget
    {
        /// <param name="damage">A positive amount, already multiplied for a critical hit.</param>
        /// <param name="isHeadshot">A critical hit: the head, or a hitbox tagged "Critical".</param>
        void Damage(float damage, bool isHeadshot);
    }
}
