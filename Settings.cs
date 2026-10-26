using System.Text.Json;

namespace LeagueFriendGuard;

/// <summary>Loaded from settings.json next to the exe (created with defaults on first run).</summary>
public sealed class Settings
{
    /// <summary>Master switch. When off, requests are never touched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long after a game ends requests are still screened.</summary>
    public int PostGameWindowSeconds { get; set; } = 60;

    /// <summary>Players from this many previous games are always allowed through.</summary>
    public int PreviousGamesToTrust { get; set; } = 3;

    /// <summary>Log what would be declined without declining anything.</summary>
    public bool DryRun { get; set; } = false;

    /// <summary>Backup polling interval (live websocket events are used when available).</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>Optional. Folder containing the League "lockfile" if auto-detection fails.</summary>
    public string? LeagueInstallPath { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { /* best-effort; the change still applies for this session */ }
    }

    public static Settings Load()
    {
        Settings s;
        try
        {
            if (File.Exists(FilePath))
            {
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            else
            {
                s = new Settings();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOpts));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings.json couldn't be read ({ex.Message}); using defaults.");
            s = new Settings();
        }

        s.PostGameWindowSeconds = Math.Max(0, s.PostGameWindowSeconds);
        s.PreviousGamesToTrust = Math.Max(0, s.PreviousGamesToTrust);
        s.PollIntervalSeconds = Math.Max(1, s.PollIntervalSeconds);
        return s;
    }
}
