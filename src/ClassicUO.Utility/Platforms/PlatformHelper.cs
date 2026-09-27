// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Utility.Platforms
{
    public static class PlatformHelper
    {
        public static readonly bool IsMonoRuntime = Type.GetType("Mono.Runtime") != null;

        public static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        public static readonly bool IsOSX = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>Set where Process.Start can't open URLs (iOS: SDL_OpenURL).</summary>
        public static Action<string> OpenUrlOverride;

        public static void LaunchBrowser(string url)
        {
            if (OpenUrlOverride != null)
            {
                OpenUrlOverride(url);

                return;
            }

            try
            {
                if (IsWindows)
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    };

                    Process.Start(psi);
                }
                else if (IsOSX)
                {
                    Process.Start("open", url);
                }
                else
                {
                    Process.Start("xdg-open", url);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }
    }
}