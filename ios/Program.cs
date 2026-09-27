// SPDX-License-Identifier: BSD-2-Clause
//
// UO Mobile: iOS entry point for ClassicUO.
//
// Boot sequence (pattern from the FNA docs, "Appendix C: FNA on Apple Platforms"):
//   Main()  -> prepare Documents/, native-library resolver, SDL hints
//           -> SDL_RunApp(...)            (SDL starts UIKit via UIApplicationMain; never returns
//                                          until the app exits)
//   FakeMain() (called by SDL on the UIKit main thread once the app has launched)
//           -> ClassicUO.Bootstrap.Main(args)   (the normal desktop entry, cuo assembly)
//
// ClassicUO.Bootstrap (the separate plugin-host launcher project) and Razor/plugins are NOT
// used on iOS: plugins are Windows DLLs and iOS cannot load code at runtime anyway.
//
// Writable data: ClassicUO writes settings.json, Data/Profiles, Data/Client, Logs, screenshots
// relative to CUOEnviroment.ExecutablePath, which on .NET (not NETFRAMEWORK) is
// Environment.CurrentDirectory, read once in a static initializer. The app bundle is read-only
// on iOS, so we chdir into the app's Documents folder BEFORE anything touches CUOEnviroment.
// No ClassicUO source change is needed for that.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SDL3;

namespace ClassicUO.iOS
{
    public static class Program
    {
        // Defaults; override without rebuilding by creating Documents/uomobile.txt, e.g.
        //   ip=192.168.68.91
        //   port=2593
        //   clientversion=7.0.116.0
        //   args=-fps 30
        private const string DEFAULT_IP = "192.168.68.91";
        private const string DEFAULT_PORT = "2593";
        private const string DEFAULT_CLIENT_VERSION = "7.0.116.0";

        private static string[] _realArgs;
        private static string _documents;

        // Must stay referenced for the lifetime of the app: SDL holds a native pointer to it.
        private static SDL.SDL_main_func _mainFunc;

        public static void Main(string[] args)
        {
            // Anything that escapes (here, in SDL callbacks, or in the game loop) is written to
            // Documents/crash.txt before the runtime aborts, so a TestFlight crash can be read
            // from the Files app / Apple Devices without a Mac.
            AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception, "unhandled");

            try
            {
                MainCore(args);
            }
            catch (Exception ex)
            {
                WriteCrash(ex, "Main");
                throw;
            }
        }

        private static void WriteCrash(Exception ex, string where)
        {
            try
            {
                string dir = _documents ?? GetDocumentsDirectory();
                File.AppendAllText(Path.Combine(dir, "crash.txt"),
                    $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({where}) ==={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
                Console.WriteLine($"[UOMobile] FATAL ({where}): {ex}");
            }
            catch
            {
                // nothing left to do
            }
        }

        private static void MainCore(string[] args)
        {
            _documents = GetDocumentsDirectory();
            Directory.CreateDirectory(_documents);

            // Everything ClassicUO writes (settings.json, Data/, Logs/) now lands in Documents.
            Directory.SetCurrentDirectory(_documents);

            StartConsoleLog();
            Console.WriteLine($"[UOMobile] Documents: {_documents}");

            string uoPath = Path.Combine(_documents, "uo");
            Directory.CreateDirectory(uoPath);
            WriteDataReadme(uoPath);

            // Windows' Apple Devices app can only drop files into the top of the app's Documents
            // folder, so accept the UO files there too when Documents/uo has none.
            if (!File.Exists(Path.Combine(uoPath, "tiledata.mul")) && File.Exists(Path.Combine(_documents, "tiledata.mul")))
            {
                uoPath = _documents;
            }

            Console.WriteLine($"[UOMobile] UO data: {uoPath}");

            _realArgs = BuildClassicUOArgs(args, uoPath);
            Console.WriteLine("[UOMobile] ClassicUO args: " + string.Join(" ", _realArgs));

            InstallNativeResolvers();
            Console.WriteLine("[UOMobile] resolvers installed; starting SDL");

#if IOS || __IOS__
            // Hints from the FNA iOS docs, plus orientation / home-indicator.
            // Touch: TouchInput consumes SDL finger events directly and GameController ignores
            // touch-synthesized mouse events, so turn the synthesis off entirely.
            SDL.SDL_SetHint(SDL.SDL_HINT_MOUSE_TOUCH_EVENTS, "0");
            SDL.SDL_SetHint(SDL.SDL_HINT_TOUCH_MOUSE_EVENTS, "0");
            SDL.SDL_SetHint(SDL.SDL_HINT_PEN_TOUCH_EVENTS, "0");
            SDL.SDL_SetHint("SDL_ACCELEROMETER_AS_JOYSTICK", "0");
            SDL.SDL_SetHint("SDL_IOS_ORIENTATIONS", "LandscapeLeft LandscapeRight");
            SDL.SDL_SetHint("SDL_IOS_HIDE_HOME_INDICATOR", "1");
            // No FNA_GRAPHICS_ENABLE_HIGHDPI on purpose: rendering at point resolution keeps the
            // UO UI readable on a phone. Pass "args=-highdpi" in uomobile.txt to try native res.

            _mainFunc = FakeMain;
            SDL.SDL_RunApp(0, IntPtr.Zero, _mainFunc, IntPtr.Zero);
#else
            // Desktop smoke-test path (used only by the Windows harness; never on iOS).
            RealMain(_realArgs);
#endif
        }

#if IOS || __IOS__
        [ObjCRuntime.MonoPInvokeCallback(typeof(SDL.SDL_main_func))]
#endif
        private static int FakeMain(int argc, IntPtr argv)
        {
            RealMain(_realArgs);
            return 0;
        }

        // Keep ClassicUO.Bootstrap and its methods alive under trimming / NativeAOT.
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods, "ClassicUO.Bootstrap", "cuo")]
        private static void RealMain(string[] args)
        {
            try
            {
                // ClassicUO.Bootstrap is internal in the cuo assembly; call its public static
                // Main(string[]) by reflection so no ClassicUO source file has to change.
                Type boot = Type.GetType("ClassicUO.Bootstrap, cuo", throwOnError: true);
                MethodInfo main = boot.GetMethod("Main", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string[]) }, null);

                if (main == null)
                {
                    throw new MissingMethodException("ClassicUO.Bootstrap", "Main(string[])");
                }

                main.Invoke(null, new object[] { args });
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                ReportFatal(inner);
            }
        }

        private static string GetDocumentsDirectory()
        {
            // On iOS HOME is the app's sandbox container; Documents is what the Files app shows
            // (UIFileSharingEnabled + LSSupportsOpeningDocumentsInPlace in Info.plist).
            string home = Environment.GetEnvironmentVariable("HOME");

            if (!string.IsNullOrEmpty(home))
            {
                return Path.Combine(home, "Documents");
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private static string[] BuildClassicUOArgs(string[] launchArgs, string uoPath)
        {
            Dictionary<string, string> cfg = ReadConfig(Path.Combine(_documents, "uomobile.txt"));

            string ip = cfg.TryGetValue("ip", out string v) && v.Length > 0 ? v : DEFAULT_IP;
            string port = cfg.TryGetValue("port", out v) && v.Length > 0 ? v : DEFAULT_PORT;
            string clientVersion = cfg.TryGetValue("clientversion", out v) && v.Length > 0 ? v : DEFAULT_CLIENT_VERSION;
            string path = cfg.TryGetValue("uopath", out v) && v.Length > 0 ? v : uoPath;

            var list = new List<string>
            {
                "-touch",
                "-ip", ip,
                "-port", port,
                "-clientversion", clientVersion,
                "-uopath", path,
                "-language", "ENU",
                // Empty plugin list: the default is ./Assistant/Razor.dll (Windows only).
                "-plugins",
                // NOTE: no "-skiploginscreen". ClassicUO treats that flag as a switch and would
                // swallow a following value such as "off", so passing "-skiploginscreen off"
                // would actually SKIP the login screen. Omitting it shows the login screen.
            };

            if (cfg.TryGetValue("args", out v) && v.Length > 0)
            {
                list.AddRange(v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }

            if (launchArgs != null)
            {
                list.AddRange(launchArgs);
            }

            return list.ToArray();
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
                        "# args=-fps 30\n");
                    return result;
                }

                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();

                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');

                    if (eq > 0)
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

        private static void WriteDataReadme(string uoPath)
        {
            try
            {
                string readme = Path.Combine(uoPath, "PUT_UO_FILES_HERE.txt");

                if (!File.Exists(readme))
                {
                    File.WriteAllText(readme,
                        "Copy the Ultima Online data files (tiledata.mul, MainMisc.uop, *.uop, *.mul, ...)\n" +
                        "into this folder. On the PC they are staged by ios\\copy-uo-data.ps1.\n");
                }
            }
            catch
            {
                // best effort
            }
        }

        // ---------------------------------------------------------------------------------
        // Native libraries. SDL3, FNA3D, FAudio and Theorafile are linked statically into the
        // app executable, but their [DllImport]s use library names ("SDL3", "FNA3D", ...).
        // Resolve those names to the main program handle when the expected symbol is there;
        // otherwise return IntPtr.Zero and let the runtime's default probing run.
        // ClassicUO.Utility.ZLib uses "libz" on non-Windows: map it to the system zlib.
        // ---------------------------------------------------------------------------------
        private static void InstallNativeResolvers()
        {
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(SDL).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // a resolver is already set for this assembly
            }

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(ClassicUO.Utility.ZLib).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            string probe;

            switch (name)
            {
                case "SDL3": probe = "SDL_Init"; break;
                case "FNA3D": probe = "FNA3D_CreateDevice"; break;
                case "FAudio": probe = "FAudioCreate"; break;
                case "libtheorafile": probe = "tf_fopen"; break;
                case "libz":
                case "zlib":
                    if (NativeLibrary.TryLoad("/usr/lib/libz.1.dylib", out IntPtr z))
                    {
                        return z;
                    }
                    probe = "uncompress";
                    break;
                default:
                    return IntPtr.Zero;
            }

            try
            {
                IntPtr self = NativeLibrary.GetMainProgramHandle();

                if (!NativeLibrary.TryGetExport(self, probe, out _))
                {
                    Console.WriteLine($"[UOMobile] resolver: '{probe}' not exported from the executable (library {name}); using it anyway");
                }

                // The fnalibs are linked statically into the app (see ClassicUO.iOS.csproj), so the
                // executable is the library - same as FNA's own LoadStaticLibrary on iOS.
                return self;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UOMobile] resolver: {name}: {ex.Message}");
            }

            return IntPtr.Zero;
        }

        // ---------------------------------------------------------------------------------
        // Logging: tee Console output to Documents/Logs/uomobile-console.log so a failed launch
        // can be diagnosed from the Files app, without a Mac attached.
        // ---------------------------------------------------------------------------------
        private static void StartConsoleLog()
        {
            try
            {
                string dir = Path.Combine(_documents, "Logs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "uomobile-console.log");

                if (File.Exists(file) && new FileInfo(file).Length > 4 * 1024 * 1024)
                {
                    File.Delete(file);
                }

                var fileWriter = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                fileWriter.WriteLine();
                fileWriter.WriteLine($"===== UO Mobile start {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

                Console.SetOut(new TeeWriter(Console.Out, fileWriter));
                Console.SetError(new TeeWriter(Console.Error, fileWriter));
            }
            catch
            {
                // logging is best effort
            }
        }

        private static void ReportFatal(Exception ex)
        {
            string text = "UO Mobile failed to start ClassicUO:\n" + ex;
            Console.WriteLine(text);

            try
            {
                string dir = Path.Combine(_documents, "Logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "uomobile-fatal.txt"), $"[{DateTime.Now:u}] {text}\n\n");
            }
            catch
            {
            }

            try
            {
                SDL.SDL_ShowSimpleMessageBox(SDL.SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "UO Mobile", ex.GetType().Name + ": " + ex.Message, IntPtr.Zero);
            }
            catch
            {
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
