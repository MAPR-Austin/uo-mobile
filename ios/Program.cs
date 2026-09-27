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
        // Defaults; override without rebuilding in Documents/uomobile.txt, e.g.
        //   ip=192.168.68.91        (the home server instead of the cloud one)
        //   port=2593
        //   clientversion=7.0.116.0
        //   files=off               (use a manual copy of the UO files instead of downloading)
        //   args=-fps 30
        // The cloud shard and its file server: ios/HOSTING.md.
        private const string DEFAULT_IP = "34.174.14.240";
        private const string DEFAULT_PORT = "2593";
        private const string DEFAULT_CLIENT_VERSION = "7.0.116.0";
        private const string DEFAULT_FILES = "https://34-174-14-240.sslip.io/files/";

        private static string[] _realArgs;
        private static string _documents;
        private static string _uoPath;

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

            Dictionary<string, string> cfg = ReadConfig(Path.Combine(_documents, "uomobile.txt"));
            string files = cfg.TryGetValue("files", out string fv) && fv.Length > 0 ? fv : DEFAULT_FILES;
            bool download = !files.Equals("off", StringComparison.OrdinalIgnoreCase);

            // A manual copy in Documents (the pre-download way: Apple Devices / the Files app).
            string manual = File.Exists(Path.Combine(_documents, "uo", "tiledata.mul")) ? Path.Combine(_documents, "uo")
                          : File.Exists(Path.Combine(_documents, "tiledata.mul")) ? _documents
                          : FindUoData(_documents);
            string uoPath;
            string adopt = null;

            if (download)
            {
                // The game downloads its files (Touch/GameFiles) into the app's private Library
                // folder: not shown in the Files app and kept out of the iCloud backup. A manual
                // copy is adopted - moved in where it matches - instead of downloaded again.
                uoPath = Path.Combine(Path.GetDirectoryName(_documents), "Library", "Application Support", "uo");
                Directory.CreateDirectory(uoPath);
                ExcludeFromBackup(uoPath);
                adopt = manual;
            }
            else
            {
                // files=off: a manual copy, or what earlier downloads left (adopting a copy moves it).
                string downloaded = Path.Combine(Path.GetDirectoryName(_documents), "Library", "Application Support", "uo");
                uoPath = manual ?? (File.Exists(Path.Combine(downloaded, "tiledata.mul")) ? downloaded : Path.Combine(_documents, "uo"));
                Directory.CreateDirectory(uoPath);
                WriteDataReadme(uoPath);
            }

            Console.WriteLine($"[UOMobile] UO data: {uoPath}" + (download ? $" (downloads from {files}" + (adopt != null ? $", reusing {adopt})" : ")") : ""));
            _uoPath = uoPath;

            // With downloads on, the music comes from the server, checked; loose copies would only
            // overwrite verified files.
            if (!download)
            {
                MoveLooseMusic(uoPath);
            }

            _realArgs = BuildClassicUOArgs(args, uoPath, cfg, download ? files : null, adopt);
            TriggerLocalNetworkPrompt(_realArgs);
            Console.WriteLine("[UOMobile] ClassicUO args: " + string.Join(" ", Redacted(_realArgs)));

            InstallNativeResolvers();
            Console.WriteLine("[UOMobile] resolvers installed; starting SDL");

#if IOS || __IOS__
            // Hints from the FNA iOS docs, plus orientation / home-indicator.
            // Touch: TouchInput consumes SDL finger events directly and GameController ignores
            // touch-synthesized mouse events, so turn the synthesis off entirely.
            SDL.SDL_SetHint(SDL.SDL_HINT_MOUSE_TOUCH_EVENTS, "0");
            SDL.SDL_SetHint(SDL.SDL_HINT_TOUCH_MOUSE_EVENTS, "0");
            SDL.SDL_SetHint(SDL.SDL_HINT_PEN_TOUCH_EVENTS, "0");
            // Portrait too (the HUD has a portrait layout). FNA sets this same list at startup.
            SDL.SDL_SetHint(SDL.SDL_HINT_ORIENTATIONS, "LandscapeLeft LandscapeRight Portrait");
            // "ambient" (SDL's default) is muted by the ring/silent switch and mixes under other
            // apps' audio; a game with its own music wants "playback".
            SDL.SDL_SetHint(SDL.SDL_HINT_AUDIO_CATEGORY, "playback");
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
                // SDL's UIKit startup changes the working directory to the (read-only) app bundle
                // between Main and this callback; ClassicUO derives its writable root from the
                // working directory (CUOEnviroment.ExecutablePath), so point it back at Documents.
                Directory.SetCurrentDirectory(_documents);
                LogDocumentsContents();

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

        /// <summary>
        /// First folder under Documents (breadth-first, a few levels, sorted, hidden folders such as
        /// the Files app's .Trash skipped) that holds tiledata.mul.
        /// </summary>
        private static string FindUoData(string root)
        {
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                (string dir, int depth) = queue.Dequeue();

                try
                {
                    if (File.Exists(Path.Combine(dir, "tiledata.mul")))
                    {
                        return dir;
                    }

                    if (depth < 4)
                    {
                        string[] subs = Directory.GetDirectories(dir);
                        Array.Sort(subs, StringComparer.OrdinalIgnoreCase);

                        foreach (string sub in subs)
                        {
                            if (!Path.GetFileName(sub).StartsWith("."))
                            {
                                queue.Enqueue((sub, depth + 1));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UOMobile] searching {dir}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// iOS asks for Local Network permission the first time the app talks to the LAN, and the
        /// connection attempt that triggered it fails. Send one harmless UDP datagram to the shard
        /// at launch so the prompt appears before the player taps Login.
        /// </summary>
        private static void TriggerLocalNetworkPrompt(string[] args)
        {
            try
            {
                int i = Array.IndexOf(args, "-ip");
                int p = Array.IndexOf(args, "-port");

                if (i < 0 || i + 1 >= args.Length)
                {
                    return;
                }

                // Only a shard on the local network needs the Local Network permission; don't
                // ask for it when playing on the cloud server.
                if (!IsLocalNetwork(args[i + 1]))
                {
                    return;
                }

                int port = p >= 0 && p + 1 < args.Length && int.TryParse(args[p + 1], out int v) ? v : 2593;

                using var udp = new System.Net.Sockets.UdpClient();
                udp.Send(new byte[] { 0 }, 1, args[i + 1], port);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UOMobile] local network probe: {ex.Message}");
            }
        }

        /// <summary>What the player copied in, for diagnosing "UO files not found" from the log.</summary>
        private static void LogDocumentsContents()
        {
            try
            {
                string uo = _uoPath ?? Path.Combine(_documents, "uo");

                foreach (string dir in new[] { _documents, uo, Path.Combine(uo, "Music", "Digital") })
                {
                    if (!Directory.Exists(dir))
                    {
                        Console.WriteLine($"[UOMobile] {dir}: (missing)");

                        continue;
                    }

                    string[] files = Directory.GetFiles(dir);
                    long bytes = 0;

                    foreach (string f in files)
                    {
                        bytes += new FileInfo(f).Length;
                    }

                    bool tiledata = File.Exists(Path.Combine(dir, "tiledata.mul"));
                    Console.WriteLine($"[UOMobile] {dir}: {files.Length} files, {bytes / (1024 * 1024)} MB, tiledata.mul={(tiledata ? "yes" : "no")}, cwd={Directory.GetCurrentDirectory()}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UOMobile] could not list Documents: {ex.Message}");
            }
        }

        /// <summary>
        /// Music goes in uo/Music/Digital. Players drop it in through the Files app, so collect it
        /// from wherever it landed: loose at the top of Documents or the UO folder, or inside a
        /// folder they copied whole (e.g. "music-mobile"), up to three levels down. Empty files are
        /// the Apple Devices copy failing (build 31: every mp3 arrived as 0 KB); they are skipped
        /// and logged, never allowed to replace a good copy. A good copy replaces the installed file
        /// in one step. One unreadable folder doesn't stop the rest. The result is logged either way.
        /// </summary>
        private static void MoveLooseMusic(string uoPath)
        {
            string target = Path.Combine(uoPath, "Music", "Digital");

            try
            {
                int moved = 0;
                var folders = new List<string> { _documents, uoPath };
                CollectMusicFolders(_documents, uoPath, target, 0, folders);

                foreach (string dir in folders)
                {
                    try
                    {
                        moved += CollectMusicFrom(dir, uoPath, target);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[UOMobile] music: skipped {dir}: {ex.Message}");
                    }
                }

                if (moved > 0)
                {
                    Console.WriteLine($"[UOMobile] music: moved {moved} files into {target}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UOMobile] music: could not move music files: {ex.Message}");
            }

            // What the game will actually find (support: "no music" starts here).
            try
            {
                if (Directory.Exists(target))
                {
                    foreach (string f in Directory.GetFiles(target))
                    {
                        Console.WriteLine($"[UOMobile] music: {Path.GetFileName(f)} {new FileInfo(f).Length} bytes");
                    }
                }
                else
                {
                    Console.WriteLine($"[UOMobile] music: no {target} folder, so no music");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UOMobile] music: could not list {target}: {ex.Message}");
            }
        }

        /// <summary>Moves one folder's music into <paramref name="target"/>; returns how many files moved.</summary>
        private static int CollectMusicFrom(string dir, string uoPath, string target)
        {
            if (!Directory.Exists(dir))
            {
                return 0;
            }

            string[] files = Directory.GetFiles(dir);
            // Only real (non-empty) mp3s make a folder a music folder: a failed copy's Config.txt
            // must not replace the installed one.
            bool hasMp3 = Array.Exists(files, f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 0);
            bool topLevel = dir == _documents || dir == uoPath;
            int moved = 0;

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                bool isMp3 = name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
                // A Config.txt is music's when it travels with mp3s, or lies loose at the top
                // (the old way) while none is installed. Anywhere else it's someone else's.
                bool isConfig = name.Equals("Config.txt", StringComparison.OrdinalIgnoreCase) &&
                                (hasMp3 || topLevel && !File.Exists(Path.Combine(target, "Config.txt")));

                if (!isMp3 && !isConfig)
                {
                    continue;
                }

                if (new FileInfo(file).Length == 0)
                {
                    Console.WriteLine($"[UOMobile] music: skipped {name} in {dir}: it is empty (0 bytes), the copy did not finish");

                    continue;
                }

                Directory.CreateDirectory(target);
                File.Move(file, Path.Combine(target, name), true); // replace in one step
                moved++;
            }

            // Tidy up a copied-in music folder we just emptied (never any other folder).
            if (moved > 0 && !topLevel && Directory.GetFileSystemEntries(dir).Length == 0)
            {
                Directory.Delete(dir);
            }

            return moved;
        }

        /// <summary>Folders under Documents that may hold copied-in music (not the UO data folder itself).</summary>
        private static void CollectMusicFolders(string dir, string uoPath, string target, int depth, List<string> into)
        {
            if (depth >= 3)
            {
                return;
            }

            string[] subdirs;

            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                return;
            }

            foreach (string sub in subdirs)
            {
                string name = Path.GetFileName(sub);

                if (name.StartsWith(".") || PathEquals(sub, uoPath) || PathEquals(sub, target))
                {
                    continue;
                }

                into.Add(sub);
                CollectMusicFolders(sub, uoPath, target, depth + 1, into);
            }
        }

        private static bool PathEquals(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.Ordinal);

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

        private static string[] BuildClassicUOArgs(string[] launchArgs, string uoPath, Dictionary<string, string> cfg, string filesUrl, string adopt)
        {
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
                // Server-list ICMP ping needs raw sockets / a ping binary; the iOS sandbox has neither.
                "-no_server_ping",
                // Empty plugin list: the default is ./Assistant/Razor.dll (Windows only).
                "-plugins",
                // NOTE: no "-skiploginscreen". ClassicUO treats that flag as a switch and would
                // swallow a following value such as "off", so passing "-skiploginscreen off"
                // would actually SKIP the login screen. Omitting it shows the login screen.
                // Behind the cloud's NAT the shard advertises an address the phone may not reach
                // after the server list; always reconnect to the host we logged in to.
                "-ignore_relay_ip",
            };

            if (filesUrl != null)
            {
                list.Add("-download");
                list.Add(filesUrl);

                if (adopt != null)
                {
                    list.Add("-download_adopt");
                    list.Add(adopt);
                }
            }

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
                        "# ip=" + DEFAULT_IP + "   (192.168.68.91 = the home server)\n" +
                        "# port=" + DEFAULT_PORT + "\n" +
                        "# clientversion=" + DEFAULT_CLIENT_VERSION + "\n" +
                        "# files=" + DEFAULT_FILES + "   (off = use a manual copy of the UO files)\n" +
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

        /// <summary>The args for the log, without password values (uomobile.txt args= could hold one).</summary>
        private static IEnumerable<string> Redacted(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                bool secret = i > 0 && args[i - 1].StartsWith("-password", StringComparison.OrdinalIgnoreCase);

                yield return secret ? "(hidden)" : args[i];
            }
        }

        private static bool IsLocalNetwork(string host)
        {
            if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!System.Net.IPAddress.TryParse(host, out System.Net.IPAddress ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return false;
            }

            byte[] b = ip.GetAddressBytes();

            return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 169 && b[1] == 254;
        }

        /// <summary>2 GB of game files have no place in the player's iCloud backup; they download again.</summary>
        private static void ExcludeFromBackup(string path)
        {
#if IOS || __IOS__
            try
            {
                using var url = Foundation.NSUrl.FromFilename(path);

                if (!url.SetResource(Foundation.NSUrl.IsExcludedFromBackupKey, Foundation.NSNumber.FromBoolean(true), out Foundation.NSError error))
                {
                    Console.WriteLine($"[UOMobile] could not exclude {path} from the backup: {error?.LocalizedDescription}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UOMobile] could not exclude {path} from the backup: {ex.Message}");
            }
#endif
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
            // Not on the FNA assembly (which also holds the SDL3 bindings): FNA's module initializer
            // (FNADllMap.Init) registers its own resolver there - on iOS it returns the main program
            // handle for the statically linked fnalibs - and a second registration throws, which
            // aborted the first TestFlight builds (TypeInitializationException in <Module>..cctor).
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
                string dir = _documents /* top level: Apple Devices cannot copy out of subfolders */;
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
                string dir = _documents;
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
