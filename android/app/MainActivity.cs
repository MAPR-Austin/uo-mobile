// SPDX-License-Identifier: BSD-2-Clause
//
// UO Mobile: Android entry point for ClassicUO - the counterpart of ios/Program.cs.
//
// Boot sequence:
//   SDLActivity.onCreate (Java)  -> loads libSDL3.so, makes the surface, starts SDL's thread
//   MainActivity.Main()          -> (SDL's thread) folders, uomobile.txt, SDL hints
//                                -> ClassicUO.Bootstrap.Main(args)   (the normal desktop entry)
//
// Folders (no storage permission needed for any of them):
//   GetExternalFilesDir  /sdcard/Android/data/<pkg>/files - the working directory, as Documents is
//                        on iOS: settings.json, Data/Profiles, Logs, uomobile.txt, crash.txt and
//                        uomobile-console.log; reachable with `adb pull` (and USB on most phones)
//   NoBackupFilesDir/uo  the 2 GB of game files Touch/GameFiles downloads: private, not backed up
// ClassicUO reads its writable root from the working directory once (CUOEnviroment), and Android
// starts apps in "/", so the folder is set before anything touches a ClassicUO type.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Android.App;
using Android.Content.PM;
using Android.Util;
using Microsoft.Xna.Framework;
using Org.Libsdl.App;

namespace UOMobile.Droid
{
    [Activity(Name = "com.mapraustin.britgraveyard.MainActivity", Label = "Graveyard Battles", MainLauncher = true, Exported = true,
              Icon = "@mipmap/appicon", Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
              ScreenOrientation = ScreenOrientation.FullUser, LaunchMode = LaunchMode.SingleTask, HardwareAccelerated = true,
              ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
                                     ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation |
                                     ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.SmallestScreenSize)]
    public class MainActivity : SDLActivity
    {
        public const string Tag = "UOMobile";

        // Defaults; override without rebuilding in uomobile.txt (see ios/Program.cs for the keys).
        private const string DEFAULT_IP = "34-174-14-240.sslip.io";
        private const string DEFAULT_PORT = "2593";
        private const string DEFAULT_CLIENT_VERSION = "7.0.116.0";
        private const string DEFAULT_FILES = "https://34-174-14-240.sslip.io/files/";

        private static string _home;

        // libmain.so isn't used: FNA3D and FAudio load through .NET's library search
        protected override string[] GetLibraries() => new[] { "SDL3" };

        protected override void Main()
        {
            FNALoggerEXT.LogInfo = m => Log.Info(Tag, m);
            FNALoggerEXT.LogWarn = m => Log.Warn(Tag, m);
            FNALoggerEXT.LogError = m => Log.Error(Tag, m);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception, "unhandled");

            try
            {
                string[] args = Prepare();
                Start(args);
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                WriteCrash(inner, "Main");
                Log.Error(Tag, "FAILED: " + inner);
            }
        }

        private string[] Prepare()
        {
            _home = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath;
            Directory.CreateDirectory(_home);
            Directory.SetCurrentDirectory(_home);
            StartConsoleLog();
            Console.WriteLine($"[UOMobile] home: {_home}");

            Dictionary<string, string> cfg = ReadConfig(Path.Combine(_home, "uomobile.txt"));
            string files = cfg.TryGetValue("files", out string fv) && fv.Length > 0 ? fv : DEFAULT_FILES;
            bool download = !files.Equals("off", StringComparison.OrdinalIgnoreCase);

            // a manual copy pushed with adb (to <home>/uo) is adopted rather than downloaded again
            string manual = File.Exists(Path.Combine(_home, "uo", "tiledata.mul")) ? Path.Combine(_home, "uo")
                          : File.Exists(Path.Combine(_home, "tiledata.mul")) ? _home
                          : null;
            string downloaded = Path.Combine(NoBackupFilesDir.AbsolutePath, "uo");
            string uoPath = download ? downloaded : manual ?? downloaded;
            Directory.CreateDirectory(uoPath);
            Console.WriteLine($"[UOMobile] UO data: {uoPath}" + (download ? $" (downloads from {files}" + (manual != null ? $", reusing {manual})" : ")") : ""));

            var list = new List<string>
            {
                "-touch",
                "-ip", cfg.TryGetValue("ip", out string v) && v.Length > 0 ? v : DEFAULT_IP,
                "-port", cfg.TryGetValue("port", out v) && v.Length > 0 ? v : DEFAULT_PORT,
                "-clientversion", cfg.TryGetValue("clientversion", out v) && v.Length > 0 ? v : DEFAULT_CLIENT_VERSION,
                "-uopath", cfg.TryGetValue("uopath", out v) && v.Length > 0 ? v : uoPath,
                "-language", "ENU",
                "-no_server_ping", // ICMP needs raw sockets
                "-plugins",        // no Razor DLL on a phone
                "-ignore_relay_ip" // behind the cloud's NAT: reconnect to the host we logged in to
            };

            if (download)
            {
                list.Add("-download");
                list.Add(files);

                if (manual != null)
                {
                    list.Add("-download_adopt");
                    list.Add(manual);
                }
            }

            if (cfg.TryGetValue("args", out v) && v.Length > 0)
            {
                list.AddRange(v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }

            string[] args = list.ToArray();
            Console.WriteLine("[UOMobile] ClassicUO args: " + string.Join(" ", Redacted(args)));

            // Touch: TouchInput reads SDL's finger events itself; no touch-made mouse events.
            SDL3.SDL.SDL_SetHint(SDL3.SDL.SDL_HINT_MOUSE_TOUCH_EVENTS, "0");
            SDL3.SDL.SDL_SetHint(SDL3.SDL.SDL_HINT_TOUCH_MOUSE_EVENTS, "0");
            SDL3.SDL.SDL_SetHint(SDL3.SDL.SDL_HINT_PEN_TOUCH_EVENTS, "0");
            SDL3.SDL.SDL_SetHint(SDL3.SDL.SDL_HINT_ORIENTATIONS, "LandscapeLeft LandscapeRight Portrait");
            // Back closes things in the game (it arrives as a key) instead of quitting the app.
            SDL3.SDL.SDL_SetHint("SDL_ANDROID_TRAP_BACK_BUTTON", "1");

            // a driver for testing, e.g. "driver=Vulkan" in uomobile.txt (GLES is FNA3D's default here)
            if (cfg.TryGetValue("driver", out v) && v.Length > 0)
            {
                SDL3.SDL.SDL_SetHint("FNA3D_FORCE_DRIVER", v);
            }

            return args;
        }

        private static void Start(string[] args)
        {
            Directory.SetCurrentDirectory(_home);

            // ClassicUO.Bootstrap is internal in the cuo assembly; its public static Main(string[])
            // is called by reflection, as on iOS, so no ClassicUO source file has to change.
            Type boot = Type.GetType("ClassicUO.Bootstrap, cuo", throwOnError: true);
            MethodInfo main = boot.GetMethod("Main", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string[]) }, null)
                              ?? throw new MissingMethodException("ClassicUO.Bootstrap", "Main(string[])");
            Console.WriteLine("[UOMobile] starting ClassicUO");
            main.Invoke(null, new object[] { args });
            Console.WriteLine("[UOMobile] ClassicUO returned");
        }

        private static Dictionary<string, string> ReadConfig(string file)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!File.Exists(file))
                {
                    File.WriteAllText(file,
                        "# UO Mobile settings. Lines are key=value; delete a line to use the default.\n" +
                        "# ip=" + DEFAULT_IP + "\n" +
                        "# port=" + DEFAULT_PORT + "\n" +
                        "# clientversion=" + DEFAULT_CLIENT_VERSION + "\n" +
                        "# files=" + DEFAULT_FILES + "   (off = use a manual copy of the UO files in uo/)\n" +
                        "# driver=Vulkan   (FNA3D's renderer; GLES by default)\n" +
                        "# args=-fps 30\n");

                    return result;
                }

                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');

                    if (line.Length > 0 && line[0] != '#' && eq > 0)
                    {
                        result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[UOMobile] cannot read " + file + ": " + ex.Message);
            }

            return result;
        }

        private static IEnumerable<string> Redacted(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                yield return i > 0 && args[i - 1].StartsWith("-password", StringComparison.OrdinalIgnoreCase) ? "(hidden)" : args[i];
            }
        }

        private static void WriteCrash(Exception ex, string where)
        {
            try
            {
                Log.Error(Tag, $"FATAL ({where}): {ex}");

                if (_home != null)
                {
                    File.AppendAllText(Path.Combine(_home, "crash.txt"),
                        $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({where}) ==={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
                }
            }
            catch
            {
                // nothing left to do
            }
        }

        // Console goes to logcat (tag DOTNET) already; keep a copy in uomobile-console.log too.
        private static void StartConsoleLog()
        {
            try
            {
                string file = Path.Combine(_home, "uomobile-console.log");

                if (File.Exists(file) && new FileInfo(file).Length > 4 * 1024 * 1024)
                {
                    File.Delete(file);
                }

                var fileWriter = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                fileWriter.WriteLine();
                fileWriter.WriteLine($"===== UO Mobile start {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                Console.SetOut(new TeeWriter(Console.Out, fileWriter));
                Console.SetError(new TeeWriter(Console.Error, fileWriter));
            }
            catch
            {
                // best effort
            }
        }

        private sealed class TeeWriter : TextWriter
        {
            private readonly TextWriter _a, _b;

            public TeeWriter(TextWriter a, TextWriter b)
            {
                _a = a;
                _b = b;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
                _a.Write(value);
                _b.Write(value);
            }

            public override void Write(string value)
            {
                _a.Write(value);
                _b.Write(value);
            }

            public override void WriteLine(string value)
            {
                _a.WriteLine(value);
                _b.WriteLine(value);
            }

            public override void Flush()
            {
                _a.Flush();
                _b.Flush();
            }
        }
    }
}
