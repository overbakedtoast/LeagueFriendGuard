using System.Text.Json;

namespace LeagueFriendGuard;

public sealed class GameRecord
{
    public long GameId { get; set; }
    public long Timestamp { get; set; } // unix ms, used for ordering
    public PlayerSet Players { get; set; } = new();
}

internal sealed class RecentGamesStore
{
    public List<GameRecord> Games { get; set; } = new();
}

/// <summary>
/// Rolling list of the most recent games' rosters, persisted to recent_players.json
/// so trust survives restarts. Seeded and refreshed from the client's match history.
/// </summary>
public sealed class RecentGames
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _gate = new();
    private int _keep;
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "recent_players.json");
    private List<GameRecord> _games = new();

    public RecentGames(int keep)
    {
        _keep = Math.Max(1, keep);
        Load();
    }

    public int GameCount { get { lock (_gate) return _games.Count; } }

    public int Keep { get { lock (_gate) return _keep; } }

    /// <summary>Changes how many games are kept. Shrinking drops the oldest; growing is filled by the next history refresh.</summary>
    public void SetKeep(int keep)
    {
        lock (_gate)
        {
            _keep = Math.Max(1, keep);
            if (_games.Count <= _keep) return;
            _games = _games.OrderByDescending(g => g.Timestamp).Take(_keep).ToList();
            SaveLocked();
        }
    }

    public bool Contains(Requester r, PlayerSet current)
    {
        lock (_gate) return current.Matches(r) || _games.Any(g => g.Players.Matches(r));
    }

    /// <summary>Records (or merges into) a game. Returns the stored roster instance.</summary>
    public PlayerSet Commit(long gameId, PlayerSet players, long? timestamp = null)
    {
        lock (_gate)
        {
            var existing = _games.FirstOrDefault(g => g.GameId == gameId);
            if (existing is not null)
            {
                existing.Players.UnionWith(players);
                SaveLocked();
                return existing.Players;
            }

            if (players.IsEmpty) return players;

            _games.Add(new GameRecord
            {
                GameId = gameId,
                Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Players = players,
            });
            _games = _games.OrderByDescending(g => g.Timestamp).Take(_keep).ToList();
            SaveLocked();
            return players;
        }
    }

    /// <summary>Adds identities to a roster that may already be stored, then persists.</summary>
    public void Merge(PlayerSet target, PlayerSet extra)
    {
        lock (_gate)
        {
            target.UnionWith(extra);
            SaveLocked();
        }
    }

    private bool HasGame(long gameId)
    {
        lock (_gate) return _games.Any(g => g.GameId == gameId);
    }

    /// <summary>Pulls the last few games from match history and records any we don't already have.</summary>
    public async Task RefreshFromHistoryAsync(LcuClient lcu, CancellationToken ct)
    {
        var list = await lcu.GetAsync(
            $"/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex={_keep}", ct);

        if (list is not { ValueKind: JsonValueKind.Object } l
            || !l.TryGetProperty("games", out var outer)
            || outer.ValueKind != JsonValueKind.Object
            || !outer.TryGetProperty("games", out var games)
            || games.ValueKind != JsonValueKind.Array)
            return;

        foreach (var game in games.EnumerateArray().Take(_keep))
        {
            if (!J.Long(game, "gameId", out var gameId) || gameId <= 0) continue;
            if (HasGame(gameId)) continue;

            J.Long(game, "gameCreation", out var created);

            // The list endpoint only includes you; the per-game endpoint has all participants.
            var detail = await lcu.GetAsync($"/lol-match-history/v1/games/{gameId}", ct);
            if (detail is not { } d) continue;

            Commit(gameId, PlayerCollector.From(d), created > 0 ? created : null);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var store = JsonSerializer.Deserialize<RecentGamesStore>(File.ReadAllText(_path));
            _games = (store?.Games ?? new()).OrderByDescending(g => g.Timestamp).Take(_keep).ToList();
        }
        catch
        {
            _games = new();
        }
    }

    private void SaveLocked()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(new RecentGamesStore { Games = _games }, JsonOpts)); }
        catch { /* best-effort */ }
    }
}
