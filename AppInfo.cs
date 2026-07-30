using System;
using System.Reflection;

namespace SpeakRect
{
    /// <summary>Build / product identity for UI labels (overlay, Help, …).</summary>
    public static class AppInfo
    {
        /// <summary>
        /// Informational version from the project (e.g. <c>1.3.0</c>), without
        /// SourceLink/git metadata suffixes.
        /// </summary>
        public static string Version
        {
            get
            {
                string ver = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                    ?? "?";
                int plus = ver.IndexOf('+');
                if (plus > 0)
                    ver = ver[..plus];
                return ver.Trim();
            }
        }

        /// <summary>Short label for tight chrome (e.g. sidebar under EXIT).</summary>
        public static string VersionTag => "v" + Version;

        /// <summary>Help / about line.</summary>
        public static string VersionLine =>
            $"SpeakRect {Version}  ·  local OCR + Windows TTS (optional SAPI 5)";
    }
}
