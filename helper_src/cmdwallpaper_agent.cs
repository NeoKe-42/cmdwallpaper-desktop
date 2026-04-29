using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Threading;
using Windows.Media.Control;
using NAudio.CoreAudioApi;

class Program
{
    // ── Safe value extraction ────────────────────────────
    static string S(object o) { return o != null ? o.ToString().Trim() : ""; }
    static int I(object o) { int v; return o != null && int.TryParse(o.ToString(), out v) ? v : 0; }
    static double D(object o) { double v; return o != null && double.TryParse(o.ToString(), out v) ? v : 0; }

    // ── JSON safe string escape ─────────────────────────
    static string J(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        var r = new char[s.Length * 2 + 2];
        int p = 0; r[p++] = '"';
        foreach (char c in s)
        {
            if (c == '"') { r[p++] = '\\'; r[p++] = '"'; }
            else if (c == '\\') { r[p++] = '\\'; r[p++] = '\\'; }
            else if (c == '\n') { r[p++] = '\\'; r[p++] = 'n'; }
            else if (c == '\r') { r[p++] = '\\'; r[p++] = 'r'; }
            else if (c == '\t') { r[p++] = '\\'; r[p++] = 't'; }
            else if (c < 0x20)
            {
                r[p++] = '\\'; r[p++] = 'u';
                r[p++] = "0123456789ABCDEF"[(c >> 12) & 0xf];
                r[p++] = "0123456789ABCDEF"[(c >> 8) & 0xf];
                r[p++] = "0123456789ABCDEF"[(c >> 4) & 0xf];
                r[p++] = "0123456789ABCDEF"[c & 0xf];
            }
            else r[p++] = c;
        }
        r[p++] = '"';
        return new string(r, 0, p);
    }

    static string F2(double v) { return v.ToString("F2"); }
    static string F1(double v) { return v.ToString("F1"); }

    // ── Atomic write ────────────────────────────────────
    static void AtomicWrite(string path, string content)
    {
        string tmp = path + ".tmp";
        try { File.WriteAllText(tmp, content); File.Move(tmp, path, true); }
        catch (IOException)
        {
            // Retry once after short delay
            try { Thread.Sleep(50); File.Move(tmp, path, true); }
            catch { }
        }
        catch { }
    }

    // ── WMI helpers ──────────────────────────────────────
    static string WmiStr(string query, string prop)
    {
        try
        {
            using (var s = new ManagementObjectSearcher(query))
            {
                foreach (var o in s.Get())
                {
                    string v = S(o[prop]);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
        }
        catch { }
        return "";
    }

    static void WmiCpu(string query, out string name, out int cores, out int threads, out int mhz)
    {
        name = ""; cores = 0; threads = 0; mhz = 0;
        try
        {
            using (var s = new ManagementObjectSearcher(query))
            {
                foreach (var o in s.Get())
                {
                    name = S(o["Name"]);
                    cores = I(o["NumberOfCores"]);
                    threads = I(o["NumberOfLogicalProcessors"]);
                    mhz = I(o["MaxClockSpeed"]);
                    return;
                }
            }
        }
        catch { }
    }

    // ── PID lock ─────────────────────────────────────────
    static bool TryLock(string dir)
    {
        string pidFile = Path.Combine(dir, "cmdwallpaper.pid");
        try
        {
            int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            if (File.Exists(pidFile))
            {
                string oldPidStr = File.ReadAllText(pidFile).Trim();
                int oldPid;
                if (int.TryParse(oldPidStr, out oldPid) && oldPid != myPid)
                {
                    try
                    {
                        var proc = System.Diagnostics.Process.GetProcessById(oldPid);
                        if (proc != null && !proc.HasExited)
                        {
                            try
                            {
                                using (var s = new ManagementObjectSearcher(
                                    "SELECT ExecutablePath FROM Win32_Process WHERE ProcessId=" + oldPid))
                                {
                                    foreach (var o in s.Get())
                                    {
                                        string exePath = S(o["ExecutablePath"]);
                                        if (!string.IsNullOrEmpty(exePath))
                                        {
                                            string exeDir = Path.GetDirectoryName(exePath);
                                            if (string.Equals(exeDir, dir, StringComparison.OrdinalIgnoreCase))
                                                return false;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            File.WriteAllText(pidFile, myPid.ToString());
            return true;
        }
        catch { return false; }
    }

    // ── Album art extraction (atomic write) ───────────────
    static bool ExtractArt(string dir, GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null) { Console.WriteLine("[art] thumbnail is null"); return false; }
        try
        {
            var aop = props.Thumbnail.OpenReadAsync();
            while (aop.Status == Windows.Foundation.AsyncStatus.Started) Thread.Sleep(30);
            if (aop.Status != Windows.Foundation.AsyncStatus.Completed) { Console.WriteLine("[art] OpenReadAsync not completed"); return false; }

            var stm = aop.GetResults();
            if (stm == null) { Console.WriteLine("[art] stream is null"); return false; }

            // Use .NET Stream adapter instead of WinRT buffer (safer in .NET 9)
            var netStream = stm.AsStreamForRead();
            if (netStream == null) { Console.WriteLine("[art] AsStreamForRead returned null"); return false; }

            using (var ms = new System.IO.MemoryStream())
            {
                netStream.CopyTo(ms);
                var bytes = ms.ToArray();

                if (bytes.Length > 128)
                {
                    string dataDir = Path.Combine(dir, "data");
                    if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                    string tmp = Path.Combine(dataDir, "album_art.tmp");
                    string final = Path.Combine(dataDir, "album_art.jpg");
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, final, true);
                    Console.WriteLine("[art] extracted ok, " + bytes.Length + " bytes");
                    return true;
                }
                Console.WriteLine("[art] too small: " + bytes.Length + " bytes");
            }
        }
        catch (Exception ex) { Console.WriteLine("[art] error: " + ex.Message); }
        return false;
    }

    // ── Main ─────────────────────────────────────────────
    static void Main(string[] args)
    {
        // Resolve project root from args or exe location
        string rawArg = args.Length > 0 ? args[0] : null;
        string dir;
        if (!string.IsNullOrEmpty(rawArg) && rawArg == ".")
            dir = Directory.GetCurrentDirectory();
        else if (!string.IsNullOrEmpty(rawArg) && Path.IsPathRooted(rawArg))
            dir = rawArg;
        else if (!string.IsNullOrEmpty(rawArg))
            dir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), rawArg));
        else
            // No args: assume exe is in <ProjectRoot>/publish/, go one level up
            dir = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location),
                ".."));

        dir = Path.GetFullPath(dir);
        string dataDir = Path.Combine(dir, "data");
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

        // Startup log
        string logFile = Path.Combine(dataDir, "cmdwallpaper_agent.log");
        try
        {
            File.AppendAllText(logFile,
                DateTime.UtcNow.ToString("o") + " | START" +
                " | exe=" + System.Reflection.Assembly.GetEntryAssembly().Location +
                " | cwd=" + Directory.GetCurrentDirectory() +
                " | arg=" + (rawArg ?? "(none)") +
                " | projectRoot=" + dir +
                " | dataDir=" + dataDir +
                Environment.NewLine);
        }
        catch { }

        if (!TryLock(dir))
        {
            try { File.AppendAllText(logFile, DateTime.UtcNow.ToString("o") + " | EXIT: already locked" + Environment.NewLine); }
            catch { }
            return;
        }

        string dataFile = Path.Combine(dataDir, "system_info.json");
        string smtcFile = Path.Combine(dataDir, "smtc_data.json");

        // ── Static info ──────────────────────────────────
        string hostname = ""; try { hostname = Environment.MachineName; } catch { }
        string username = ""; try { username = Environment.UserName; } catch { }

        string osName = "", osVer = "", osArch = "";
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Caption,Version,OSArchitecture FROM Win32_OperatingSystem"))
            {
                foreach (var o in s.Get())
                {
                    osName = S(o["Caption"]); osVer = S(o["Version"]); osArch = S(o["OSArchitecture"]);
                }
            }
        }
        catch { }

        string cpuName = ""; int cpuCores = 0, cpuThreads = 0, cpuMHz = 0;
        WmiCpu("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor",
            out cpuName, out cpuCores, out cpuThreads, out cpuMHz);

        string gpuName = "";
        try
        {
            // First try: exclude OrayIdd virtual display
            using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
            {
                foreach (var o in s.Get())
                {
                    string n = S(o["Name"]);
                    if (!string.IsNullOrEmpty(n) && n.IndexOf("OrayIdd") < 0)
                    { gpuName = n; break; }
                }
            }
            // Fallback: take first available
            if (string.IsNullOrEmpty(gpuName))
            {
                gpuName = WmiStr("SELECT Name FROM Win32_VideoController", "Name");
            }
        }
        catch { }

        string mbName = WmiStr("SELECT Manufacturer,Product FROM Win32_BaseBoard", "Manufacturer");
        string mbProd = WmiStr("SELECT Manufacturer,Product FROM Win32_BaseBoard", "Product");
        if (!string.IsNullOrEmpty(mbProd)) mbName = mbName + " " + mbProd;

        // ── SMTC init ────────────────────────────────────
        GlobalSystemMediaTransportControlsSessionManager smtcMgr = null;
        var smtcReady = new ManualResetEvent(false);
        var t = new Thread(delegate()
        {
            try
            {
                var op = GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                while (op.Status == Windows.Foundation.AsyncStatus.Started) Thread.Sleep(50);
                if (op.Status == Windows.Foundation.AsyncStatus.Completed) smtcMgr = op.GetResults();
            }
            catch { }
            smtcReady.Set();
        });
        t.SetApartmentState(ApartmentState.STA); t.Start();
        if (!smtcReady.WaitOne(8000)) smtcMgr = null;
        smtcReady.Dispose();

        try
        {
            File.AppendAllText(logFile,
                DateTime.UtcNow.ToString("o") + " | SMTC init " +
                (smtcMgr != null ? "OK" : "FAILED") +
                " | entering main loop" +
                Environment.NewLine);
        }
        catch { }

        // ── Audio device init ─────────────────────────────
        MMDeviceEnumerator audioEnum = null;
        MMDevice audioDevice = null;
        string eqFile = Path.Combine(dataDir, "eq_data.json");
        try
        {
            audioEnum = new MMDeviceEnumerator();
            audioDevice = audioEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Console.WriteLine("[audio] device: " + (audioDevice != null ? audioDevice.FriendlyName : "null"));
            try { File.AppendAllText(logFile, DateTime.UtcNow.ToString("o") + " | Audio init OK | device=" + (audioDevice != null ? audioDevice.FriendlyName : "null") + Environment.NewLine); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[audio] init failed: " + ex.Message);
            try { File.AppendAllText(logFile, DateTime.UtcNow.ToString("o") + " | Audio init FAILED: " + ex.Message + Environment.NewLine); } catch { }
            // Write error state so frontend knows
            AtomicWrite(eqFile, "{\"source\":\"DESKTOP-MASTER-PEAK\",\"volume\":0,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}");
        }

        // ── Main loop ────────────────────────────────────
        string lastDataJson = "", lastSmtcJson = "", lastArtTitle = "";
        int artVersion = 0;
        int tick = 0;

        // Persistent values — only updated on tick % 10 == 0
        string memJson = "\"total_gb\":0,\"used_gb\":0,\"percent\":0";
        string diskJson = "{}";
        string netJson = "";

        // Extract album art for currently playing song on startup
        bool didInitialArt = false;

        while (true)
        {
            // ─── SMTC (every 1s) ─────────────────────────
            string smtcJson = "{\"status\":\"no_media\"}";
            if (smtcMgr != null)
            {
                try
                {
                    var session = smtcMgr.GetCurrentSession();
                    if (session != null)
                    {
                        var mpOp = session.TryGetMediaPropertiesAsync();
                        while (mpOp.Status == Windows.Foundation.AsyncStatus.Started) Thread.Sleep(50);
                        var props = mpOp.Status == Windows.Foundation.AsyncStatus.Completed ? mpOp.GetResults() : null;

                        if (props != null && !string.IsNullOrEmpty(props.Title))
                        {
                            var tl = session.GetTimelineProperties();
                            double pos = -1, dur = -1;
                            if (tl != null) { pos = tl.Position.TotalSeconds; dur = tl.EndTime.TotalSeconds; }

                            // Album art: extract on song change + initial startup
                            bool hasArt = false;
                            string artPath = Path.Combine(dataDir, "album_art.jpg");
                            if (props.Title != lastArtTitle || !didInitialArt)
                            {
                                lastArtTitle = props.Title;
                                didInitialArt = true;
                                if (props.Thumbnail != null)
                                {
                                    hasArt = ExtractArt(dir, props);
                                    if (hasArt) artVersion++;
                                }
                            }
                            else
                            {
                                hasArt = File.Exists(artPath);
                            }

                            smtcJson = "{\"status\":\"playing\"";
                            smtcJson += ",\"title\":" + J(props.Title);
                            if (!string.IsNullOrEmpty(props.Artist)) smtcJson += ",\"artist\":" + J(props.Artist);
                            if (!string.IsNullOrEmpty(props.AlbumTitle)) smtcJson += ",\"album\":" + J(props.AlbumTitle);
                            if (pos >= 0) smtcJson += ",\"pos\":" + pos.ToString("F1");
                            if (dur > 0) smtcJson += ",\"dur\":" + dur.ToString("F1");
                            smtcJson += ",\"has_art\":" + (hasArt ? "true" : "false");
                            smtcJson += ",\"art_version\":" + artVersion;
                            smtcJson += "}";
                        }
                    }
                }
                catch { smtcJson = "{\"status\":\"error\"}"; }
            }
            if (smtcJson != lastSmtcJson)
            {
                lastSmtcJson = smtcJson;
                AtomicWrite(smtcFile, smtcJson);
            }

            // ─── Audio peak → eq_data.json ──────────────
            string eqJson = "{\"source\":\"DESKTOP-MASTER-PEAK\",\"volume\":0}";
            if (audioDevice != null)
            {
                try
                {
                    float peak = audioDevice.AudioMeterInformation.MasterPeakValue;
                    string updatedAt = DateTime.UtcNow.ToString("o");
                    eqJson = "{\"source\":\"DESKTOP-MASTER-PEAK\",\"volume\":" + peak.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ",\"peak\":" + peak.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ",\"updatedAt\":\"" + updatedAt + "\"}";
                }
                catch (Exception ex)
                {
                    eqJson = "{\"source\":\"DESKTOP-MASTER-PEAK\",\"volume\":0,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}";
                }
            }
            AtomicWrite(eqFile, eqJson);

            // ─── System info (every 10s) ──────────────────

            if (tick % 10 == 0)
            {
                // Memory
                try
                {
                    using (var s = new ManagementObjectSearcher(
                        "SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem"))
                    {
                        foreach (var o in s.Get())
                        {
                            double tot = D(o["TotalVisibleMemorySize"]);
                            double fre = D(o["FreePhysicalMemory"]);
                            double tg = Math.Round(tot / 1048576.0, 2);
                            double fg = Math.Round(fre / 1048576.0, 2);
                            double ug = tg - fg;
                            double pct = tg > 0 ? Math.Round(ug / tg * 100, 1) : 0;
                            memJson = "\"total_gb\":" + F2(tg) + ",\"used_gb\":" + F2(ug)
                                + ",\"available_gb\":" + F2(fg) + ",\"percent\":" + F1(pct);
                        }
                    }
                }
                catch { }

                // Disk
                var disks = new List<string>();
                try
                {
                    using (var s = new ManagementObjectSearcher(
                        "SELECT DeviceID,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
                    {
                        foreach (var o in s.Get())
                        {
                            string drv = S(o["DeviceID"]).TrimEnd(':');
                            double sz = D(o["Size"]);
                            double fr = D(o["FreeSpace"]);
                            double tg = Math.Round(sz / 1073741824.0, 2);
                            double fg = Math.Round(fr / 1073741824.0, 2);
                            double ug = tg - fg;
                            double pct = tg > 0 ? Math.Round(ug / tg * 100, 1) : 0;
                            disks.Add("\"" + drv + ":\":{\"mount\":\"" + drv + ":\\\\\",\"total_gb\":"
                                + F2(tg) + ",\"used_gb\":" + F2(ug) + ",\"free_gb\":" + F2(fg)
                                + ",\"percent\":" + F1(pct) + "}");
                        }
                    }
                }
                catch { }
                diskJson = "{" + string.Join(",", disks.ToArray()) + "}";

                // Network
                try
                {
                    using (var s = new ManagementObjectSearcher(
                        "SELECT IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=True"))
                    {
                        foreach (var o in s.Get())
                        {
                            string[] ips = o["IPAddress"] as string[];
                            if (ips != null)
                            {
                                foreach (string ip in ips)
                                {
                                    if (!string.IsNullOrEmpty(ip) && ip.Contains(".") && !ip.StartsWith("127."))
                                    { netJson = "\"ipv4\":" + J(ip); break; }
                                }
                            }
                            if (!string.IsNullOrEmpty(netJson)) break;
                        }
                    }
                }
                catch { }
            }

            // ─── Build data.json ──────────────────────────
            string dj = "{";
            dj += "\"timestamp\":" + J(DateTime.UtcNow.ToString("o")) + ",";
            dj += "\"system\":{\"hostname\":" + J(hostname) + ",\"username\":" + J(username) + "},";
            dj += "\"os\":{\"name\":" + J(osName) + ",\"version\":" + J(osVer) + ",\"architecture\":" + J(osArch) + "},";
            dj += "\"cpu\":{\"name\":" + J(cpuName) + ",\"cores\":" + cpuCores + ",\"threads\":" + cpuThreads + ",\"frequency_mhz\":" + cpuMHz + "},";
            dj += "\"gpu\":" + J(gpuName) + ",";
            dj += "\"memory\":{" + memJson + "},";
            dj += "\"disk\":" + diskJson + ",";
            if (!string.IsNullOrEmpty(netJson))
                dj += "\"network\":{" + netJson + "},";
            dj += "\"motherboard\":" + J(mbName);
            dj += "}";

            if (dj != lastDataJson)
            {
                lastDataJson = dj;
                AtomicWrite(dataFile, dj);
            }

            tick++;
            Thread.Sleep(1000);
        }
    }
}
