using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LeagueFriendGuard;

public sealed record LcuCredentials(int Port, string Password);

/// <summary>
/// Minimal League Client Update (LCU) API client: REST over https://127.0.0.1:{port}
/// and the WAMP-style event websocket on the same port.
/// </summary>
public sealed class LcuClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly int _port;
    private readonly string _basicToken;

    public LcuClient(LcuCredentials creds)
    {
        _port = creds.Port;
        _basicToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{creds.Password}"));

        // The LCU serves a self-signed Riot certificate on localhost only.
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{_port}"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", _basicToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ---------- Discovery ----------

    /// <summary>Reads the client's lockfile ("LeagueClient:pid:port:password:https").</summary>
    public static LcuCredentials? TryFindCredentials(string? overrideDir)
    {
        foreach (var dir in CandidateDirs(overrideDir))
        {
            var path = Path.Combine(dir, "lockfile");
            if (!File.Exists(path)) continue;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                var parts = sr.ReadToEnd().Trim().Split(':');
                if (parts.Length >= 5 && int.TryParse(parts[2], out var port))
                    return new LcuCredentials(port, parts[3]);
            }
            catch (IOException) { /* client is mid-write; try again next loop */ }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir)) yield return overrideDir;

        // The lockfile sits next to the running client executable.
        foreach (var name in new[] { "LeagueClientUx", "LeagueClient" })
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                string? dir = null;
                try { dir = Path.GetDirectoryName(p.MainModule?.FileName); }
                catch { /* access denied etc. */ }
                finally { p.Dispose(); }
                if (!string.IsNullOrEmpty(dir)) yield return dir;
            }
        }

        yield return @"C:\Riot Games\League of Legends";
    }

    // ---------- REST ----------

    public async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public async Task<(bool Ok, int Status)> DeleteAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.DeleteAsync(path, ct);
        return (resp.IsSuccessStatusCode, (int)resp.StatusCode);
    }

    // ---------- Events ----------

    /// <summary>
    /// Subscribes to LCU events and invokes <paramref name="onEvent"/> for each one.
    /// Returns when the socket closes. Messages look like: [8, "EventName", { data, eventType, uri }].
    /// </summary>
    public async Task RunEventLoopAsync(IEnumerable<string> events, Func<string, JsonElement, Task> onEvent, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        ws.Options.SetRequestHeader("Authorization", "Basic " + _basicToken);
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{_port}/"), ct);

        foreach (var e in events)
        {
            var msg = Encoding.UTF8.GetBytes($"[5, \"{e}\"]"); // 5 = SUBSCRIBE
            await ws.SendAsync(new ArraySegment<byte>(msg), WebSocketMessageType.Text, true, ct);
        }

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (ms.Length == 0) continue;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(ms.ToArray());
                root = doc.RootElement.Clone();
            }
            catch (JsonException) { continue; }

            if (root.ValueKind == JsonValueKind.Array
                && root.GetArrayLength() >= 3
                && root[0].ValueKind == JsonValueKind.Number
                && root[0].GetInt32() == 8) // 8 = EVENT
            {
                await onEvent(root[1].GetString() ?? "", root[2]);
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
