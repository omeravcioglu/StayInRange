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

        [Tooltip("What the other players' copies of a third-person player animate with: Malbers' controller without its " +
                 "behaviours (ThirdPersonPlayerBuilder makes it from the character's own), so they only play the animation " +
                 "the owner publishes (PlayerNetworkBridge.RemoteBody).")]
        public RuntimeAnimatorController remoteBodyController;

        [Tooltip("The Malbers weapons the other players can see on a third-person player, by prefab: the owner says which " +
                 "of these each weapon on its character is (PlayerNetworkBridge.RemoteWeapons). ThirdPersonPlayerBuilder " +
                 "lists every MWeapon prefab under Malbers' Common/Prefabs/Weapons; add the game's own weapons here.")]
        public GameObject[] remoteWeapons;

        [Header("Collar")]
        [Tooltip("The collar model every player wears. ThirdPersonPlayerBuilder copies it, and where PlayerCharacterBuilder " +
                 "fitted it on the character's neck, so the body the other players see wears it too.")]
        public GameObject collarPrefab;
        [Tooltip("The collar against the neck bone: position, in the bone's space.")]
        public Vector3 collarPosition;
        public Quaternion collarRotation = Quaternion.identity;
        public float collarScale = 1f;

        public static PlayerRigRecipe Load() => Resources.Load<PlayerRigRecipe>(ResourcePath);

        /// <summary>The third-person player prefab when networked players should use it, otherwise null.</summary>
        public static ThirdPersonPlayer ThirdPersonPrefabInUse()
        {
            var recipe = Load();
            return recipe != null && recipe.useThirdPersonPlayer ? recipe.thirdPersonPlayer : null;
        }
    }
}
