using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FlugelKranz.Diagnostics;

/// <summary>Where the logs this PC's VR stack writes live (the folders collect-vr-logs.ps1 reads).</summary>
public sealed record DiagnosticSources(
    string SteamPath,
    string SlimeVrLogs,
    string VirtualDesktopData,
    string OpenVrPaths,
    string FlugelKranzConfig)
{
    public string SteamLogs => Path.Combine(SteamPath, "logs");

    public static DiagnosticSources ForThisPc(string flugelKranzConfig)
    {
        string? steam = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            }
            catch (Exception) { }
        }
        if (string.IsNullOrWhiteSpace(steam)) steam = @"C:\Program Files (x86)\Steam";
        return new(
            steam,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dev.slimevr.SlimeVR", "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Virtual Desktop"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath"),
            flugelKranzConfig);
    }
}

/// <summary>One file that goes into the archive: its name there and where it is read from.</summary>
public sealed record DiagnosticFile(string ArchivePath, string SourcePath, long Length, DateTime LastWriteTime);

/// <summary>
/// Replaces the PC name, the Windows user name and user-profile paths in text, so a log
/// archive can be attached to a public issue. Other content (device serials, network
/// addresses, Steam account names) is left as it is.
/// </summary>
public sealed class DiagnosticRedactor
{
    public const string ProfileReplacement = "%USERPROFILE%";
    public const string UserReplacement = "<user>";
    public const string MachineReplacement = "<pc>";
    // Whole-token matches only; shorter names are still removed inside profile paths.
    private const int MinimumTokenLength = 3;
    private readonly List<(Regex Pattern, string Replacement)> rules = [];

    public DiagnosticRedactor(string? machineName, string? userName, IEnumerable<string?> profilePaths)
    {
        var users = new List<string>();
        if (!string.IsNullOrWhiteSpace(userName)) users.Add(userName);
        foreach (string? profile in profilePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(p => p!.Length))
        {
            string[] segments = profile!.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;
            // C:\Users\name, C:/Users/name and JSON-escaped C:\\Users\\name all match.
            string pattern = string.Join(@"[\\/]+", segments.Select(Regex.Escape)) + @"(?![\w.-])";
            rules.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), ProfileReplacement));
            users.Add(segments[^1]);
        }
        foreach (string user in users.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(u => u.Length))
            AddToken(user, UserReplacement);
        if (!string.IsNullOrWhiteSpace(machineName)) AddToken(machineName, MachineReplacement);
    }

    public static DiagnosticRedactor ForThisPc() => new(
        Environment.MachineName,
        Environment.UserName,
        [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("USERPROFILE")]);

    private void AddToken(string value, string replacement)
    {
        if (value.Length < MinimumTokenLength) return;
        rules.Add((new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(value) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), replacement));
    }

    public string Apply(string text)
    {
        foreach (var (pattern, replacement) in rules)
            text = pattern.Replace(text, replacement);
        return text;
    }
}

/// <summary>
/// The in-app port of tools/collect-vr-logs.ps1: picks the same SteamVR, SlimeVR, Virtual
/// Desktop and FlugelKranz files, writes the same summary.txt, and packs them, redacted,
/// into one zip. Source files are only opened for reading; nothing is sent anywhere.
/// </summary>
public static class DiagnosticLogCollector
{
    public static IReadOnlyList<DiagnosticFile> Plan(DiagnosticSources sources)
    {
        var files = new List<DiagnosticFile>();
        void Add(string folder, IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                string archivePath = folder + "/" + info.Name;
                if (files.Any(f => string.Equals(f.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase))) continue;
                files.Add(new(archivePath, info.FullName, info.Length, info.LastWriteTime));
            }
        }
        string steamLogs = sources.SteamLogs;
        Add("steamvr", new[] { "vrserver.txt", "vrserver.previous.txt", "vrcompositor.txt", "vrcompositor.previous.txt",
            "vrmonitor.txt", "driver_vrlink.txt", "vrclient_vrcompositor.txt" }.Select(n => Path.Combine(steamLogs, n)));
        Add("steamvr", new[] { "vrclient_FlugelKranz*.txt", "vrclient_VRChat*.txt", "vrclient_SlimeVR-*.txt", "vrclient_AdvancedSettings.txt" }
            .SelectMany(f => Latest(steamLogs, f, 2)));
        Add("steamvr-config", [Path.Combine(sources.SteamPath, "config", "steamvr.vrsettings"), sources.OpenVrPaths]);
        Add("slimevr", new[] { Path.Combine(sources.SlimeVrLogs, "slimevr-openvr-driver.log"),
                Path.Combine(sources.SlimeVrLogs, "slimevr-bindings-provider.log") }
            .Concat(Latest(sources.SlimeVrLogs, "slimevr-server_*.log", 2))
            .Concat(Latest(sources.SlimeVrLogs, "slimevr-gui_*.log", 1)));
        Add("virtualdesktop", new[] { "StreamerLog.txt", "OpenXR.log", "ServiceLog.txt", "SetupLog.txt" }
            .Select(n => Path.Combine(sources.VirtualDesktopData, n)));
        Add("flugelkranz", [sources.FlugelKranzConfig]);
        return files;
    }

    /// <summary>Reads each planned file, redacts it, and writes the archive with summary.txt at its root.</summary>
    public static void WriteArchive(string zipPath, IReadOnlyList<DiagnosticFile> files, DiagnosticRedactor redactor, DateTime collectedAt)
    {
        var texts = new List<(DiagnosticFile File, string Text)>();
        foreach (var file in files)
        {
            string? text = ReadShared(file.SourcePath);
            if (text is not null) texts.Add((file, redactor.Apply(text)));
        }
        string summary = redactor.Apply(BuildSummary(texts.Select(t => (t.File.ArchivePath, t.Text)).ToList(), collectedAt));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (directory is not null) Directory.CreateDirectory(directory);
        string temporary = zipPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "summary.txt", summary);
            foreach (var (file, text) in texts)
                Write(zip, redactor.Apply(file.ArchivePath), text);
        }
        File.Move(temporary, zipPath, true);
    }

    public static string DefaultArchivePath(DateTime collectedAt) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FlugelKranz Logs",
        $"FlugelKranz-logs-{collectedAt:yyyyMMdd-HHmmss}.zip");

    private static void Write(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static IEnumerable<string> Latest(string directory, string filter, int count)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return new DirectoryInfo(directory).EnumerateFiles(filter).OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(count).Select(f => f.FullName).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Read-only, and shares the file with a writer that still has it open (SlimeVR, VD).</summary>
    private static string? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception) { return null; }
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex SessionLine = new("Active HMD set to|Loaded server driver|Not loading driver", Options);
    private static readonly Regex DatePrefix = new(@"^\w{3} \w{3} \d{2} \d{4} ", Options);
    private static readonly Regex DriverLine = new("flugelkranz: (FlugelKranz|FrameAudit: [^t])", Options);
    private static readonly Regex DriverPrefix = new("^.*flugelkranz: ", Options);
    private static readonly Regex FrameAudit = new(
        @"FrameAudit t=\d+ sampled=(\d) headAge=(-?\d+) .*?corrected=(\d) .*?timed=(\d) identityBypass=(\d) .*?layers=(\d+) missed=(\d+) held=(\d+) .*?commandQxyzw=\[([-\d\. ]+)\] commandP=\[([-\d\. ]+)\]",
        Options);
    private static readonly Regex DriverAudit = new(
        @"DriverAudit t=\d+ .*?command: changes=(\d+) intervalMs\[[^\]]*\]=\[([\d ]+)\].*?skew: n=(\d+) skewed=(\d+) maxM=([\d.]+) meanM=([\d.]+) commandStepM: n=(\d+) max=([\d.]+) mean=([\d.]+)",
        Options);

    private sealed record AuditRow(int Sampled, int HeadAge, int Layers, int Missed, int Held, int Bypass, bool Flying);

    /// <summary>summary.txt: the numbers compared between flight sessions (same as collect-vr-logs.ps1, plus DriverAudit).</summary>
    public static string BuildSummary(IReadOnlyList<(string ArchivePath, string Text)> files, DateTime collectedAt)
    {
        var s = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        void Line(string text = "") => s.Append(text).Append('\n');
        string? Find(string archivePath) => files.FirstOrDefault(f => string.Equals(f.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase)).Text;

        Line($"Collected {collectedAt:yyyyMMdd-HHmmss} by FlugelKranz (PC name, user name and profile paths redacted)");
        Line();
        string? vrserver = Find("steamvr/vrserver.txt");
        if (vrserver is not null)
        {
            string[] lines = vrserver.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            Line("== SteamVR session (vrserver.txt)");
            foreach (string l in lines.Where(l => SessionLine.IsMatch(l)))
                Line("  " + DatePrefix.Replace(l, ""));
            Line();
            Line("== FlugelKranz driver");
            var driver = lines.Where(l => DriverLine.IsMatch(l)).Distinct().ToList();
            if (driver.Count > 0) foreach (string l in driver) Line("  " + DriverPrefix.Replace(l, ""));
            else Line("  (no driver lines: driver not loaded this session)");

            var rows = new List<AuditRow>();
            foreach (string l in lines)
            {
                var m = FrameAudit.Match(l);
                if (!m.Success) continue;
                double[] q = Numbers(m.Groups[9].Value), p = Numbers(m.Groups[10].Value);
                bool identity = q.Length >= 4 && p.Length >= 3 && q[0] == 0 && q[1] == 0 && q[2] == 0 && Math.Abs(q[3]) == 1 &&
                    p[0] == 0 && p[1] == 0 && p[2] == 0;
                rows.Add(new(Int(m, 1), Int(m, 2), Int(m, 6), Int(m, 7), Int(m, 8), Int(m, 5), !identity));
            }
            Line();
            Line("== FrameAudit (headAge >= 1000 ms samples excluded)");
            if (rows.Count == 0) Line("  (no FrameAudit lines)");
            else
            {
                foreach (var (name, group) in new[] { ("all", rows), ("flying", rows.Where(r => r.Flying).ToList()) })
                {
                    var r = group.Where(x => x.Sampled == 0 || (x.HeadAge >= 0 && x.HeadAge < 1000)).ToList();
                    var aged = r.Where(x => x.Sampled == 1).ToList();
                    int old = aged.Count(x => x.HeadAge >= 47);
                    long layers = r.Sum(x => (long)x.Layers), missed = r.Sum(x => (long)x.Missed), held = r.Sum(x => (long)x.Held);
                    string Pct(double a, double b) => b != 0 ? (100.0 * a / b).ToString("N2", inv) + "%" : "n/a";
                    Line(string.Format(inv,
                        "  {0,-7} seconds={1}  headAge>=47ms={2} of {3} ({4})  missed={5} of {6} layers ({7})  held={8}  identityBypass seconds={9}",
                        name, r.Count, old, aged.Count, Pct(old, aged.Count), missed, layers, Pct(missed, layers), held,
                        r.Count(x => x.Bypass == 1)));
                }
            }
            DriverAuditSummary(lines, Line);
        }
        else Line("vrserver.txt not found");

        Line();
        CountLevels(files, "slimevr/slimevr-openvr-driver.log", @"\[(WARN|ERROR)\]", Line);
        var server = files.Where(f => f.ArchivePath.StartsWith("slimevr/slimevr-server_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (server.Count > 0) CountLevels(files, server[0].ArchivePath, " (WARNING|SEVERE|ERROR) ", Line);
        CountLevels(files, "virtualdesktop/StreamerLog.txt", @"\|(ERROR|WARN)\|", Line);
        Line();
        Line("== Files copied");
        foreach (var f in files) Line("  " + f.ArchivePath.Replace('/', '\\'));
        return s.ToString();
    }

    /// <summary>
    /// DriverAudit seconds grouped by the app's command interval (the bucket most command
    /// changes fell in), so a session flown with the high-rate loop off and then on shows
    /// both halves separately. Only seconds with at least 20 command changes count.
    /// </summary>
    private static void DriverAuditSummary(string[] lines, Action<string> line)
    {
        var inv = CultureInfo.InvariantCulture;
        var groups = new SortedDictionary<string, (int Seconds, long Steps, double StepSum, double StepMax, long Skews, long Skewed, double SkewSum, double SkewMax)>();
        bool any = false;
        foreach (string l in lines)
        {
            var m = DriverAudit.Match(l);
            if (!m.Success) continue;
            any = true;
            long changes = long.Parse(m.Groups[1].Value, inv);
            long[] buckets = m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => long.Parse(v, inv)).ToArray();
            if (changes < 20 || buckets.Length < 8) continue;
            int dominant = Array.IndexOf(buckets, buckets.Max());
            string name = dominant <= 1 ? "interval <10 ms" : dominant <= 3 ? "interval 10-20 ms" : "interval >=20 ms";
            long skews = long.Parse(m.Groups[3].Value, inv), skewed = long.Parse(m.Groups[4].Value, inv);
            double skewMax = double.Parse(m.Groups[5].Value, inv), skewMean = double.Parse(m.Groups[6].Value, inv);
            long steps = long.Parse(m.Groups[7].Value, inv);
            double stepMax = double.Parse(m.Groups[8].Value, inv), stepMean = double.Parse(m.Groups[9].Value, inv);
            groups.TryGetValue(name, out var g);
            groups[name] = (g.Seconds + 1, g.Steps + steps, g.StepSum + stepMean * steps, Math.Max(g.StepMax, stepMax),
                g.Skews + skews, g.Skewed + skewed, g.SkewSum + skewMean * skews, Math.Max(g.SkewMax, skewMax));
        }
        line("");
        line("== DriverAudit by command interval (seconds with >= 20 command changes)");
        if (!any) { line("  (no DriverAudit lines: driverAudit off)"); return; }
        if (groups.Count == 0) { line("  (no seconds with flight commands)"); return; }
        foreach (var (name, g) in groups)
            line(string.Format(inv,
                "  {0,-17} seconds={1}  commandStepM mean={2:0.0000} max={3:0.0000}  skew mean={4:0.0000} max={5:0.0000} skewed={6} of {7}",
                name, g.Seconds, g.Steps > 0 ? g.StepSum / g.Steps : 0, g.StepMax, g.Skews > 0 ? g.SkewSum / g.Skews : 0, g.SkewMax,
                g.Skewed, g.Skews));
    }

    private static void CountLevels(IReadOnlyList<(string ArchivePath, string Text)> files, string archivePath, string pattern, Action<string> line)
    {
        var file = files.FirstOrDefault(f => string.Equals(f.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase));
        if (file.Text is null) return;
        line("== " + archivePath[(archivePath.LastIndexOf('/') + 1)..]);
        var regex = new Regex(pattern, Options);
        var hits = file.Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => regex.IsMatch(l)).ToList();
        if (hits.Count == 0) { line("  no warnings/errors"); return; }
        var timestamp = new Regex(@"^\S+ \S+\s+");
        var tag = new Regex(@"^\[[^\]]+\]\s*");
        var grouped = hits.Select(h => tag.Replace(timestamp.Replace(h, ""), ""))
            .Select(h => h.Length > 140 ? h[..140] : h)
            .Select((text, index) => (text, index))
            .GroupBy(x => x.text)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.First().index)
            .Take(8);
        foreach (var g in grouped)
            line(string.Format(CultureInfo.InvariantCulture, "  {0,5}x {1}", g.Count(), g.Key));
    }

    private static int Int(System.Text.RegularExpressions.Match m, int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    private static double[] Numbers(string text) => text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN).ToArray();
}
