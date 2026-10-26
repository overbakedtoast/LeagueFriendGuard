using System.Text.Json;

namespace LeagueFriendGuard;

/// <summary>
/// Rule: a friend request that ARRIVES while you're in a game, or within N seconds after
/// the game ends, is declined unless the sender was in the current game or one of your
/// last few games. Requests that arrive at any other time are never touched.
/// </summary>
public sealed class FriendGuard
{
    private static readonly HashSet<string> InGamePhases = new() { "InProgress", "Reconnect" };
    private static readonly HashSet<string> PostGamePhases = new() { "WaitingForStats", "PreEndOfGame", "EndOfGame" };

    private readonly LcuClient _lcu;
    private readonly Settings _s;
    private readonly RecentGames _recent;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Sender PUUIDs of still-pending requests we've already looked at (including ones that existed
    // before the guard window). Pruned when a request goes away, so a re-sent request is judged fresh.
    private readonly HashSet<string> _seenRequests = new(StringComparer.OrdinalIgnoreCase);

    // Requests that arrived in the guard window before the game's roster could be read. Re-checked
    // every tick until the roster is available, even if the window has closed by then.
    private readonly HashSet<string> _deferred = new(StringComparer.OrdinalIgnoreCase);

    private string _phase = "None";
    private DateTimeOffset? _gameEndedAt;
    private PlayerSet _activeRoster = new();
    private long _activeGameId;
    private bool _guardWasActive;

    public FriendGuard(LcuClient lcu, Settings settings, RecentGames recent)
    {
        _lcu = lcu;
        _s = settings;
        _recent = recent;
    }

    private bool InGame => InGamePhases.Contains(_phase);

    private bool GuardActive =>
        InGame || (_gameEndedAt is { } t && DateTimeOffset.Now - t <= TimeSpan.FromSeconds(_s.PostGameWindowSeconds));

    // ---------- Lifecycle ----------

    public async Task RunAsync(CancellationToken ct)
    {
        await WaitForLoginAsync(ct);
        await InitializeAsync(ct);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var events = RunEventsAsync(linked.Token);
        try
        {
            await PollLoopAsync(ct); // throws when the client goes away
        }
        finally
        {
            linked.Cancel();
            try { await events; } catch { /* shutting down */ }
        }
    }

    /// <summary>
    /// --test: prints what the guard can see right now (phase, roster, trusted games, pending requests)
    /// and how each pending request would be judged. Never declines anything.
    /// </summary>
    public async Task DiagnoseAsync(CancellationToken ct)
    {
        await WaitForLoginAsync(ct);

        _phase = await GetPhaseAsync(ct) ?? "unknown";
        Log.Info($"Game phase      : {_phase}{(InGame ? " (guard would be active)" : "")}");

        // Outside a game the session still holds the last game's data, so only read it when it's current.
        if (InGame || PostGamePhases.Contains(_phase))
        {
            await CaptureRosterAsync(ct);
            if (_activeRoster.IsEmpty) Log.Warn("Current game    : roster couldn't be read - requests would be held, not declined.");
            else Log.Info($"Current game    : {_activeRoster.Count} players read (game {_activeGameId})");
        }
        else Log.Info("Current game    : not in a game");

        await _recent.RefreshFromHistoryAsync(_lcu, ct);
        Log.Info($"Trusted games   : {_recent.GameCount} from match history");

        var requests = await GetIncomingRequestsAsync(ct);
        if (requests is null)
        {
            Log.Warn("Friend requests : couldn't read /lol-chat/v2/friend-requests - the route may have changed.");
            return;
        }
        Log.Info($"Friend requests : {requests.Count} incoming");
        foreach (var r in requests)
        {
            if (_recent.Contains(r, _activeRoster)) Log.Good($"  {r.Display} - trusted (played together recently)");
            else Log.Deny($"  {r.Display} - would be declined if it arrived during or right after a game");
        }
    }

    private async Task WaitForLoginAsync(CancellationToken ct)
    {
        var failures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var me = await _lcu.GetAsync("/lol-summoner/v1/current-summoner", ct);
                if (me is { ValueKind: JsonValueKind.Object } m)
                {
                    var who = J.Str(m, "gameName", out var gn) && J.Str(m, "tagLine", out var tl) ? $"{gn}#{tl}"
                            : J.Str(m, "displayName", out var dn) ? dn : "your account";
                    Log.Good($"Connected to the League client as {who}.");
                    AppStatus.Connected = true;
                    AppStatus.Client = $"Connected as {who}";
                    return;
                }
            }
            catch (Exception ex) when (IsConnectionError(ex, ct))
            {
                if (++failures > 20) throw;
            }
            await Task.Delay(2000, ct);
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        _phase = await GetPhaseAsync(ct) ?? "None";

        // Grandfather anything already pending: only requests that arrive while guarding are judged.
        var pending = await GetIncomingRequestsAsync(ct)
            ?? throw new HttpRequestException("Couldn't read pending friend requests.");
        foreach (var r in pending) _seenRequests.Add(r.Puuid);

        try { await _recent.RefreshFromHistoryAsync(_lcu, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn($"Couldn't read match history: {ex.Message}"); }
        Log.Info($"Trusting players from {_recent.GameCount} recent game(s).");

        if (InGame)
        {
            await CaptureRosterAsync(ct);
            Log.Info("Already in a game - guard is active.");
        }
        else if (PostGamePhases.Contains(_phase))
        {
            await CaptureRosterAsync(ct);
            EndGame();
        }

        UpdateTitle();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (true)
        {
            try
            {
                await TickAsync(ct);
                failures = 0;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                if (!IsConnectionError(ex, ct)) Log.Warn($"Error while checking: {ex.Message}");
                if (++failures >= 3) throw; // client probably closed; the outer loop reconnects
            }
            await Task.Delay(TimeSpan.FromSeconds(_s.PollIntervalSeconds), ct);
        }
    }

    private async Task RunEventsAsync(CancellationToken ct)
    {
        try
        {
            await _lcu.RunEventLoopAsync(
                new[]
                {
                    "OnJsonApiEvent_lol-chat_v2_friend-requests",
                    "OnJsonApiEvent_lol-gameflow_v1_gameflow-phase",
                },
                async (_, _) =>
                {
                    try { await TickAsync(ct); }
                    catch (Exception) when (!ct.IsCancellationRequested) { /* polling will retry */ }
                },
                ct);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Log.Warn($"Live events unavailable ({ex.Message}); using polling every {_s.PollIntervalSeconds}s.");
        }
    }

    /// <summary>One pass: sync the game phase, then screen any new friend requests.</summary>
    private async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // The trusted-games setting changed in the UI: resize the history, filling it in if it grew.
            if (_recent.Keep != Math.Max(1, _s.PreviousGamesToTrust + 1))
            {
                _recent.SetKeep(_s.PreviousGamesToTrust + 1);
                await _recent.RefreshFromHistoryAsync(_lcu, ct);
            }

            var phase = await GetPhaseAsync(ct);
            if (phase is not null && phase != _phase) await OnPhaseChangedAsync(_phase, phase, ct);

            if (InGame && _activeRoster.IsEmpty) await CaptureRosterAsync(ct);

            await CheckRequestsAsync(ct);
            UpdateTitle();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- Game tracking ----------

    private async Task OnPhaseChangedAsync(string oldPhase, string newPhase, CancellationToken ct)
    {
        _phase = newPhase;
        var wasInGame = InGamePhases.Contains(oldPhase);
        var nowInGame = InGamePhases.Contains(newPhase);

        if (!wasInGame && nowInGame)
        {
            _activeRoster = new PlayerSet();
            _activeGameId = 0;
            _gameEndedAt = null;
            await CaptureRosterAsync(ct);
            Log.Info(_activeRoster.IsEmpty
                ? "Game started - roster not available yet, will keep trying."
                : $"Game started - guard active ({_activeRoster.Count} players in this game are trusted).");
        }
        else if (wasInGame && !nowInGame)
        {
            if (_activeRoster.IsEmpty) await CaptureRosterAsync(ct); // last chance from the session
            EndGame();
        }
    }

    private void EndGame()
    {
        _gameEndedAt = DateTimeOffset.Now;
        var gameId = _activeGameId != 0 ? _activeGameId : -DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _activeRoster = _recent.Commit(gameId, _activeRoster);
        Log.Info($"Game ended - still guarding for {_s.PostGameWindowSeconds}s.");

        // Fill in from the end-of-game screen / match history once they're available.
        _ = Task.Run(() => AfterGameAsync(gameId));
    }

    private async Task AfterGameAsync(long gameId)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            var eog = await _lcu.GetAsync("/lol-end-of-game/v1/eog-stats-block", CancellationToken.None);
            if (eog is { } e)
            {
                var players = PlayerCollector.From(e);
                if (!players.IsEmpty)
                {
                    await _gate.WaitAsync();
                    try
                    {
                        if (_gameEndedAt is not null) _activeRoster = _recent.Commit(gameId, players);
                        else _recent.Commit(gameId, players); // a new game already started; just record it
                    }
                    finally { _gate.Release(); }
                }
            }
            await _recent.RefreshFromHistoryAsync(_lcu, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't fetch end-of-game roster: {ex.Message}");
        }
    }

    private async Task CaptureRosterAsync(CancellationToken ct)
    {
        var session = await _lcu.GetAsync("/lol-gameflow/v1/session", ct);
        if (session is not { ValueKind: JsonValueKind.Object } s) return;
        if (!s.TryGetProperty("gameData", out var gameData)) return;

        if (J.Long(gameData, "gameId", out var id) && id > 0) _activeGameId = id;

        var players = PlayerCollector.From(gameData);
        if (!players.IsEmpty) _recent.Merge(_activeRoster, players);
    }

    // ---------- Friend requests ----------

    private async Task CheckRequestsAsync(CancellationToken ct)
    {
        var requests = await GetIncomingRequestsAsync(ct);
        if (requests is null) return; // couldn't read the list; never treat that as "no requests"

        // Forget requests that were accepted, declined or cancelled, so a re-sent one is judged fresh.
        var pending = requests.Select(r => r.Puuid).ToList();
        _seenRequests.IntersectWith(pending);
        _deferred.IntersectWith(pending);

        // Switched off in the UI: requests arriving now are marked seen and left alone.
        if (!_s.Enabled) _deferred.Clear();
        var active = GuardActive && _s.Enabled;

        foreach (var r in requests)
        {
            if (!_deferred.Contains(r.Puuid))
            {
                if (!_seenRequests.Add(r.Puuid)) continue; // already handled
                if (!active) continue;                     // arrived outside the guard window: leave it alone
            }

            if (_activeRoster.IsEmpty)
            {
                if (_deferred.Add(r.Puuid))
                    Log.Warn($"Request from {r.Display} arrived during a game, but the roster isn't readable yet - holding it until it is.");
                continue;
            }
            _deferred.Remove(r.Puuid);

            if (_recent.Contains(r, _activeRoster))
            {
                Log.Good($"Allowed request from {r.Display} - you played with them recently.");
                continue;
            }

            int? secondsAfterGame = !InGame && _gameEndedAt is { } t ? (int)(DateTimeOffset.Now - t).TotalSeconds : null;

            if (_s.DryRun)
            {
                Log.Deny($"[dry run] Would decline request from {r.Display}.");
                DeclineLog.Append(r, "dry-run", _phase, _activeGameId, secondsAfterGame);
                continue;
            }

            var (ok, detail) = await DeclineAsync(r, ct);
            if (ok) Log.Deny($"Declined request from {r.Display} (not in this game or your last {_s.PreviousGamesToTrust}).");
            else Log.Warn($"Tried to decline {r.Display}, but it failed ({detail}).");
            DeclineLog.Append(r, ok ? "declined" : $"failed: {detail}", _phase, _activeGameId, secondsAfterGame);
        }
    }

    private async Task<(bool Ok, string Detail)> DeclineAsync(Requester r, CancellationToken ct)
    {
        var (ok, status) = await _lcu.DeleteAsync($"/lol-chat/v2/friend-requests/{Uri.EscapeDataString(r.Puuid)}", ct);
        if (!ok) return (false, $"HTTP {status}");

        // Don't trust the status code alone: confirm the request is actually gone.
        var after = await GetIncomingRequestsAsync(ct);
        if (after is not null && after.Any(x => x.Puuid == r.Puuid)) return (false, "still pending after decline");
        return (true, "");
    }

    /// <summary>Incoming requests, or null if the list couldn't be read.</summary>
    private async Task<List<Requester>?> GetIncomingRequestsAsync(CancellationToken ct)
    {
        var json = await _lcu.GetAsync("/lol-chat/v2/friend-requests", ct);
        if (json is not { ValueKind: JsonValueKind.Array } arr) return null;
        var list = new List<Requester>();
        foreach (var item in arr.EnumerateArray())
            if (Requester.FromJson(item) is { } r) list.Add(r);
        return list;
    }

    // ---------- Helpers ----------

    private async Task<string?> GetPhaseAsync(CancellationToken ct)
    {
        var p = await _lcu.GetAsync("/lol-gameflow/v1/gameflow-phase", ct);
        return p is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    }

    private void UpdateTitle()
    {
        var active = GuardActive;
        string state;
        if (!_s.Enabled) state = "Off - requests are not touched";
        else if (InGame) state = "In game - guarding";
        else if (active && _gameEndedAt is { } t)
            state = $"Post-game - guarding {Math.Max(0, _s.PostGameWindowSeconds - (int)(DateTimeOffset.Now - t).TotalSeconds)}s";
        else state = $"Idle ({_phase})";

        AppStatus.Guard = state;
        AppStatus.Guarding = active && _s.Enabled;
        try { Console.Title = $"League Friend Guard - {state}{(_s.DryRun ? " [DRY RUN]" : "")}"; } catch { }

        if (_guardWasActive && !active) Log.Info("Guard window closed.");
        _guardWasActive = active;
    }

    private static bool IsConnectionError(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
