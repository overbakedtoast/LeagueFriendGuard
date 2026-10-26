using LeagueFriendGuard;

var settings = Settings.Load();
bool HasArg(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
if (HasArg("--dry-run")) settings.DryRun = true;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (HasArg("--test"))
{
    var testCreds = LcuClient.TryFindCredentials(settings.LeagueInstallPath);
    if (testCreds is null)
    {
        Log.Warn("League client not found. Start it and try again.");
        return 1;
    }
    using var testLcu = new LcuClient(testCreds);
    try
    {
        await new FriendGuard(testLcu, settings, new RecentGames(settings.PreviousGamesToTrust + 1)).DiagnoseAsync(cts.Token);
    }
    catch (Exception ex) when (!cts.IsCancellationRequested)
    {
        Log.Warn($"Test failed: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
    return 0;
}

if (HasArg("--snapshot"))
{
    // Prints every UI screen as text, to check the layout without a live console.
    Log.ConsoleEnabled = false;
    var ui = new ConsoleUi(settings);
    foreach (var (w, h) in new[] { (120, 30), (120, 19), (100, 16) }) Console.Write(ui.Snapshot(w, h));
    return 0;
}

var recent = new RecentGames(settings.PreviousGamesToTrust + 1);

// Plain scrolling log (also used when output is redirected, where the full-screen UI can't draw).
if (HasArg("--console") || Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.WriteLine("League Friend Guard - auto-declines bot friend requests during and right after games.");
    Console.WriteLine($"Guard window : while in game + {settings.PostGameWindowSeconds}s after it ends");
    Console.WriteLine($"Trusted      : players in the current game and your last {settings.PreviousGamesToTrust} games");
    if (settings.DryRun) Log.Warn("DRY RUN - requests will be logged but never declined.");
    Console.WriteLine("Press Ctrl+C to quit.");
    Console.WriteLine();

    await RunGuardAsync(settings, recent, cts.Token);
    Log.Info("Stopped.");
    return 0;
}

Log.ConsoleEnabled = false;
var guard = Task.Run(() => RunGuardAsync(settings, recent, cts.Token));
new ConsoleUi(settings).Run(cts);
await guard;
return 0;

// Connects to the client, runs the guard, and reconnects whenever the client restarts.
static async Task RunGuardAsync(Settings settings, RecentGames recent, CancellationToken ct)
{
    var waitingShown = false;
    while (!ct.IsCancellationRequested)
    {
        var creds = LcuClient.TryFindCredentials(settings.LeagueInstallPath);
        if (creds is null)
        {
            AppStatus.Connected = false;
            AppStatus.Client = "Waiting for the League client...";
            AppStatus.Guard = "Idle";
            AppStatus.Guarding = false;
            if (!waitingShown) { Log.Info("Waiting for the League client..."); waitingShown = true; }
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
            continue;
        }

        waitingShown = false;
        AppStatus.Client = "Connecting...";
        using (var lcu = new LcuClient(creds))
        {
            try
            {
                await new FriendGuard(lcu, settings, recent).RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AppStatus.Connected = false;
                AppStatus.Client = "Reconnecting...";
                Log.Warn($"Lost connection to the League client ({ex.GetType().Name}: {ex.Message}). Reconnecting...");
            }
        }

        try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
    }
}
