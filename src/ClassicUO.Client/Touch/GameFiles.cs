// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Touch
{
    internal sealed class GameFileEntry
    {
        [JsonPropertyName("path")] public string Path { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; }
    }

    internal sealed class GameFileManifest
    {
        [JsonPropertyName("version")] public long Version { get; set; }
        [JsonPropertyName("files")] public List<GameFileEntry> Files { get; set; } = new List<GameFileEntry>();
    }

    internal sealed class InstalledGameFiles
    {
        [JsonPropertyName("version")] public long Version { get; set; }
        /// <summary>True only once every file of <see cref="Version"/> is in place and checked.</summary>
        [JsonPropertyName("complete")] public bool Complete { get; set; }
        /// <summary>Manifest path -> sha256 of the file on disk.</summary>
        [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = new Dictionary<string, string>();
    }

    [JsonSerializable(typeof(GameFileManifest))]
    [JsonSerializable(typeof(InstalledGameFiles))]
    internal sealed partial class GameFilesJsonContext : JsonSerializerContext { }

    /// <summary>
    /// The game's files (UO client data and music) come from the shard's file server
    /// (ios/HOSTING.md sections 3-4). manifest.json lists every file with its size and sha256;
    /// this brings the local UO folder in line with it before UO.Load:
    /// <list type="bullet">
    /// <item>files are fetched to name.part, resumable with HTTP Range, checked, then renamed;</item>
    /// <item>installed.json is only marked complete when everything checks out, so a partial set
    /// never reaches UO.Load;</item>
    /// <item>a manual copy already on the phone (Documents) is adopted - moved in - instead of
    /// downloaded, when its size and hash match;</item>
    /// <item>offline, the installed set (or the manual copy) is used as it is.</item>
    /// </list>
    /// Runs on a worker task; the UI (<see cref="DownloadScreen"/>) only reads its state.
    /// </summary>
    internal sealed class GameFiles
    {
        /// <summary>-download &lt;url&gt;: the file server's base URL (".../files/").</summary>
        public static string BaseUrl;

        /// <summary>-download_adopt &lt;dir&gt;: an older manual copy of the UO files to reuse.</summary>
        public static string AdoptFrom;

        public static bool Configured => !string.IsNullOrEmpty(BaseUrl);

        public enum Phase { Checking, AskToDownload, Adopting, Downloading, Done, Failed }

        private const string PREFIX = "uo/";
        private const string INSTALLED_FILE = ".uomobile-installed.json";
        private const long ASK_ABOVE_BYTES = 50L * 1024 * 1024; // smaller updates just start
        private const int WORKERS = 3;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

        // The managed handler on every platform: it is the one the desktop tests exercise, and
        // unlike iOS's default NSUrlSessionHandler it has no response cache that could hand back
        // an old copy of a small file after an update.
        private static readonly HttpClient Http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private const int MIN_MANIFEST_FILES = 20;

        private readonly string _dir;
        private readonly object _lock = new object();
        private Phase _phase = Phase.Checking;
        private string _status = "Checking the game files...";
        private string _detail = "";
        private long _total, _done;
        private TaskCompletionSource<bool> _consent;
        private InstalledGameFiles _installed;

        public GameFiles(string uoDirectory)
        {
            _dir = uoDirectory;
        }

        public Phase State { get { lock (_lock) { return _phase; } } }
        public string Status { get { lock (_lock) { return _status; } } }
        public string Detail { get { lock (_lock) { return _detail; } } }
        public long TotalBytes => Interlocked.Read(ref _total);
        public long DoneBytes => Interlocked.Read(ref _done);

        /// <summary>Set when the game should load from somewhere else (offline with only a manual copy).</summary>
        public string UoPathOverride { get; private set; }

        public void Start() => Task.Run(RunAsync);

        /// <summary>The player agreed to a large download.</summary>
        public void Consent() => _consent?.TrySetResult(true);

        public void Retry()
        {
            if (State == Phase.Failed)
            {
                Set(Phase.Checking, "Checking the game files...", "");
                Start();
            }
        }

        private void Set(Phase phase, string status, string detail = null)
        {
            lock (_lock)
            {
                _phase = phase;
                _status = status;

                if (detail != null)
                {
                    _detail = detail;
                }
            }
        }

        private async Task RunAsync()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                _installed = LoadInstalled();
                GameFileManifest manifest = await FetchManifestAsync();

                if (manifest == null)
                {
                    // Offline: play with what's here.
                    if (_installed.Complete && _installed.Files.Keys.All(p => File.Exists(LocalPath(p))))
                    {
                        Log.Warn("[UOMobile] files: server unreachable, using the installed files");
                        Set(Phase.Done, "Ready");

                        return;
                    }

                    // Only before any adoption: a half-moved copy is missing files.
                    if (_installed.Files.Count == 0 && AdoptFromUsable())
                    {
                        Log.Warn($"[UOMobile] files: server unreachable, using the copy in {AdoptFrom}");
                        UoPathOverride = AdoptFrom;
                        Set(Phase.Done, "Ready");

                        return;
                    }

                    Set(Phase.Failed, "Can't reach the game server.", "The game files download from it the first time. Check your connection, then tap Retry.");

                    return;
                }

                List<GameFileEntry> needed = manifest.Files.Where(f => !UpToDate(f)).ToList();
                List<string> unlisted = _installed.Files.Keys.Where(p => manifest.Files.All(f => f.Path != p)).ToList();
                bool changes = needed.Count > 0 || unlisted.Count > 0 || _installed.Version != manifest.Version || !_installed.Complete;

                if (needed.Count > 0)
                {
                    _installed.Complete = false;
                    SaveInstalled();

                    // Files already in the folder but not on record (a lost state file, or a
                    // load failure that cleared it): keep the ones that still match.
                    needed = VerifyLocal(needed);

                    if (needed.Count > 0 && AdoptFromUsable())
                    {
                        needed = Adopt(needed);
                    }
                }

                if (needed.Count > 0)
                {
                    long bytes = needed.Sum(f => f.Size);

                    if (bytes > ASK_ABOVE_BYTES)
                    {
                        _consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        bool update = _installed.Files.Count > 0;
                        Set(Phase.AskToDownload, update ? $"An update needs {Size(bytes)} of game files." : $"The game needs {Size(bytes)} of game files.",
                            "Wi-Fi recommended. Keep the app open while it downloads; if it stops, it picks up where it left off.");
                        await _consent.Task;
                    }

                    await DownloadAllAsync(needed);
                }

                // Files the server no longer lists: only now that the new set is complete.
                foreach (string old in unlisted)
                {
                    TryDelete(LocalPath(old));
                    _installed.Files.Remove(old);
                }

                if (changes)
                {
                    _installed.Version = manifest.Version;
                    _installed.Complete = true;
                    SaveInstalled();
                }

                Log.Info($"[UOMobile] files: ready (manifest {manifest.Version}, {manifest.Files.Count} files)");
                Set(Phase.Done, "Ready", "");
            }
            catch (Exception e)
            {
                Log.Error($"[UOMobile] files: {e}");
                Set(Phase.Failed, "The download stopped.", $"{Friendly(e)} Tap Retry to continue where it left off.");
            }
        }

        // ---------- manifest / installed state ----------

        private async Task<GameFileManifest> FetchManifestAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "manifest.json?t=" + DateTime.UtcNow.Ticks);
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using HttpResponseMessage resp = await Http.SendAsync(req, cts.Token);
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync(cts.Token);
                GameFileManifest m = JsonSerializer.Deserialize(json, GameFilesJsonContext.Default.GameFileManifest);

                // A wrong manifest (built from the wrong folder, truncated) must not wipe good
                // installs: treat it like no manifest at all.
                if (m?.Files == null || m.Files.Count < MIN_MANIFEST_FILES ||
                    !m.Files.Any(f => string.Equals(f.Path, PREFIX + "tiledata.mul", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"manifest looks wrong ({m?.Files?.Count ?? 0} files, no tiledata.mul)");
                }

                foreach (GameFileEntry f in m.Files)
                {
                    LocalPath(f.Path); // validates: throws on anything outside the UO folder
                }

                return m;
            }
            catch (Exception e)
            {
                Log.Warn($"[UOMobile] files: manifest unavailable: {e.Message}");

                return null;
            }
        }

        private bool UpToDate(GameFileEntry f)
        {
            string local = LocalPath(f.Path);

            return _installed.Files.TryGetValue(f.Path, out string sha) && sha == f.Sha256 &&
                   File.Exists(local) && new FileInfo(local).Length == f.Size;
        }

        private string InstalledPath => Path.Combine(_dir, INSTALLED_FILE);

        private InstalledGameFiles LoadInstalled()
        {
            try
            {
                if (File.Exists(InstalledPath))
                {
                    return JsonSerializer.Deserialize(File.ReadAllText(InstalledPath), GameFilesJsonContext.Default.InstalledGameFiles)
                           ?? new InstalledGameFiles();
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[UOMobile] files: {INSTALLED_FILE} unreadable, checking everything again: {e.Message}");
            }

            return new InstalledGameFiles();
        }

        private void SaveInstalled()
        {
            lock (_lock)
            {
                string tmp = InstalledPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_installed, GameFilesJsonContext.Default.InstalledGameFiles));
                File.Move(tmp, InstalledPath, true);
            }
        }

        /// <summary>
        /// A manifest path ("uo/Music/Digital/x.mp3") in the local UO folder. Anything that could
        /// land outside it, or on the state and temp files, is rejected.
        /// </summary>
        private string LocalPath(string manifestPath)
        {
            string[] segments = manifestPath != null && manifestPath.StartsWith(PREFIX, StringComparison.Ordinal)
                ? manifestPath.Substring(PREFIX.Length).Split('/')
                : null;

            bool ok = segments != null && segments.Length > 0 && Array.TrueForAll(segments, seg =>
                seg.Length > 0 && seg != "." && seg != ".." && seg.IndexOfAny(new[] { '\\', ':' }) < 0);
            string name = ok ? segments[segments.Length - 1] : "";

            if (!ok || name.Equals(INSTALLED_FILE, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"bad path in manifest: {manifestPath}");
            }

            string root = Path.GetFullPath(_dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));

            if (!full.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"bad path in manifest: {manifestPath}");
            }

            return full;
        }

        /// <summary>Records files already in place whose hash matches; returns the rest.</summary>
        private List<GameFileEntry> VerifyLocal(List<GameFileEntry> needed)
        {
            var remaining = new List<GameFileEntry>();
            int i = 0;

            foreach (GameFileEntry f in needed)
            {
                i++;
                string local = LocalPath(f.Path);

                if (File.Exists(local) && new FileInfo(local).Length == f.Size)
                {
                    Set(Phase.Adopting, "Checking the game files...", $"{i} of {needed.Count}");

                    if (HashFile(local) == f.Sha256)
                    {
                        lock (_lock)
                        {
                            _installed.Files[f.Path] = f.Sha256;
                        }

                        continue;
                    }
                }

                remaining.Add(f);
            }

            if (remaining.Count != needed.Count)
            {
                SaveInstalled();
            }

            return remaining;
        }

        /// <summary>
        /// The game failed to load from these files: drop the record, so the next launch checks
        /// every file again (by hash; only mismatches download).
        /// </summary>
        public static void ForgetInstalled(string uoDirectory)
        {
            try
            {
                File.Delete(Path.Combine(uoDirectory, INSTALLED_FILE));
            }
            catch (Exception e)
            {
                Log.Warn($"[UOMobile] files: could not reset {INSTALLED_FILE}: {e.Message}");
            }
        }

        // ---------- adopting a manual copy ----------

        // Any case: iOS file names are case-sensitive and copies vary ("TileData.mul").
        private static bool AdoptFromUsable() =>
            !string.IsNullOrEmpty(AdoptFrom) && Directory.Exists(AdoptFrom) &&
            Directory.EnumerateFiles(AdoptFrom).Any(f => Path.GetFileName(f).Equals("tiledata.mul", StringComparison.OrdinalIgnoreCase));

        /// <summary>Moves matching files from the manual copy into place; returns what still needs downloading.</summary>
        private List<GameFileEntry> Adopt(List<GameFileEntry> needed)
        {
            // Relative path (lower-case) -> file in the manual copy.
            var candidates = new Dictionary<string, string>();
            string root = Path.GetFullPath(AdoptFrom);

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                candidates[Path.GetRelativePath(root, file).Replace('\\', '/').ToLowerInvariant()] = file;
            }

            var remaining = new List<GameFileEntry>();
            int i = 0;

            foreach (GameFileEntry f in needed)
            {
                i++;
                string rel = f.Path.Substring(PREFIX.Length).ToLowerInvariant();

                if (candidates.TryGetValue(rel, out string src) && new FileInfo(src).Length == f.Size)
                {
                    Set(Phase.Adopting, "Checking the game files already on this phone...", $"{i} of {needed.Count}");

                    if (HashFile(src) == f.Sha256)
                    {
                        string local = LocalPath(f.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(local));
                        File.Move(src, local, true);
                        _installed.Files[f.Path] = f.Sha256;
                        SaveInstalled();

                        continue;
                    }
                }

                remaining.Add(f);
            }

            Log.Info($"[UOMobile] files: adopted {needed.Count - remaining.Count} files from {AdoptFrom}");

            return remaining;
        }

        // ---------- downloading ----------

        private async Task DownloadAllAsync(List<GameFileEntry> files)
        {
            Interlocked.Exchange(ref _total, files.Sum(f => f.Size));
            Interlocked.Exchange(ref _done, 0);
            Set(Phase.Downloading, "Downloading the game files...", "");

            var queue = new ConcurrentQueue<GameFileEntry>(files.OrderByDescending(f => f.Size));
            Task[] workers = Enumerable.Range(0, WORKERS).Select(_ => Task.Run(async () =>
            {
                while (queue.TryDequeue(out GameFileEntry f))
                {
                    await DownloadOneAsync(f);
                }
            })).ToArray();

            await Task.WhenAll(workers);
        }

        private async Task DownloadOneAsync(GameFileEntry f)
        {
            string local = LocalPath(f.Path);
            string part = local + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(local));
            long counted = 0; // this file's share of _done
            int failures = 0;

            while (true)
            {
                long before = counted;

                try
                {
                    long have = File.Exists(part) ? new FileInfo(part).Length : 0;

                    if (have > f.Size)
                    {
                        File.Delete(part);
                        have = 0;
                    }

                    Interlocked.Add(ref _done, have - counted);
                    counted = have;

                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    if (have > 0)
                    {
                        HashInto(hash, part); // resuming: the part already on disk
                    }

                    if (have < f.Size)
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + EscapePath(f.Path));
                        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                        if (have > 0)
                        {
                            req.Headers.Range = new RangeHeaderValue(have, null);
                        }

                        using var cts = new CancellationTokenSource();
                        cts.CancelAfter(IdleTimeout);
                        using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        resp.EnsureSuccessStatusCode();

                        if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                        {
                            // The server sent the whole file: start this one over.
                            Interlocked.Add(ref _done, -counted);
                            counted = have = 0;
                            hash.GetHashAndReset();
                        }

                        using Stream body = await resp.Content.ReadAsStreamAsync(cts.Token);
                        using var fs = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                        byte[] buffer = new byte[1 << 20];

                        while (true)
                        {
                            cts.CancelAfter(IdleTimeout); // stalled for 30 s: give up this attempt
                            int n = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);

                            if (n == 0)
                            {
                                break;
                            }

                            fs.Write(buffer, 0, n);
                            hash.AppendData(buffer, 0, n);
                            counted += n;
                            Interlocked.Add(ref _done, n);
                        }
                    }

                    string sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

                    if (counted != f.Size || sha != f.Sha256)
                    {
                        string what = $"{f.Path} arrived damaged ({counted} of {f.Size} bytes, checksum {(sha == f.Sha256 ? "ok" : "wrong")})";
                        File.Delete(part);
                        Interlocked.Add(ref _done, -counted);
                        counted = 0;

                        throw new InvalidDataException(what);
                    }

                    File.Move(part, local, true);

                    lock (_lock)
                    {
                        _installed.Files[f.Path] = f.Sha256;
                    }

                    SaveInstalled();

                    return;
                }
                catch (Exception e)
                {
                    // Leaving the app drops the connection; an attempt that got somewhere
                    // doesn't count against the four tries.
                    failures = counted > before ? 1 : failures + 1;

                    if (failures >= 4)
                    {
                        throw;
                    }

                    Log.Warn($"[UOMobile] files: {f.Path} failed ({failures}/4): {e.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(2 * failures));
                }
            }
        }

        private static string EscapePath(string path) =>
            string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

        private static void HashInto(IncrementalHash hash, string file)
        {
            byte[] buffer = new byte[1 << 20];
            using var fs = File.OpenRead(file);
            int n;

            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, n);
            }
        }

        private static string HashFile(string file)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            HashInto(hash, file);

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static void TryDelete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        private static string Friendly(Exception e) => e switch
        {
            IOException io when io.Message.IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0 => "The phone is out of storage space.",
            HttpRequestException _ => "The connection dropped.",
            OperationCanceledException _ => "The connection stalled.",
            InvalidDataException _ => "A file arrived damaged.",
            _ => e.Message
        };

        public static string Size(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" :
            bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" :
            $"{Math.Max(1, bytes / 1024)} KB";
    }
}
