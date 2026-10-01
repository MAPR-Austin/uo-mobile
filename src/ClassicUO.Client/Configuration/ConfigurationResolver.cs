// SPDX-License-Identifier: BSD-2-Clause

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Configuration
{
    internal static class ConfigurationResolver
    {
        /// <param name="escapeBackslashes">
        /// Doubles lone backslashes first, so hand-edited profiles with Windows paths still parse.
        /// Pass false for files only this client writes: the rewrite turns the serializer's own
        /// escapes (backslash-u003C for &lt;, backslash-u0027 for ') into literal text, so a saved
        /// "hp &lt; 70" came back as "hp < 70". Files already saved that way are repaired.
        /// </param>
        public static T Load<T>(string file, JsonTypeInfo<T> ctx, bool escapeBackslashes = true) where T : class
        {
            if (!File.Exists(file))
            {
                Log.Warn(file + " not found.");

                return null;
            }

            var text = File.ReadAllText(file);

            if (!escapeBackslashes)
            {
                // undo the old rewrite: an escaped backslash before uXXXX (a literal "<" in the
                // string) becomes the escape it should have stayed
                text = Regex.Replace(text, @"(?<!\\)\\\\u([0-9A-Fa-f]{4})", @"\u$1");
            }
            else
            {
                text = Regex.Replace
                (
                    text,
                    @"(?<!\\)  # lookbehind: Check that previous character isn't a \
                                                    \\         # match a \
                                                    (?!\\)     # lookahead: Check that the following character isn't a \",
                    @"\\",
                    RegexOptions.IgnorePatternWhitespace
                );
            }

            try
            {
                return JsonSerializer.Deserialize(text, ctx);
            }
            catch (JsonException e)
            {
                // A file cut short (e.g. the app was killed mid-save) must not crash every launch:
                // keep it aside and start from defaults.
                Log.Error($"{file} is corrupt, starting fresh: {e.Message}");

                try
                {
                    File.Move(file, file + ".corrupt", true);
                }
                catch (IOException)
                {
                }

                return null;
            }
        }

        public static void Save<T>(T obj, string file, JsonTypeInfo<T> ctx) where T : class
        {
            // this try catch is necessary when multiples cuo instances points to this file.
            try
            {
                var fileInfo = new FileInfo(file);

                if (fileInfo.Directory != null && !fileInfo.Directory.Exists)
                {
                    fileInfo.Directory.Create();
                }

                var json = JsonSerializer.Serialize(obj, ctx);

                // write-then-rename, so a kill mid-save leaves the previous file intact
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, file, true);
            }
            catch (IOException e)
            {
                Log.Error(e.ToString());
            }
        }
    }
}