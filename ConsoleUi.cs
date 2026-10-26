using System.Diagnostics;
using System.Text;

namespace LeagueFriendGuard;

/// <summary>
/// Full-screen console UI in the style of LeagueAutoAccept: ASCII logo, centered menu with dot
/// leaders, arrow keys to move, Enter or Left/Right to change a value, Esc to go back.
/// Each frame is composed off-screen and only the rows that changed are written, so the live
/// status can refresh several times a second without flicker.
/// </summary>
public sealed class ConsoleUi
{
    private const int MinWidth = 100;
    private const int MinHeight = 16;
    private const int FullHeight = 28; // below this the main screen drops the logo
    private const int MenuWidth = 44;
    private const int WideWidth = 90;

    private static readonly string[] Logo =
    {
        @"  ______    _                _    _____                     _ ",
        @" |  ____|  (_)              | |  / ____|                   | |",
        @" | |__ _ __ _  ___ _ __   __| | | |  __ _   _  __ _ _ __ __| |",
        @" |  __| '__| |/ _ \ '_ \ / _` | | | |_ | | | |/ _` | '__/ _` |",
        @" | |  | |  | |  __/ | | | (_| | | |__| | |_| | (_| | | | (_| |",
        @" |_|  |_|  |_|\___|_| |_|\__,_|  \_____|\__,_|\__,_|_|  \__,_|",
    };

    private static readonly int[] WindowChoices = { 30, 45, 60, 90, 120, 180, 300 };
    private static readonly int[] TrustChoices = Enumerable.Range(1, 10).ToArray();
    private static readonly int[] PollChoices = { 1, 2, 3, 5, 10 };

    private enum Screen { Main, Activity, Settings, Info, Exit }

    /// <summary>A menu row. Value null means it's a button; Change(+1/-1) runs on Enter or Left/Right.</summary>
    private sealed record Item(string Label, Func<string>? Value, Action<int> Change, string[]? Desc = null);

    private readonly Settings _s;
    private readonly string _version;
    private Screen _screen = Screen.Main;
    private int _pos;
    private int _mainPos;
    private bool _quit;

    // Frame being composed, and what's currently on screen.
    private int _w, _h;
    private char[][] _chars = Array.Empty<char[]>();
    private ConsoleColor[][] _colors = Array.Empty<ConsoleColor[]>();
    private char[][] _shownChars = Array.Empty<char[]>();
    private ConsoleColor[][] _shownColors = Array.Empty<ConsoleColor[]>();

    private int _declinesVersion = -1;
    private List<DeclineLog.Entry> _declines = new();

    public ConsoleUi(Settings settings)
    {
        _s = settings;
        var v = typeof(ConsoleUi).Assembly.GetName().Version;
        _version = v is null ? "" : $"v{v.Major}.{v.Minor}";
    }

    // ---------- Main loop ----------

    /// <summary>Runs until the user exits (or Ctrl+C), then cancels <paramref name="cts"/>.</summary>
    public void Run(CancellationTokenSource cts)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.CursorVisible = false; } catch { }
        try { Console.Title = "League Friend Guard"; } catch { }
        try
        {
            // Only conhost honors this; Windows Terminal keeps its own size.
            if (OperatingSystem.IsWindows() && (Console.WindowWidth < 120 || Console.WindowHeight < 30))
                Console.SetWindowSize(Math.Max(Console.WindowWidth, 120), Math.Max(Console.WindowHeight, 30));
        }
        catch { }

        while (!cts.IsCancellationRequested && !_quit)
        {
            while (Console.KeyAvailable) HandleKey(Console.ReadKey(true));
            if (_quit) break;
            Compose(Console.WindowWidth, Console.WindowHeight);
            Flush();
            Thread.Sleep(50);
        }

        cts.Cancel();
        Console.ResetColor();
        Console.Clear();
        Console.CursorVisible = true;
    }

    /// <summary>Every screen rendered as plain text, for checking the layout without a live console.</summary>
    public string Snapshot(int width, int height)
    {
        var sb = new StringBuilder();
        foreach (var screen in Enum.GetValues<Screen>())
        {
            _screen = screen;
            _pos = 0;
            Compose(width, height);
            sb.AppendLine($"===== {screen} ({width}x{height}) " + new string('=', 40));
            foreach (var row in _chars) sb.AppendLine(new string(row).TrimEnd());
        }
        _screen = Screen.Main;
        return sb.ToString();
    }

    // ---------- Input ----------

    private void HandleKey(ConsoleKeyInfo k)
    {
        if (_screen is Screen.Activity or Screen.Info)
        {
            if (_screen == Screen.Activity && k.Key == ConsoleKey.Enter) OpenPath(DeclineLog.FilePath);
            else if (k.Key is ConsoleKey.Escape or ConsoleKey.Backspace or ConsoleKey.Enter) Open(Screen.Main);
            return;
        }

        if (k.Key is ConsoleKey.Escape or ConsoleKey.Backspace)
        {
            Open(_screen == Screen.Main ? Screen.Exit : Screen.Main);
            return;
        }

        var items = Items();
        _pos = Math.Clamp(_pos, 0, items.Count - 1);
        var item = items[_pos];
        var onButton = item.Value is null;
        var firstButton = items.FindIndex(i => i.Value is null);

        switch (k.Key)
        {
            case ConsoleKey.UpArrow:
                if (onButton) { if (firstButton > 0) _pos = firstButton - 1; }
                else if (_pos > 0) _pos--;
                break;
            case ConsoleKey.DownArrow:
                if (!onButton && _pos < items.Count - 1) _pos++;
                break;
            case ConsoleKey.LeftArrow:
                if (!onButton) item.Change(-1);
                else if (_pos > firstButton) _pos--;
                break;
            case ConsoleKey.RightArrow:
                if (!onButton) item.Change(+1);
                else if (_pos < items.Count - 1) _pos++;
                break;
            case ConsoleKey.Enter:
            case ConsoleKey.Spacebar:
                item.Change(+1);
                break;
        }
    }

    private void Open(Screen screen)
    {
        if (_screen == Screen.Main) _mainPos = _pos;
        _screen = screen;
        _pos = screen == Screen.Main ? _mainPos : 0;
    }

    private List<Item> Items() => _screen switch
    {
        Screen.Main => new()
        {
            new("Friend guard", () => _s.Enabled ? "Enabled" : "Disabled",
                _ => { _s.Enabled = !_s.Enabled; Save(); }),
            new("Post-game window", () => $"{_s.PostGameWindowSeconds}s",
                d => { _s.PostGameWindowSeconds = Cycle(WindowChoices, _s.PostGameWindowSeconds, d); Save(); }),
            new("Trust players from last", () => $"{_s.PreviousGamesToTrust} game{(_s.PreviousGamesToTrust == 1 ? "" : "s")}",
                d => { _s.PreviousGamesToTrust = Cycle(TrustChoices, _s.PreviousGamesToTrust, d); Save(); }),
            new("Activity", null, _ => Open(Screen.Activity)),
            new("Settings", null, _ => Open(Screen.Settings)),
            new("Info", null, _ => Open(Screen.Info)),
        },
        Screen.Settings => new()
        {
            new("Mode", () => _s.DryRun ? "Dry run" : "Decline",
                _ => { _s.DryRun = !_s.DryRun; Save(); },
                new[] { "Dry run logs what would be declined without declining anything.",
                        "Handy for the first few games after a League patch." }),
            new("Check interval", () => $"{_s.PollIntervalSeconds}s",
                d => { _s.PollIntervalSeconds = Cycle(PollChoices, _s.PollIntervalSeconds, d); Save(); },
                new[] { "How often the client is polled as a backup to live events.",
                        "Live events normally react instantly, so this rarely needs changing." }),
            new("Open data folder", () => "",
                _ => OpenPath(AppContext.BaseDirectory),
                new[] { "Opens the folder with settings.json, declines.csv and friend-guard.log.", "" }),
        },
        Screen.Exit => new()
        {
            new("Yes", null, _ => _quit = true),
            new("No", null, _ => Open(Screen.Main)),
        },
        _ => new(),
    };

    private void Save()
    {
        _s.Save();
        Log.Info($"Settings: guard {(_s.Enabled ? "on" : "off")}, window {_s.PostGameWindowSeconds}s, " +
                 $"trust last {_s.PreviousGamesToTrust}, {(_s.DryRun ? "dry run" : "decline")}, check every {_s.PollIntervalSeconds}s.");
    }

    private static int Cycle(int[] choices, int current, int dir)
    {
        var i = Array.IndexOf(choices, current);
        if (i < 0) return choices[0];
        return choices[(i + dir + choices.Length) % choices.Length];
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"Couldn't open {path}: {ex.Message}"); }
    }

    // ---------- Screens ----------

    private void Compose(int width, int height)
    {
        _w = Math.Max(1, width);
        _h = Math.Max(1, height);
        _chars = new char[_h][];
        _colors = new ConsoleColor[_h][];
        for (var r = 0; r < _h; r++)
        {
            _chars[r] = new string(' ', _w).ToCharArray();
            _colors[r] = Enumerable.Repeat(ConsoleColor.Gray, _w).ToArray();
        }

        if (_w < MinWidth || _h < MinHeight)
        {
            var dim = _w < MinWidth ? "width" : "height";
            Center(_h / 2 - 1, $"Console {dim} is too small. Please resize it.");
            Center(_h / 2, $"Minimum: {MinWidth}x{MinHeight} | Current: {_w}x{_h}");
            return;
        }

        switch (_screen)
        {
            case Screen.Main: DrawMain(); break;
            case Screen.Settings: DrawSettings(); break;
            case Screen.Activity: DrawActivity(); break;
            case Screen.Info: DrawInfo(); break;
            case Screen.Exit: DrawExit(); break;
        }
        Put(_h - 1, 0, _version, ConsoleColor.DarkGray);
    }

    private void DrawMain()
    {
        // Short consoles (e.g. the VS Code terminal panel) get a one-line title instead of the logo.
        int subtitleRow;
        if (_h < FullHeight)
        {
            Center(0, "League Friend Guard", ConsoleColor.Cyan);
            subtitleRow = 1;
        }
        else
        {
            var top = Math.Max(0, (_h - FullHeight) / 2);
            for (var i = 0; i < Logo.Length; i++) Center(top + 1 + i, Logo[i], ConsoleColor.Cyan);
            subtitleRow = top + 8;
        }

        if (_s.DryRun) Center(subtitleRow, "DRY RUN - requests are logged, never declined", ConsoleColor.Yellow);
        else Center(subtitleRow, "Auto-declines bot friend requests during and right after games", ConsoleColor.DarkGray);

        var row = subtitleRow + 2;
        var items = Items();
        var options = items.Where(i => i.Value is not null).ToList();
        for (var i = 0; i < options.Count; i++)
        {
            var value = options[i].Value!();
            var color = value switch { "Enabled" => ConsoleColor.Green, "Disabled" => ConsoleColor.Red, _ => ConsoleColor.Gray };
            DotRow(row + i, options[i].Label, value, color, selected: _pos == i);
        }
        DrawButtons(row + options.Count + 1, items, options.Count);

        // Live status
        var statusRow = row + options.Count + 4;
        DotRow(statusRow, "Client", AppStatus.Client, AppStatus.Connected ? ConsoleColor.Green : ConsoleColor.Yellow);
        DotRow(statusRow + 1, "Status", AppStatus.Guard,
            !_s.Enabled ? ConsoleColor.DarkGray : AppStatus.Guarding ? ConsoleColor.Cyan : ConsoleColor.Gray);
        ReloadDeclines();
        var total = _declines.Count(d => d.Result == "declined");
        DotRow(statusRow + 2, "Declined", $"{DeclineLog.SessionDeclines} this session, {total} all time",
            DeclineLog.SessionDeclines > 0 ? ConsoleColor.Red : ConsoleColor.Gray);

        // Recent activity from the log
        var left = (_w - WideWidth) / 2;
        var logRow = statusRow + 4;
        Put(logRow, left, "Recent activity", ConsoleColor.DarkGray);
        var room = Math.Max(0, _h - 2 - logRow);
        foreach (var e in Log.Recent(room))
            Put(++logRow, left, Fit($"{e.Time:HH:mm:ss}  {e.Message}", WideWidth), e.Color);
    }

    private void DrawSettings()
    {
        var items = Items();
        var top = _h / 2 - items.Count - 2;
        Center(top - 2, "Settings", ConsoleColor.Cyan);
        for (var i = 0; i < items.Count; i++)
            DotRow(top + i, items[i].Label, items[i].Value?.Invoke() ?? "", ConsoleColor.Gray, selected: _pos == i);

        var desc = items[Math.Clamp(_pos, 0, items.Count - 1)].Desc ?? Array.Empty<string>();
        for (var i = 0; i < desc.Length; i++) Center(top + items.Count + 2 + i, desc[i], ConsoleColor.DarkGray);

        Center(_h - 2, "Enter / Left / Right: change    Esc: back", ConsoleColor.DarkGray);
    }

    private void DrawActivity()
    {
        ReloadDeclines();
        var left = (_w - WideWidth) / 2;
        var now = DateTime.Now;
        var declined = _declines.Where(d => d.Result == "declined").ToList();
        var dryRuns = _declines.Count(d => d.Result == "dry-run");

        Center(1, "Declined friend requests", ConsoleColor.Cyan);
        var summary = $"{declined.Count} all time, {declined.Count(d => now - d.Time < TimeSpan.FromDays(7))} in the last 7 days, " +
                      $"{declined.Count(d => d.Time.Date == now.Date)} today";
        if (dryRuns > 0) summary += $"  ({dryRuns} dry-run)";
        Center(3, summary);

        Put(5, left, $"{"Time",-18}{"Riot ID",-32}{"Result",-22}{"When",-18}", ConsoleColor.DarkGray);
        Put(6, left, new string('-', WideWidth), ConsoleColor.DarkGray);

        if (_declines.Count == 0) Center(8, "Nothing declined yet.", ConsoleColor.DarkGray);

        var row = 7;
        foreach (var d in Enumerable.Reverse(_declines).Take(Math.Max(0, _h - 9)))
        {
            var when = d.SecondsAfterGame is { } s ? $"{s}s after game" : "during game";
            var color = d.Result switch
            {
                "declined" => ConsoleColor.Red,
                "dry-run" => ConsoleColor.Yellow,
                _ => ConsoleColor.DarkYellow,
            };
            Put(row, left, $"{d.Time:yyyy-MM-dd HH:mm}  ");
            Put(row, left + 18, Fit(d.RiotId, 30));
            Put(row, left + 50, Fit(d.Result, 20), color);
            Put(row, left + 72, when);
            row++;
        }

        Center(_h - 2, "Enter: open declines.csv    Esc: back", ConsoleColor.DarkGray);
    }

    private void DrawInfo()
    {
        var top = Math.Max(0, _h / 2 - 7);
        Center(top, "League Friend Guard", ConsoleColor.Cyan);
        Center(top + 1, "github.com/overbakedtoast/LeagueFriendGuard", ConsoleColor.DarkGray);
        Center(top + 2, Dots("Version", _version));
        Center(top + 4, "Declines bot friend requests that arrive during a game or right after it,");
        Center(top + 5, "unless the sender was in that game or one of your last few games.");
        Center(top + 7, "Inspired by LeagueAutoAccept:");
        Center(top + 8, "github.com/sweetriverfish/LeagueAutoAccept");
        Center(top + 10, "Uses the League client's local API. Not endorsed by Riot Games.", ConsoleColor.DarkGray);
        Center(top + 11, "Data folder: " + Fit(AppContext.BaseDirectory, WideWidth - 13), ConsoleColor.DarkGray);
        Center(_h - 2, "Esc: back", ConsoleColor.DarkGray);
    }

    private void DrawExit()
    {
        var row = _h / 2 + 1;
        Center(row - 2, "Are you sure you want to close this app?");
        Center(row - 1, "Friend requests won't be screened while it's closed.", ConsoleColor.DarkGray);
        var left = (_w - 38) / 2;
        DrawButton(row, left + 3, "Yes", _pos == 0);
        DrawButton(row, left + 32, "No", _pos == 1);
    }

    private void DrawButtons(int row, List<Item> items, int firstButton)
    {
        var buttons = items.Skip(firstButton).ToList();
        var left = (_w - MenuWidth) / 2;
        for (var i = 0; i < buttons.Count; i++)
        {
            var label = buttons[i].Label;
            // Spread across the menu width: first left-aligned, last right-aligned, the rest evenly between.
            var col = buttons.Count == 1 ? left
                : left + (MenuWidth - label.Length) * i / (buttons.Count - 1);
            DrawButton(row, col, label, _pos == firstButton + i);
        }
    }

    private void DrawButton(int row, int col, string label, bool selected)
    {
        if (selected) Put(row, col - 3, "->", ConsoleColor.Cyan);
        Put(row, col, label, selected ? ConsoleColor.White : ConsoleColor.Gray);
    }

    private void DotRow(int row, string label, string value, ConsoleColor valueColor, bool selected = false)
    {
        var left = (_w - MenuWidth) / 2;
        var text = Dots(label, value);
        if (selected) Put(row, left - 3, "->", ConsoleColor.Cyan);
        Put(row, left, text, selected ? ConsoleColor.White : ConsoleColor.Gray);
        if (value.Length > 0)
        {
            var shown = Fit(value, MenuWidth - label.Length - 4); // same truncation as Dots
            Put(row, left + text.Length - shown.Length, shown, valueColor);
        }
    }

    private void ReloadDeclines()
    {
        var v = DeclineLog.Version;
        if (v == _declinesVersion) return;
        _declinesVersion = v;
        _declines = DeclineLog.ReadAll();
    }

    // ---------- Text helpers ----------

    /// <summary>"Label ........ Value" padded to the menu width, with at least two dots.</summary>
    private static string Dots(string label, string value, int width = MenuWidth)
    {
        if (value.Length == 0) return label;
        value = Fit(value, width - label.Length - 4);
        return $"{label} {new string('.', Math.Max(2, width - label.Length - value.Length - 2))} {value}";
    }

    private static string Fit(string s, int max) =>
        max <= 0 ? "" : s.Length <= max ? s : max <= 3 ? s[..max] : s[..(max - 3)] + "...";

    // ---------- Frame buffer ----------

    private void Put(int row, int col, string text, ConsoleColor color = ConsoleColor.Gray)
    {
        if (row < 0 || row >= _h) return;
        for (var i = 0; i < text.Length; i++)
        {
            var c = col + i;
            if (c < 0 || c >= _w) continue;
            _chars[row][c] = text[i];
            _colors[row][c] = color;
        }
    }

    private void Center(int row, string text, ConsoleColor color = ConsoleColor.Gray) =>
        Put(row, (_w - text.Length) / 2, text, color);

    /// <summary>Writes the rows that differ from what's on screen. A size change triggers a full redraw.</summary>
    private void Flush()
    {
        var full = _shownChars.Length != _h || (_h > 0 && _shownChars[0].Length != _w);
        try
        {
            if (full) Console.Clear();
            Console.CursorVisible = false;
            for (var r = 0; r < _h; r++)
            {
                if (!full && _chars[r].AsSpan().SequenceEqual(_shownChars[r]) && _colors[r].AsSpan().SequenceEqual(_shownColors[r]))
                    continue;

                // Never write the bottom-right cell: that scrolls the console.
                var len = r == _h - 1 ? _w - 1 : _w;
                Console.SetCursorPosition(0, r);
                var start = 0;
                while (start < len)
                {
                    var color = _colors[r][start];
                    var end = start;
                    while (end < len && _colors[r][end] == color) end++;
                    Console.ForegroundColor = color;
                    Console.Write(_chars[r], start, end - start);
                    start = end;
                }
            }
            Console.ResetColor();
            _shownChars = _chars;
            _shownColors = _colors;
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException)
        {
            // Window resized mid-draw; redraw everything next frame.
            _shownChars = Array.Empty<char[]>();
        }
    }
}
