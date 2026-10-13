using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Tickets.Common
{
    public static class AttachmentRules
    {
        private static readonly HashSet<string> Blocked =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".bat", ".cmd", ".com", ".scr",
            ".msi", ".js", ".jse", ".vbs", ".vbe",
            ".ps1", ".jar", ".dll", ".lnk", ".hta",
            ".reg", ".apk"
        };

        public static bool IsBlocked(string fileName) =>
            Blocked.Contains(Path.GetExtension(fileName));

        /// <summary>
        /// Strips paths and illegal characters, caps the length; never returns empty.
        /// </summary>
        public static string SafeFileName(string? raw)
        {
            var name = Path.GetFileName(
                (raw ?? string.Empty).Replace('\\', '/'));

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            name = name.Trim().Trim('.');

            if (name.Length == 0)
                return "attachment";

            if (name.Length <= 150)
                return name;

            var ext = Path.GetExtension(name);

            return name[..(150 - ext.Length)] + ext;
        }
    }
}
