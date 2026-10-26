using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeagueFriendGuard;

/// <summary>A set of player identities. Matching uses PUUID first, then Riot ID (name#tag), then summoner ID.</summary>
public sealed class PlayerSet
{
    public HashSet<string> Puuids { get; set; } = new();
    public HashSet<string> RiotIds { get; set; } = new();
    public HashSet<long> SummonerIds { get; set; } = new();

    [JsonIgnore] public bool IsEmpty => Puuids.Count == 0 && RiotIds.Count == 0 && SummonerIds.Count == 0;
    [JsonIgnore] public int Count => Puuids.Count > 0 ? Puuids.Count : RiotIds.Count;

    public void UnionWith(PlayerSet other)
    {
        Puuids.UnionWith(other.Puuids);
        RiotIds.UnionWith(other.RiotIds);
        SummonerIds.UnionWith(other.SummonerIds);
    }

    public bool Matches(Requester r) =>
        Puuids.Contains(r.Puuid)
        || (r.RiotId is not null && RiotIds.Contains(r.RiotId))
        || (r.SummonerId is long id && id > 0 && SummonerIds.Contains(id));
}

/// <summary>
/// Pulls every player identity out of an LCU payload by walking the JSON tree.
/// Deliberately schema-agnostic so it keeps working across client patches and
/// across endpoints (gameflow session, end-of-game stats, match history).
/// Over-collecting only makes the guard more lenient, never stricter.
/// </summary>
public static class PlayerCollector
{
    private static readonly (string Name, string Tag)[] NamePairs =
    {
        ("gameName", "tagLine"),
        ("riotIdGameName", "riotIdTagLine"),
        ("gameName", "gameTag"),
    };

    public static PlayerSet From(JsonElement el)
    {
        var set = new PlayerSet();
        Collect(el, set);
        return set;
    }

    public static void Collect(JsonElement el, PlayerSet into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (J.Str(el, "puuid", out var puuid)) into.Puuids.Add(J.NormPuuid(puuid));
                foreach (var (n, t) in NamePairs)
                    if (J.Str(el, n, out var name) && J.Str(el, t, out var tag))
                        into.RiotIds.Add(J.RiotId(name, tag));
                if (J.Long(el, "summonerId", out var sid) && sid > 0) into.SummonerIds.Add(sid);

                foreach (var prop in el.EnumerateObject()) Collect(prop.Value, into);
                break;

            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Collect(item, into);
                break;
        }
    }
}

/// <summary>An incoming friend request from /lol-chat/v2/friend-requests, keyed by the sender's PUUID.</summary>
public sealed record Requester(string Puuid, string? RiotId, long? SummonerId, string Display)
{
    public static Requester? FromJson(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;

        // Only incoming requests. Outgoing ones are "out".
        if (!J.Str(r, "direction", out var dir) || !dir.Equals("in", StringComparison.OrdinalIgnoreCase)) return null;

        // The PUUID is the only usable key: current clients send an empty "pid" and "id": "0" for
        // every request, and the decline route is DELETE /lol-chat/v2/friend-requests/{puuid}.
        if (!J.Str(r, "puuid", out var puuid)) return null;

        string? riotId = null;
        string? display = null;
        if (J.Str(r, "gameName", out var gameName))
        {
            display = gameName;
            if (J.Str(r, "gameTag", out var tag) || J.Str(r, "tagLine", out tag))
            {
                riotId = J.RiotId(gameName, tag);
                display = $"{gameName}#{tag}";
            }
        }
        if (display is null && J.Str(r, "name", out var name)) display = name;

        long? summonerId = J.Long(r, "summonerId", out var sid) && sid > 0 ? sid : null;

        return new Requester(J.NormPuuid(puuid), riotId, summonerId, display ?? puuid);
    }
}

/// <summary>Small JSON helpers.</summary>
internal static class J
{
    public static bool Str(JsonElement obj, string prop, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(prop, out var el)
            && el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)) { value = s; return true; }
        }
        return false;
    }

    public static bool Long(JsonElement obj, string prop, out long value)
    {
        value = 0;
        return obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(prop, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt64(out value);
    }

    public static string NormPuuid(string puuid) => puuid.Trim().ToLowerInvariant();
    public static string RiotId(string name, string tag) => $"{name.Trim()}#{tag.Trim()}".ToLowerInvariant();
}
