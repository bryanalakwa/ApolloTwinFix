// Apollo Twin Fix: single-exe patcher. WinForms window hosting the React UI
// (embedded index.html) in WebView2. Runs elevated (see app.manifest).
//
// The fix it applies (found by hand on a broken UAD v12 install):
//   1. Install the cached apollo-*bit.msi packages the UAD installer skipped.
//      They contain UAMixerEngine.exe, the Apollo mixer engine (TCP 4710).
//   2. Register the Twin USB ASIO driver (uausbaudioasio*.dll).
//   3. Restart the UA apps; the UA tray icon launches the engine.
//
// Built with the .NET Framework 4.8 csc (C# 5), so no modern syntax here.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);

    [STAThread]
    static void Main()
    {
        // WebView2's managed DLLs live inside this exe as resources.
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (st == null) return null;
                var buf = new byte[st.Length];
                st.Read(buf, 0, buf.Length);
                return Assembly.Load(buf);
            }
        };
        // Native loader must be a real file; load it once so P/Invoke finds it by name.
        var loader = Path.Combine(Host.DataDir, "WebView2Loader.dll");
        Directory.CreateDirectory(Host.DataDir);
        using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebView2Loader.dll"))
        using (var f = File.Create(loader)) st.CopyTo(f);
        LoadLibrary(loader);

        Application.EnableVisualStyles();
        Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run() { Application.Run(new Host()); }
}

class Host : Form
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApolloTwinFix");

    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    readonly Microsoft.Web.WebView2.WinForms.WebView2 web = new Microsoft.Web.WebView2.WinForms.WebView2();
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly bool dark;
    int busy;
    Microsoft.Web.WebView2.Core.CoreWebView2Environment env;
    bool cleaned;

    static string CacheDir { get { return Path.Combine(DataDir, "WebView2"); } }

    public Host()
    {
        var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        dark = k == null || Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 0;

        Text = "Apollo Twin Fix";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = dark ? System.Drawing.Color.FromArgb(14, 14, 16) : System.Drawing.Color.FromArgb(250, 250, 250);
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);
        Load += OnLoad;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref on, 4); // dark title bar
        float scale = DeviceDpi / 96f;
        ClientSize = new System.Drawing.Size((int)(460 * scale), (int)(620 * scale));
    }

    async void OnLoad(object sender, EventArgs e)
    {
        try
        {
            env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, CacheDir);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show("This app needs the Microsoft Edge WebView2 Runtime (built into Windows 11).\n\n" + ex.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        var s = web.CoreWebView2.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsStatusBarEnabled = false;
        web.CoreWebView2.WebMessageReceived += (o, a) => OnMessage(a.WebMessageAsJson);
        using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html"))
            web.CoreWebView2.NavigateToString(new StreamReader(st).ReadToEnd());
    }

    void Send(object msg)
    {
        var s = json.Serialize(msg);
        BeginInvoke((Action)(() => { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(s); }));
    }
    void Log(string line)
    {
        line = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
        try { File.AppendAllText(Path.Combine(DataDir, "fix.log"), line + Environment.NewLine); } catch (Exception) { }
        Send(new { type = "log", line = line });
    }
    void Step(string id, string state, string detail) { Send(new { type = "step", id = id, state = state, detail = detail }); }

    void OnMessage(string raw)
    {
        var m = json.Deserialize<Dictionary<string, object>>(raw);
        var type = m["type"] as string;
        if (type == "openConsole") { OpenConsole(); return; }
        if (type == "size") { FitHeight(Convert.ToInt32(m["height"])); return; }
        if (type != "scan" && type != "fix") return;
        if (Interlocked.Exchange(ref busy, 1) == 1) return; // one job at a time
        Task.Run(() =>
        {
            try
            {
                if (type == "scan") Send(new { type = "checks", checks = Fixer.Scan() });
                else Send(new { type = "result", ok = new Fixer(Log, Step).Run(), message = Fixer.LastMessage });
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                Send(new { type = "result", ok = false, message = ex.Message });
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
    }

    // UI reports its content height (CSS px); grow/shrink the window to match, staying on screen.
    void FitHeight(int cssHeight)
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int chrome = Height - ClientSize.Height;
        int h = Math.Min((int)(cssHeight * DeviceDpi / 96f), wa.Height - chrome);
        if (h == ClientSize.Height) return;
        ClientSize = new System.Drawing.Size(ClientSize.Width, h);
        if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
    }

    // Delete the WebView2 browser cache on exit so the app leaves nothing big behind.
    // The cache is locked until the browser process exits, so close in two steps.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!cleaned && env != null)
        {
            e.Cancel = true;
            cleaned = true;
            Hide();
            env.BrowserProcessExited += (s, a) =>
            {
                try { Directory.Delete(CacheDir, true); } catch (Exception) { } // ponytail: best effort
                BeginInvoke((Action)Close);
            };
            web.Dispose();
            return;
        }
        base.OnFormClosing(e);
    }

    static void OpenConsole()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Universal Audio\UAD Console\UAD Console.exe");
        if (File.Exists(exe)) Task.Run(() => Fixer.Launch(exe, s => { }));
    }
}

class Fixer
{
    public static string LastMessage = "";
    readonly Action<string> log;
    readonly Action<string, string, string> step;

    public Fixer(Action<string> log, Action<string, string, string> step) { this.log = log; this.step = step; }

    static string Tools
    {
        get
        {
            var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Universal Audio\Powered Plugins");
            return k == null ? null : k.GetValue("ToolsInstallDir") as string;
        }
    }
    static string Version
    {
        get
        {
            var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Universal Audio\Powered Plugins");
            return k == null ? null : k.GetValue("Version") as string;
        }
    }
    static bool AsioRegistered { get { return Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASIO\Universal Audio USB") != null; } }
    static bool EngineRunning { get { return Process.GetProcessesByName("UAMixerEngine").Length > 0; } }
    // True only when the engine itself owns port 4710 (not some other app).
    static bool EngineListening
    {
        get
        {
            var engines = new HashSet<int>(Process.GetProcessesByName("UAMixerEngine").Select(p => p.Id));
            return Port4710Owners().Any(engines.Contains);
        }
    }

    static List<int> Port4710Owners()
    {
        var pids = new List<int>();
        var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using (var ns = Process.Start(psi))
            foreach (var l in ns.StandardOutput.ReadToEnd().Split('\n'))
            {
                var c = l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int pid;
                if (c.Length == 5 && c[1].EndsWith(":4710") && c[3] == "LISTENING" && int.TryParse(c[4], out pid)) pids.Add(pid);
            }
        return pids;
    }

    static string DeviceStatus(out bool ok)
    {
        var names = new Dictionary<string, string> { { "PID_0002", "Apollo Twin USB" }, { "PID_000D", "Apollo Solo USB" }, { "PID_000F", "Apollo Twin X USB" } };
        bool loader = false;
        using (var q = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_2B5A%'"))
            foreach (ManagementObject o in q.Get())
            {
                var id = ((string)o["DeviceID"]).ToUpperInvariant();
                foreach (var n in names) if (id.Contains(n.Key)) { ok = true; return n.Value + " found"; }
                if (id.Contains("PID_0001")) loader = true;
            }
        ok = false;
        return loader ? "Still starting up. Wait a few seconds, then re-check" : "Not found. Turn it on and connect USB";
    }

    public static object[] Scan()
    {
        bool dev;
        var devDetail = DeviceStatus(out dev);
        var tools = Tools;
        bool sw = tools != null && Directory.Exists(tools);
        bool engine = sw && File.Exists(Path.Combine(tools, "UAMixerEngine.exe"));
        bool asio = AsioRegistered;
        bool running = EngineRunning;
        return new object[]
        {
            new { id = "device", label = "Apollo Twin connected", ok = dev, detail = devDetail },
            new { id = "software", label = "UAD software installed", ok = sw, detail = sw ? "Version " + Version : "Not installed. Install UAD software first" },
            new { id = "engine", label = "Apollo mixer engine", ok = engine, detail = engine ? "Installed" : "Missing. The UAD installer skipped it" },
            new { id = "asio", label = "USB audio driver registered", ok = asio, detail = asio ? "Universal Audio USB" : "Not registered" },
            new { id = "running", label = "Mixer engine running", ok = running, detail = running ? "Running" : "Not running" },
        };
    }

    public bool Run()
    {
        var tools = Tools;
        if (tools == null || !Directory.Exists(tools))
        {
            LastMessage = "UAD software isn't installed. Install UAD v12 first, then run this again.";
            step("software", "failed", "Not installed");
            return false;
        }
        log("Fix started. Running as " + Environment.UserName + ", Windows " + Environment.OSVersion.Version);
        log("UAD tools folder: " + tools);
        return InstallEngine(tools) & RegisterAsio(tools) & RestartApps(tools)
            ? Done("UAD Console can see your Apollo Twin again.")
            : Done(null);
    }

    bool Done(string ok)
    {
        if (ok != null) { LastMessage = ok; log("All steps finished."); return true; }
        LastMessage = "One or more steps failed. Open Details for what happened.";
        return false;
    }

    bool InstallEngine(string tools)
    {
        step("engine", "running", "Checking…");
        if (File.Exists(Path.Combine(tools, "UAMixerEngine.exe")))
        {
            log("Mixer engine already installed.");
            step("engine", "done", "Already installed");
            return true;
        }
        step("engine", "running", "Installing skipped Apollo packages…");
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache");
        var msis = FindFiles(cache, "apollo-*bit.msi");
        if (msis.Count == 0)
        {
            log("No apollo-*bit.msi in " + cache);
            step("engine", "failed", "Installer files not found. Rerun the UAD installer with the Twin on");
            return false;
        }
        foreach (var msi in msis.OrderByDescending(p => p)) // 64-bit first, as tested
        {
            log("Installing " + msi);
            int code = Exec("msiexec.exe", "/i \"" + msi + "\" /qn /norestart");
            log("  msiexec exit " + code);
            if (code != 0 && code != 3010)
            {
                step("engine", "failed", "Install failed (msiexec " + code + ")");
                return false;
            }
        }
        bool ok = File.Exists(Path.Combine(tools, "UAMixerEngine.exe"));
        step("engine", ok ? "done" : "failed", ok ? "Installed" : "Installed, but the engine file is still missing");
        return ok;
    }

    bool RegisterAsio(string tools)
    {
        step("asio", "running", "Registering…");
        var dir = Path.Combine(tools, @"Drivers\audio\usb\x64");
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pairs = new[]
        {
            new[] { Path.Combine(win, @"System32\regsvr32.exe"), Path.Combine(dir, "uausbaudioasio_x64.dll") },
            new[] { Path.Combine(win, @"SysWOW64\regsvr32.exe"), Path.Combine(dir, "uausbaudioasio.dll") }, // 32-bit apps
        };
        foreach (var p in pairs)
        {
            if (!File.Exists(p[1]))
            {
                log("Missing " + p[1]);
                step("asio", "failed", "Driver files missing. Reinstall UAD software");
                return false;
            }
            int code = Exec(p[0], "/s \"" + p[1] + "\"");
            log("regsvr32 " + Path.GetFileName(p[1]) + " exit " + code);
            if (code != 0) { step("asio", "failed", "regsvr32 failed (" + code + ")"); return false; }
        }
        bool ok = AsioRegistered;
        step("asio", ok ? "done" : "failed", ok ? "Universal Audio USB" : "Registered, but the ASIO entry is missing");
        return ok;
    }

    bool RestartApps(string tools)
    {
        step("running", "running", "Restarting UA apps…");
        foreach (var n in new[] { "UAD Console", "UAMixerHelper", "UAMixerEngine", "UATrayIcon" })
            foreach (var p in Process.GetProcessesByName(n))
            {
                log("Stopping " + n);
                try { p.Kill(); p.WaitForExit(5000); } catch (Exception ex) { log("  " + ex.Message); }
            }
        // The tray icon runs at login and launches the engine, so start it the same way.
        Launch(Path.Combine(tools, "UATrayIcon.exe"), log);
        step("running", "running", "Waiting for the mixer engine…");
        bool crashed;
        bool ok = WaitForEngine(tools, 10, out crashed);

        // The engine reopens the last mixer session on startup. A session it can't load
        // (seen with sessions left from older versions) crashes it every time, so set it aside.
        if (!ok && crashed && ResetSession())
        {
            step("running", "running", "Starting with a fresh mixer session…");
            ok = WaitForEngine(tools, 0, out crashed);
        }
        if (ok) log("Mixer engine listening on port 4710");
        else
        {
            log(EngineRunning ? "Mixer engine is running but doesn't own port 4710 (another app may be using it)"
                              : "Mixer engine is not running");
            Diagnose(tools);
            log("Full log saved to " + Path.Combine(Host.DataDir, "fix.log"));
        }
        step("running", ok ? "done" : "failed", ok ? "Running" : "Didn't start. Restart Windows, then run this again");
        return ok;
    }

    // Wait up to 30 s for the engine to own port 4710. If it isn't running after
    // directAfter seconds, start it ourselves. crashed = it started and then quit.
    bool WaitForEngine(string tools, int directAfter, out bool crashed)
    {
        crashed = false;
        Process engine = null;
        for (int i = 0; i < 30; i++)
        {
            if (EngineRunning && EngineListening) return true;
            if (i == directAfter && !EngineRunning)
            {
                if (directAfter > 0) log("Tray didn't start the engine; starting it directly");
                engine = Launch(Path.Combine(tools, "UAMixerEngine.exe"), log);
            }
            if (engine != null && Exited(engine))
            {
                log("Mixer engine quit right after starting (" + ExitCodeText(engine) + ")");
                crashed = true;
                return false;
            }
            Thread.Sleep(1000);
        }
        return EngineRunning && EngineListening;
    }

    // Rename the engine's saved session to a dated backup so it starts fresh. Nothing is deleted.
    bool ResetSession()
    {
        var session = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Universal Audio\LastUsedSession.uadmix");
        if (!File.Exists(session)) { log("No saved mixer session to reset (" + session + ")"); return false; }
        var backup = session + ".crashed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
        try
        {
            File.Move(session, backup);
            log("The saved mixer session crashes the engine. Moved it aside: " + backup);
            return true;
        }
        catch (Exception ex) { log("Couldn't move the saved session: " + ex.Message); return false; }
    }

    // Start un-elevated (normal user) via Explorer, like the UA installer does. Explorer can
    // silently do nothing (e.g. this app was elevated with a different admin account), so
    // check the process appeared and otherwise start it directly from its own folder.
    public static Process Launch(string exe, Action<string> log)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false }); }
        catch (Exception ex) { log("Explorer launch failed: " + ex.Message); }
        for (int i = 0; i < 8; i++)
        {
            var found = Process.GetProcessesByName(name);
            if (found.Length > 0)
            {
                // Open the handle now; if the process quits later we can still read its exit code.
                try { var h = found[0].Handle; } catch (Exception) { }
                log("Started " + name);
                return found[0];
            }
            Thread.Sleep(500);
        }
        log(name + " didn't start through Explorer; starting it directly");
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
            log("Started " + name + " directly (pid " + p.Id + ")");
            return p;
        }
        catch (Exception ex) { log("Couldn't start " + name + ": " + ex.Message); return null; }
    }

    static bool Exited(Process p) { try { return p.HasExited; } catch (Exception) { return true; } }
    static string ExitCodeText(Process p)
    {
        try { return "exit code " + p.ExitCode + " / 0x" + p.ExitCode.ToString("X8"); }
        catch (Exception) { return "exit code unavailable"; }
    }

    // Everything needed to tell why the engine won't run, written to the log.
    void Diagnose(string tools)
    {
        log("--- Diagnostics ---");
        bool dev; log("Apollo: " + DeviceStatus(out dev));

        // Other UA processes still running (e.g. an old v11 "UA Mixer Engine").
        foreach (var p in Process.GetProcesses())
        {
            string path = null;
            try { path = p.MainModule.FileName; } catch (Exception) { }
            if (p.ProcessName.IndexOf("Mixer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (path != null && path.IndexOf("Universal Audio", StringComparison.OrdinalIgnoreCase) >= 0))
                log("Running: " + p.ProcessName + " (pid " + p.Id + ") " + path);
        }

        // Who holds port 4710.
        try
        {
            var owners = Port4710Owners();
            if (owners.Count == 0) log("Port 4710: free");
            foreach (var pid in owners)
            {
                string who = "?";
                try { who = Process.GetProcessById(pid).ProcessName; } catch (Exception) { }
                log("Port 4710 is held by " + who + " (pid " + pid + ")");
            }
        }
        catch (Exception ex) { log("netstat failed: " + ex.Message); }

        // The engine is a 32-bit app that needs the Microsoft Visual C++ runtime (x86).
        var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
        foreach (var dll in new[] { "vcruntime140.dll", "msvcp140.dll" })
            log("C++ runtime " + dll + ": " + (File.Exists(Path.Combine(wow, dll)) ? "present" : "MISSING"));
        log("Engine file: " + new FileInfo(Path.Combine(tools, "UAMixerEngine.exe")).Length / 1024 + " KB, version " +
            FileVersionInfo.GetVersionInfo(Path.Combine(tools, "UAMixerEngine.exe")).FileVersion);

        // Windows crash reports for the engine from the last 15 minutes.
        try
        {
            var since = DateTime.Now.AddMinutes(-15);
            var entries = new EventLog("Application").Entries;
            int shown = 0;
            for (int i = entries.Count - 1; i >= 0 && shown < 3; i--)
            {
                var e = entries[i];
                if (e.TimeGenerated < since) break;
                if ((e.Source == "Application Error" || e.Source == ".NET Runtime" || e.Source == "SideBySide") &&
                    e.Message.IndexOf("UAMixerEngine", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    log("Windows event (" + e.Source + ", " + e.TimeGenerated.ToString("HH:mm:ss") + "): " +
                        string.Join(" | ", e.Message.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Take(8)));
                    shown++;
                }
            }
            if (shown == 0) log("No Windows crash report for the engine in the last 15 minutes");
        }
        catch (Exception ex) { log("Couldn't read the event log: " + ex.Message); }

        DumpEngineLog();
    }

    // Last lines of the engine's own log (newest across all user profiles) to show why it failed.
    void DumpEngineLog()
    {
        var logs = new List<FileInfo>();
        try
        {
            foreach (var u in Directory.GetDirectories(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Users")))
            {
                var d = Path.Combine(u, @"AppData\Local\Universal Audio\Logs");
                if (Directory.Exists(d)) logs.AddRange(new DirectoryInfo(d).GetFiles("UAMixerEngine*"));
            }
        }
        catch (Exception) { }
        var newest = logs.OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        if (newest == null) { log("No mixer engine log found (the engine never ran)"); return; }
        log("Mixer engine log " + newest.FullName + " (" + newest.LastWriteTime + "):");
        try
        {
            using (var s = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                foreach (var l in new StreamReader(s).ReadToEnd().Split('\n').Where(l => l.Trim().Length > 0).Reverse().Take(40).Reverse())
                    log("  " + l.TrimEnd());
        }
        catch (Exception ex) { log("  (couldn't read: " + ex.Message + ")"); }
    }

    static int Exec(string file, string args)
    {
        using (var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true }))
        {
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    static List<string> FindFiles(string root, string pattern)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var d in Directory.GetDirectories(root)) // Package Cache is one level deep
            try { found.AddRange(Directory.GetFiles(d, pattern)); } catch (UnauthorizedAccessException) { }
        return found;
    }
}
