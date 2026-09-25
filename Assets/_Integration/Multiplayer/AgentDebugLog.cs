using System.IO;
using System.Text;
using cowsins;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CollarCali
{
    /// <summary>Debug-mode NDJSON logger for session 98e9a2. Remove after fix verified.</summary>
    public static class AgentDebugLog
    {
        const string SessionId = "98e9a2";
        static readonly string LogPath =
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "debug-98e9a2.log"));

        public static void Write(string hypothesisId, string location, string message, string dataJson = "{}")
        {
            try
            {
                long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line =
                    "{\"sessionId\":\"" + SessionId +
                    "\",\"hypothesisId\":\"" + Escape(hypothesisId) +
                    "\",\"location\":\"" + Escape(location) +
                    "\",\"message\":\"" + Escape(message) +
                    "\",\"data\":" + dataJson +
                    ",\"timestamp\":" + ts + "}\n";
                File.AppendAllText(LogPath, line);
            }
            catch
            {
                // ignore IO errors during play mode
            }
        }

        /// <summary>Logs FPS/Steve identity, wallrun flag, and weapon material/shader samples.</summary>
        public static void LogPlayerSetup(string hypothesisId, string location, string pathLabel,
            GameObject fpsRoot, GameObject steveRoot, bool networkMode)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"path\":\"").Append(Escape(pathLabel)).Append("\"");
            sb.Append(",\"networkMode\":").Append(networkMode ? "true" : "false");
            sb.Append(",\"fpsName\":\"").Append(Escape(fpsRoot != null ? fpsRoot.name : "null")).Append("\"");
            sb.Append(",\"steveName\":\"").Append(Escape(steveRoot != null ? steveRoot.name : "null")).Append("\"");

            var movement = fpsRoot != null
                ? fpsRoot.GetComponentInChildren<PlayerMovement>(true)
                : null;
            if (movement != null)
            {
                sb.Append(",\"movementGo\":\"").Append(Escape(movement.gameObject.name)).Append("\"");
                sb.Append(",\"canWallRun\":")
                    .Append(movement.playerSettings != null && movement.playerSettings.canWallRun
                        ? "true" : "false");
                sb.Append(",\"fpsRootPath\":\"").Append(Escape(GetHierarchyPath(movement.transform))).Append("\"");
            }
            else
            {
                sb.Append(",\"canWallRun\":null");
            }

            if (steveRoot != null)
                sb.Append(",\"stevePath\":\"").Append(Escape(GetHierarchyPath(steveRoot.transform))).Append("\"");

            AppendWeaponMaterialSample(sb, fpsRoot);
            AppendWeaponCameraState(sb);
            sb.Append("}");
            Write(hypothesisId, location, "player_setup", sb.ToString());
        }

        static void AppendWeaponMaterialSample(StringBuilder sb, GameObject fpsRoot)
        {
            sb.Append(",\"weaponSamples\":[");
            int count = 0;
            int weaponsLayer = LayerMask.NameToLayer("Weapons");
            if (fpsRoot != null)
            {
                foreach (var r in fpsRoot.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.sharedMaterial == null)
                        continue;

                    bool interesting =
                        (weaponsLayer >= 0 && r.gameObject.layer == weaponsLayer) ||
                        r.name.IndexOf("Weapon", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.name.IndexOf("Gun", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.name.IndexOf("Pistol", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.name.IndexOf("Rifle", System.StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!interesting)
                    {
                        var parentCam = r.GetComponentInParent<Camera>();
                        interesting = parentCam != null && parentCam.name == "WeaponCamera";
                    }

                    if (!interesting)
                        continue;

                    var mat = r.sharedMaterial;
                    var shader = mat.shader != null ? mat.shader.name : "null";
                    string baseCol = mat.HasProperty("_BaseColor")
                        ? mat.GetColor("_BaseColor").ToString() : "n/a";
                    string mainCol = mat.HasProperty("_Color")
                        ? mat.GetColor("_Color").ToString() : "n/a";
                    if (count > 0) sb.Append(',');
                    sb.Append("{\"go\":\"").Append(Escape(r.gameObject.name)).Append("\"");
                    sb.Append(",\"layer\":").Append(r.gameObject.layer);
                    sb.Append(",\"mat\":\"").Append(Escape(mat.name)).Append("\"");
                    sb.Append(",\"shader\":\"").Append(Escape(shader)).Append("\"");
                    sb.Append(",\"baseColor\":\"").Append(Escape(baseCol)).Append("\"");
                    sb.Append(",\"color\":\"").Append(Escape(mainCol)).Append("\"}");
                    count++;
                    if (count >= 6)
                        break;
                }
            }

            sb.Append("],\"weaponSampleCount\":").Append(count);
        }

        static void AppendWeaponCameraState(StringBuilder sb)
        {
            Camera weaponCam = null;
            foreach (var cam in Camera.allCameras)
            {
                if (cam != null && cam.name == "WeaponCamera" && cam.isActiveAndEnabled)
                {
                    weaponCam = cam;
                    break;
                }
            }

            if (weaponCam == null)
            {
                sb.Append(",\"weaponCam\":\"missing\"");
                return;
            }

            var data = weaponCam.GetUniversalAdditionalCameraData();
            sb.Append(",\"weaponCam\":\"")
                .Append(Escape(GetHierarchyPath(weaponCam.transform))).Append("\"");
            sb.Append(",\"weaponCamRenderType\":\"")
                .Append(data != null ? data.renderType.ToString() : "null").Append("\"");
            sb.Append(",\"weaponCamEnabled\":")
                .Append(weaponCam.isActiveAndEnabled ? "true" : "false");
        }

        static string GetHierarchyPath(Transform t)
        {
            if (t == null) return "";
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
