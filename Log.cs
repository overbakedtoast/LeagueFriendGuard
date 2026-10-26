using System.Globalization;

namespace LeagueFriendGuard;

/// <summary>
/// Colored output, mirrored to friend-guard.log so you can audit decisions later. In the full-screen
/// UI console printing is turned off and the UI shows the most recent entries instead.
/// </summary>
public static class Log
{
    public sealed record Entry(DateTime Time, string Message, ConsoleColor Color);

    private const int Keep = 100;
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "friend-guard.log");
    private static readonly List<Entry> RecentEntries = new();

    public static bool ConsoleEnabled { get; set; } = true;

    public static void Info(string msg) => Write(msg, ConsoleColor.Gray);
    public static void Good(string msg) => Write(msg, ConsoleColor.Green);
    public static void Warn(string msg) => Write(msg, ConsoleColor.Yellow);
    public static void Deny(string msg) => Write(msg, ConsoleColor.Red);

    public static List<Entry> Recent(int count)
    {
        lock (Gate) return RecentEntries.TakeLast(count).ToList();
    }

    private static void Write(string msg, ConsoleColor color)
    {
        var now = DateTime.Now;
        var line = $"[{now:HH:mm:ss}] {msg}";
        lock (Gate)
        {
            RecentEntries.Add(new Entry(now, msg, color));
            if (RecentEntries.Count > Keep) RecentEntries.RemoveAt(0);

            if (ConsoleEnabled)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                Console.ForegroundColor = prev;
            }
            try { File.AppendAllText(FilePath, $"{now:yyyy-MM-dd} {line}{Environment.NewLine}"); } catch { /* logging is best-effort */ }
        }
    }
}

/// <summary>One row per decline (including dry-run and failed attempts) in declines.csv, for looking up who was blocked.</summary>
public static class DeclineLog
{
    public sealed record Entry(DateTime Time, string RiotId, string Result, int? SecondsAfterGame);

    private const string Header = "time,riot_id,puuid,result,phase,game_id,seconds_after_game";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private static readonly object Gate = new();
    private static int _version;
    private static int _sessionDeclines;

    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "declines.csv");

    /// <summary>Bumped on every append, so readers know when to reload.</summary>
    public static int Version => Volatile.Read(ref _version);

    public static int SessionDeclines => Volatile.Read(ref _sessionDeclines);

    /// <param name="secondsAfterGame">Null when the request was judged during the game.</param>
    public static void Append(Requester r, string result, string phase, long gameId, int? secondsAfterGame)
    {
        var fields = new[]
        {
            DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture),
            r.Display,
            r.Puuid,
            result,
            phase,
            gameId > 0 ? gameId.ToString(CultureInfo.InvariantCulture) : "",
            secondsAfterGame?.ToString(CultureInfo.InvariantCulture) ?? "",
        };
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) File.WriteAllText(FilePath, Header + Environment.NewLine);
                File.AppendAllText(FilePath, string.Join(",", fields.Select(Csv)) + Environment.NewLine);
            }
            catch { /* logging is best-effort */ }
        }
        if (result == "declined") Interlocked.Increment(ref _sessionDeclines);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Every row in declines.csv, oldest first.</summary>
    public static List<Entry> ReadAll()
    {
        string[] lines;
        lock (Gate)
        {
            try { lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : Array.Empty<string>(); }
            catch { return new(); }
        }

        var list = new List<Entry>();
        foreach (var line in lines.Skip(1))
        {
            var f = ParseCsv(line);
            if (f.Count < 7) continue;
            if (!DateTime.TryParseExact(f[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            int? secs = int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
            list.Add(new Entry(time, f[1], f[3], secs));
        }
        return list;
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    private static List<string> ParseCsv(string line)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        fields.Add(cur.ToString());
        return fields;
    }
}
