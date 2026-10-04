using System;
using Android.App;
using Android.Content.PM;
using Android.Util;
using Microsoft.Xna.Framework;
using Org.Libsdl.App;

namespace UOMobile.AndroidHello
{
    /// <summary>
    /// SDL's activity runs the game: GetLibraries loads only SDL3 (the default list also wants a
    /// libmain.so this app doesn't have; FNA3D and FAudio load through .NET's own library search),
    /// and Main() - SDL's "main", on its own thread once the surface exists - runs FNA. Start with
    /// `am start ... -e driver OpenGL` (or Vulkan) to force FNA3D's driver.
    /// </summary>
    [Activity(Name = "com.mapraustin.britgraveyard.hello.MainActivity", Label = "UO Mobile Hello", MainLauncher = true, Exported = true,
              Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
              ScreenOrientation = ScreenOrientation.FullUser, LaunchMode = LaunchMode.SingleTask, HardwareAccelerated = true,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
                                     ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation |
                                     ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.SmallestScreenSize)]
    public class MainActivity : SDLActivity
    {
        public const string Tag = "UOM-HELLO";

        protected override string[] GetLibraries() => new[] { "SDL3" };

        protected override void Main()
        {
            // FNA logs to stderr, which logcat doesn't show
            FNALoggerEXT.LogInfo = m => Log.Info(Tag, m);
            FNALoggerEXT.LogWarn = m => Log.Warn(Tag, m);
            FNALoggerEXT.LogError = m => Log.Error(Tag, m);

            string driver = Intent?.GetStringExtra("driver");

            if (!string.IsNullOrEmpty(driver))
            {
                SDL3.SDL.SDL_SetHint("FNA3D_FORCE_DRIVER", driver);
            }

            Log.Info(Tag, $"starting (driver {(string.IsNullOrEmpty(driver) ? "default" : driver)}, {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

            try
            {
                using HelloGame game = new HelloGame();
                game.Run();
            }
            catch (Exception e)
            {
                Log.Error(Tag, "FAILED: " + e);

                throw;
            }
        }
    }
}
