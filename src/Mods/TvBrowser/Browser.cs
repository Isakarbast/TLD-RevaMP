using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace TvBrowser
{
    /// The browser itself: a separate program this mod starts and drives over the DevTools protocol (Cdp). One program
    /// for every TV (a page each), started when a TV near the player is on and closed a minute after the last one goes.
    ///
    /// Which program depends on where the game runs:
    /// - Under Proton, tvbrowser-host (Host/host.cc): our program on the Chromium Embedded Framework (CEF), its Linux
    ///   build, run outside Wine. Its pages are drawn off-screen by a normal Chromium, not a headless one: YouTube stops
    ///   its videos after about 40 s in headless Chromium and in any Chromium under Wine. The mod carries tvbrowser-host
    ///   inside its DLL (Host/linux-x64) and writes it next to CEF's files; pages are made through a connection to it.
    /// - On Windows, Chromium's headless shell (Google's "Chrome for Testing" build), sandboxed, with one page process
    ///   for every TV, until the mod carries a Windows build of tvbrowser-host.
    ///
    /// Neither is shipped with the mod: the first time a player takes a TV's keyboard, the TV asks to download it (one
    /// pinned version, checked against the SHA-256 written below before anything is kept). Its profile (cookies, cache)
    /// lives in a temporary folder that is deleted when the game closes: nothing a page stored stays.
    internal static class Browser
    {
        /// A pinned download.
        internal sealed class Package
        {
            public string Name, Version, Url, Sha256, Folder;
            /// The file (in Folder) that is there once it is unpacked; for the headless shell, the program.
            public string Check;
            public long Bytes;
            public bool TarBz2;
        }

        /// CEF, Spotify's builds (cef-builds.spotifycdn.com), the "minimal" Linux one. tvbrowser-host is built against
        /// this version (Host/build.sh): the two check each other's API, so a new CEF needs a new tvbrowser-host.
        public static readonly Package Cef = new Package
        {
            Name = "the Chromium Embedded Framework (CEF) of the open-source Chromium, its Linux build, run outside Wine",
            Version = "156.0.15+gcf671c0+chromium-156.0.8078.80",
            Url = "https://cef-builds.spotifycdn.com/cef_binary_156.0.15%2Bgcf671c0%2Bchromium-156.0.8078.80_linux64_minimal.tar.bz2",
            Sha256 = "6873f975bb047316680ea1839980ed5bd4c33c8cd4ce45761c94211856605388", Bytes = 333318600, TarBz2 = true,
            Folder = "cef-156.0.15-linux64", Check = "libcef.so",
        };
        public static readonly Package Headless = new Package
        {
            Name = "Chromium's headless shell, Google's \"Chrome for Testing\" build of the open-source Chromium",
            Version = "156.0.8078.47",
            Url = "https://storage.googleapis.com/chrome-for-testing-public/156.0.8078.47/win64/chrome-headless-shell-win64.zip",
            Sha256 = "e0af9b23f8051cca4f37ca49cd425e8055701bfc0383c198d67f8d5cc5884249", Bytes = 126051683,
            Folder = "chromium-156.0.8078.47", Check = "chrome-headless-shell-win64/chrome-headless-shell.exe",
        };
        /// The one this game uses.
        public static Package Pinned => UnderWine ? Cef : Headless;
        /// Pages are made by tvbrowser-host, not over DevTools.
        public static bool Host => Pinned == Cef;

        public enum Phase { Missing, Downloading, Off, Starting, Running, Failed }
        public static Phase Now = Phase.Missing;
        public static string Status = "";
        public static float DownloadFrac = -1f;
        /// The player agreed to the download (this game session).
        public static bool Accepted;

        public static string Dir, ProgramDir, ProfileRoot;
        public static Cdp Connection;
        private static Process _proc;
        private static HostProcess _host;
        private static string _profile;
        private static float _idleSince = -1f, _startedAt;
        private static readonly List<Tab> Tabs = new List<Tab>();
        private static readonly HashSet<string> OurTargets = new HashSet<string>();
        /// The blank page the headless shell opens with: the first TV takes it (one page process fewer).
        private static string _spare;
        private static readonly Dictionary<string, string> Popups = new Dictionary<string, string>();   // popup target -> its opener's target
        private static readonly Dictionary<string, Tab> HostPages = new Dictionary<string, Tab>();     // tvbrowser-host's page key -> its tab
        private static int _pageNo;
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> Main = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        public static int Starts, TabsMade;

        public static string Exe => ProgramDir == null ? null : Path.Combine(ProgramDir, Pinned.Check.Replace('/', Path.DirectorySeparatorChar));
        public static bool Installed => Exe != null && File.Exists(Exe);
        public static int OpenTabs => Tabs.Count;
        public static long ProcessMB { get { try { return !Host && _proc != null && !_proc.HasExited ? _proc.WorkingSet64 / 1048576 : 0; } catch { return 0; } } }

        public static void Init(string configDir)
        {
            Dir = Path.Combine(configDir, "TvBrowser");
            ProgramDir = Path.Combine(Dir, Pinned.Folder);
            ProfileRoot = Path.Combine(Path.GetTempPath(), "TvBrowser");
            try { Directory.CreateDirectory(Dir); } catch { }
            Now = Installed ? Phase.Off : Phase.Missing;
            ThreadPool.QueueUserWorkItem(_ => Leftovers());
        }

        // ------------------------------------------------------------------ the download

        public static void Download()
        {
            if (Installed || Now == Phase.Downloading) return;
            Accepted = true;
            Now = Phase.Downloading; Status = "downloading the browser...";
            Plugin.Runner.StartCoroutine(DownloadRoutine(Pinned));
        }

        private static IEnumerator DownloadRoutine(Package pkg)
        {
            string part = Path.Combine(Dir, pkg.Folder + ".download");
            Plugin.Log.LogInfo("downloading the browser (" + pkg.Url + ")");
            using (var req = new UnityWebRequest(pkg.Url, UnityWebRequest.kHttpVerbGET, new DownloadHandlerFile(part) { removeFileOnAbort = true }, null))
            {
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    DownloadFrac = Mathf.Clamp01(req.downloadedBytes / (float)pkg.Bytes);
                    Status = $"downloading the browser... {req.downloadedBytes / 1048576f:0} of {pkg.Bytes / 1048576f:0} MB";
                    yield return null;
                }
                DownloadFrac = -1f;
                if (req.result != UnityWebRequest.Result.Success) { TryDelete(part); Fail("the browser's download failed: " + req.error); yield break; }
            }
            Status = "checking the browser's download...";
            string hash = null, error = null;
            bool done = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var sha = SHA256.Create()) using (var f = File.OpenRead(part))
                        hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                    if (hash == pkg.Sha256) { if (pkg.TarBz2) UnpackCef(part); else UnpackZip(part); }
                }
                catch (Exception e) { error = e.Message; }
                done = true;
            });
            while (!done)
            {
                if (_unpackFrac >= 0f) Status = $"unpacking the browser... {_unpackFrac * 100f:0}%";
                yield return null;
            }
            _unpackFrac = -1f;
            TryDelete(part);
            if (error != null) { Fail("the browser couldn't be unpacked: " + error); yield break; }
            if (hash != pkg.Sha256) { Fail("the browser's download didn't match its checksum: thrown away"); yield break; }
            Plugin.Log.LogInfo($"browser downloaded and verified (sha256 {hash.Substring(0, 12)})");
            Now = Phase.Off; Status = "";
        }

        private static volatile float _unpackFrac = -1f;

        /// A fresh folder to unpack into; moved to ProgramDir once everything is there.
        private static string UnpackFolder()
        {
            string tmp = ProgramDir + ".part";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            return tmp;
        }

        private static void Finish(string tmp)
        {
            if (Directory.Exists(ProgramDir)) Directory.Delete(ProgramDir, true);
            Directory.Move(tmp, ProgramDir);
        }

        /// The zip's files into ProgramDir (only inside it: an entry that would land elsewhere stops the unpacking).
        private static void UnpackZip(string zipPath)
        {
            string tmp = UnpackFolder();
            string root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
            // ZipArchive on a stream: ZipFile is in System.IO.Compression.FileSystem, which the game's Mono may not have
            using (var file = File.OpenRead(zipPath))
            using (var zip = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Read))
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    string to = Path.GetFullPath(Path.Combine(tmp, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!to.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("an entry outside the folder: " + e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    using (var src = e.Open()) using (var dst = File.Create(to)) src.CopyTo(dst);
                }
            if (!File.Exists(Path.Combine(tmp, Pinned.Check.Replace('/', Path.DirectorySeparatorChar)))) throw new IOException("no " + Pinned.Check + " in it");
            Finish(tmp);
        }

        /// What tvbrowser-host needs of CEF's archive, all into one folder (the locale under locales/): about 330 MB of
        /// its 1.5 GB. English only: every machine's pages then lay out alike.
        private static readonly string[] CefFiles =
        {
            "Release/libcef.so", "Release/libvk_swiftshader.so", "Release/libvulkan.so.1", "Release/vk_swiftshader_icd.json",
            "Release/v8_context_snapshot.bin", "Resources/chrome_100_percent.pak", "Resources/chrome_200_percent.pak",
            "Resources/resources.pak", "Resources/icudtl.dat", "Resources/locales/en-US.pak",
        };

        private static void UnpackCef(string archive)
        {
            string tmp = UnpackFolder();
            var found = new HashSet<string>();
            _unpackFrac = 0f;
            using (var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var bz = new Bzip2Stream(file);
                long total = file.Length;
                var buf = new byte[1 << 16];
                Tar.Read(bz, (name, body) =>
                {
                    int slash = name.IndexOf('/');
                    string rel = slash < 0 ? name : name.Substring(slash + 1);
                    if (Array.IndexOf(CefFiles, rel) < 0) return;
                    string to = Path.Combine(tmp, rel.StartsWith("Resources/locales/") ? Path.Combine("locales", Path.GetFileName(rel)) : Path.GetFileName(rel));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    using (var dst = File.Create(to))
                    {
                        long left = rel == "Release/libcef.so" ? LoadableHead(body, dst) : long.MaxValue;
                        int r;
                        while (left > 0 && (r = body.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
                        {
                            dst.Write(buf, 0, r); left -= r;
                            _unpackFrac = (float)bz.CompressedRead / total;
                        }
                    }
                    found.Add(rel);
                });
            }
            foreach (var f in CefFiles) if (!found.Contains(f)) throw new IOException("no " + f + " in it");
            Finish(tmp);
        }

        /// libcef.so without what only debuggers read: 1.5 GB with its debugging data, 0.28 GB without. Everything the
        /// dynamic loader maps (the program headers' file ranges) comes first in the file and is kept; the debugging
        /// sections and symbol tables after it are dropped, and the header says there are no section headers. Writes
        /// the file's first bytes and returns how many more to copy.
        private static long LoadableHead(Stream body, Stream dst)
        {
            var head = new byte[4096];
            int got = 0, r;
            while (got < head.Length && (r = body.Read(head, got, head.Length - got)) > 0) got += r;
            if (got < 64 || head[0] != 0x7f || head[1] != 'E' || head[2] != 'L' || head[3] != 'F' || head[4] != 2 || head[5] != 1)
                throw new IOException("libcef.so isn't a 64-bit ELF file");
            long phoff = BitConverter.ToInt64(head, 0x20);
            int phentsize = BitConverter.ToUInt16(head, 0x36), phnum = BitConverter.ToUInt16(head, 0x38);
            if (phoff < 64 || phoff + (long)phnum * phentsize > got) throw new IOException("libcef.so's program headers aren't where expected");
            long end = 0;
            for (int i = 0; i < phnum; i++)
            {
                int at = (int)phoff + i * phentsize;
                end = Math.Max(end, BitConverter.ToInt64(head, at + 8) + BitConverter.ToInt64(head, at + 32));
            }
            Array.Clear(head, 0x28, 8);   // e_shoff
            Array.Clear(head, 0x3c, 4);   // e_shnum, e_shstrndx
            dst.Write(head, 0, (int)Math.Min(got, end));
            return end - got;
        }

        /// A player asks again after a failure (taking a TV's keyboard): the next TV that needs the browser starts it.
        public static void Retry() { if (Now == Phase.Failed) Now = Installed ? Phase.Off : Phase.Missing; }

        public static void Fail(string why)
        {
            Status = why; Now = Phase.Failed; DownloadFrac = -1f;
            Plugin.Log.LogWarning("TV browser: " + why);
            Stop();
        }

        // ------------------------------------------------------------------ the program

        /// Under Wine (Proton) the Linux build runs, outside it; on Windows the headless shell, sandboxed.
        public static bool UnderWine
        {
            get
            {
                if (_wine == null)
                {
                    try { var ntdll = GetModuleHandle("ntdll.dll"); _wine = ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero; }
                    catch { _wine = false; }
                }
                return _wine.Value;
            }
        }
        private static bool? _wine;
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private const string CommonArgs = " --mute-audio --hide-scrollbars --disable-extensions --disable-sync --disable-background-networking" +
                                          " --disable-component-update --disable-features=Translate,MediaRouter,OptimizationHints --autoplay-policy=user-gesture-required";

        private static void Start()
        {
            if (Now != Phase.Off && Now != Phase.Failed || !Installed) return;
            Now = Phase.Starting; Status = "starting the browser..."; _startedAt = Time.realtimeSinceStartup; Starts++;
            string profile = _profile = Path.Combine(ProfileRoot, "profile-" + Process.GetCurrentProcess().Id + "-" + Starts);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(profile);
                    var cdp = Host ? StartHost(profile) : StartHeadless(profile);
                    Main.Enqueue(() => Connected(cdp));
                }
                catch (Exception e) { Main.Enqueue(() => Fail("the browser couldn't start: " + e.Message)); }
            });
        }

        private static Cdp StartHeadless(string profile)
        {
            string args = "--remote-debugging-port=0 --remote-debugging-address=127.0.0.1 --user-data-dir=\"" + profile + "\"" +
                          " --no-first-run --no-default-browser-check --window-size=" + Tab.Width + "," + Tab.Height + CommonArgs + " about:blank";
            var psi = new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Exe),
                                                        RedirectStandardError = true, RedirectStandardOutput = true };
            var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (o, e) => { };
            p.ErrorDataReceived += (o, e) => { };
            p.Start();
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            _proc = p;
            try { File.WriteAllText(Path.Combine(ProfileRoot, "browser.pid"), p.Id.ToString()); } catch { }
            // the program writes the port it listens on, and the browser's address, once it is up
            string portFile = Path.Combine(profile, "DevToolsActivePort");
            var sw = Stopwatch.StartNew();
            string[] lines = null;
            while (sw.ElapsedMilliseconds < 45000)
            {
                if (p.HasExited) throw new IOException("the browser closed at once (exit code " + p.ExitCode + ")");
                try { if (File.Exists(portFile)) { lines = File.ReadAllLines(portFile); if (lines.Length >= 2) break; } } catch (IOException) { }
                Thread.Sleep(100);
            }
            if (lines == null || lines.Length < 2) throw new IOException("the browser didn't start in 45 s");
            return Cdp.Connect(int.Parse(lines[0].Trim()), lines[1].Trim());
        }

        /// tvbrowser-host as it runs: its control connection, and whether it has gone (the connection closed).
        private sealed class HostProcess
        {
            public TcpClient Tcp;
            public StreamWriter Out;
            public volatile bool Gone;
        }

        /// Under Wine: tvbrowser-host is a Linux program. Wine starts a Linux program it is asked to (but not the
        /// dynamic loader, a library, and not a file without its execute bit, which a file the game writes doesn't
        /// have), so /bin/sh starts the loader with it. Wine gives such a program no pipes, so the two talk over a
        /// connection on 127.0.0.1: the mod listens, tvbrowser-host connects and says "hello <token>". The program is
        /// given its DevTools port (a free one; 0 would make Chromium report itself as automated).
        private static Cdp StartHost(string profile)
        {
            WriteHostFiles();
            int port = FreePort();
            string host = UnixName(Path.Combine(ProgramDir, "tvbrowser-host")), data = UnixName(profile), sh = DosName("/bin/sh");
            if (host == null || data == null || sh == null) throw new IOException("Wine didn't give the browser's Linux paths");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                string token = Guid.NewGuid().ToString("N");
                string args = "-c \"exec /lib64/ld-linux-x86-64.so.2 \\\"$0\\\" \\\"$@\\\"\" " + Quote(host) +
                              " --tvb-control=" + ((IPEndPoint)listener.LocalEndpoint).Port + ":" + token +
                              " --remote-debugging-port=" + port + " --remote-debugging-address=127.0.0.1 --user-data-dir=" + Quote(data) +
                              " --lang=en-US" + CommonArgs;
                // no Process to keep: Wine has no handle for a Linux program (its id is 0)
                using (Process.Start(new ProcessStartInfo(sh, args) { UseShellExecute = false, CreateNoWindow = true })) { }
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    if (sw.ElapsedMilliseconds > 45000) throw new IOException("the browser didn't start in 45 s");
                    if (!listener.Pending()) { Thread.Sleep(50); continue; }
                    var tcp = listener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    var reader = new StreamReader(tcp.GetStream(), new UTF8Encoding(false));
                    tcp.ReceiveTimeout = 5000;
                    string hello;
                    try { hello = reader.ReadLine(); } catch (IOException) { hello = null; }
                    if (hello != "hello " + token) { tcp.Close(); continue; }   // not ours
                    tcp.ReceiveTimeout = 0;
                    var h = new HostProcess { Tcp = tcp, Out = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true } };
                    var ready = new ManualResetEvent(false);
                    new Thread(() =>
                    {
                        try
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line == "ready") ready.Set();
                                else { string l = line; Main.Enqueue(() => HostSaid(h, l)); }
                            }
                        }
                        catch (Exception) { }
                        h.Gone = true; ready.Set();
                    }) { IsBackground = true, Name = "TvBrowser host" }.Start();
                    _host = h;
                    if (!ready.WaitOne(Math.Max(1000, 45000 - (int)sw.ElapsedMilliseconds))) throw new IOException("the browser didn't start in 45 s");
                    if (h.Gone) throw new IOException("the browser closed at once");
                    return Cdp.Connect(port, DevToolsPath(port));
                }
            }
            finally { listener.Stop(); }
        }

        private static string Quote(string s) => "\"" + s + "\"";

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// The browser's DevTools address (ws://127.0.0.1:port/devtools/browser/<id>), from its /json/version.
        private static string DevToolsPath(int port)
        {
            var sw = Stopwatch.StartNew();
            var rx = new Regex("\"webSocketDebuggerUrl\"\\s*:\\s*\"ws://[^/\"]+(/[^\"]+)\"");
            while (true)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        tcp.Connect("127.0.0.1", port);
                        tcp.ReceiveTimeout = 3000;
                        var s = tcp.GetStream();
                        var req = Encoding.ASCII.GetBytes("GET /json/version HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                        s.Write(req, 0, req.Length);
                        var text = new StringBuilder();
                        var buf = new byte[4096];
                        int r;
                        while ((r = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            text.Append(Encoding.UTF8.GetString(buf, 0, r));
                            var m = rx.Match(text.ToString());
                            if (m.Success) return m.Groups[1].Value;
                        }
                    }
                }
                catch (Exception e) when (e is SocketException || e is IOException) { }
                if (sw.ElapsedMilliseconds > 15000) throw new IOException("the browser's DevTools didn't answer");
                Thread.Sleep(200);
            }
        }

        /// tvbrowser-host and the two accessibility stand-ins (Steam's newer runtime lacks the libraries CEF links
        /// to; nothing here reads a page aloud), from the DLL into ProgramDir when they differ.
        private static void WriteHostFiles()
        {
            foreach (var name in new[] { "tvbrowser-host", "libatk-bridge-2.0.so.0", "libatspi.so.0" })
            {
                byte[] want;
                using (var s = typeof(Browser).Assembly.GetManifestResourceStream("host." + name))
                {
                    if (s == null) throw new IOException("this build of the mod has no " + name);
                    using (var m = new MemoryStream()) { s.CopyTo(m); want = m.ToArray(); }
                }
                string to = Path.Combine(ProgramDir, name);
                if (File.Exists(to))
                {
                    var have = File.ReadAllBytes(to);
                    if (have.Length == want.Length && ((IStructuralEquatable)have).Equals(want, StructuralComparisons.StructuralEqualityComparer)) continue;
                }
                File.WriteAllBytes(to, want);
            }
        }

        // Wine's own calls (kernel32 under Wine only): a Windows path's Linux path, and the other way
        [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "wine_get_unix_file_name")] private static extern IntPtr WineUnixFileName(string dos);
        [DllImport("kernel32", EntryPoint = "wine_get_dos_file_name")] private static extern IntPtr WineDosFileName(byte[] unix);
        [DllImport("kernel32")] private static extern IntPtr GetProcessHeap();
        [DllImport("kernel32")] private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr mem);

        internal static string UnixName(string dos)
        {
            IntPtr p = WineUnixFileName(Path.GetFullPath(dos));
            if (p == IntPtr.Zero) return null;
            try
            {
                int n = 0;
                while (Marshal.ReadByte(p, n) != 0) n++;
                var b = new byte[n];
                Marshal.Copy(p, b, 0, n);
                return Encoding.UTF8.GetString(b);
            }
            finally { HeapFree(GetProcessHeap(), 0, p); }
        }

        internal static string DosName(string unix)
        {
            IntPtr p = WineDosFileName(Encoding.UTF8.GetBytes(unix + "\0"));
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { HeapFree(GetProcessHeap(), 0, p); }
        }

        private static void Connected(Cdp cdp)
        {
            if (Now != Phase.Starting) { cdp.Dispose(); return; }
            Connection = cdp; Now = Phase.Running; Status = "";
            Plugin.Log.LogInfo($"TV browser running ({(Host ? "CEF, outside Wine" : "headless shell")}, started in {Time.realtimeSinceStartup - _startedAt:0.0} s)");
            cdp.Send("Browser.setDownloadBehavior", Json.O("behavior", "deny"));
            if (Host)
            {
                UserAgent = null;
                foreach (var t in Tabs) if (!t.Ready && t.TargetId == null) { t.Create(); TabsMade++; }
                return;
            }
            // sites refuse "HeadlessChrome" (DuckDuckGo's search answers with an error page): the pages say plain Chromium
            cdp.Send("Browser.getVersion", null, null, v =>
            {
                UserAgent = (v.Result != null ? Json.Str(v.Result, "userAgent") : null)?.Replace("HeadlessChrome", "Chrome");
                cdp.Send("Target.getTargets", null, null, r =>
                {
                    if (r.Result != null && r.Result.TryGetValue("targetInfos", out var o) && o is List<object> list)
                        foreach (var x in list)
                            if (x is Dictionary<string, object> info && Json.Str(info, "type") == "page") { _spare = Json.Str(info, "targetId"); OurTargets.Add(_spare); break; }
                    cdp.Send("Target.setDiscoverTargets", Json.O("discover", true));
                    foreach (var t in Tabs) if (!t.Ready && t.TargetId == null) { t.Create(); TabsMade++; }
                });
            });
        }

        /// The headless shell's user agent without "Headless" (null until it runs, and with tvbrowser-host).
        internal static string UserAgent;

        /// Emulation.setUserAgentOverride for a page: that user agent, and client hints naming Chromium, not HeadlessChrome.
        internal static Dictionary<string, object> UserAgentArgs()
        {
            var m = Regex.Match(UserAgent ?? "", @"Chrome/(\d+)");
            string major = m.Success ? m.Groups[1].Value : "0";
            var brands = new List<object> { Json.O("brand", "Chromium", "version", major), Json.O("brand", "Not-A.Brand", "version", "24") };
            return Json.O("userAgent", UserAgent, "userAgentMetadata", Json.O("brands", brands, "platform", "Windows", "platformVersion", "10.0.0",
                                                                            "architecture", "x86", "model", "", "mobile", false));
        }

        /// Stops the program and forgets every page (the TVs make new ones when they need them).
        public static void Stop()
        {
            foreach (var t in Tabs) t.Screen.TabLost(t);
            Tabs.Clear(); OurTargets.Clear(); Popups.Clear(); HostPages.Clear(); _spare = null;
            var cdp = Connection; Connection = null;
            var host = _host; _host = null;
            if (cdp != null) { if (host == null) { try { cdp.Send("Browser.close"); } catch { } } cdp.Dispose(); }
            // tvbrowser-host closes its pages and exits on "quit", and when its connection closes
            if (host != null) { try { host.Out.WriteLine("quit"); } catch { } }
            var p = _proc; _proc = null;
            string profile = _profile; _profile = null;
            if (Now == Phase.Running || Now == Phase.Starting) Now = Installed ? Phase.Off : Phase.Missing;
            if (p == null && host == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (host != null) { var sw = Stopwatch.StartNew(); while (!host.Gone && sw.ElapsedMilliseconds < 8000) Thread.Sleep(100); try { host.Tcp.Close(); } catch { } }
                else try { if (!p.WaitForExit(3000)) KillTree(p); } catch { }
                Thread.Sleep(500);
                if (profile != null) TryDeleteDir(profile);
            });
        }

        /// The game is closing: the program goes with it, and its profile.
        public static void Shutdown()
        {
            var p = _proc;
            Stop();
            if (p != null) { try { if (!p.HasExited) KillTree(p); } catch { } }
        }

        private static void KillTree(Process p)
        {
            try
            {
                using (var k = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {p.Id}") { UseShellExecute = false, CreateNoWindow = true }))
                    k?.WaitForExit(5000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        /// What a crashed game left: its headless shell (by the pid it wrote; tvbrowser-host exits by itself when its
        /// connection to the game closes), old profiles, and browsers of earlier versions of the mod.
        private static void Leftovers()
        {
            try
            {
                string pidFile = Path.Combine(ProfileRoot, "browser.pid");
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
                {
                    try
                    {
                        var old = Process.GetProcessById(pid);
                        if (old.ProcessName.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0) KillTree(old);
                    }
                    catch { }
                    TryDelete(pidFile);
                }
                if (Directory.Exists(ProfileRoot))
                    foreach (var d in Directory.GetDirectories(ProfileRoot, "profile-*")) TryDeleteDir(d);
                foreach (var pattern in new[] { "chromium-*", "cef-*" })
                    foreach (var d in Directory.GetDirectories(Dir, pattern))
                        if (!string.Equals(Path.GetFullPath(d), Path.GetFullPath(ProgramDir), StringComparison.OrdinalIgnoreCase) && !d.EndsWith(".part")) TryDeleteDir(d);
            }
            catch { }
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        private static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

        // ------------------------------------------------------------------ pages

        /// A page for a TV: made now if the browser runs, else when it has started.
        public static Tab Open(TvScreen screen)
        {
            var t = new Tab(screen);
            Tabs.Add(t);
            _idleSince = -1f;
            if (Now == Phase.Running && Connection != null) { t.Create(); TabsMade++; }
            else if (Now == Phase.Off) Start();
            return t;
        }

        public static void Close(Tab t)
        {
            if (t == null) return;
            Tabs.Remove(t);
            if (!string.IsNullOrEmpty(t.TargetId)) OurTargets.Remove(t.TargetId);
            t.Dispose();
        }

        /// The headless shell's first blank page, once (null after, and with tvbrowser-host).
        internal static string TakeSpare() { var s = _spare; _spare = null; return s; }

        /// tvbrowser-host makes a page for the tab; it is attached once the program says it's made (HostSaid).
        internal static string NewPage(Tab t)
        {
            string key = "tv" + (++_pageNo);
            HostPages[key] = t;
            HostSend("new " + key + " " + Tab.Width + " " + Tab.Height);
            return key;
        }

        private static void HostSend(string line)
        {
            try { _host?.Out.WriteLine(line); }
            catch (Exception e) { Plugin.Log.LogWarning("TV browser: couldn't tell tvbrowser-host: " + e.Message); }
        }

        /// A tab's page goes (tvbrowser-host's by its key, the headless shell's by its target).
        internal static void ClosePage(Tab t)
        {
            if (t.Key != null) { HostSend("close " + t.Key); return; }
            if (string.IsNullOrEmpty(t.TargetId)) return;
            OurTargets.Remove(t.TargetId);
            Connection?.Send("Target.closeTarget", Json.O("targetId", t.TargetId));
        }

        /// A line from tvbrowser-host (main thread).
        private static void HostSaid(HostProcess from, string line)
        {
            if (from != _host) return;
            var parts = line.Split(new[] { ' ' }, 3);
            string key = parts.Length > 1 ? parts[1] : "";
            HostPages.TryGetValue(key, out var tab);
            switch (parts[0])
            {
                case "made":
                    if (tab != null) FindTarget(key, tab, 0);
                    break;
                case "closed":
                    HostPages.Remove(key);
                    // not one the mod closed (a page's window.close()): its TV makes a new one
                    if (tab != null && !tab.Closed) { Tabs.Remove(tab); tab.Screen.TabLost(tab); tab.Dispose(); }
                    break;
                case "popup":
                {
                    // a link that opens a new window, or window.open(): it opens in its TV instead
                    string clean = parts.Length > 2 ? Address.Clean(parts[2]) : null;
                    if (tab != null && !tab.Closed && clean != null) tab.Screen.PopupOpened(tab, clean);
                    break;
                }
                default:
                    Plugin.Log.LogWarning("TV browser (tvbrowser-host): " + line);
                    break;
            }
        }

        /// A page tvbrowser-host made, found among the DevTools targets by its address (about:blank#tvb=<key>).
        private static void FindTarget(string key, Tab tab, int tries)
        {
            var cdp = Connection;
            if (cdp == null) return;
            if (tab.Closed) { HostSend("close " + key); return; }
            cdp.Send("Target.getTargets", null, null, r =>
            {
                if (cdp != Connection) return;
                if (r.Result != null && r.Result.TryGetValue("targetInfos", out var o) && o is List<object> list)
                    foreach (var x in list)
                        if (x is Dictionary<string, object> info && Json.Str(info, "type") == "page" && (Json.Str(info, "url") ?? "").EndsWith("#tvb=" + key))
                        {
                            tab.Made(Json.Str(info, "targetId"));
                            return;
                        }
                if (tries >= 25) { Fail("the browser's new page didn't appear"); return; }
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(200); Main.Enqueue(() => FindTarget(key, tab, tries + 1)); });
            });
        }

        public static void Tick()
        {
            while (Main.TryDequeue(out var a)) { try { a(); } catch (Exception e) { Plugin.Log.LogError("TV browser: " + e); } }
            if (Connection != null)
            {
                Connection.Pump(OnEvent);
                if (!Connection.Open) { Fail("the browser stopped: " + Connection.Problem); return; }
                foreach (var t in Tabs) { if (!string.IsNullOrEmpty(t.TargetId)) OurTargets.Add(t.TargetId); t.Tick(); }
            }
            if (Now == Phase.Running)
            {
                bool gone;
                if (Host) gone = _host == null || _host.Gone;
                else if (_proc == null) gone = false;
                else { try { gone = _proc.HasExited; } catch { gone = true; } }
                if (gone) { Fail("the browser closed"); return; }
            }
            // pages opened while the browser was still downloading (or before a retry) wait for it: start it for them now
            if (Now == Phase.Off && Tabs.Count > 0) Start();
            // no TV needs it for a minute: it closes (it comes back when one does)
            if ((Now == Phase.Running || Now == Phase.Starting) && Tabs.Count == 0)
            {
                if (_idleSince < 0f) _idleSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - _idleSince > IdleSeconds) { Plugin.Log.LogInfo("TV browser closed: no TV in use"); Stop(); }
            }
            else _idleSince = -1f;
        }

        public static float IdleSeconds = 60f;

        private static void OnEvent(Cdp.Message m)
        {
            if (m.Session != null)
            {
                foreach (var t in Tabs) if (t.Session == m.Session) { t.OnEvent(m); return; }
                return;
            }
            // the headless shell's popups (tvbrowser-host makes none: it says "popup")
            if (Host) return;
            var info = Json.Obj(m.Params, "targetInfo");
            switch (m.Method)
            {
                case "Target.targetCreated":
                {
                    // a page we didn't make: a popup or a link that opens a new window. It opens in its TV instead.
                    string id = Json.Str(info, "targetId"), type = Json.Str(info, "type"), opener = Json.Str(info, "openerId");
                    if (id == null || type != "page" || OurTargets.Contains(id) || Tabs.Exists(t => t.TargetId == id)) break;
                    Popups[id] = opener;
                    Popup(id, Json.Str(info, "url"));
                    break;
                }
                case "Target.targetInfoChanged":
                {
                    string id = Json.Str(info, "targetId");
                    if (id != null && Popups.ContainsKey(id)) Popup(id, Json.Str(info, "url"));
                    break;
                }
                case "Target.targetDestroyed":
                    Popups.Remove(Json.Str(m.Params, "targetId") ?? "");
                    break;
            }
        }

        private static void Popup(string id, string url)
        {
            // one of ours after all (its creation was announced before the browser answered with its id)
            if (OurTargets.Contains(id) || Tabs.Exists(t => t.TargetId == id)) { Popups.Remove(id); return; }
            if (string.IsNullOrEmpty(url) || url == "about:blank") return;   // its address comes a moment later
            Popups.TryGetValue(id, out var opener);
            Popups.Remove(id);
            Connection?.Send("Target.closeTarget", Json.O("targetId", id));
            var tab = Tabs.Find(t => t.TargetId == opener);
            string clean = Address.Clean(url);
            if (tab != null && clean != null) tab.Screen.PopupOpened(tab, clean);
        }

        public static string Describe() =>
            $"{Now}{(Status.Length > 0 ? " (" + Status + ")" : "")}, {(Host ? "cef" : "headless")}, tabs {Tabs.Count}, starts {Starts}, wine {UnderWine}, installed {Installed}, " +
            $"process {ProcessMB} MB, sent {Connection?.Sent ?? 0}, received {Connection?.Received ?? 0}, pictures {(Connection?.PictureBytes ?? 0) / 1024} KB";
    }
}
