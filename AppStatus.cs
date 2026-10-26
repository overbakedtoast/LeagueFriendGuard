namespace LeagueFriendGuard;

/// <summary>Live state written by the guard and read by the UI.</summary>
public static class AppStatus
{
    public static bool Connected { get; set; }
    public static string Client { get; set; } = "Waiting for the League client...";
    public static string Guard { get; set; } = "Idle";
    public static bool Guarding { get; set; }
}
