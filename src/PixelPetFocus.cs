// PixelPet Focus: a desktop pet that closes your doomscrolling.
// One raw Win32 layered window + GDI, no WinForms/WebView, so it stays in single-digit MB.
// Same brain as BadKat/FocusCat by X-DIABLO-X (MIT), rewritten natively: watch the foreground window (title, process, browser URL
// via UI Automation), and when a rule's grace runs out the pet walks over and closes the tab.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

class Rule { public string Label; public int Grace; public string[] Any; }

class Cfg
{
    public int Countdown = 3, Snooze = 5, Focus = 25, Break = 5;
    public bool Nag, Wander = true, Sleepy = true, Typing = true, EnterRope = true, Audio = true, Clipboard = true, Hotkey = true;
    public double Size = 1;
    public string[] Never = new string[0];
    public Rule[] Rules = new Rule[0];
}

class Reminder { public string Line, Kind, Msg; public DateTime At, Next; public int Every, From = -1, To = -1; }

class Bust { public IntPtr Hwnd; public string Title, Proc, Label, Scold; public int CenterX; }

class Part { public double X, Y, VY, Life; public string Text; }

static class App
{
    // ------------------------------------------------------------------ settings file
    const string DefaultRules =
@"# PixelPet Focus rules. Saved changes apply within a second (size needs a restart).
countdown = 3      # seconds of warning before it closes the tab
snooze = 5         # minutes a click on the pet buys you
mode = close       # close | nag  (nag only complains, never closes)
wander = yes       # walk around, jump on windows
sleepy = yes       # take naps
typing = yes       # pulls out a laptop while you type (never records keys)
enterrope = yes    # throws a rope at your caret/cursor when you press Enter
clipboard = yes    # notices useful copied text (times, links, sums); ignores passwords
hotkey = yes       # Ctrl+Alt+R opens the clipboard menu (restart to apply)
audio = yes        # wears headphones when they're connected; dances to music, takes notes in class/calls
size = 1           # pet size multiplier (restart)
focus = 25         # focus timer minutes
break = 5          # break minutes

# Never touch: any window containing one of these is left alone
never | zoom meeting, microsoft teams, google meet

# label | seconds allowed | phrases matched against URL, title or process (any one)
# First match wins: keep specific rules above general ones.
YouTube Shorts  | 6   | youtube.com/shorts
Instagram Reels | 6   | instagram.com/reel
TikTok          | 6   | tiktok.com
Facebook Reels  | 10  | facebook.com/reel, facebook.com/watch, fb.watch
Snapchat        | 15  | snapchat.com
Streaming       | 20  | netflix.com, netflix, primevideo.com, prime video, hotstar, disneyplus.com, disney+, crunchyroll, hulu.com, jiocinema, sonyliv, zee5, mxplayer, peacocktv.com, tv.apple.com
Instagram       | 45  | instagram.com
Reddit          | 90  | reddit.com
YouTube         | 240 | youtube.com/watch, - youtube
";

    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "chromium", "librewolf", "zen", "opera_gx" };
    static readonly string[] Scolds = { "Shorts. Again.", "That's enough of that.", "Closing this one.", "You said you'd stop.", "Nope." };

    static string Dir, RulesPath, ProgressPath, RemPath;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    const string DefaultReminders =
@"# PixelPet Focus reminders, one per line:   when | message
# Saved changes apply within a second. Remove the # to turn an example on.
# Recurring reminders pause while you are away, wait for your break during a focus
# session, and stay quiet during presentations and fullscreen apps.
#
# every 45m | Drink some water
# every 20m 09:00-18:00 | 20-20-20: look at something 20 feet away
# daily 13:00 | Lunch break
# weekdays 09:25 | Standup
#
# One-shot reminders look like the line below. Copy text like ""call mom in 20 min""
# and click the pet: it adds these for you and deletes them when you click Done.
# 2026-09-18 17:40 | Call mom
";

    static Cfg Parse(string[] lines)
    {
        var c = new Cfg(); var rules = new List<Rule>();
        foreach (var raw in lines)
        {
            string line = raw; int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.Contains("|"))
            {
                var p = line.Split('|');
                var pats = List(p[p.Length - 1]);
                if (p[0].Trim().ToLowerInvariant() == "never") c.Never = pats;
                else if (p.Length >= 3) { int g; if (int.TryParse(p[1].Trim(), out g) && pats.Length > 0) rules.Add(new Rule { Label = p[0].Trim(), Grace = g, Any = pats }); }
            }
            else if (line.Contains("="))
            {
                int eq = line.IndexOf('=');
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim().ToLowerInvariant();
                int n; int.TryParse(v, out n); bool yes = v == "yes" || v == "true" || v == "on";
                switch (k)
                {
                    case "countdown": c.Countdown = Math.Max(0, n); break;
                    case "snooze": c.Snooze = Math.Max(1, n); break;
                    case "focus": c.Focus = Math.Max(1, n); break;
                    case "break": c.Break = Math.Max(1, n); break;
                    case "mode": c.Nag = v == "nag"; break;
                    case "wander": c.Wander = yes; break;
                    case "sleepy": c.Sleepy = yes; break;
                    case "typing": c.Typing = yes; break;
                    case "enterrope": c.EnterRope = yes; break;
                    case "clipboard": c.Clipboard = yes; break;
                    case "hotkey": c.Hotkey = yes; break;
                    case "audio": c.Audio = yes; break;
                    case "size": double d; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) c.Size = Math.Max(0.5, Math.Min(4, d)); break;
                }
            }
        }
        c.Rules = rules.ToArray();
        return c;
    }

    static string[] List(string s)
    {
        var o = new List<string>();
        foreach (var x in s.Split(',')) { var t = x.Trim().ToLowerInvariant(); if (t.Length > 0) o.Add(t); }
        return o.ToArray();
    }

    static Rule Match(Cfg c, string url, string title, string proc)
    {
        if (title.Length == 0 && url.Length == 0) return null;
        string hay = (url + " | " + title + " | " + proc).ToLowerInvariant();
        foreach (var n in c.Never) if (hay.Contains(n)) return null;
        foreach (var r in c.Rules) foreach (var p in r.Any) if (hay.Contains(p)) return r;
        return null;
    }

    // Browsers prepend unread counts "(8) " and tick them mid-countdown; that must not look like a new page.
    static string StripBadge(string t)
    {
        t = t.TrimStart();
        if (t.StartsWith("("))
        {
            int close = t.IndexOf(')');
            if (close > 1)
            {
                bool digits = true;
                for (int i = 1; i < close; i++) if (!char.IsDigit(t[i])) digits = false;
                if (digits) return t.Substring(close + 1).TrimStart();
            }
        }
        return t;
    }

    static int Cost(int level) { return Math.Max(5, (int)(Math.Round(50 * Math.Pow(1.28, level - 1) / 5) * 5)); }

    // ------------------------------------------------------------------ state shared with the watcher thread
    static readonly object Sync = new object();
    static volatile Cfg cfg;
    static volatile bool patrol = true;
    static DateTime snoozeUntil, cooldownUntil;
    static Bust pendingBust;
    static double watchRemaining = -1;
    static int watchX;

    // ------------------------------------------------------------------ pet state (UI thread only)
    const int SIT = 0, WALK = 1, SLEEP = 2, AIR = 3, HELD = 4, RUN = 5, GLARE = 6, SWIPE = 7, TYPE = 8;
    static IntPtr hwnd, mdc;
    static int W, H, U; static double S;
    static RECT wa;
    static double x, y, vx, vy, walkTarget; static int facing = 1;
    static int state; static double stateT, stateDur = 2, animT;
    static IntPtr perch, ignorePerch; static RECT perchRect;
    static double joyT, angryT, sqT, sqK, blinkT = 3, blinkOn, zT;
    static string bubble; static double bubbleT;
    static int lookX, lookY;
    static bool pressed, held, hidden; static POINT downPt; static double grabDx, grabDy;
    static Bust alert; static int glareLeft; static double glareTick; static bool acted;
    static readonly List<Part> parts = new List<Part>();
    static readonly Random rnd = new Random();
    static int winLeft = int.MinValue, winTop = int.MinValue, lastTick;
    static int level = 1, xp; static DateTime lastPat;
    static int focusPhase; static DateTime focusEnd;   // 0 off, 1 focus, 2 break
    static double lapOpen, lastKeyAgo = 99, keyBurst, codeT; static uint lastInput; static POINT lastCur;
    static IntPtr ropeWnd; static double ropeT = -1, ropeTx, ropeTy, ropeCool; static bool enterDown;
    static POINT[] ropeStar; static int ropeW, ropeH;
    // written by the audio thread, read by the UI thread
    static volatile bool headphones; static volatile int audioKind; static volatile float audioPeak; static volatile string audioDevice = "";
    static bool wasPhones; static int lastKind; static double noteT, danceLevel;
    static double chargeT; static int batteryPct = -1, lowWarned = 100, powerKnown; static bool onAC;
    static List<Reminder> rems = new List<Reminder>(); static DateTime remStamp; static Reminder sign; static string signText;
    static double signT; static bool pauseRecurring, bubbleSign;
    static string clipText, clipMsg; static DateTime clipWhen; static double clipOfferT; static uint ownSeq, fmtExclude, fmtHistory, fmtIgnore;
    static int clipRetry, gcSec; static bool hintShown; static string tagCache; static IntPtr trayIcon;
    static readonly int[] Legs = { 3, 5, 8, 10 };
    static readonly StringBuilder clsBuf = new StringBuilder(64);
    static readonly string[] Codes = { "{ }", "01", ";", "</>", "#", "=>", "()" };
    static double secAcc;

    const int CUP = 0xC8D25A, CUPDARK = 0x8C9637, PAPER = 0xF0F5F5, INK = 0xB4AAA0;
    const int ROPE = 0x325A8C, STAR = 0x30D8FF, GREEN = 0x50C850, SIGN = 0x9CF0FF;
    const int LID = 0x4E4646, BASE = 0x322D2D, LOGO = 0xB48C64, GLOW = 0xFFDC8C;
    const int KEY = 0xFF00FF, CORAL = 0xB6C42E, ANGRY = 0x4058E8, EYE = 0x141414, DARK = 0x282828, WHITE = 0xFFFFFF, PINK = 0x875FFF;
    static readonly Dictionary<int, IntPtr> brushes = new Dictionary<int, IntPtr>();
    static IntPtr fontBubble, fontSmall, penDark;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") { Environment.Exit(SelfTest()); }
        bool created; var mutex = new Mutex(true, "PixelPetFocus.Single", out created);
        if (!created) return;

        SetProcessDPIAware();
        S = GetDpiForSystem() / 96.0;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Dir = Path.Combine(appData, "PixelPet Focus");
        try { if (!Directory.Exists(Dir) && Directory.Exists(Path.Combine(appData, "FocusCat Pixel"))) Directory.Move(Path.Combine(appData, "FocusCat Pixel"), Dir); } catch { }   // keep XP from the pre-rename builds
        Directory.CreateDirectory(Dir);
        RulesPath = Path.Combine(Dir, "rules.txt");
        ProgressPath = Path.Combine(Dir, "progress.txt");
        RemPath = Path.Combine(Dir, "reminders.txt");
        if (!File.Exists(RulesPath)) File.WriteAllText(RulesPath, DefaultRules);
        cfg = Parse(File.ReadAllLines(RulesPath));
        try { var p = File.ReadAllText(ProgressPath).Split(' '); level = Math.Max(1, int.Parse(p[0])); xp = int.Parse(p[1]); } catch { }

        U = Math.Max(2, (int)Math.Round(5 * S * cfg.Size));
        W = Math.Max((int)(260 * S), 22 * U);
        H = 12 * U + (int)(100 * S);                                             // room for a 3-line reminder sign
        SystemParametersInfo(0x30, 0, ref wa, 0);
        x = (wa.L + wa.R) / 2; y = wa.B;

        var wc = new WNDCLASSEX();
        wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procKeep);
        wc.hInstance = GetModuleHandle(null);
        wc.hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32649);   // hand
        wc.lpszClassName = "PixelPetFocus";
        RegisterClassEx(ref wc);
        // layered | topmost | toolwindow (no taskbar button) | noactivate (never steal focus: Ctrl+W must hit the browser)
        hwnd = CreateWindowEx(0x80000 | 0x8 | 0x80 | 0x8000000, "PixelPetFocus", "PixelPet Focus", 0x80000000, 0, 0, W, H, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        SetLayeredWindowAttributes(hwnd, KEY, 0, 1);
        // the Enter rope: a click-through window cut to the rope's shape, so no screen-sized bitmap is ever held
        ropeWnd = CreateWindowEx(0x80000 | 0x20 | 0x8 | 0x80 | 0x8000000, "PixelPetFocus", "", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        SetLayeredWindowAttributes(ropeWnd, 0, 255, 2);

        IntPtr screen = GetDC(IntPtr.Zero);
        mdc = CreateCompatibleDC(screen);
        SelectObject(mdc, CreateCompatibleBitmap(screen, W, H));
        ReleaseDC(IntPtr.Zero, screen);
        fontBubble = CreateFont(-(int)(13 * S), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        fontSmall = CreateFont(-(int)(13 * S), 0, 0, 0, 800, 0, 0, 0, 1, 0, 0, 3, 0, "Segoe UI");  // non-antialiased: no pink fringe on the key colour
        penDark = CreatePen(0, Math.Max(1, (int)(2 * S)), DARK);
        SetBkMode(mdc, 1);

        Tray(0);
        LoadReminders();
        fmtExclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");   // set by password managers
        fmtHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        fmtIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
        AddClipboardFormatListener(hwnd);
        if (cfg.Hotkey && !RegisterHotKey(hwnd, 1, 0x4003, 0x52)) Say("Ctrl+Alt+R is taken by another app.", 3);   // CTRL|ALT|NOREPEAT, R
        lastTick = Environment.TickCount;
        SetTimer(hwnd, (IntPtr)1, 33, IntPtr.Zero);
        var t = new Thread(Watch); t.IsBackground = true; t.Start();
        var at = new Thread(AudioWatch); at.IsBackground = true; at.Start();
        ShowWindow(hwnd, 4);
        if (bubbleT <= 0) Say("Hi! I'll keep you focused.", 3);
        joyT = 2;

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        Tray(2);
        GC.KeepAlive(mutex);
    }

    static readonly WndProcD procKeep = Proc;
    const uint WM_TRAY = 0x8001;

    static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case 0x0113: Tick(); return IntPtr.Zero;                   // WM_TIMER
            case 0x0021: return (IntPtr)3;                             // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            case 0x0014: return (IntPtr)1;                             // WM_ERASEBKGND
            case 0x000F: if (h == ropeWnd) PaintRope(); else Render(); break;   // WM_PAINT (DefWindowProc validates)
            case 0x0201:                                               // WM_LBUTTONDOWN
                GetCursorPos(out downPt); pressed = true; grabDx = x - downPt.X; grabDy = y - downPt.Y; SetCapture(hwnd);
                return IntPtr.Zero;
            case 0x0200:                                               // WM_MOUSEMOVE
                if (pressed && !held)
                {
                    POINT c; GetCursorPos(out c);
                    if (Math.Abs(c.X - downPt.X) + Math.Abs(c.Y - downPt.Y) > 6 * S) StartHold();
                }
                return IntPtr.Zero;
            case 0x0202: LeftUp(); return IntPtr.Zero;                 // WM_LBUTTONUP
            case 0x0205: Menu(); return IntPtr.Zero;                   // WM_RBUTTONUP
            case WM_TRAY:
                int e = (int)l;
                if (e == 0x0205) Menu(); else if (e == 0x0203) ToggleHidden();
                return IntPtr.Zero;
            case 0x031D: clipRetry = 3; return IntPtr.Zero;              // WM_CLIPBOARDUPDATE: read on the next tick
            case 0x0312: ClipMenu(); return IntPtr.Zero;                 // WM_HOTKEY
            case 0x0002: if (h == hwnd) PostQuitMessage(0); return IntPtr.Zero;   // WM_DESTROY
        }
        return DefWindowProc(h, m, w, l);
    }

    // ------------------------------------------------------------------ the brain, 30 times a second
    static void Tick()
    {
        int now = Environment.TickCount;
        double dt = Math.Min(0.1, (now - lastTick) / 1000.0); lastTick = now;
        animT += dt; stateT += dt;
        chargeT = Math.Max(0, chargeT - dt);
        clipOfferT -= dt;
        joyT = Math.Max(0, joyT - dt); angryT = Math.Max(0, angryT - dt); sqT = Math.Max(0, sqT - dt); bubbleT -= dt;
        blinkT -= dt; blinkOn -= dt;
        if (blinkT <= 0) { blinkOn = 0.13; blinkT = Rand(2, 6); }

        secAcc += dt;
        if (secAcc >= 1)
        {
            secAcc = 0;
            SystemParametersInfo(0x30, 0, ref wa, 0);                           // taskbar moved / resolution changed
            SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, 0, 0, 0x13);                 // stay above the taskbar
            FocusTimer();
            Power();
            Reminders();
            tagCache = null;
            if (focusPhase != 0)
            {
                var left = focusEnd - DateTime.Now; if (left.Ticks < 0) left = TimeSpan.Zero;
                tagCache = (focusPhase == 1 ? "Focus " : "Break ") + (int)left.TotalMinutes + ":" + left.Seconds.ToString("00");
            }
            if (++gcSec % 10 == 0) GC.Collect();   // .NET otherwise lets short-lived garbage pile up for MBs before collecting
        }

        Bust b; double wr; int wx;
        lock (Sync) { b = pendingBust; pendingBust = null; wr = watchRemaining; wx = watchX; }
        if (b != null && alert == null && state != HELD) BeginAlert(b);
        bool eyeing = alert == null && wr > 0 && wr <= 5;                       // it notices before it acts
        if (eyeing && state == WALK) SetState(SIT, 2);

        if (clipRetry > 0) ReadClipboard(true);
        POINT c; GetCursorPos(out c);
        DetectTyping(c, dt);
        bool enter = (GetAsyncKeyState(0x0D) & 0x8000) != 0;
        ropeCool -= dt;
        if (enter && !enterDown && cfg.EnterRope && !hidden && alert == null && ropeT < 0 && ropeCool <= 0
            && (state == SIT || state == WALK || state == SLEEP || state == TYPE)) ThrowRope();
        enterDown = enter;
        if (ropeT >= 0 && (ropeT += dt) > 0.6) { ropeT = -1; ShowWindow(ropeWnd, 0); }
        bool phones = cfg.Audio && headphones; int kind = cfg.Audio ? audioKind : 0;
        if (phones != wasPhones)
        {
            wasPhones = phones;
            if (alert == null) { Say(phones ? "Headphones on!" : "Headphones off.", 2.2); if (phones) { joyT = 1.5; Hearts(2); } }
        }
        if (kind != lastKind)
        {
            lastKind = kind;
            if (alert == null && kind == 1) Say("Ooh, music!", 2);
            else if (alert == null && kind == 2) { Say("Class time. Taking notes.", 2.5); if (state == WALK || state == SLEEP) SetState(SIT, 6); }
        }
        danceLevel = danceLevel * 0.55 + (kind == 1 ? audioPeak : 0) * 0.45;       // fast attack: bounces on the beat
        if (kind == 1 && audioPeak > 0.05 && (state == SIT || state == TYPE || state == WALK) && (noteT -= dt) <= 0)
        {
            noteT = Rand(0.6, 1.2);
            parts.Add(new Part { X = x + Rand(-7, 6) * U, Y = y - 11 * U, VY = -35 * S, Life = 1.5, Text = rnd.Next(2) == 0 ? "\u266A" : "\u266B" });
        }
        if (keyBurst >= 3 && cfg.Typing && alert == null && (state == SIT || state == WALK || state == SLEEP)) SetState(TYPE, 0);
        lapOpen = state != TYPE ? 0 : Clamp(lapOpen + (lastKeyAgo <= 2.5 ? dt : -dt) / 0.3, 0, 1);
        double lx = c.X, ly = c.Y;
        if (alert != null) { lx = alert.CenterX; ly = y - 50 * U; }
        else if (eyeing) { lx = wx; ly = y - 50 * U; }
        bool near = alert != null || eyeing || Math.Abs(lx - x) + Math.Abs(ly - y) < 450 * S;
        lookX = !near ? (state == WALK ? facing : 0) : lx > x + 4 * U ? 1 : lx < x - 4 * U ? -1 : 0;
        lookY = near && ly < y - 12 * U ? -1 : 0;
        if (state == TYPE) lookX = lookY = 0;                                   // eyes on the screen

        Ride();
        switch (state)
        {
            case SIT:
                if (stateT >= stateDur && sign == null) ChooseNext();          // a reminder sign keeps it put
                break;
            case WALK:
                if (Step(walkTarget, 55 * S * Math.Max(0.5, cfg.Size), dt)) SetState(SIT, Rand(2, 6));
                break;
            case SLEEP:
                zT -= dt;
                if (zT <= 0) { zT = 1.3; parts.Add(new Part { X = x + facing * 5 * U, Y = y - 7 * U, VY = -22 * S, Life = 1.8, Text = "z" }); }
                if (stateT >= stateDur) SetState(SIT, 2);
                break;
            case AIR: Physics(dt); break;
            case TYPE:
                if (lastKeyAgo > 2.5 && lapOpen <= 0) SetState(SIT, Rand(1.5, 3));
                else if (lastKeyAgo < 0.4 && lapOpen >= 1 && (codeT -= dt) <= 0)
                {
                    codeT = Rand(0.5, 1.1);
                    parts.Add(new Part { X = x + Rand(-4, 3) * U, Y = y - 8 * U, VY = -30 * S, Life = 1.2, Text = Codes[rnd.Next(Codes.Length)] });
                }
                break;
            case HELD:
                double px = x, py = y;
                x = Clamp(c.X + grabDx, wa.L + 8 * U, wa.R - 8 * U);
                y = Clamp(c.Y + grabDy, wa.T + 10 * U, wa.B);
                if (dt > 0) { vx = vx * 0.5 + (x - px) / dt * 0.5; vy = vy * 0.5 + (y - py) / dt * 0.5; }
                break;
            case RUN:
                double target = alert.CenterX == int.MinValue || !cfg.Wander ? x : Clamp(alert.CenterX, MinX(), MaxX());
                if (Step(target, 240 * S, dt)) StartGlare();
                break;
            case GLARE:
                if (cfg.Nag) { if (stateT >= 2.6) EndAlert(1); break; }
                glareTick += dt;
                if (glareTick >= 1)
                {
                    glareTick -= 1; glareLeft--;
                    if (glareLeft <= 0) { SetState(SWIPE, 0); acted = false; }
                    else Say(alert.Scold + "  " + glareLeft + "s", 99);
                }
                break;
            case SWIPE:
                if (!acted && stateT >= 0.3)
                {
                    acted = true; string why;
                    if (Act(alert, out why))
                    {
                        Say("Closed. Back to work!", 2.4); joyT = 2.4;
                        lock (Sync) cooldownUntil = DateTime.Now.AddSeconds(12);
                        Award(25);
                    }
                    else Say(why, 2.4);
                }
                if (stateT >= 0.7) EndAlert(2.4);
                break;
        }

        for (int i = parts.Count - 1; i >= 0; i--)
        {
            var p = parts[i]; p.Y += p.VY * dt; p.Life -= dt;
            if (p.Life <= 0 || parts.Count > 40) parts.RemoveAt(i);
        }

        int wl = Clamp((int)Math.Round(x) - W / 2, wa.L, wa.R - W);
        int wt = (int)Math.Round(y) + U - H;
        if (wl != winLeft || wt != winTop) { SetWindowPos(hwnd, IntPtr.Zero, wl, wt, 0, 0, 0x15); winLeft = wl; winTop = wt; }
        if (ropeT >= 0) DrawRope();
        Render();
    }

    // Enter: lasso the spot you just typed at. The text caret when the app exposes one, else the mouse.
    static void ThrowRope()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == hwnd) return;
        POINT t; GetCursorPos(out t);
        uint pid; var gti = new GUITHREADINFO(); gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        if (GetGUIThreadInfo(GetWindowThreadProcessId(fg, out pid), ref gti) && gti.hwndCaret != IntPtr.Zero)
        {
            POINT p; p.X = gti.rcCaret.L; p.Y = (gti.rcCaret.T + gti.rcCaret.B) / 2;
            if (ClientToScreen(gti.hwndCaret, ref p)) t = p;
        }
        ropeTx = t.X; ropeTy = t.Y; ropeT = 0; ropeCool = 1.2;
        facing = ropeTx >= x ? 1 : -1;
        if (state == SLEEP || state == WALK) SetState(SIT, 2);
    }

    static void DrawRope()
    {
        double t = ropeT, e = t < 0.22 ? t / 0.22 : t < 0.38 ? 1 : Math.Max(0, 1 - (t - 0.38) / 0.22);
        int dy = state == TYPE || state == SLEEP ? 2 : 0;
        double hx = x + facing * 6 * U, hy = y - (9 - dy) * U;                  // the raised paw
        double tx = hx + (ropeTx - hx) * e, ty = hy + (ropeTy - hy) * e;
        double dist = Math.Sqrt((tx - hx) * (tx - hx) + (ty - hy) * (ty - hy));
        if (dist < 3) { ShowWindow(ropeWnd, 0); return; }
        double sag = Math.Min(90 * S, dist * 0.25) * (t < 0.22 ? 1 - e * 0.8 : 0.15);
        double mx = (hx + tx) / 2, my = (hy + ty) / 2 + sag, th = Math.Max(2, U * 0.45);
        const int N = 16;
        var bx = new double[N + 1]; var bY = new double[N + 1];
        for (int i = 0; i <= N; i++)
        {
            double u = (double)i / N, a = (1 - u) * (1 - u), b = 2 * u * (1 - u), c = u * u;
            bx[i] = a * hx + b * mx + c * tx; bY[i] = a * hy + b * my + c * ty;
        }
        bool impact = t >= 0.2 && t < 0.48;
        double r = impact ? U * (2.5 + 1.5 * Math.Sin((t - 0.2) / 0.28 * Math.PI)) : 0;
        double minX = tx - r, minY = ty - r, maxX = tx + r, maxY = ty + r;
        for (int i = 0; i <= N; i++) { minX = Math.Min(minX, bx[i]); maxX = Math.Max(maxX, bx[i]); minY = Math.Min(minY, bY[i]); maxY = Math.Max(maxY, bY[i]); }
        int ox = (int)(minX - th - 2), oy = (int)(minY - th - 2);
        ropeW = (int)(maxX + th + 3) - ox; ropeH = (int)(maxY + th + 3) - oy;

        var pts = new POINT[2 * (N + 1)];
        for (int i = 0; i <= N; i++)
        {
            double dx = bx[Math.Min(i + 1, N)] - bx[Math.Max(i - 1, 0)], dyy = bY[Math.Min(i + 1, N)] - bY[Math.Max(i - 1, 0)];
            double len = Math.Max(0.001, Math.Sqrt(dx * dx + dyy * dyy)), nx = -dyy / len * th / 2, ny = dx / len * th / 2;
            pts[i].X = (int)(bx[i] + nx) - ox; pts[i].Y = (int)(bY[i] + ny) - oy;
            pts[2 * N + 1 - i].X = (int)(bx[i] - nx) - ox; pts[2 * N + 1 - i].Y = (int)(bY[i] - ny) - oy;
        }
        IntPtr rgn = CreatePolygonRgn(pts, pts.Length, 2);
        ropeStar = null;
        if (impact)
        {
            ropeStar = new POINT[16];
            for (int i = 0; i < 16; i++)
            {
                double ang = i * Math.PI / 8 + t * 6, rr = i % 2 == 0 ? r : r * 0.45;
                ropeStar[i].X = (int)(tx + Math.Cos(ang) * rr) - ox; ropeStar[i].Y = (int)(ty + Math.Sin(ang) * rr) - oy;
            }
            IntPtr star = CreatePolygonRgn(ropeStar, 16, 2);
            CombineRgn(rgn, rgn, star, 2);
            DeleteObject(star);
        }
        SetWindowRgn(ropeWnd, rgn, false);                                       // the system owns rgn now
        SetWindowPos(ropeWnd, (IntPtr)(-1), ox, oy, ropeW, ropeH, 0x10 | 0x40);  // NOACTIVATE | SHOWWINDOW
        PaintRope();
    }

    static void PaintRope()
    {
        IntPtr dc = GetDC(ropeWnd);
        var all = new RECT { R = ropeW, B = ropeH };
        FillRect(dc, ref all, Brush(ROPE));
        if (ropeStar != null) { SelectObject(dc, Brush(STAR)); SelectObject(dc, penDark); Polygon(dc, ropeStar, 16); }
        ReleaseDC(ropeWnd, dc);
    }

    // Typing without a keyboard hook and without ever knowing WHICH key: new input arrived, the mouse
    // did not move, and some text-ish key is down right now. Only feeds a "user is typing" level.
    static void DetectTyping(POINT c, double dt)
    {
        lastKeyAgo += dt; keyBurst = Math.Max(0, keyBurst - dt * 1.5);
        bool mouseStill = c.X == lastCur.X && c.Y == lastCur.Y; lastCur = c;
        var li = new LASTINPUTINFO(); li.cbSize = 8;
        if (!GetLastInputInfo(ref li) || li.dwTime == lastInput) return;
        lastInput = li.dwTime;
        if (!mouseStill) return;
        for (int vk = 0x08; vk <= 0xDE; vk++)
        {
            bool texty = vk == 0x08 || vk == 0x0D || vk == 0x20 || (vk >= 0x30 && vk <= 0x5A) || (vk >= 0x60 && vk <= 0x6F) || vk >= 0xBA;
            if (texty && (GetAsyncKeyState(vk) & 0x8000) != 0) { lastKeyAgo = 0; keyBurst += 1; return; }
        }
    }

    static void SetState(int s, double dur) { state = s; stateT = 0; stateDur = dur; }
    static void Say(string text, double seconds) { bubble = text; bubbleT = seconds; bubbleSign = false; }
    static double Rand(double a, double b) { return a + rnd.NextDouble() * (b - a); }
    static double Clamp(double v, double a, double b) { return v < a ? a : v > b ? b : v; }
    static int Clamp(int v, int a, int b) { return v < a ? a : v > b ? b : v; }
    static double MinX() { return perch != IntPtr.Zero ? perchRect.L + 7 * U : wa.L + 8 * U; }
    static double MaxX() { return perch != IntPtr.Zero ? perchRect.R - 7 * U : wa.R - 8 * U; }

    static bool Step(double target, double speed, double dt)
    {
        double d = target - x;
        if (Math.Abs(d) <= speed * dt) { x = target; return true; }
        facing = d > 0 ? 1 : -1; x += facing * speed * dt;
        return false;
    }

    static void ChooseNext()
    {
        double r = rnd.NextDouble();
        if (lastKind == 2 || (lastKind == 1 && r < 0.6)) { SetState(SIT, Rand(4, 8)); return; }   // class: don't distract
        if (perch != IntPtr.Zero)
        {
            if (r < 0.35 && cfg.Wander) Walk(Rand(MinX(), MaxX()));
            else if (r < 0.5 && cfg.Sleepy) SetState(SLEEP, Rand(12, 30));
            else if (r < 0.8) HopDown();
            else SetState(SIT, Rand(2, 5));
            return;
        }
        if (r < 0.35 && cfg.Wander) Walk(Rand(MinX(), MaxX()));
        else if (r < 0.5 && cfg.Sleepy) SetState(SLEEP, Rand(12, 30));
        else if (r < 0.72 && cfg.Wander && JumpOntoWindow()) { }
        else if (r < 0.87 && cfg.Wander) { POINT c; GetCursorPos(out c); Walk(c.X); }   // come see what you're doing
        else if (r < 0.95) Hop(0, -520 * S);
        else SetState(SIT, Rand(2, 5));
    }

    static void Walk(double t) { walkTarget = Clamp(t, MinX(), MaxX()); SetState(WALK, 0); }
    static void Hop(double hvx, double hvy) { vx = hvx; vy = hvy; perch = IntPtr.Zero; SetState(AIR, 0); }
    static void HopDown()
    {
        ignorePerch = perch;
        Hop((x < (wa.L + wa.R) / 2 ? 1 : -1) * 160 * S, -380 * S);
    }

    // ------------------------------------------------------------------ physics: throws, falls, window tops
    static void Physics(double dt)
    {
        double prevY = y, g = 2600 * S;
        vy += g * dt; x += vx * dt; y += vy * dt;
        double minX = wa.L + 8 * U, maxX = wa.R - 8 * U;
        if (x < minX) { x = minX; vx = -vx * 0.5; }
        if (x > maxX) { x = maxX; vx = -vx * 0.5; }
        if (y < wa.T + 10 * U) { y = wa.T + 10 * U; if (vy < 0) vy = 0; }
        if (vy <= 0) return;

        // sample just below our own window so we see what is underneath the feet
        IntPtr h = Root((int)x, (int)y + U + 2); RECT r;
        if (h != hwnd && h != ignorePerch && Ledge(h, out r) && r.T >= prevY - 1 && r.T <= y + U + 2 && x > r.L + 4 * U && x < r.R - 4 * U)
        {
            y = r.T; perch = h; perchRect = r; Land();
            return;
        }
        if (y >= wa.B) { y = wa.B; perch = IntPtr.Zero; ignorePerch = IntPtr.Zero; Land(); }
    }

    static void Land()
    {
        sqK = Math.Min(1, vy / (1400 * S)); sqT = 0.25; vx = 0; vy = 0;
        if (alert != null) { if (perch != IntPtr.Zero) HopDown(); else SetState(RUN, 0); }
        else SetState(SIT, Rand(1.5, 4));
    }

    // Riding a window: move with it, fall off when it is minimised, maximised, closed or covered.
    static void Ride()
    {
        if (perch == IntPtr.Zero || state == AIR || state == HELD) return;
        RECT r;
        if (!IsWindowVisible(perch) || IsIconic(perch) || IsZoomed(perch) || !Frame(perch, out r)) { Fall(); return; }
        x += r.L - perchRect.L; y = r.T; perchRect = r;
        if (x < r.L + 2 * U || x > r.R - 2 * U || Root((int)x, r.T + U + 3) != perch) Fall();
    }

    static void Fall() { perch = IntPtr.Zero; vx = 0; vy = 0; SetState(AIR, 0); }

    static bool JumpOntoWindow()
    {
        IntPtr h = GetForegroundWindow(); RECT r;
        if (h == IntPtr.Zero || h == hwnd || !Ledge(h, out r)) return false;
        double tx = Clamp(x, r.L + 10 * U, r.R - 10 * U), dy = r.T - y, g = 2600 * S;
        if (dy > -60 * S || Root((int)tx, r.T + U + 3) != h) return false;       // too low, or that spot is covered
        double T = Math.Max(0.7, Math.Sqrt(2 * -dy / g) * 1.25);
        ignorePerch = IntPtr.Zero;
        Hop((tx - x) / T, (dy - 0.5 * g * T * T) / T);
        if (vx != 0) facing = vx > 0 ? 1 : -1;
        return true;
    }

    static bool Ledge(IntPtr h, out RECT r)
    {
        r = new RECT();
        if (h == IntPtr.Zero || !IsWindowVisible(h) || IsZoomed(h) || IsIconic(h) || !Frame(h, out r)) return false;
        if (r.T < wa.T + 30 * S || r.T > wa.B - 80 * S || r.R - r.L < 160 * S) return false;
        clsBuf.Length = 0; GetClassName(h, clsBuf, 64);
        string cls = clsBuf.ToString();
        return cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd" && cls != "Progman" && cls != "WorkerW";
    }

    static IntPtr Root(int px, int py) { POINT p; p.X = px; p.Y = py; return GetAncestor(WindowFromPoint(p), 2); }

    static bool Frame(IntPtr h, out RECT r)
    {
        // the visible frame, without Windows 10/11's invisible resize borders
        if (DwmGetWindowAttribute(h, 9, out r, 16) == 0) return true;
        return GetWindowRect(h, out r);
    }

    // ------------------------------------------------------------------ interaction
    static void StartHold()
    {
        held = true; perch = IntPtr.Zero; ignorePerch = IntPtr.Zero; vx = vy = 0;
        if (alert != null) { alert = null; bubbleT = 0; }
        SetState(HELD, 0);
        Say("Wheee!", 1);
    }

    static void LeftUp()
    {
        ReleaseCapture();
        if (!pressed) return;
        pressed = false;
        if (held)
        {
            held = false;
            vx = Clamp(vx, -2500 * S, 2500 * S); vy = Clamp(vy, -2500 * S, 2500 * S);
            SetState(AIR, 0);
            return;
        }
        if (alert != null && (state == RUN || state == GLARE))
        {
            int mins = cfg.Snooze;
            lock (Sync) snoozeUntil = DateTime.Now.AddMinutes(mins);
            alert = null; joyT = 1.2;
            Say("Fine. " + mins + " minutes.", 2.5);
            SetState(SIT, 2.5);
            return;
        }
        if (state == SWIPE) return;
        if (sign != null) { ReminderDone(); return; }
        if (clipOfferT > 0 && clipText != null) { ClipMenu(); return; }
        if (state == SLEEP) { angryT = 1.5; Say("Hmph. I was napping.", 2); SetState(SIT, 2); return; }
        joyT = 1.6; Hearts(3);
        if (state != AIR && state != TYPE) Hop(0, -420 * S);
        if ((DateTime.Now - lastPat).TotalSeconds >= 4) { lastPat = DateTime.Now; Award(5); }
    }

    static void Hearts(int n)
    {
        for (int i = 0; i < n; i++)
            parts.Add(new Part { X = x + Rand(-6, 6) * U, Y = y - Rand(9, 12) * U, VY = -Rand(35, 70) * S, Life = Rand(0.9, 1.5) });
    }

    static void Award(int n)
    {
        xp += n; int gained = 0;
        while (xp >= Cost(level) && gained < 50) { xp -= Cost(level); level++; gained++; }
        try { File.WriteAllText(ProgressPath, level + " " + xp); } catch { }
        parts.Add(new Part { X = x, Y = y - 11 * U, VY = -40 * S, Life = 1.4, Text = "+" + n + " XP" });
        if (gained > 0) { Say("Level " + level + "!", 3); joyT = 3; Hearts(6); }
    }

    static void ToggleHidden() { hidden = !hidden; ShowWindow(hwnd, hidden ? 0 : 4); }

    // Charger in/out and low battery. Desktops (no battery) never trigger anything.
    static void Power()
    {
        SYSTEM_POWER_STATUS ps;
        if (!GetSystemPowerStatus(out ps) || (ps.BatteryFlag & 128) != 0 || ps.BatteryFlag == 255 || ps.ACLineStatus > 1) return;
        bool ac = ps.ACLineStatus == 1; int pct = ps.BatteryLifePercent > 100 ? -1 : ps.BatteryLifePercent;
        if (powerKnown == 0) { powerKnown = 1; onAC = ac; batteryPct = pct; return; }
        if (alert != null) return;                                             // an intervention owns the bubble; retry next second
        batteryPct = pct;
        if (ac && !onAC)
        {
            onAC = true; lowWarned = 100; chargeT = 3; joyT = 2;
            Say("Charging! " + (pct >= 0 ? pct + "%" : ""), 3);
            if (state == SIT || state == WALK || state == SLEEP) Hop(0, -560 * S);
        }
        else if (!ac && onAC) { onAC = false; chargeT = 2.5; Say("Unplugged. " + (pct >= 0 ? pct + "% left." : ""), 3); }
        else if (!ac && pct >= 0 && pct <= 10 && lowWarned > 10) { lowWarned = 10; chargeT = 4; angryT = 1.5; Say("Battery " + pct + "%! Save your work.", 6); }
        else if (!ac && pct >= 0 && pct <= 20 && lowWarned > 20) { lowWarned = 20; chargeT = 4; Say("Battery " + pct + "%. Plug me in?", 5); }
    }

    static void DrawBattery()
    {
        int t = Math.Max(2, U / 3), bw = 6 * U, bh = 3 * U, bx = (int)cx + 9 * U, bt = (int)by - 8 * U;
        if (bx + bw + t > W) bx = (int)cx - 9 * U - bw;                          // no room on the right: other side
        double level = onAC ? ((3 - chargeT) * 0.8) % 1.0 : Math.Max(0, batteryPct) / 100.0;
        int fill = batteryPct >= 0 && batteryPct <= 20 && !onAC ? ANGRY : GREEN;
        Box(bx, bt, bw, bh, DARK); Box(bx + bw, bt + bh / 3, t, bh / 3, DARK);
        Box(bx + t, bt + t, bw - 2 * t, bh - 2 * t, WHITE);
        Box(bx + t, bt + t, (int)((bw - 2 * t) * level), bh - 2 * t, fill);
        if (!onAC) return;
        int mx = bx + bw / 2, my = bt + bh / 2, q = U;
        var bolt = new POINT[6];
        double[] bxs = { 0.3, -0.7, -0.05, -0.3, 0.7, 0.05 }, bys = { -1.7, 0.2, 0.2, 1.7, -0.2, -0.2 };
        for (int i = 0; i < 6; i++) { bolt[i].X = mx + (int)(bxs[i] * q); bolt[i].Y = my + (int)(bys[i] * q); }
        SelectObject(mdc, Brush(STAR)); SelectObject(mdc, penDark); Polygon(mdc, bolt, 6);
    }

    static void FocusTimer()
    {
        if (focusPhase == 0 || DateTime.Now < focusEnd) return;
        if (focusPhase == 1)
        {
            focusPhase = 2; focusEnd = DateTime.Now.AddMinutes(cfg.Break);
            Say("Break time! Stretch a bit.", 6);
        }
        else { focusPhase = 0; Say("Break's over. Ready?", 6); }
        joyT = 3; Hearts(6);
        if (state == SIT || state == WALK || state == SLEEP) Hop(0, -650 * S);
    }

    const uint GRAY = 1, CHECK = 8, SEP = 0x800, POPUP = 0x10;

    static void Menu()
    {
        bool snoozing; DateTime until;
        lock (Sync) { until = snoozeUntil; snoozing = DateTime.Now < until; }
        bool auto = AutoStart(null);
        IntPtr m = CreatePopupMenu();
        if (sign != null)
        {
            AppendMenu(m, 0, (UIntPtr)30, "Done: " + Menuish(sign.Msg));
            AppendMenu(m, 0, (UIntPtr)31, "Snooze 5 min");
            AppendMenu(m, 0, (UIntPtr)32, "Snooze 15 min");
            AppendMenu(m, 0, (UIntPtr)33, "Snooze 1 hour");
            AppendMenu(m, 0, (UIntPtr)34, "Tomorrow 9:00");
            AppendMenu(m, SEP, UIntPtr.Zero, null);
        }
        AppendMenu(m, GRAY, UIntPtr.Zero, "Level " + level + "   " + xp + " / " + Cost(level) + " XP");
        if (powerKnown != 0 && batteryPct >= 0) AppendMenu(m, GRAY, UIntPtr.Zero, "Battery " + batteryPct + "%" + (onAC ? " (charging)" : ""));
        if (cfg.Audio && (headphones || audioKind != 0))
        {
            string dev = audioDevice ?? ""; if (dev.Length > 36) dev = dev.Substring(0, 36) + "...";
            AppendMenu(m, GRAY, UIntPtr.Zero, (headphones ? "Headphones: " + dev : "Speakers") + (audioKind == 1 ? "  (music)" : audioKind == 2 ? "  (class / call)" : ""));
        }
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        AppendMenu(m, 0, (UIntPtr)10, focusPhase == 0 ? "Start focus timer (" + cfg.Focus + " min)" : "Stop focus timer");
        AppendMenu(m, 0, (UIntPtr)11, snoozing ? "Resume patrol (snoozed until " + until.ToString("HH:mm", Inv) + ")" : "Snooze patrol " + cfg.Snooze + " min");
        AppendMenu(m, patrol ? CHECK : 0, (UIntPtr)12, "On patrol");
        AppendMenu(m, 0, (UIntPtr)13, state == SLEEP ? "Wake up" : "Nap now");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        IntPtr clip = CreatePopupMenu();
        ReadClipboard(false);
        AddClipItems(clip);
        AppendMenu(m, POPUP, Sub(clip), "Clipboard");
        IntPtr rm = CreatePopupMenu();
        var upcoming = new List<Reminder>(rems);
        upcoming.Sort(delegate (Reminder a, Reminder b) { return a.Next.CompareTo(b.Next); });
        for (int i = 0; i < upcoming.Count && i < 5; i++)
        {
            var r = upcoming[i];
            AppendMenu(rm, GRAY, UIntPtr.Zero, (r.Kind == "every" ? "every " + r.Every + "m  " + Menuish(r.Msg) + "  -  next " + r.Next.ToString("HH:mm", Inv) : When(r.Next) + "  " + Menuish(r.Msg)));
        }
        if (upcoming.Count == 0) AppendMenu(rm, GRAY, UIntPtr.Zero, "None yet. Copy \"call mom in 20 min\" and click me.");
        AppendMenu(rm, SEP, UIntPtr.Zero, null);
        AppendMenu(rm, pauseRecurring ? CHECK : 0, (UIntPtr)25, "Pause recurring");
        AppendMenu(rm, 0, (UIntPtr)24, "Edit reminders...");
        AppendMenu(m, POPUP, Sub(rm), "Reminders");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        AppendMenu(m, 0, (UIntPtr)20, "Edit rules...");
        AppendMenu(m, auto ? CHECK : 0, (UIntPtr)21, "Start with Windows");
        AppendMenu(m, 0, (UIntPtr)22, hidden ? "Show pet" : "Hide pet");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        AppendMenu(m, 0, (UIntPtr)23, "Quit");
        IntPtr prev; int cmd = Popup(m, out prev);
        if (cmd != 20 && cmd != 24) Restore(prev);                              // Notepad takes focus itself
        if (DoClip(cmd)) return;
        DateTime now = DateTime.Now;
        switch (cmd)
        {
            case 10:
                if (focusPhase == 0) { focusPhase = 1; focusEnd = now.AddMinutes(cfg.Focus); Say("Focus mode. I'm watching.", 2.5); joyT = 1.5; }
                else { focusPhase = 0; Say("Timer stopped.", 1.5); }
                break;
            case 11:
                lock (Sync) snoozeUntil = snoozing ? DateTime.MinValue : now.AddMinutes(cfg.Snooze);
                Say(snoozing ? "Back on patrol." : "Snoozing " + cfg.Snooze + " min.", 2);
                break;
            case 12: patrol = !patrol; Say(patrol ? "On patrol." : "Off duty.", 2); break;
            case 13: if (state == SLEEP) SetState(SIT, 2); else if (state == SIT || state == WALK) SetState(SLEEP, 45); break;
            case 20: ShellExecute(IntPtr.Zero, "open", "notepad.exe", "\"" + RulesPath + "\"", null, 1); break;
            case 21: AutoStart(!auto); break;
            case 22: ToggleHidden(); break;
            case 23: DestroyWindow(hwnd); break;
            case 24: LoadReminders(); ShellExecute(IntPtr.Zero, "open", "notepad.exe", "\"" + RemPath + "\"", null, 1); break;
            case 25: pauseRecurring = !pauseRecurring; Say(pauseRecurring ? "Recurring reminders paused." : "Recurring reminders on.", 2); break;
            case 30: if (sign != null) ReminderDone(); break;
            case 31: if (sign != null) ReminderSnooze(now.AddMinutes(5)); break;
            case 32: if (sign != null) ReminderSnooze(now.AddMinutes(15)); break;
            case 33: if (sign != null) ReminderSnooze(now.AddHours(1)); break;
            case 34: if (sign != null) ReminderSnooze(now.Date.AddDays(1).AddHours(9)); break;
        }
    }

    static UIntPtr Sub(IntPtr menu) { return (UIntPtr)(ulong)menu.ToInt64(); }
    static string Menuish(string s) { return s.Replace("&", "&&"); }

    static int Popup(IntPtr m, out IntPtr prev)
    {
        prev = GetForegroundWindow();
        SetForegroundWindow(hwnd);                                              // required for the menu to dismiss properly
        POINT c; GetCursorPos(out c);
        int cmd = TrackPopupMenu(m, 0x100 | 0x2 | 0x20, c.X, c.Y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(m);                                                          // also destroys submenus
        return cmd;
    }

    static void Restore(IntPtr prev) { if (prev != IntPtr.Zero && prev != hwnd) SetForegroundWindow(prev); }

    // ------------------------------------------------------------------ clipboard
    // Only ever reads up to 2,000 characters into .NET: bigger copies are ignored outright,
    // which is what keeps a copied log file from costing megabytes.
    static void ReadClipboard(bool react)
    {
        if (!cfg.Clipboard) { clipRetry = 0; clipText = null; return; }
        if (react && GetClipboardSequenceNumber() == ownSeq) { clipRetry = 0; return; }
        if (!OpenClipboard(hwnd)) { clipRetry--; return; }
        clipRetry = 0;
        string text = null; bool secret;
        try
        {
            secret = IsClipboardFormatAvailable(fmtExclude) || IsClipboardFormatAvailable(fmtHistory) || IsClipboardFormatAvailable(fmtIgnore);
            IntPtr h = secret ? IntPtr.Zero : GetClipboardData(13);           // CF_UNICODETEXT
            long bytes = h == IntPtr.Zero ? 0 : (long)GlobalSize(h).ToUInt64();
            if (bytes > 0 && bytes <= 2 * 2001)
            {
                IntPtr ptr = GlobalLock(h);
                if (ptr != IntPtr.Zero)
                {
                    text = Marshal.PtrToStringUni(ptr, (int)(bytes / 2));
                    GlobalUnlock(h);
                    int z = text.IndexOf('\0'); if (z >= 0) text = text.Substring(0, z);
                }
            }
        }
        finally { CloseClipboard(); }
        clipText = null;
        if (react) clipOfferT = 0;
        if (secret || text == null || text.Trim().Length == 0 || TextTools.LooksSecret(text)) return;
        clipText = text;
        if (!react || hidden || alert != null) return;

        DateTime w; string msg; int removed; double v;
        bool useful = TextTools.ParseWhen(text, DateTime.Now, out w, out msg) || TextTools.Calc(text, out v);
        if (!useful) { TextTools.CleanLink(text, out removed); useful = removed > 0; }
        if (!useful) return;
        clipOfferT = 8;                                                         // click within 8 s for options; otherwise nothing happens
        parts.Add(new Part { X = x + facing * 3 * U, Y = y - 14 * U, VY = -14 * S, Life = 1.6, Text = "!" });
        if (!hintShown) { hintShown = true; Say("Click me for options", 2.5); }
    }

    static void ClipMenu()
    {
        ReadClipboard(false);
        IntPtr m = CreatePopupMenu();
        AddClipItems(m);
        IntPtr prev; int cmd = Popup(m, out prev);
        Restore(prev);
        clipOfferT = 0;
        DoClip(cmd);
    }

    static void AddClipItems(IntPtr m)
    {
        if (clipText == null) { AppendMenu(m, GRAY, UIntPtr.Zero, cfg.Clipboard ? "Nothing usable copied (or it looked private)" : "Clipboard features are off in rules.txt"); return; }
        string first = FirstLine(clipText, 40);
        AppendMenu(m, GRAY, UIntPtr.Zero, "\"" + Menuish(first) + "\"");
        AppendMenu(m, GRAY, UIntPtr.Zero, Words(clipText) + " words, " + clipText.Length + " chars");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        DateTime w; string msg;
        if (TextTools.ParseWhen(clipText, DateTime.Now, out w, out msg))
        {
            clipWhen = w; clipMsg = msg;
            AppendMenu(m, 0, (UIntPtr)100, "Remind \"" + Menuish(msg) + "\" " + (w.Date == DateTime.Today ? "at " : "") + When(w));
        }
        IntPtr later = CreatePopupMenu();
        AppendMenu(later, 0, (UIntPtr)101, "In 10 min");
        AppendMenu(later, 0, (UIntPtr)102, "In 30 min");
        AppendMenu(later, 0, (UIntPtr)103, "In 1 hour");
        AppendMenu(later, 0, (UIntPtr)104, "In 3 hours");
        AppendMenu(later, 0, (UIntPtr)105, "Tomorrow 9:00");
        AppendMenu(m, POPUP, Sub(later), "Remind me about this");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        int removed; TextTools.CleanLink(clipText, out removed);
        if (removed > 0) AppendMenu(m, 0, (UIntPtr)110, "Copy clean link (" + removed + (removed == 1 ? " tracker)" : " trackers)"));
        double v;
        if (TextTools.Calc(clipText, out v)) AppendMenu(m, 0, (UIntPtr)111, "Copy result: " + Num(v));
        AppendMenu(m, 0, (UIntPtr)112, "Tidy whitespace");
        IntPtr cs = CreatePopupMenu();
        AppendMenu(cs, 0, (UIntPtr)113, "UPPER CASE");
        AppendMenu(cs, 0, (UIntPtr)114, "lower case");
        AppendMenu(cs, 0, (UIntPtr)115, "Title Case");
        AppendMenu(m, POPUP, Sub(cs), "Change case");
    }

    static bool DoClip(int cmd)
    {
        if (cmd < 100 || cmd > 115) return false;
        if (clipText == null) return true;
        DateTime now = DateTime.Now; string about = FirstLine(clipText, 60);
        int removed; double v;
        switch (cmd)
        {
            case 100: AddReminder(clipWhen, clipMsg); break;
            case 101: AddReminder(now.AddMinutes(10), about); break;
            case 102: AddReminder(now.AddMinutes(30), about); break;
            case 103: AddReminder(now.AddHours(1), about); break;
            case 104: AddReminder(now.AddHours(3), about); break;
            case 105: AddReminder(now.Date.AddDays(1).AddHours(9), about); break;
            case 110:
                string clean = TextTools.CleanLink(clipText, out removed);
                if (SetClip(clean)) Say("Link cleaned. " + removed + (removed == 1 ? " tracker gone." : " trackers gone."), 2.5);
                break;
            case 111: if (TextTools.Calc(clipText, out v) && SetClip(Num(v))) Say("= " + Num(v) + ". Copied.", 2.5); break;
            case 112: if (SetClip(TextTools.Tidy(clipText))) Say("Tidied.", 1.5); break;
            case 113: if (SetClip(clipText.ToUpperInvariant())) Say("DONE.", 1.5); break;
            case 114: if (SetClip(clipText.ToLowerInvariant())) Say("done.", 1.5); break;
            case 115: if (SetClip(Inv.TextInfo.ToTitleCase(clipText.ToLowerInvariant()))) Say("Done.", 1.5); break;
        }
        joyT = 1;
        return true;
    }

    static bool SetClip(string text)
    {
        if (!OpenClipboard(hwnd)) { Say("Clipboard is busy. Try again.", 2); return false; }
        try
        {
            EmptyClipboard();
            IntPtr h = GlobalAlloc(2, (UIntPtr)(ulong)((text.Length + 1) * 2));  // GMEM_MOVEABLE
            IntPtr ptr = GlobalLock(h);
            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr, text.Length * 2, 0);
            GlobalUnlock(h);
            if (SetClipboardData(13, h) == IntPtr.Zero) GlobalFree(h);
        }
        finally { CloseClipboard(); }
        ownSeq = GetClipboardSequenceNumber();                                  // don't react to our own write
        clipText = text;
        return true;
    }

    static string FirstLine(string s, int max)
    {
        s = s.Trim(); int nl = s.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) s = s.Substring(0, nl);
        return s.Length > max ? s.Substring(0, max) + "..." : s;
    }

    static int Words(string s)
    {
        int n = 0; bool inWord = false;
        foreach (char ch in s) { bool w = !char.IsWhiteSpace(ch); if (w && !inWord) n++; inWord = w; }
        return n;
    }

    static string Num(double v) { return v.ToString("0.##########", Inv); }
    static string When(DateTime t) { return t.Date == DateTime.Today ? t.ToString("HH:mm", Inv) : t.ToString("ddd HH:mm", Inv); }

    // ------------------------------------------------------------------ reminders
    static Reminder ParseReminder(string line, DateTime now)
    {
        int bar = line.IndexOf('|');
        if (bar < 0) return null;
        string when = line.Substring(0, bar).Trim().ToLowerInvariant(), msg = line.Substring(bar + 1).Trim();
        if (msg.Length == 0) return null;
        var r = new Reminder { Line = line.Trim(), Msg = msg.Length > 60 ? msg.Substring(0, 60) + "..." : msg };
        var parts = when.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        TimeSpan tod; DateTime at;
        if (parts.Length >= 2 && parts.Length <= 3 && parts[0] == "every")
        {
            string p = parts[1]; int n; char unit = p[p.Length - 1];
            if ((unit != 'm' && unit != 'h') || !int.TryParse(p.Substring(0, p.Length - 1), NumberStyles.None, Inv, out n) || n <= 0) return null;
            r.Kind = "every"; r.Every = Math.Max(5, unit == 'h' ? n * 60 : n);
            if (parts.Length == 3)
            {
                var win = parts[2].Split('-'); TimeSpan a, b;
                if (win.Length != 2 || !Tod(win[0], out a) || !Tod(win[1], out b)) return null;
                r.From = (int)a.TotalMinutes; r.To = (int)b.TotalMinutes;
            }
            r.Next = now.AddMinutes(r.Every);
        }
        else if (parts.Length == 2 && (parts[0] == "daily" || parts[0] == "weekdays") && Tod(parts[1], out tod))
        {
            r.Kind = parts[0]; r.At = DateTime.MinValue.Add(tod); r.Next = NextDaily(r, now);
        }
        else if (DateTime.TryParseExact(when, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm" }, Inv, DateTimeStyles.None, out at))
        {
            r.Kind = "once"; r.At = at; r.Next = at;
        }
        else return null;
        return r;
    }

    static bool Tod(string s, out TimeSpan t)
    {
        DateTime d; t = TimeSpan.Zero;
        if (!DateTime.TryParseExact(s, new[] { "H:mm", "HH:mm" }, Inv, DateTimeStyles.None, out d)) return false;
        t = d.TimeOfDay; return true;
    }

    static DateTime NextDaily(Reminder r, DateTime after)
    {
        DateTime d = after.Date.Add(r.At.TimeOfDay);
        while (d <= after || (r.Kind == "weekdays" && (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday))) d = d.AddDays(1);
        return d;
    }

    static void LoadReminders()
    {
        try
        {
            if (!File.Exists(RemPath)) File.WriteAllText(RemPath, DefaultReminders);
            DateTime st = File.GetLastWriteTimeUtc(RemPath);
            if (st == remStamp) return;
            string[] lines = File.ReadAllLines(RemPath);
            remStamp = st;
            var list = new List<Reminder>(); DateTime now = DateTime.Now; int bad = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                var r = ParseReminder(l, now);
                if (r == null) { if (bad == 0) bad = i + 1; continue; }
                foreach (var o in rems) if (o.Line == r.Line && o.Kind != "once") { r.Next = o.Next; break; }   // saving the file must not restart running timers
                list.Add(r);
            }
            rems = list;
            if (bad > 0) Say("reminders.txt line " + bad + " isn't understood.", 3);
        }
        catch { }   // Notepad mid-save: next second
    }

    static void Reminders()
    {
        LoadReminders();
        DateTime now = DateTime.Now;
        if (sign != null)
        {
            signT += 1;
            if (signT <= 300 && (int)signT % 30 == 0 && state == SIT) Hop(0, -480 * S);
            if (alert == null && !bubbleSign && bubbleT <= 0) ShowSign();       // something else borrowed the bubble; take it back
            return;
        }
        if (alert != null || state == HELD) return;
        var li = new LASTINPUTINFO(); li.cbSize = 8; GetLastInputInfo(ref li);
        uint idleMs = (uint)Environment.TickCount - li.dwTime;
        int quns; bool quiet = SHQueryUserNotificationState(out quns) == 0 && quns >= 2 && quns <= 4;   // fullscreen, D3D game, presenting
        foreach (var r in rems)
        {
            if (now < r.Next) continue;
            if (r.Kind == "every")
            {
                int mins = now.Hour * 60 + now.Minute;
                bool outside = r.From >= 0 && (r.From <= r.To ? (mins < r.From || mins >= r.To) : (mins < r.From && mins >= r.To));
                if (pauseRecurring || idleMs > 5 * 60 * 1000 || outside) { r.Next = now.AddMinutes(r.Every); continue; }
                if (focusPhase == 1) { r.Next = focusEnd.AddSeconds(3); continue; }   // hold it for the break
            }
            if (quiet) return;
            sign = r; signT = 0;
            signText = (r.Kind == "once" && now - r.At > TimeSpan.FromMinutes(2) ? "Missed at " + r.At.ToString("HH:mm", Inv) + ": " : "") + r.Msg;
            ShowSign();
            MessageBeep(0x40);
            if (hidden) Tray(1, "Reminder", signText);
            if (state == SLEEP) SetState(SIT, 2);
            joyT = 1;
            return;
        }
    }

    static void ShowSign() { Say(signText + "\nclick = done  |  right-click = snooze", 99); bubbleSign = true; }

    static Reminder FindLive(Reminder r)
    {
        foreach (var o in rems) if (o.Line == r.Line) return o;
        return r;
    }

    static void ReminderDone()
    {
        var r = sign; sign = null; bubbleT = 0; bubbleSign = false;
        DateTime now = DateTime.Now;
        if (r.Kind == "once") EditReminderLine(r.Line, null);
        else FindLive(r).Next = r.Kind == "every" ? now.AddMinutes(r.Every) : NextDaily(r, now);
        joyT = 1.5; Hearts(3); Say("Nice.", 1.5);
        if (signT <= 120) Award(5);
    }

    static void ReminderSnooze(DateTime until)
    {
        var r = sign; sign = null; bubbleSign = false;
        if (r.Kind == "once") EditReminderLine(r.Line, until.ToString("yyyy-MM-dd HH:mm", Inv) + " " + r.Line.Substring(r.Line.IndexOf('|')));
        else FindLive(r).Next = until;
        Say("Okay, " + (until.Date == DateTime.Today ? until.ToString("HH:mm", Inv) : until.ToString("ddd HH:mm", Inv)) + ".", 2);
    }

    // Read-modify-write so edits you made in Notepad since the last load survive.
    static void EditReminderLine(string oldLine, string newLine)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(RemPath));
            int i = lines.FindIndex(delegate (string l) { return l.Trim() == oldLine; });
            if (i < 0) return;
            if (newLine == null) lines.RemoveAt(i); else lines[i] = newLine;
            File.WriteAllLines(RemPath, lines.ToArray());
            remStamp = DateTime.MinValue; LoadReminders();
        }
        catch { Say("Couldn't update reminders.txt.", 2); }
    }

    static void AddReminder(DateTime when, string msg)
    {
        msg = msg.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (msg.Length == 0) msg = "Reminder";
        if (msg.Length > 120) msg = msg.Substring(0, 120);
        try
        {
            if (!File.Exists(RemPath)) File.WriteAllText(RemPath, DefaultReminders);
            string existing = File.ReadAllText(RemPath);
            string sep = existing.Length > 0 && !existing.EndsWith("\n") ? "\r\n" : "";
            File.AppendAllText(RemPath, sep + when.ToString("yyyy-MM-dd HH:mm", Inv) + " | " + msg + "\r\n");
            remStamp = DateTime.MinValue; LoadReminders();
            Say("Got it. " + When(when) + " - " + msg, 3); joyT = 1.2;
        }
        catch { Say("Couldn't save the reminder.", 2); }
    }

    static bool AutoStart(bool? set)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (set == true) k.SetValue("PixelPetFocus", "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\"");
                if (set == false) k.DeleteValue("PixelPetFocus", false);
                k.DeleteValue("FocusCatPixel", false);                          // pre-rename entry points at a dead path
                return k.GetValue("PixelPetFocus") != null;
            }
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ the intervention
    static void BeginAlert(Bust b)
    {
        b.Scold = b.Label.Contains("Short") ? Scolds[0] : Scolds[1 + rnd.Next(Scolds.Length - 1)];
        alert = b; bubbleT = 0;
        if (perch != IntPtr.Zero) HopDown();
        else if (state != AIR) SetState(RUN, 0);
    }

    static void StartGlare()
    {
        if (alert.CenterX != int.MinValue) facing = alert.CenterX > x ? 1 : -1;
        SetState(GLARE, 0); glareTick = 0;
        if (cfg.Nag) { Say(alert.Label + "? Really?", 2.6); return; }
        glareLeft = cfg.Countdown;
        if (glareLeft <= 0) { SetState(SWIPE, 0); acted = false; }
        else Say(alert.Scold + "  " + glareLeft + "s", 99);
    }

    static void EndAlert(double rest) { alert = null; if (bubbleT > rest) bubbleT = rest; SetState(SIT, rest); }

    // Only acts if the same window is still in front: you may have alt-tabbed to real work meanwhile.
    static bool Act(Bust b, out string why)
    {
        IntPtr cur = GetForegroundWindow();
        if (cur != b.Hwnd) { why = "You moved on. Good."; return false; }
        string now = StripBadge(Title(cur)), want = StripBadge(b.Title);
        string tail = want.Length > 40 ? want.Substring(want.Length - 40) : want;
        if (tail.Length > 0 && !now.Contains(tail)) { why = "The window moved on."; return false; }
        if (Array.IndexOf(Browsers, b.Proc) >= 0)
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x57, 0, 0, UIntPtr.Zero);
            keybd_event(0x57, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
        }
        else PostMessage(cur, 0x10, IntPtr.Zero, IntPtr.Zero);                 // WM_CLOSE
        why = ""; return true;
    }

    static string Title(IntPtr h) { var sb = new StringBuilder(1024); GetWindowText(h, sb, 1024); return sb.ToString(); }

    // ------------------------------------------------------------------ watcher thread
    static void Watch()
    {
        var reader = new UrlReader();
        string matchId = "", pendingKey = ""; DateTime since = DateTime.UtcNow; IntPtr lastH = IntPtr.Zero;
        DateTime stamp = File.GetLastWriteTimeUtc(RulesPath);
        while (true)
        {
            Thread.Sleep(1000);
            try
            {
                DateTime st = File.GetLastWriteTimeUtc(RulesPath);
                if (st != stamp) { cfg = Parse(File.ReadAllLines(RulesPath)); stamp = st; }

                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero || h == hwnd) continue;
                if (h != lastH) { lastH = h; pendingKey = ""; }
                string title = Title(h);
                uint pid; GetWindowThreadProcessId(h, out pid);
                string proc = ProcName(pid);
                string url = Array.IndexOf(Browsers, proc) >= 0 ? reader.Read(h) : "";

                bool paused;
                lock (Sync) paused = !patrol || DateTime.Now < snoozeUntil || DateTime.Now < cooldownUntil;
                Rule rule = paused ? null : Match(cfg, url, title, proc);
                if (rule == null) { matchId = ""; lock (Sync) watchRemaining = -1; continue; }

                // the clock runs per rule+window: swiping to the next reel must not restart it
                string id = rule.Label + "|" + h;
                if (id != matchId) { matchId = id; since = DateTime.UtcNow; }
                double remaining = rule.Grace - (DateTime.UtcNow - since).TotalSeconds;
                RECT r; int cx = Frame(h, out r) ? (r.L + r.R) / 2 : int.MinValue;
                if (remaining > 0) { lock (Sync) { watchRemaining = remaining; watchX = cx; } continue; }

                string key = h + "|" + title + "|" + url;
                lock (Sync) watchRemaining = -1;
                if (key != pendingKey)
                {
                    pendingKey = key;
                    var b = new Bust { Hwnd = h, Title = title, Proc = proc, Label = rule.Label, CenterX = cx };
                    lock (Sync) pendingBust = b;
                }
            }
            catch { }   // a half-saved rules file or a dying window: try again next second
        }
    }

    // ------------------------------------------------------------------ audio: headphones, music vs class
    // Polls Core Audio at 10 Hz: the default output device (headphones? Bluetooth?) and its peak level.
    // Every 2 s, while something is playing, it asks WHICH apps are playing and reads their window titles.
    static readonly string[] TalkWords = { "zoom", "teams", "meet", "webex", "skype", "discord", "lecture", "class", "course", "lesson", "tutorial", "webinar", "seminar", "udemy", "coursera", "nptel", "edx", "unacademy", "khan academy", "podcast", "interview" };
    static readonly string[] MusicWords = { "spotify", "music", "song", "songs", "lyrics", "playlist", "album", "soundcloud", "jiosaavn", "gaana", "wynk", "deezer", "tidal", "lofi", "lo-fi", "remix", "official video", "official audio", "mix" };
    static readonly string[] PhoneWords = { "headphone", "headset", "earphone", "earbud", "buds", "airpods", "hands-free", "bluetooth", "wh-", "wf-" };

    static bool HasWord(string text, string w)
    {
        for (int i = text.IndexOf(w); i >= 0; i = text.IndexOf(w, i + 1))
        {
            bool before = i == 0 || !char.IsLetter(text[i - 1]), after = i + w.Length >= text.Length || !char.IsLetter(text[i + w.Length]);
            if (before && after) return true;
        }
        return false;
    }

    static int Classify(string text)
    {
        foreach (var w in TalkWords) if (HasWord(text, w)) return 2;
        foreach (var w in MusicWords) if (HasWord(text, w)) return 1;
        return 0;
    }

    static bool IsHeadphones(string name, int formFactor)
    {
        if (formFactor == 3 || formFactor == 5) return true;                   // Headphones, Headset
        foreach (var w in PhoneWords) if (name.Contains(w)) return true;
        return false;
    }

    // speech has gaps between words; music rarely drops to near silence
    static bool Speechy(float[] peaks)
    {
        float max = 0; foreach (var v in peaks) max = Math.Max(max, v);
        if (max < 0.02f) return false;
        int quiet = 0; foreach (var v in peaks) if (v < max * 0.12f) quiet++;
        return quiet >= peaks.Length / 4;
    }

    static void Release(object o) { if (o != null) try { Marshal.ReleaseComObject(o); } catch { } }

    static void AudioWatch()
    {
        IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioMeterInformation meter = null; string devId = "";
        var peaks = new float[40]; int tick = 0, quietTicks = 999, candidate = 0, seen = 0;
        while (true)
        {
            try
            {
                if (en == null) en = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
                if (tick % 20 == 0)
                {
                    IMMDevice d; string id = "";
                    if (en.GetDefaultAudioEndpoint(0, 1, out d) != 0) d = null;       // eRender, eMultimedia
                    if (d != null) d.GetId(out id);
                    if (id != devId)
                    {
                        Release(meter); Release(dev); meter = null; dev = d; devId = id ?? "";
                        if (dev != null)
                        {
                            object o; Guid iid = typeof(IAudioMeterInformation).GUID;
                            dev.Activate(ref iid, 23, IntPtr.Zero, out o); meter = (IAudioMeterInformation)o;
                        }
                    }
                    else Release(d);
                    // Any connected output, not just the default: enhancers like FxSound or Voicemeeter sit in front
                    // of the real headphones as the default device. Bluetooth endpoints are only active while connected.
                    string phonesName = null;
                    IMMDeviceCollection all;
                    if (en.EnumAudioEndpoints(0, 1, out all) == 0)
                    {
                        uint count; all.GetCount(out count);
                        for (uint i = 0; i < count && phonesName == null; i++)
                        {
                            IMMDevice e; all.Item(i, out e);
                            string name; int ff; DeviceInfo(e, out name, out ff);
                            if (IsHeadphones(name.ToLowerInvariant(), ff)) phonesName = name;
                            Release(e);
                        }
                        Release(all);
                    }
                    headphones = phonesName != null;
                    audioDevice = phonesName ?? "";
                }
                float pk = 0; if (meter != null) meter.GetPeakValue(out pk);
                audioPeak = pk; peaks[tick % peaks.Length] = pk;
                quietTicks = pk > 0.01f ? 0 : quietTicks + 1;
                if (tick % 20 == 10)
                {
                    int kind = quietTicks < 60 && dev != null ? ClassifySessions(dev, peaks) : 0;   // lecture pauses under 6 s keep class mode
                    if (kind == candidate) seen++; else { candidate = kind; seen = 1; }
                    if (seen >= 2) audioKind = candidate;                               // must hold for two checks: no flicker
                }
            }
            catch { Release(meter); Release(dev); meter = null; dev = null; devId = ""; }
            tick++;
            Thread.Sleep(100);
        }
    }

    static void DeviceInfo(IMMDevice d, out string name, out int formFactor)
    {
        name = ""; formFactor = -1;
        IPropertyStore ps; d.OpenPropertyStore(0, out ps);
        var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };   // Device_FriendlyName
        PROPVARIANT v; ps.GetValue(ref key, out v);
        if (v.vt == 31) name = Marshal.PtrToStringUni(v.p) ?? "";
        PropVariantClear(ref v);
        key = new PROPERTYKEY { fmtid = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), pid = 0 };          // AudioEndpoint_FormFactor
        ps.GetValue(ref key, out v);
        if (v.vt == 19) formFactor = v.i;
        PropVariantClear(ref v);
        Release(ps);
    }

    static string enumProc; static readonly StringBuilder enumTitles = new StringBuilder();
    static readonly EnumWindowsProc enumCb = EnumCb;
    static bool EnumCb(IntPtr h, IntPtr l)
    {
        if (!IsWindowVisible(h)) return true;
        string t = Title(h);
        if (t.Length == 0) return true;
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (ProcName(pid) == enumProc) enumTitles.Append(' ').Append(t.ToLowerInvariant());
        return enumTitles.Length < 4000;
    }

    static int ClassifySessions(IMMDevice dev, float[] peaks)
    {
        object o; Guid iid = typeof(IAudioSessionManager2).GUID;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        var mgr = (IAudioSessionManager2)o; IAudioSessionEnumerator se = null;
        bool active = false, talk = false, music = false;
        try
        {
            mgr.GetSessionEnumerator(out se);
            int n; se.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IAudioSessionControl2 ctl; se.GetSession(i, out ctl);
                try
                {
                    int st; ctl.GetState(out st);
                    var m = ctl as IAudioMeterInformation; float sp = 0; if (m != null) m.GetPeakValue(out sp);
                    uint pid; ctl.GetProcessId(out pid);
                    if (st != 1 || sp < 0.005f || pid == 0) continue;
                    active = true;
                    enumProc = ProcName(pid); enumTitles.Length = 0; enumTitles.Append(enumProc);
                    EnumWindows(enumCb, IntPtr.Zero);                                 // a browser's audio process shares the browser's exe name
                    int k = Classify(enumTitles.ToString());
                    if (k == 2) talk = true; else if (k == 1) music = true;
                }
                finally { Release(ctl); }
            }
        }
        finally { Release(se); Release(mgr); }
        if (talk) return 2;
        if (music) return 1;
        return !active ? 0 : Speechy(peaks) ? 2 : 1;
    }

    static string ProcName(uint pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return "";
        var sb = new StringBuilder(520); int n = sb.Capacity;
        bool ok = QueryFullProcessImageName(h, 0, sb, ref n);
        CloseHandle(h);
        return ok ? Path.GetFileNameWithoutExtension(sb.ToString()).ToLowerInvariant() : "";
    }

    // ------------------------------------------------------------------ drawing
    static double cx, by, sx = 1, sy = 1;

    static IntPtr Brush(int color)
    {
        IntPtr b;
        if (!brushes.TryGetValue(color, out b)) { b = CreateSolidBrush(color); brushes[color] = b; }
        return b;
    }

    static void Box(int l, int t, int w, int h, int color) { var r = new RECT { L = l, T = t, R = l + w, B = t + h }; FillRect(mdc, ref r, Brush(color)); }

    // one sprite cell: 14 columns x 9 rows, feet on row 9, squashed about the bottom centre
    static void Px(double col, double row, double w, double h, int color)
    {
        int x0 = (int)Math.Round(cx + (col - 7) * U * sx), x1 = (int)Math.Round(cx + (col + w - 7) * U * sx);
        int y0 = (int)Math.Round(by - (9 - row) * U * sy), y1 = (int)Math.Round(by - (9 - row - h) * U * sy);
        Box(x0, y0, x1 - x0, y1 - y0, color);
    }

    static void Render()
    {
        if (hidden || mdc == IntPtr.Zero) return;
        Box(0, 0, W, H, KEY);
        cx = x - winLeft; by = y - winTop;
        double k = sqT > 0 ? sqK * sqT / 0.25 : 0;
        sx = 1 + 0.16 * k; sy = 1 - 0.22 * k;
        if (state == HELD) { sx = 0.94; sy = 1.06; }
        bool vibing = lastKind == 1 && (state == SIT || state == TYPE) && danceLevel > 0.04;
        if (vibing) { double d = Math.Min(1, danceLevel * 1.6); sx *= 1 + 0.07 * d; sy *= 1 - 0.11 * d; }

        bool sleeping = state == SLEEP;
        bool angry = angryT > 0 || state == RUN || state == GLARE || state == SWIPE;
        bool joy = (joyT > 0 || (vibing && state == SIT)) && !angry;
        int body = angry ? ANGRY : onAC && chargeT > 2.3 && (int)(animT * 10) % 2 == 0 ? STAR : CORAL;   // zap flash on plug-in
        bool typing = state == TYPE;
        int dy = sleeping || typing ? 2 : 0;

        if (dy == 0)
        {
            bool walking = state == WALK || state == RUN;
            int phase = walking ? (int)(animT * (state == RUN ? 14 : 8)) % 2 : -1;
            for (int i = 0; i < 4; i++) Px(Legs[i], 7, 1, state == HELD ? 3 : (i % 2 == phase ? 1 : 2), body);
        }
        double wob = (state == WALK || state == RUN) ? ((int)(animT * 8) % 2 == 0 ? -0.25 : 0.25) : 0;
        int tip = sleeping ? LOGO : angry ? PINK : lastKeyAgo < 0.15 ? WHITE : STAR;
        Px(3.5 + wob, dy - 1.5, 1, 1.5, body); Px(9.5 + wob, dy - 1.5, 1, 1.5, body);   // antennae
        Px(3 + wob, dy - 2.5, 2, 1, tip); Px(9 + wob, dy - 2.5, 2, 1, tip);
        Px(3, dy, 8, 1, body);                                                  // rounded head
        Px(2, dy + 1, 10, 6, body);

        bool up = joy || state == HELD || (sign != null && state != TYPE);   // holding up the reminder sign
        bool swipeRaised = state == SWIPE && stateT < 0.25, swipeStrike = state == SWIPE && stateT >= 0.25;
        bool roping = ropeT >= 0;
        bool leftBusy = facing < 0 && (swipeRaised || swipeStrike || roping), rightBusy = facing > 0 && (swipeRaised || swipeStrike || roping);
        if (typing && lapOpen > 0)
        {
            double off = (1 - lapOpen) * 3.5;                                   // laptop rises out of the ground
            bool tapping = lastKeyAgo < 0.4 && lapOpen >= 1;
            int ph = (int)(animT * 12) % 2;
            Px(3.5, 5.5 + off, 7, 3, LID);
            Px(2, 8.4 + off, 10, 0.6, BASE);
            Px(6.5, 6.5 + off, 1, 1, tapping && (int)(animT * 6) % 2 == 0 ? GLOW : LOGO);
            if (!(roping && facing < 0)) Px(1.5, 6.5 + (tapping && ph == 0 ? 0.5 : 0), 2.5, 1.5, body);
            if (!(roping && facing > 0)) Px(10, 6.5 + (tapping && ph == 1 ? 0.5 : 0), 2.5, 1.5, body);
            up = false; leftBusy = rightBusy = true;
        }
        bool leftUp = up, rightUp = up;
        if (vibing && joyT <= 0 && state == SIT) { leftUp = (int)(animT * 2.5) % 2 == 0; rightUp = !leftUp; }   // arms sway to the music
        bool notes = lastKind == 2 && state == SIT && alert == null;
        if (notes) leftBusy = true;                                             // left paw holds the notepad
        if (!leftBusy) Px(0, leftUp ? 1 : 4 + dy, 2, 2, body);
        if (!rightBusy) Px(12, rightUp ? 1 : 4 + dy, 2, 2, body);
        if (notes)
        {
            double wig = Math.Sin(animT * 9) * 0.6;
            Px(0.5, 4.6, 4.4, 3.8, PAPER); Px(1, 5.4, 3.2, 0.3, INK); Px(1, 6.3, 2.6, 0.3, INK); Px(1, 7.2, 3, 0.3, INK);
            Px(2.6 + wig, 3.9, 0.6, 2.2, STAR); Px(2.6 + wig, 6.1, 0.6, 0.5, DARK);   // pencil scribbling
        }
        if (swipeRaised) Px(facing > 0 ? 12 : 0, 0, 2, 3, body);
        if (roping) Px(facing > 0 ? 12 : 0, dy, 2, 3, body);
        if (swipeStrike) Px(facing > 0 ? 12 : -2, 3, 4, 2, body);

        if (sleeping || (blinkOn > 0 && !angry && !joy)) { Px(3.8, 3 + dy, 1.4, 0.5, EYE); Px(8.8, 3 + dy, 1.4, 0.5, EYE); }
        else if (joy) { Px(3, 3 + dy, 1, 1, EYE); Px(4, 2 + dy, 1, 1, EYE); Px(5, 3 + dy, 1, 1, EYE); Px(8, 3 + dy, 1, 1, EYE); Px(9, 2 + dy, 1, 1, EYE); Px(10, 3 + dy, 1, 1, EYE); }
        else if (angry) { Px(3.5 + lookX, 2 + dy, 1, 1, EYE); Px(4.5 + lookX, 3 + dy, 1, 1, EYE); Px(9.5 + lookX, 2 + dy, 1, 1, EYE); Px(8.5 + lookX, 3 + dy, 1, 1, EYE); }
        else { Px(4 + lookX, 2 + lookY + dy, 1, 2, EYE); Px(9 + lookX, 2 + lookY + dy, 1, 2, EYE); }

        if (wasPhones)
        {
            Px(2.4, dy - 1.2, 9.2, 0.7, DARK);                                      // band over the head
            Px(1.7, dy - 0.8, 0.8, 1.8, DARK); Px(11.5, dy - 0.8, 0.8, 1.8, DARK);
            Px(1, dy + 0.8, 2, 2.6, CUP); Px(11, dy + 0.8, 2, 2.6, CUP);            // ear cups
            Px(1.4, dy + 1.3, 0.8, 1.6, CUPDARK); Px(11.8, dy + 1.3, 0.8, 1.6, CUPDARK);
            if (lastKind == 2) { Px(12.2, dy + 3.3, 0.5, 1.4, DARK); Px(9.4, dy + 4.4, 3.3, 0.5, DARK); Px(8.7, dy + 4.1, 1, 1.1, DARK); }   // mic boom
        }

        sx = sy = 1;
        if (chargeT > 0 && onAC && dy == 0) { Px(-9, 8.4, 8, 0.6, DARK); Px(-1.6, 7.6, 1.8, 1.5, INK); Px(0.2, 7.9, 0.7, 0.3, STAR); Px(0.2, 8.5, 0.7, 0.3, STAR); }   // plugged in
        if (chargeT > 0) DrawBattery();
        int hp = Math.Max(2, U / 2);
        foreach (var p in parts)
        {
            int px = (int)(p.X - winLeft), py = (int)(p.Y - winTop);
            if (p.Text == null)
            {
                Box(px - 2 * hp, py, hp, hp, PINK); Box(px, py, hp, hp, PINK);
                Box(px - 2 * hp, py + hp, 3 * hp, hp, PINK); Box(px - hp, py + 2 * hp, hp, hp, PINK);
                Box(px - 3 * hp, py + hp, hp, hp, PINK); Box(px + hp, py + hp, hp, hp, PINK);
            }
            else Outlined(p.Text, px, py);
        }

        double top = by - (dy > 0 ? 10 : 12) * U - 6 * S;
        if (bubbleT > 0 && bubble != null) Pill(bubble, (int)top, bubbleSign ? SIGN : WHITE, DARK);
        else if (tagCache != null) Pill(tagCache, (int)top, DARK, WHITE);

        IntPtr dc = GetDC(hwnd);
        BitBlt(dc, 0, 0, W, H, mdc, 0, 0, 0x00CC0020);
        ReleaseDC(hwnd, dc);
    }

    static void Pill(string text, int bottom, int fill, int ink)
    {
        SelectObject(mdc, fontBubble);
        int padX = (int)(9 * S), padY = (int)(5 * S);
        var t = new RECT { R = W - (int)(24 * S) };
        DrawText(mdc, text, -1, ref t, 0x400 | 0x10 | 0x1);                     // CALCRECT | WORDBREAK | CENTER
        int bw = t.R + 2 * padX, bh = t.B + 2 * padY;
        int bx = Clamp((int)cx - bw / 2, 1, W - bw - 1), bt = Math.Max(1, bottom - bh);
        SelectObject(mdc, Brush(fill)); SelectObject(mdc, penDark);
        RoundRect(mdc, bx, bt, bx + bw, bt + bh, (int)(12 * S), (int)(12 * S));
        SetTextColor(mdc, ink);
        var r = new RECT { L = bx + padX, T = bt + padY, R = bx + bw - padX, B = bt + bh - padY };
        DrawText(mdc, text, -1, ref r, 0x10 | 0x1);                              // WORDBREAK | CENTER
    }

    static void Outlined(string s, int px, int py)
    {
        SelectObject(mdc, fontSmall);
        SetTextColor(mdc, DARK);
        TextOut(mdc, px - 1, py, s, s.Length); TextOut(mdc, px + 1, py, s, s.Length);
        TextOut(mdc, px, py - 1, s, s.Length); TextOut(mdc, px, py + 1, s, s.Length);
        SetTextColor(mdc, WHITE);
        TextOut(mdc, px, py, s, s.Length);
    }

    static void Tray(int op) { Tray(op, null, null); }

    static void Tray(int op, string title, string info)   // 0 add, 1 modify, 2 delete
    {
        var n = new NID();
        n.cbSize = Marshal.SizeOf(typeof(NID));
        n.hWnd = hwnd; n.uID = 1; n.uFlags = 7; n.uCallbackMessage = (int)WM_TRAY;
        if (trayIcon == IntPtr.Zero) trayIcon = ExtractIcon(GetModuleHandle(null), System.Reflection.Assembly.GetEntryAssembly().Location, 0);
        n.hIcon = trayIcon;
        n.szTip = "PixelPet Focus - right-click for menu, double-click to hide/show";
        if (info != null) { n.uFlags |= 0x10; n.szInfoTitle = title; n.szInfo = info; n.dwInfoFlags = 1; }
        Shell_NotifyIcon(op, ref n);
    }

    // ------------------------------------------------------------------ self-check: PixelPetFocus.exe --selftest ; exit code 0 = pass
    static int SelfTest()
    {
        var c = Parse(DefaultRules.Split('\n'));
        int fails = 0;
        Action<bool> ok = delegate (bool b) { if (!b) fails++; };
        ok(c.Countdown == 3 && c.Snooze == 5 && !c.Nag && c.Rules.Length == 9 && c.Never.Length == 3);
        ok(Match(c, "instagram.com/reels/abc/", "(8) Instagram", "chrome").Label == "Instagram Reels");   // reels only visible in URL
        ok(Match(c, "", "Crunchyroll - Season 3", "msedge").Label == "Streaming");                         // fullscreen: title only
        ok(Match(c, "youtube.com/shorts/x", "Some Short - YouTube", "chrome").Label == "YouTube Shorts");   // specific beats general
        ok(Match(c, "youtube.com/watch?v=a", "A Talk - YouTube", "chrome").Grace == 240);
        ok(Match(c, "github.com/foo", "foo", "chrome") == null);
        ok(Match(c, "tiktok.com", "standup - google meet", "chrome") == null);                             // never list wins
        ok(StripBadge("(8) Instagram - Chrome") == StripBadge("(9) Instagram - Chrome"));
        ok(StripBadge("(Draft) Report") == "(Draft) Report");
        ok(Cost(1) == 50 && Cost(2) == 65 && Cost(3) == 80);
        ok(Classify("chrome lecture 5: thermodynamics - youtube") == 2 && Classify("ms-teams standup") == 2);
        ok(Classify("spotify daft punk") == 1 && Classify("chrome classic rock playlist - youtube") == 1);    // "classic" is not "class"
        ok(Classify("chrome some random video") == 0);
        ok(IsHeadphones("speakers (realtek(r) audio)", 1) == false && IsHeadphones("headphones (wh-1000xm4)", 3) && IsHeadphones("airpods pro", 1));
        var talk = new float[40]; for (int i = 0; i < 40; i++) talk[i] = i % 3 == 0 ? 0.001f : 0.4f;
        var song = new float[40]; for (int i = 0; i < 40; i++) song[i] = 0.3f + (i % 4) * 0.1f;
        ok(Speechy(talk) && !Speechy(song));

        var n0 = new DateTime(2026, 9, 17, 18, 0, 0); DateTime w; string msg;   // a Thursday, 18:00
        ok(TextTools.ParseWhen("call mom in 20 min", n0, out w, out msg) && w == n0.AddMinutes(20) && msg == "call mom");
        ok(TextTools.ParseWhen("remind me to pay rent at 5pm", n0, out w, out msg) && w == new DateTime(2026, 9, 18, 17, 0, 0) && msg == "pay rent");
        ok(TextTools.ParseWhen("standup tomorrow", n0, out w, out msg) && w == new DateTime(2026, 9, 18, 9, 0, 0) && msg == "standup");
        ok(TextTools.ParseWhen("gym tomorrow at 7:30am", n0, out w, out msg) && w == new DateTime(2026, 9, 18, 7, 30, 0) && msg == "gym");
        ok(TextTools.ParseWhen("stretch in 1.5h", n0, out w, out msg) && w == n0.AddMinutes(90) && msg == "stretch");
        ok(TextTools.ParseWhen("Deploy 20:15", n0, out w, out msg) && w == new DateTime(2026, 9, 17, 20, 15, 0) && msg == "Deploy");
        ok(!TextTools.ParseWhen("meet at 5", n0, out w, out msg));
        ok(!TextTools.ParseWhen("the cat sat in the hat", n0, out w, out msg));
        ok(TextTools.LooksSecret("Hunter2!xYz9") && TextTools.LooksSecret("482913") && TextTools.LooksSecret("ghp_abcdef123"));
        ok(!TextTools.LooksSecret("call mom in 20 min") && !TextTools.LooksSecret("https://example.com/a?b=1") && !TextTools.LooksSecret("12*7.5"));
        double v;
        ok(TextTools.Calc("12*7.5", out v) && v == 90);
        ok(TextTools.Calc("(2+3)^2 - 1", out v) && v == 24);
        ok(!TextTools.Calc("2026-09-18", out v) && !TextTools.Calc("18/09/2026", out v) && !TextTools.Calc("hello 2+2", out v));
        int rmv;
        ok(TextTools.CleanLink("https://x.com/p?utm_source=a&id=3&fbclid=z#top", out rmv) == "https://x.com/p?id=3#top" && rmv == 2);
        ok(TextTools.CleanLink("https://youtu.be/abc?si=XYZ", out rmv) == "https://youtu.be/abc" && rmv == 1);
        ok(TextTools.Tidy("  a  line\nbroken\n\nnext   para ") == "a line broken\r\n\r\nnext para");
        var r1 = ParseReminder("every 45m | Drink water", n0);
        ok(r1 != null && r1.Kind == "every" && r1.Next == n0.AddMinutes(45));
        var r2 = ParseReminder("weekdays 09:25 | Standup", new DateTime(2026, 9, 18, 10, 0, 0));   // Friday after standup
        ok(r2 != null && r2.Next == new DateTime(2026, 9, 21, 9, 25, 0));                        // skips the weekend
        var r3 = ParseReminder("2026-09-18 17:40 | call mom", n0);
        ok(r3 != null && r3.Kind == "once" && r3.Next == new DateTime(2026, 9, 18, 17, 40, 0));
        ok(ParseReminder("sometime | nope", n0) == null && ParseReminder("every 20m 09:00-18:00 | eyes", n0).From == 540);
        return fails;
    }

    // ------------------------------------------------------------------ Win32
    delegate IntPtr WndProcD(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [StructLayout(LayoutKind.Sequential)] struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr mem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public int cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NID
    {
        public int cbSize; public IntPtr hWnd; public int uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, int key, byte alpha, int flags);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO li);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreatePolygonRgn(POINT[] pts, int n, int mode);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] static extern bool Polygon(IntPtr dc, POINT[] pts, int n);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT v);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint p, ref RECT r, uint winIni);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr m, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int reserved, IntPtr h, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawText(IntPtr dc, string s, int n, ref RECT r, uint flags);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr id);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int style, int width, int color);
    [DllImport("gdi32.dll")] static extern bool RoundRect(IntPtr dc, int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr dc, int color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool TextOut(IntPtr dc, int x, int y, string s, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("shell32.dll")] static extern bool Shell_NotifyIcon(int op, ref NID data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ExtractIcon(IntPtr inst, string file, int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ShellExecute(IntPtr h, string op, string file, string args, string dir, int show);
}

// ---------------------------------------------------------------------- clipboard text helpers (pure; covered by --selftest)
static class TextTools
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Trackers = { "fbclid", "gclid", "dclid", "msclkid", "yclid", "twclid", "igsh", "igshid", "mc_cid", "mc_eid", "ref_src", "_hsenc", "_hsmktg" };
    static readonly string[] SecretPrefixes = { "sk-", "ghp_", "github_pat_", "AKIA", "xox", "eyJ" };
    static readonly string[] Leads = { "remind me to ", "remind me ", "don't forget to ", "dont forget to ", "to " };

    // Passwords, one-time codes, API keys: never previewed, stored or offered.
    public static bool LooksSecret(string t)
    {
        t = t.Trim();
        if (t.Contains("-----BEGIN")) return true;
        bool oneToken = t.IndexOf(' ') < 0 && t.IndexOf('\n') < 0 && t.IndexOf('\t') < 0;
        if (!oneToken) return false;
        foreach (var p in SecretPrefixes) if (t.StartsWith(p, StringComparison.Ordinal)) return true;
        bool digits = t.Length > 0;
        foreach (char ch in t) if (!char.IsDigit(ch)) digits = false;
        if (digits) return t.Length >= 4 && t.Length <= 8;
        double v;
        if (t.Length < 8 || t.Length > 128 || IsUrl(t) || Calc(t, out v)) return false;
        int lower = 0, upper = 0, digit = 0, sym = 0;
        foreach (char ch in t) { if (char.IsLower(ch)) lower = 1; else if (char.IsUpper(ch)) upper = 1; else if (char.IsDigit(ch)) digit = 1; else sym = 1; }
        return lower + upper + digit + sym >= 3;
    }

    static bool IsUrl(string t)
    {
        return t.IndexOf(' ') < 0 && (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    public static string CleanLink(string text, out int removed)
    {
        removed = 0;
        string url = text.Trim();
        if (!IsUrl(url) || url.IndexOf('\n') >= 0) return text;
        int hash = url.IndexOf('#');
        string frag = hash >= 0 ? url.Substring(hash) : "", head = hash >= 0 ? url.Substring(0, hash) : url;
        int q = head.IndexOf('?');
        if (q < 0) return text;
        string host = head.Substring(0, q).ToLowerInvariant();
        bool siHost = host.Contains("youtube.com") || host.Contains("youtu.be") || host.Contains("spotify.com");
        var kept = new StringBuilder();
        foreach (var pair in head.Substring(q + 1).Split('&'))
        {
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            string key = (eq >= 0 ? pair.Substring(0, eq) : pair).ToLowerInvariant();
            if (key.StartsWith("utm_") || Array.IndexOf(Trackers, key) >= 0 || (siHost && key == "si")) { removed++; continue; }
            if (kept.Length > 0) kept.Append('&');
            kept.Append(pair);
        }
        if (removed == 0) return text;
        return head.Substring(0, q) + (kept.Length > 0 ? "?" + kept.ToString() : "") + frag;
    }

    // + - * / % ^ ( ) and x-ish symbols. Refuses dates (2026-09-18, 18/09/2026) and phone numbers.
    public static bool Calc(string s, out double v)
    {
        v = 0; s = s.Trim();
        if (s.Length == 0 || s.Length > 100) return false;
        int slashes = 0; bool digit = false, strongOp = false;
        foreach (char ch in s)
        {
            if (char.IsDigit(ch)) digit = true;
            else if ("+-*/%^(). \u00D7\u00F7".IndexOf(ch) < 0) return false;
            if (ch == '/') slashes++;
            if (ch == '*' || ch == '^' || ch == '+' || ch == '\u00D7' || ch == '\u00F7' || ch == '%') strongOp = true;
        }
        bool op = strongOp || (slashes == 1) || s.Contains(" - ");
        if (!digit || !op || (slashes >= 2 && !strongOp)) return false;
        string e = s.Replace(" ", "").Replace('\u00D7', '*').Replace('\u00F7', '/');
        int pos = 0;
        try { v = Expr(e, ref pos); } catch { return false; }
        return pos == e.Length && !double.IsNaN(v) && !double.IsInfinity(v);
    }

    static double Expr(string e, ref int p)
    {
        double v = Term(e, ref p);
        while (p < e.Length && (e[p] == '+' || e[p] == '-')) { char o = e[p++]; double r = Term(e, ref p); v = o == '+' ? v + r : v - r; }
        return v;
    }

    static double Term(string e, ref int p)
    {
        double v = Pow(e, ref p);
        while (p < e.Length && (e[p] == '*' || e[p] == '/' || e[p] == '%')) { char o = e[p++]; double r = Pow(e, ref p); v = o == '*' ? v * r : o == '/' ? v / r : v % r; }
        return v;
    }

    static double Pow(string e, ref int p)
    {
        double v = Unary(e, ref p);
        if (p < e.Length && e[p] == '^') { p++; v = Math.Pow(v, Pow(e, ref p)); }
        return v;
    }

    static double Unary(string e, ref int p)
    {
        if (p < e.Length && e[p] == '-') { p++; return -Unary(e, ref p); }
        if (p < e.Length && e[p] == '+') { p++; return Unary(e, ref p); }
        if (p < e.Length && e[p] == '(')
        {
            p++; double v = Expr(e, ref p);
            if (p >= e.Length || e[p] != ')') throw new FormatException();
            p++; return v;
        }
        int st = p;
        while (p < e.Length && (char.IsDigit(e[p]) || e[p] == '.')) p++;
        if (st == p) throw new FormatException();
        return double.Parse(e.Substring(st, p - st), NumberStyles.Float, Inv);
    }

    // Joins lines broken mid-paragraph (PDF copies), collapses runs of spaces, keeps blank-line paragraphs.
    public static string Tidy(string s)
    {
        var outp = new StringBuilder(); var para = new StringBuilder();
        foreach (var raw in s.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) { Flush(outp, para); continue; }
            if (para.Length > 0) para.Append(' ');
            para.Append(line);
        }
        Flush(outp, para);
        var r = new StringBuilder(outp.Length); bool prevSpace = false;
        for (int i = 0; i < outp.Length; i++)
        {
            char ch = outp[i]; bool sp = ch == ' ' || ch == '\t';
            if (sp && prevSpace) continue;
            r.Append(sp ? ' ' : ch); prevSpace = sp;
        }
        return r.ToString();
    }

    static void Flush(StringBuilder o, StringBuilder p)
    {
        if (p.Length == 0) return;
        if (o.Length > 0) o.Append("\r\n\r\n");
        o.Append(p.ToString()); p.Length = 0;
    }

    // "in 20 min", "in 1.5h", "in an hour", "at 5pm", "at 17:30", "tomorrow", "tomorrow at 9am", bare "5:30pm"/"17:30".
    public static bool ParseWhen(string text, DateTime now, out DateTime when, out string msg)
    {
        when = DateTime.MinValue; msg = null;
        if (text == null || text.Length > 200) return false;
        string line = text.Trim();
        int nl = line.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) line = line.Substring(0, nl);
        var words = new List<string>(); var starts = new List<int>(); var ends = new List<int>();
        for (int i = 0; i < line.Length; )
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            int st = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            if (i > st) { words.Add(line.Substring(st, i - st).ToLowerInvariant().TrimEnd(',', '.', '!', '?', ';')); starts.Add(st); ends.Add(i); }
        }
        int from = -1, last = -1;
        for (int k = 0; k < words.Count && from < 0; k++)
        {
            string w = words[k]; int h, m, used;
            if (w == "in" && k + 1 < words.Count)
            {
                double n; string unit;
                if (words[k + 1] == "an" || words[k + 1] == "a") { n = 1; unit = k + 2 < words.Count ? words[k + 2] : ""; used = 2; }
                else if (!SplitNumber(words[k + 1], out n, out unit)) continue;
                else if (unit.Length == 0) { unit = k + 2 < words.Count ? words[k + 2] : ""; used = 2; }
                else used = 1;
                double total = n * UnitMinutes(unit);
                if (total < 1 || total > 30 * 24 * 60) continue;
                when = now.AddMinutes(Math.Round(total)); from = k; last = k + used;
            }
            else if (w == "tomorrow")
            {
                int j = k + 1 < words.Count && words[k + 1] == "at" ? k + 2 : k + 1;
                if (j < words.Count && Clock(words, j, out h, out m, out used)) last = j + used - 1;
                else { h = 9; m = 0; last = k; }
                when = now.Date.AddDays(1).AddHours(h).AddMinutes(m); from = k;
            }
            else if (w == "at" && k + 1 < words.Count && Clock(words, k + 1, out h, out m, out used))
            {
                when = now.Date.AddHours(h).AddMinutes(m);
                if (when <= now) when = when.AddDays(1);
                from = k; last = k + used;
            }
        }
        if (from < 0 && line.Length <= 80)
            for (int k = 0; k < words.Count && from < 0; k++)
            {
                int h, m, used;
                if (!Clock(words, k, out h, out m, out used)) continue;
                when = now.Date.AddHours(h).AddMinutes(m);
                if (when <= now) when = when.AddDays(1);
                from = k; last = k + used - 1;
            }
        if (from < 0) return false;

        string rest = (line.Substring(0, starts[from]) + " " + line.Substring(ends[last])).Trim();
        string lower = rest.ToLowerInvariant();
        foreach (var lead in Leads) if (lower.StartsWith(lead)) { rest = rest.Substring(lead.Length); break; }
        var sb = new StringBuilder(); bool sp = false;
        foreach (char ch in rest) { bool isSp = char.IsWhiteSpace(ch); if (isSp && sp) continue; sb.Append(isSp ? ' ' : ch); sp = isSp; }
        rest = sb.ToString().Trim(' ', ',', '.', ';', ':', '-');
        if (rest.Length == 0) rest = "Reminder";
        msg = rest.Length > 60 ? rest.Substring(0, 60) : rest;
        return true;
    }

    static bool SplitNumber(string w, out double n, out string unit)
    {
        int k = 0;
        while (k < w.Length && (char.IsDigit(w[k]) || w[k] == '.')) k++;
        unit = w.Substring(k); n = 0;
        return k > 0 && double.TryParse(w.Substring(0, k), NumberStyles.Float, Inv, out n);
    }

    static double UnitMinutes(string u)
    {
        switch (u)
        {
            case "m": case "min": case "mins": case "minute": case "minutes": return 1;
            case "h": case "hr": case "hrs": case "hour": case "hours": return 60;
            case "d": case "day": case "days": return 1440;
        }
        return 0;
    }

    // "5pm", "5 pm", "5:30pm", "17:30". A bare "5" is not a time.
    static bool Clock(List<string> w, int j, out int h, out int m, out int used)
    {
        h = m = 0; used = 1;
        string t = w[j], suffix = "";
        if (t.EndsWith("am") || t.EndsWith("pm")) { suffix = t.Substring(t.Length - 2); t = t.Substring(0, t.Length - 2); }
        else if (j + 1 < w.Count && (w[j + 1] == "am" || w[j + 1] == "pm")) { suffix = w[j + 1]; used = 2; }
        int colon = t.IndexOf(':');
        if (colon < 0 && suffix.Length == 0) return false;
        string hs = colon < 0 ? t : t.Substring(0, colon), ms = colon < 0 ? "0" : t.Substring(colon + 1);
        if (hs.Length == 0 || hs.Length > 2 || (colon >= 0 && ms.Length != 2)) return false;
        if (!int.TryParse(hs, NumberStyles.None, Inv, out h) || !int.TryParse(ms, NumberStyles.None, Inv, out m) || m > 59) return false;
        if (suffix.Length > 0) { if (h < 1 || h > 12) return false; if (h == 12) h = 0; if (suffix == "pm") h += 12; }
        else if (h > 23) return false;
        return true;
    }
}

// ---------------------------------------------------------------------- browser address bar via UI Automation
// Titles never say "shorts" or "reels"; only the URL does. The address bar is the first Edit in the
// browser's tree (the toolbar precedes page content), so FindFirst stops early.
class UrlReader
{
    IUIAutomation auto; IntPtr cachedFor; IUIAutomationElement bar; string last = ""; DateTime lastRead;

    public UrlReader()
    {
        try { auto = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))); }
        catch { }
    }

    public string Read(IntPtr h)
    {
        if (auto == null) return "";
        if (h == cachedFor && (DateTime.UtcNow - lastRead).TotalMilliseconds < 1500) return last;
        try
        {
            if (h != cachedFor || bar == null) { Drop(); cachedFor = h; bar = Resolve(h); }
            last = bar == null ? "" : (bar.GetCurrentPropertyValue(30045) as string) ?? "";   // ValueValue
            if (last.Length == 0) Drop();                                                        // stale element: re-resolve next time
        }
        catch { Drop(); last = ""; }
        lastRead = DateTime.UtcNow;
        return last;
    }

    IUIAutomationElement Resolve(IntPtr h)
    {
        var root = auto.ElementFromHandle(h);
        var cond = auto.CreatePropertyCondition(30003, 50004);                                  // ControlType == Edit
        var e = root.FindFirst(4, cond);                                                         // TreeScope_Descendants
        Marshal.ReleaseComObject(cond); Marshal.ReleaseComObject(root);
        if (e == null) return null;
        string name = (e.GetCurrentPropertyValue(30005) as string) ?? "";
        string value = (e.GetCurrentPropertyValue(30045) as string) ?? "";
        if (name.ToLowerInvariant().Contains("address") || (value.Contains(".") && !value.Contains(" "))) return e;
        Marshal.ReleaseComObject(e);
        return null;
    }

    void Drop() { if (bar != null) { try { Marshal.ReleaseComObject(bar); } catch { } } bar = null; }
}

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomation
{
    void _0(); void _1(); void _2();
    IUIAutomationElement ElementFromHandle(IntPtr hwnd);
    void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
    IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationElement
{
    void _0(); void _1();
    IUIAutomationElement FindFirst(int scope, IUIAutomationCondition condition);
    void _3(); void _4(); void _5(); void _6();
    [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
}

[ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationCondition { }

// ---------------------------------------------------------------------- Core Audio (only the vtable slots we call are named)
[StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
[StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; [FieldOffset(8)] public int i; }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection { void GetCount(out uint n); void Item(uint i, out IMMDevice device); }

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    void OpenPropertyStore(int access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore { void _0(); void _1(); void GetValue(ref PROPERTYKEY key, out PROPVARIANT v); }

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation { void GetPeakValue(out float peak); }

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2 { void _0(); void _1(); void GetSessionEnumerator(out IAudioSessionEnumerator e); }

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator { void GetCount(out int n); void GetSession(int i, out IAudioSessionControl2 s); }

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    void GetState(out int state);
    void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10();
    [PreserveSig] int GetProcessId(out uint pid);    // S_OK or AUDCLNT_S_NO_SINGLE_PROCESS
}
