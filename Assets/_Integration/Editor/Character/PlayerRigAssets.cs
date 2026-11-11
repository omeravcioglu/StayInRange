#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>Where the third-person player rig's assets live, and the body profile they are built from.</summary>
    public static class PlayerRigAssets
    {
        public const string Folder = "Assets/_Integration/Characters/Player";
        public const string ProfilePath = Folder + "/CharacterBodyProfile.asset";
        public const string CharacterPath = Folder + "/PlayerCharacter.prefab";
        public const string CameraRigPath = Folder + "/PlayerCameraRig (CM3).prefab";

        /// <summary>The body profile, created with its defaults the first time.</summary>
        public static CharacterBodyProfile LoadOrCreateProfile(StringBuilder log)
        {
            var profile = AssetDatabase.LoadAssetAtPath<CharacterBodyProfile>(ProfilePath);
            if (profile != null)
            {
                if (profile.model == null)
                    log?.AppendLine("! The body profile names no model.");
                return profile;
            }

            EnsureFolder(Folder);
            profile = ScriptableObject.CreateInstance<CharacterBodyProfile>();
            profile.model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(CharacterAvatarFixer.ModelGuid));
            AssetDatabase.CreateAsset(profile, ProfilePath);
            AssetDatabase.SaveAssets();
            log?.AppendLine("Created " + ProfilePath + " with its defaults.");
            return profile;
        }

        /// <summary>Creates <paramref name="folder"/> and its parents in the asset database.</summary>
        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>Records a change to a component of a prefab instance as an override, so it is saved.</summary>
        public static void Record(Object target)
        {
            if (target == null)
                return;
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            EditorUtility.SetDirty(target);
        }
    }
}
#endif
