using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Which controller a networked player gets on its owner's machine (Resources/PlayerRigRecipe).
    /// Off: DualPlayerController, the first/third-person controller the game shipped with. On: the
    /// third-person-only ThirdPersonPlayer, made from the prefab below. A switch while the move to
    /// third person only is tested; the old controller goes once the new one has taken over.
    /// </summary>
    [CreateAssetMenu(menuName = "CollarCali/Player Rig Recipe", fileName = "PlayerRigRecipe")]
    public class PlayerRigRecipe : ScriptableObject
    {
        public const string ResourcePath = "PlayerRigRecipe";

        [Tooltip("Networked players use the third-person-only controller below instead of DualPlayerController.")]
        public bool useThirdPersonPlayer;

        [Tooltip("The player character with ThirdPersonPlayer on it: Characters/Player/ThirdPersonPlayer.")]
        public ThirdPersonPlayer thirdPersonPlayer;

        public static PlayerRigRecipe Load() => Resources.Load<PlayerRigRecipe>(ResourcePath);

        /// <summary>The third-person player prefab when networked players should use it, otherwise null.</summary>
        public static ThirdPersonPlayer ThirdPersonPrefabInUse()
        {
            var recipe = Load();
            return recipe != null && recipe.useThirdPersonPlayer ? recipe.thirdPersonPlayer : null;
        }
    }
}
