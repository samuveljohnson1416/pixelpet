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
using System.Web.Script.Serialization;

class Rule { public string Label; public int Grace; public string[] Any; }

class Cfg
{
    public int Countdown = 3, Snooze = 5, Focus = 25, Break = 5, Stretch = 90, Water = 45, ScreenTime = 60;
    public bool Claude = true, Push = true, Quiet;
    public string ClipKey = "ctrl+alt+shift+c", SwingKey = "ctrl+alt+shift+w";
    public bool Costumes = true;
    public bool Nag, Wander = true, Sleepy = true, Typing = true, EnterRope = true, Climb = true, Audio = true, Clipboard = true, Hotkey = true, Curious = true, WebTravel = true;
    public double Size = 1;
    public string[] Never = new string[0];
    public Rule[] Rules = new Rule[0];
}

class Reminder { public string Line, Kind, Msg; public DateTime At, Next; public int Every, From = -1, To = -1; }

class AgentSess { public string State = "", Project = "", Who = "Agent"; public DateTime Since, Seen, LastPing; }

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
climb = yes        # climbs the screen edges and walks upside-down along the top
webtravel = yes    # shoots a web to the cursor and swings the pet there (swingkey below)
clipboard = yes    # notices useful copied text (times, links, sums); ignores passwords
hotkey = yes       # turn the two shortcut keys below on or off (restart to apply)
clipkey = ctrl+alt+shift+c    # opens the clipboard menu. ctrl / alt / shift / win + a letter, digit or F-key; off = none
swingkey = ctrl+alt+shift+w   # web-swings the pet to your mouse pointer
curious = yes      # visits the window you're using, comments on it, asks what you're doing
audio = yes        # wears headphones when they're connected; dances to music, takes notes in class/calls
size = 1           # pet size multiplier (restart)
focus = 25         # focus timer minutes
break = 5          # break minutes
stretch = 90       # stand up and stretch after this many minutes of non-stop use (0 = off)
water = 45         # a sip of water after this many minutes of non-stop use (0 = off)
screentime = 60    # says how long you've been on the screen, every this many minutes of non-stop use (0 = off)
agents = yes       # reacts when a coding agent (Claude Code, Codex, Gemini, Antigravity...) works, needs you, or finishes
push = yes         # nudges windows around like furniture now and then; throw it at a window to knock it aside
costumes = yes     # hats and props for the moment: telescope, detective glass, lab flask, wizard, parachute...
quiet = no         # yes = barely talks: still visits and reacts, keeps chit-chat to a minimum

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
                    case "stretch": c.Stretch = Math.Max(0, n); break;
                    case "water": c.Water = Math.Max(0, n); break;
                    case "screentime": c.ScreenTime = Math.Max(0, n); break;
                    case "claude": case "agents": c.Claude = yes; break;
                    case "push": c.Push = yes; break;
                    case "quiet": c.Quiet = yes; break;
                    case "costumes": c.Costumes = yes; break;
                    case "mode": c.Nag = v == "nag"; break;
                    case "wander": c.Wander = yes; break;
                    case "sleepy": c.Sleepy = yes; break;
                    case "typing": c.Typing = yes; break;
                    case "enterrope": c.EnterRope = yes; break;
                    case "climb": c.Climb = yes; break;
                    case "webtravel": c.WebTravel = yes; break;
                    case "clipboard": c.Clipboard = yes; break;
                    case "hotkey": c.Hotkey = yes; break;
                    case "clipkey": c.ClipKey = v; break;
                    case "swingkey": c.SwingKey = v; break;
                    case "curious": c.Curious = yes; break;
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
    const int SIT = 0, WALK = 1, SLEEP = 2, AIR = 3, HELD = 4, RUN = 5, GLARE = 6, SWIPE = 7, TYPE = 8, CLIMB = 9, HANG = 10, SWING = 11, PUSH = 12;
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
    // web travel: a quadratic-bezier swing from wherever the pet is to the cursor, anchored above the midpoint
    static double swingSX, swingSY, swingAX, swingAY, swingTX, swingTY, webCool; static bool webShown;
    static POINT menuOpenPt;
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
    // climbing: which surface the feet are on (0 floor, 1 left edge, 2 right edge, 3 top edge, upside down).
    // x,y is always the contact point; on a wall y runs along it, on the top edge x does.
    static int orient, climbNext; static double climbTarget; static bool climbUp, climbAfterWalk, hangSleep;
    static readonly StringBuilder clsBuf = new StringBuilder(64);
    static string fgProc = "", fgTitle = "", fgUrl = ""; static IntPtr fgHwnd;      // latest foreground snapshot, written by the watcher
    static double curiousT = 90, askT; static string curiousLine, lastActivity = ""; static bool curiousAsk; static DateTime curiousAt;
    static string doing = ""; static DateTime doingUntil;
    // movie night: the flavour guessed from the title, a slow average of the volume to spot jump scares, and timers
    static string movieGenre = ""; static double movieAvg, movieCool, movieChat = 15, movieLoud, movieQuiet, scaredT;
    static double detectiveT, wizardT, paraT; static bool eyeingNow, agentBusy;   // what the pet is dressed for
    static string clipKeyText = "", swingKeyText = "";                        // the shortcuts that actually registered
    static readonly string[] ClipFallbacks = { "ctrl+alt+shift+c", "ctrl+shift+f10", "ctrl+alt+r" };
    static readonly string[] SwingFallbacks = { "ctrl+alt+shift+w", "ctrl+shift+f11", "ctrl+alt+g" };
    static double snackT = 5, snackLeft; static int snackKind;                   // 0 = just holding them, 1 = munching popcorn, 2 = sipping the drink
    static bool MovieOn { get { return doing == "movie" && DateTime.Now < doingUntil; } }
    static readonly Dictionary<string, AgentSess> agents = new Dictionary<string, AgentSess>();   // Claude Code sessions, by session id
    static DateTime lastAgentXp;
    static int useSec, waterSec, stretchSec, sinceNudge = 9999, lastTold, screenToday; static DateTime screenDay;   // break buddy
    static double drinkT, stretchT, clockT, grooveT, ropeWait; static bool grooving; static int webHops;
    static readonly AutoResetEvent askFocus = new AutoResetEvent(false);        // Enter: the watcher finds the focused field
    static bool focusAsk, focusGot; static int focusX, focusY;
    static int closes, pats, focusDone, remDone, clipActs, claudeDone, streak, bestStreak, ach; static DateTime lastDay; static bool nightOwl;
    const int CAP = 0xC86E3C, HEADBAND = 0x3C3CDC;
    static string MemPath; static readonly List<string> memTail = new List<string>();     // the last few memories, for the menu
    static int[] today = new int[7]; static DateTime todayDate, met;                       // today: closes, pats, focus, reminders, clipboard, claude, late
    static readonly List<string> hist = new List<string>();                                // "yyyyMMdd:c,p,f,r,x,k,l" for up to 13 past days
    static bool traitFocused, traitLoved, traitOwl, traitGuard, traitBuddy, traitsReady;
    static IntPtr pushTarget; static double pushAt, pushLeft, pushAcc, pushed; static int pushDir; static RECT pushRect; static DateTime lastPush, diskWarned;
    static readonly List<IntPtr> enumList = new List<IntPtr>();
    static readonly EnumWindowsProc collectCb = CollectCb;
    static readonly string[] PushLines = { "There. Much better.", "Rearranging the furniture.", "Tidy desk, tidy mind.", "Hnngh... done!" };
    static readonly string[] AchNames = { "First patrol", "Doomscroll slayer", "Deep focus", "Focus machine", "Remembered!", "Clipboard pro",
        "Agent buddy", "Pair programmer", "On a roll", "Week warrior", "Night owl", "Companion", "Hero", "Legend" };
    static readonly string[] AchHow = { "close a doomscroll tab", "close 25 of them", "finish a focus session", "finish 25 focus sessions",
        "finish a reminder", "use 10 clipboard actions", "a coding agent finishes a task", "agents finish 100 tasks",
        "3-day streak", "7-day streak", "be active after midnight", "reach level 5", "reach level 20", "reach level 35" };                        // your quick-reply answer and how long it holds
    static readonly string[] Codes = { "{ }", "01", ";", "</>", "#", "=>", "()" };
    static readonly string[] LandingLines = { "Ta-da!", "Stuck the landing!", "Whee, made it!", "Web-slinging pro!", "Boop!" };
    static double secAcc;

    const int CUP = 0xC8D25A, CUPDARK = 0x8C9637, PAPER = 0xF0F5F5, INK = 0xB4AAA0;
    const int ROPE = 0x325A8C, STAR = 0x30D8FF, GREEN = 0x50C850, SIGN = 0x9CF0FF;
    const int LID = 0x4E4646, BASE = 0x322D2D, LOGO = 0xB48C64, GLOW = 0xFFDC8C;
    const int KEY = 0xFF00FF, CORAL = 0x5777D9, ANGRY = 0x4058E8, EYE = 0x141414, DARK = 0x282828, WHITE = 0xFFFFFF, PINK = 0x875FFF;
    static readonly Dictionary<int, IntPtr> brushes = new Dictionary<int, IntPtr>();
    static IntPtr fontBubble, fontSmall, penDark;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") { Environment.Exit(SelfTest()); }
        if (args.Length > 2 && args[0] == "--agent-hook") { ForwardHook(args[1], args[2]); return; }   // run by an agent CLI on its events
        if (args.Length > 0 && args[0] == "--claude-hook") { ForwardHook("claude", ""); return; }        // hooks installed by older builds
        if (args.Length > 1 && (args[0] == "--connect" || args[0] == "--disconnect")) { Environment.Exit(AgentSetup.Run(args[1], args[0] == "--connect")); }
        if (args.Length > 0 && (args[0] == "--connect-claude" || args[0] == "--disconnect-claude")) { Environment.Exit(AgentSetup.Run("claude", args[0] == "--connect-claude")); }
        bool created; var mutex = new Mutex(true, "PixelPetFocus.Single", out created);
        if (!created) { PostMessage(FindWindow("PixelPetFocus", null), 0x8003, IntPtr.Zero, IntPtr.Zero); return; }   // 2nd launch: open Settings

        SetProcessDPIAware();
        S = GetDpiForSystem() / 96.0;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Dir = Path.Combine(appData, "PixelPet Focus");
        try { if (!Directory.Exists(Dir) && Directory.Exists(Path.Combine(appData, "FocusCat Pixel"))) Directory.Move(Path.Combine(appData, "FocusCat Pixel"), Dir); } catch { }   // keep XP from the pre-rename builds
        Directory.CreateDirectory(Dir);
        RulesPath = Path.Combine(Dir, "rules.txt");
        ProgressPath = Path.Combine(Dir, "progress.txt");
        RemPath = Path.Combine(Dir, "reminders.txt");
        MemPath = Path.Combine(Dir, "memories.txt");
        if (!File.Exists(RulesPath)) File.WriteAllText(RulesPath, DefaultRules);
        cfg = Parse(File.ReadAllLines(RulesPath));
        LoadProgress();
        InitMemories();

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
        if (cfg.Hotkey) TakeHotkey(1, cfg.ClipKey, "clipkey", ClipFallbacks, out clipKeyText);
        if (cfg.Hotkey && cfg.WebTravel) TakeHotkey(2, cfg.SwingKey, "swingkey", SwingFallbacks, out swingKeyText);
        lastTick = Environment.TickCount;
        SetTimer(hwnd, (IntPtr)1, 33, IntPtr.Zero);
        var t = new Thread(Watch); t.IsBackground = true; t.Start();
        var at = new Thread(AudioWatch); at.IsBackground = true; at.Start();
        ShowWindow(hwnd, 4);
        if (bubbleT <= 0) Chirp("Hi! I'll keep you focused.", 3);
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
            case 0x8003: OpenSettings(); return IntPtr.Zero;              // from a second launch
            case 0x004A: OnCopyData(l); return (IntPtr)1;                  // WM_COPYDATA from --claude-hook
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
            case 0x0312: if (w.ToInt32() == 2) { POINT hp; GetCursorPos(out hp); StartSwing(hp.X); } else ClipMenu(); return IntPtr.Zero;   // WM_HOTKEY
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
        if (drinkT > 0 && (drinkT -= dt) <= 0) Hearts(2);
        stretchT = Math.Max(0, stretchT - dt); clockT = Math.Max(0, clockT - dt);
        detectiveT = Math.Max(0, detectiveT - dt); wizardT = Math.Max(0, wizardT - dt); paraT = Math.Max(0, paraT - dt);
        clipOfferT -= dt; curiousT -= dt; askT -= dt;
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
            Wellness();
            if (gcSec % 60 == 0) RollDay();
            if (gcSec % 600 == 30) DiskCheck();
            string agentTag = AgentTag();
            if (tagCache == null) tagCache = agentTag;
            if (++gcSec % 10 == 0) GC.Collect();   // .NET otherwise lets short-lived garbage pile up for MBs before collecting
        }

        Bust b; double wr; int wx;
        lock (Sync) { b = pendingBust; pendingBust = null; wr = watchRemaining; wx = watchX; }
        if (b != null && alert == null && state != HELD) BeginAlert(b);
        if (orient != 0 && sign != null && state != AIR && state != HELD) LetGo(null);   // come down to show the sign
        bool eyeing = alert == null && wr > 0 && wr <= 5;                       // it notices before it acts
        eyeingNow = eyeing;
        if (eyeing && state == WALK) SetState(SIT, 2);

        if (clipRetry > 0) ReadClipboard(true);
        POINT c; GetCursorPos(out c);
        DetectTyping(c, dt);
        bool enter = (GetAsyncKeyState(0x0D) & 0x8000) != 0;
        ropeCool -= dt; webCool -= dt;
        if (ropeWait > 0)
        {
            bool got; int fx, fy; lock (Sync) { got = focusGot; fx = focusX; fy = focusY; focusGot = false; }
            if (got && fx != int.MinValue) { ropeWait = 0; Lasso(fx, fy); }
            else if (got || (ropeWait -= dt) <= 0) { ropeWait = 0; POINT mp; GetCursorPos(out mp); Lasso(mp.X, mp.Y); }
        }
        if (enter && !enterDown && cfg.EnterRope && !hidden && alert == null && ropeT < 0 && ropeWait <= 0 && ropeCool <= 0
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
            lastKind = kind; grooving = kind == 1; grooveT = Rand(15, 35);
            if (alert == null && kind == 1) Say("Ooh, music!", 2);
            else if (alert == null && kind == 2) { Say("Class time. Taking notes.", 2.5); if (state == WALK || state == SLEEP) SetState(SIT, 6); }
        }
        if (kind == 1 && (grooveT -= dt) <= 0) { grooving = !grooving; grooveT = grooving ? Rand(15, 35) : Rand(30, 70); }   // dances a while, then a break
        danceLevel = danceLevel * 0.55 + (kind == 1 && grooving ? audioPeak : 0) * 0.45;   // fast attack: bounces on the beat
        if (kind == 1 && grooving && audioPeak > 0.05 && (state == SIT || state == TYPE || state == WALK) && (noteT -= dt) <= 0)
        {
            noteT = Rand(0.6, 1.2);
            parts.Add(new Part { X = x + Rand(-7, 6) * U, Y = y - 11 * U, VY = -35 * S, Life = 1.5, Text = rnd.Next(2) == 0 ? "\u266A" : "\u266B" });
        }
        MovieTick(dt);
        if (keyBurst >= 3 && cfg.Typing && alert == null && (state == SIT || state == WALK || state == SLEEP)) SetState(TYPE, 0);
        lapOpen = state != TYPE ? 0 : Clamp(lapOpen + (lastKeyAgo <= 2.5 ? dt : -dt) / 0.3, 0, 1);
        double lx = c.X, ly = c.Y;
        if (alert != null) { lx = alert.CenterX; ly = y - 50 * U; }
        else if (eyeing) { lx = wx; ly = y - 50 * U; }
        bool near = alert != null || eyeing || Math.Abs(lx - x) + Math.Abs(ly - y) < 450 * S;
        lookX = !near ? (state == WALK ? facing : 0) : lx > x + 4 * U ? 1 : lx < x - 4 * U ? -1 : 0;
        lookY = near && ly < y - 12 * U ? -1 : 0;
        if (state == TYPE) lookX = lookY = 0;                                   // eyes on the screen
        if (orient == 1 || orient == 2)                                         // on a wall the face turns sideways
        {
            bool upward = state == CLIMB ? climbUp : c.Y < y;
            lookX = (orient == 1) == upward ? 1 : -1; lookY = 0;
        }
        else if (orient == 3) lookY = c.Y > y + 12 * U ? -1 : 0;                // hanging: peek down at the cursor

        Ride();
        switch (state)
        {
            case SIT:
                if (curiousLine != null && stateT >= 0.4) DeliverCurious();     // arrived at your window: say it
                if (stateT >= stateDur && sign == null) ChooseNext();          // a reminder sign keeps it put
                break;
            case WALK:
                if (Step(walkTarget, 55 * S * Math.Max(0.5, cfg.Size), dt))
                {
                    if (climbAfterWalk) { climbAfterWalk = false; StartClimb(walkTarget <= (wa.L + wa.R) / 2); }
                    else if (pushTarget != IntPtr.Zero && Math.Abs(walkTarget - pushAt) < 1) StartPush();
                    else { pushTarget = IntPtr.Zero; SetState(SIT, Rand(2, 6)); }
                }
                break;
            case CLIMB: Climb(dt); break;
            case HANG:
                if (hangSleep && (zT -= dt) <= 0) { zT = 1.3; parts.Add(new Part { X = x + (orient == 2 ? -12 : 12) * U, Y = y + (orient == 3 ? 6 : -2) * U, VY = -22 * S, Life = 1.8, Text = "z" }); }
                if (stateT >= stateDur && sign == null) HangNext();
                break;
            case SLEEP:
                zT -= dt;
                if (zT <= 0) { zT = 1.3; parts.Add(new Part { X = x + facing * 5 * U, Y = y - 7 * U, VY = -22 * S, Life = 1.8, Text = "z" }); }
                if (stateT >= stateDur) SetState(SIT, 2);
                break;
            case AIR: Physics(dt); break;
            case PUSH: PushTick(dt); break;
            case SWING:
                {
                    double u = Clamp(stateT / Math.Max(0.01, stateDur), 0, 1);
                    SwingPos(swingSX, swingSY, swingAX, swingAY, swingTX, swingTY, u, out x, out y);
                    if (u >= 1) { x = swingTX; y = swingTY; Land(true); }
                }
                break;
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
                        Award(25); Did(ref closes, 0);
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

        int rx = (int)Math.Round(x), ry = (int)Math.Round(y);
        int wl = orient == 1 ? rx - U : orient == 2 ? rx + U - W : rx - W / 2;
        int wt = orient == 1 || orient == 2 ? ry - H / 2 : orient == 3 ? ry - U : ry + U - H;
        wl = Clamp(wl, wa.L, wa.R - W);
        if (wl != winLeft || wt != winTop) { SetWindowPos(hwnd, IntPtr.Zero, wl, wt, 0, 0, 0x15); winLeft = wl; winTop = wt; }
        if (ropeT >= 0) DrawRope();
        else if (state == SWING) DrawWeb();
        else if (webShown) { ShowWindow(ropeWnd, 0); webShown = false; }
        Render();
    }

    // Web travel: shoot a strand to wherever the cursor is and swing there, on a big arc anchored
    // above the midpoint, so it reads as real travel rather than a teleport. Works from any state the
    // pet can safely leave unattended (walking, sitting, napping, typing, or hanging/climbing a wall).
    static bool StartSwing(int cursorX, bool forAlert = false, bool trip = false)
    {
        if (!cfg.WebTravel || held || hidden) return false;
        if (forAlert) { if (state == SWING || state == AIR || state == HELD) return false; }   // a doomscroll alert: from any calm or wall state
        else
        {
            if (alert != null || sign != null || (webCool > 0 && !trip)) return false;
            if (state != SIT && state != WALK && state != SLEEP && state != TYPE && state != HANG && state != CLIMB) return false;
        }
        double tx = Clamp((double)cursorX, wa.L + 8 * U, wa.R - 8 * U);
        double dist = Math.Abs(tx - x);
        if (dist < 40 * S) { if (!forAlert && !trip) Say("Already here!", 1.2); return false; }
        orient = 0; climbNext = 0; hangSleep = false; climbAfterWalk = false; perch = IntPtr.Zero; ignorePerch = IntPtr.Zero;
        curiousLine = null; askT = 0; bubbleT = 0; pushTarget = IntPtr.Zero;
        swingSX = x; swingSY = y; swingTX = tx; swingTY = wa.B;
        double baseline = Math.Min(swingSY, swingTY);
        double rise = Clamp(dist * 0.45, 80 * S, Math.Max(40 * S, baseline - (wa.T + 14 * U)));
        swingAX = swingSX + (swingTX - swingSX) * 0.5;
        swingAY = baseline - rise;
        facing = swingTX >= swingSX ? 1 : -1;
        SetState(SWING, Clamp(dist / ((forAlert ? 1200 : 1000) * S), 0.35, 0.9));   // quick, and quicker still for an alert
        return true;
    }

    // A single quadratic bezier from start to target through an overhead control point: pure and
    // testable on its own (u=0 -> start, u=1 -> target, u=0.5 rises toward the anchor in between).
    static void SwingPos(double sx0, double sy0, double ax, double ay, double tx, double ty, double u, out double px, out double py)
    {
        double e = u * u * (3 - 2 * u);                                        // ease in/out like a real swing
        double a1 = (1 - e) * (1 - e), b1 = 2 * e * (1 - e), c1 = e * e;
        px = a1 * sx0 + b1 * ax + c1 * tx;
        py = a1 * sy0 + b1 * ay + c1 * ty;
    }

    // The strand itself: a short taut quad from the raised paw to the fixed anchor point, redrawn
    // every frame like the Enter-rope but without its slack/snap-back timing (it stays taut throughout).
    static void DrawWeb()
    {
        webShown = true;
        double hx = x + facing * 6 * U, hy = y - 9 * U, ax = swingAX, ay = swingAY;
        double dist = Math.Sqrt((ax - hx) * (ax - hx) + (ay - hy) * (ay - hy));
        if (dist < 3) { ShowWindow(ropeWnd, 0); webShown = false; return; }
        double th = Math.Max(2, U * 0.4), nx = -(ay - hy) / dist * th / 2, ny = (ax - hx) / dist * th / 2;
        var pts = new POINT[4];
        pts[0].X = (int)(hx + nx); pts[0].Y = (int)(hy + ny);
        pts[1].X = (int)(ax + nx); pts[1].Y = (int)(ay + ny);
        pts[2].X = (int)(ax - nx); pts[2].Y = (int)(ay - ny);
        pts[3].X = (int)(hx - nx); pts[3].Y = (int)(hy - ny);
        int minX = pts[0].X, minY = pts[0].Y, maxX = pts[0].X, maxY = pts[0].Y;
        for (int i = 1; i < 4; i++) { minX = Math.Min(minX, pts[i].X); maxX = Math.Max(maxX, pts[i].X); minY = Math.Min(minY, pts[i].Y); maxY = Math.Max(maxY, pts[i].Y); }
        int ox = minX - 2, oy = minY - 2;
        ropeW = maxX - ox + 3; ropeH = maxY - oy + 3;
        for (int i = 0; i < 4; i++) { pts[i].X -= ox; pts[i].Y -= oy; }
        ropeStar = null;
        SetWindowRgn(ropeWnd, CreatePolygonRgn(pts, 4, 2), false);              // the system owns the region now
        SetWindowPos(ropeWnd, (IntPtr)(-1), ox, oy, ropeW, ropeH, 0x10 | 0x40); // NOACTIVATE | SHOWWINDOW
        PaintRope();
    }

    // Enter: lasso the spot you just typed at. A classic text caret (Notepad, dialogs) is exact. Browsers and modern
    // apps have none, so the watcher thread asks UI Automation for the focused field (the address bar, a chat box,
    // a search box) and the rope lands there. The mouse pointer is only the last resort.
    static void ThrowRope()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == hwnd) return;
        uint pid; var gti = new GUITHREADINFO(); gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        if (GetGUIThreadInfo(GetWindowThreadProcessId(fg, out pid), ref gti) && gti.hwndCaret != IntPtr.Zero)
        {
            POINT p; p.X = gti.rcCaret.L; p.Y = (gti.rcCaret.T + gti.rcCaret.B) / 2;
            if (ClientToScreen(gti.hwndCaret, ref p)) { Lasso(p.X, p.Y); return; }
        }
        lock (Sync) { focusAsk = true; focusGot = false; }
        askFocus.Set(); ropeWait = 0.35;                                         // it answers in a few milliseconds
    }

    static void Lasso(int tx, int ty)
    {
        ropeTx = tx; ropeTy = ty; ropeT = 0; ropeCool = 1.2;
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
        if (webHops > 0) { webHops--; if (WebHop()) return; webHops = 0; }       // the rest of a web-slinging trip
        if (MovieOn && r < 0.8) { SetState(SIT, Rand(10, 20)); return; }        // watch with you, with the odd break
        if (MovieOn) { Chirp(Pick("Be right back!", "Popcorn refill!", "Stretching my legs."), 2); r = rnd.NextDouble() * 0.5; }   // a stroll or a nap
        if (lastKind == 2 || (lastKind == 1 && grooving)) { SetState(SIT, Rand(4, 8)); return; }   // class: stay put; music: dance, between breaks
        if (CuriousVisit()) return;
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
        else if (r < 0.82 && cfg.Wander && cfg.Climb) GoClimb();
        else if (r < 0.86 && cfg.Wander && PlanPush()) { }
        else if (r < 0.9 && cfg.Wander && cfg.WebTravel && WebTrip()) { }
        else if (r < 0.93 && cfg.Wander) { POINT c; GetCursorPos(out c); Walk(c.X); }   // come see what you're doing
        else if (r < 0.97) Hop(0, -520 * S);
        else SetState(SIT, Rand(2, 5));
    }

    // ------------------------------------------------------------------ coding-agent status (idea from AgentPet, MIT)
    // Each agent CLI runs `PixelPetFocus.exe --agent-hook <agent> <state>` on its own events; the state is baked
    // into the command at install time, so nothing has to be guessed here. This short-lived process forwards it
    // over WM_COPYDATA and exits.
    static void ForwardHook(string key, string state)
    {
        string json = "";
        var t = new Thread(delegate () { try { json = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8).ReadToEnd(); } catch { } });
        t.IsBackground = true; t.Start(); t.Join(1500);                          // never hold the agent up
        if (json.Length > 200000) json = json.Substring(0, 200000);
        if (state.Length == 0) state = TextTools.JsonField(json, "hook_event_name");   // old --claude-hook installs send the event name
        if (state.Length == 0) return;
        string sid = First(TextTools.JsonField(json, "session_id"), TextTools.JsonField(json, "sessionId"),
                           TextTools.JsonField(json, "conversation_id"), TextTools.JsonField(json, "conversationId"));
        string cwd = First(TextTools.JsonField(json, "cwd"), TextTools.JsonField(json, "workspace_path"), TextTools.JsonField(json, "workspaceRoot"));
        string[] spec = AgentDefs.Find(key);
        string data = state + "\n" + sid + "\n" + cwd + "\n" + TextTools.JsonField(json, "message") + "\n" + (spec != null ? spec[1] : key);
        IntPtr w = FindWindow("PixelPetFocus", "PixelPet Focus");
        if (w == IntPtr.Zero) return;                                            // pet not running: nothing to do
        var cds = new COPYDATASTRUCT { dwData = (IntPtr)0x5050, cbData = (data.Length + 1) * 2, lpData = Marshal.StringToHGlobalUni(data) };
        IntPtr res;
        SendMessageTimeout(w, 0x004A, IntPtr.Zero, ref cds, 2, 1000, out res);   // SMTO_ABORTIFHUNG
        Marshal.FreeHGlobal(cds.lpData);
    }

    static string First(params string[] v) { foreach (var x in v) if (x != null && x.Length > 0) return x; return ""; }

    static void OnCopyData(IntPtr l)
    {
        var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(l, typeof(COPYDATASTRUCT));
        if (cds.dwData != (IntPtr)0x5050 || cds.cbData <= 0 || cds.cbData > 8192 || cds.lpData == IntPtr.Zero) return;
        var f = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2).TrimEnd('\0').Split('\n');
        if (f.Length >= 4) OnAgentEvent(f[0], f[1], f[2], f[3], f.Length >= 5 && f[4].Length > 0 ? f[4] : "Claude Code");
    }

    static void OnAgentEvent(string ev, string sid, string cwd, string msg, string who)
    {
        if (!cfg.Claude) return;
        DateTime now = DateTime.Now;
        string st = ev == "working" || ev == "waiting" || ev == "done" || ev == "end" ? ev
            : ev == "UserPromptSubmit" || ev == "PreToolUse" ? "working"                    // names from old --claude-hook installs
            : ev == "Notification" ? "waiting" : ev == "Stop" ? "done" : ev == "SessionEnd" ? "end" : "";
        if (st.Length == 0) return;
        string id = who + "|" + (sid.Length > 0 ? sid : cwd);                    // two agents can share a project folder
        if (st == "end") { agents.Remove(id); return; }
        AgentSess a;
        if (!agents.TryGetValue(id, out a)) { a = new AgentSess(); agents[id] = a; }
        a.Project = TextTools.ProjectName(cwd); a.Seen = now; a.Who = who;
        if (a.State != st) { a.State = st; a.Since = now; }
        string proj = a.Project.Length > 0 ? " (" + a.Project + ")" : "";
        bool free = alert == null && sign == null;
        if (st == "waiting")
        {
            if ((now - a.LastPing).TotalSeconds < 60) return;                      // one nudge a minute per session
            a.LastPing = now;
            string text = msg.ToLowerInvariant().Contains("permission") ? who + " needs your OK" + proj + "!" : who + " is waiting for you" + proj + ".";
            MessageBeep(0x30);
            if (hidden) Tray(1, who, text);
            if (!free) return;
            Say(text, 6); joyT = 3;                                              // arms up, waving you over
            if (orient == 0 && (state == SIT || state == WALK || state == SLEEP || state == TYPE)) { POINT c; GetCursorPos(out c); Walk(c.X); }
        }
        else if (st == "done")
        {
            Did(ref claudeDone, 5);
            if (hidden) Tray(1, who, who + " finished" + proj + ".");
            if (!free) return;
            Say(who + " finished" + proj + "!", 4); Hearts(4);
            if (orient == 0 && (state == SIT || state == WALK || state == SLEEP)) Hop(0, -520 * S);
            if ((now - lastAgentXp).TotalSeconds >= 30) { lastAgentXp = now; Award(10); }
        }
    }

    // A tag above the pet while an agent works or waits; also drops sessions that went quiet.
    static string AgentTag()
    {
        DateTime now = DateTime.Now, since = now; int working = 0, waiting = 0; string who = "", waitWho = "";
        List<string> stale = null;
        foreach (var kv in agents)
        {
            var a = kv.Value;
            if (now - a.Seen > TimeSpan.FromHours(3) || (a.State == "done" && now - a.Since > TimeSpan.FromMinutes(15)))
            {
                if (stale == null) stale = new List<string>();
                stale.Add(kv.Key); continue;
            }
            if (a.State == "working" && now - a.Seen > TimeSpan.FromMinutes(30)) a.State = "idle";   // interrupted without a Stop
            if (a.State == "working") { working++; if (a.Since < since) { since = a.Since; who = a.Who; } }
            else if (a.State == "waiting" && now - a.Since < TimeSpan.FromMinutes(3)) { waiting++; waitWho = a.Who; }
        }
        if (stale != null) foreach (var k in stale) agents.Remove(k);
        agentBusy = working > 0;
        if (!cfg.Claude) return null;
        if (waiting == 1) return waitWho + " needs you";
        if (waiting > 1) return waiting + " agents need you";
        if (working == 1) return who + " working " + Dur(now - since);
        if (working > 1) return working + " agents working";
        return null;
    }

    static string Dur(TimeSpan d) { return d.TotalHours >= 1 ? (int)d.TotalHours + "h" + d.Minutes.ToString("00") : (int)d.TotalMinutes + ":" + d.Seconds.ToString("00"); }

    static void AddAgentItems(IntPtr m)
    {
        int shown = 0; DateTime now = DateTime.Now;
        foreach (var kv in agents)
        {
            var a = kv.Value;
            if (a.State.Length == 0) continue;
            string what = a.State == "waiting" ? "needs you" : a.State;
            AppendMenu(m, GRAY, UIntPtr.Zero, Menuish(a.Who + (a.Project.Length > 0 ? "  -  " + a.Project : "") + "  -  " + what + "  " + Dur(now - a.Since)));
            shown++;
        }
        if (shown == 0) AppendMenu(m, GRAY, UIntPtr.Zero, "No agent activity yet");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        int found = 0;
        for (int i = 0; i < AgentDefs.All.Length; i++)
        {
            string[] sp = AgentDefs.All[i];
            if (!AgentDefs.Installed(sp)) continue;
            found++;
            bool on = AgentDefs.Connected(sp);
            AppendMenu(m, on ? CHECK : 0, (UIntPtr)(70 + i), (on ? "Disconnect " : "Connect ") + sp[1] + (on ? "" : "..."));
        }
        if (found == 0) AppendMenu(m, GRAY, UIntPtr.Zero, "No agent CLIs found on this PC");
    }

    // ------------------------------------------------------------------ progress, streaks, achievements, ranks (ideas from AgentPet)
    static string Rank(int lv) { return lv >= 35 ? "Legend" : lv >= 20 ? "Hero" : lv >= 10 ? "Scout" : lv >= 5 ? "Companion" : "Hatchling"; }
    static int NextRank(int lv) { return lv < 5 ? 5 : lv < 10 ? 10 : lv < 20 ? 20 : lv < 35 ? 35 : 0; }

    // Level looks: a sprout, an explorer cap, a hero headband, a crown. Hats step aside for headphones.
    static void DrawRank(int dy, bool phones)
    {
        if (level >= 35) { if (!phones) { Px(4, dy - 1, 6, 1, STAR); Px(4, dy - 2, 1, 1, STAR); Px(6.5, dy - 2, 1, 1, STAR); Px(9, dy - 2, 1, 1, STAR); Px(6.7, dy - 0.8, 0.6, 0.6, PINK); } }
        else if (level >= 20) { Px(2, dy + 1, 10, 0.7, HEADBAND); Px(12, dy + 1.1, 1.4, 0.5, HEADBAND); Px(12.8, dy + 1.5, 1, 0.5, HEADBAND); }
        else if (level >= 10) { if (!phones) { Px(3, dy - 1, 8, 1, CAP); Px(facing > 0 ? 10 : 1, dy - 0.35, 3, 0.4, CAP); } }
        else if (level >= 5) { if (!phones) { Px(6.7, dy - 1.2, 0.6, 1.2, GREEN); Px(7.3, dy - 1.9, 1.4, 0.8, GREEN); Px(5.4, dy - 1.6, 1.3, 0.6, GREEN); } }
    }

    static void Did(ref int counter, int day)
    {
        counter++;
        RollDay();
        today[day]++;
        if (DateTime.Now.Hour < 4) today[6] = 1;
        DateTime d = DateTime.Today;
        if (lastDay != d)
        {
            streak = lastDay == d.AddDays(-1) ? streak + 1 : 1; lastDay = d;
            if (streak > bestStreak) { bestStreak = streak; if (streak >= 3) Remember("New best streak: " + streak + " days in a row"); }
        }
        if (DateTime.Now.Hour < 4) nightOwl = true;
        CheckAch();
        SaveProgress();
    }

    static bool AchMet(int i)
    {
        switch (i)
        {
            case 0: return closes >= 1; case 1: return closes >= 25;
            case 2: return focusDone >= 1; case 3: return focusDone >= 25;
            case 4: return remDone >= 1; case 5: return clipActs >= 10;
            case 6: return claudeDone >= 1; case 7: return claudeDone >= 100;
            case 8: return streak >= 3; case 9: return streak >= 7; case 10: return nightOwl;
            case 11: return level >= 5; case 12: return level >= 20; case 13: return level >= 35;
        }
        return false;
    }

    static void CheckAch()
    {
        for (int i = 0; i < AchNames.Length; i++)
        {
            if ((ach & (1 << i)) != 0 || !AchMet(i)) continue;
            ach |= 1 << i;
            Remember("Unlocked \"" + AchNames[i] + "\": " + AchHow[i]);
            Say("Achievement: " + AchNames[i] + "!", 4); joyT = 2.5; Hearts(5); MessageBeep(0x40); wizardT = 5;
            SaveProgress();
            return;                                                              // one at a time; the next shows on the next event
        }
    }

    static void AddStatsItems(IntPtr m)
    {
        int next = NextRank(level), cur = lastDay >= DateTime.Today.AddDays(-1) ? streak : 0, got = 0;
        for (int i = 0; i < AchNames.Length; i++) if ((ach & (1 << i)) != 0) got++;
        AppendMenu(m, GRAY, UIntPtr.Zero, "Rank: " + Rank(level) + (next > 0 ? "  (next rank at level " + next + ")" : "  (top rank)"));
        AppendMenu(m, GRAY, UIntPtr.Zero, "Tabs closed: " + closes + "     Pats: " + pats);
        AppendMenu(m, GRAY, UIntPtr.Zero, "Focus sessions: " + focusDone + "     Reminders done: " + remDone);
        AppendMenu(m, GRAY, UIntPtr.Zero, "Clipboard actions: " + clipActs + "     Agent tasks: " + claudeDone);
        AppendMenu(m, GRAY, UIntPtr.Zero, "Streak: " + cur + (cur == 1 ? " day" : " days") + "   (best " + bestStreak + ")");
        AppendMenu(m, GRAY, UIntPtr.Zero, "Personality: " + Traits() + "     Together for " + ((DateTime.Today - met).Days + 1) + " days");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        AppendMenu(m, GRAY, UIntPtr.Zero, "Achievements " + got + " / " + AchNames.Length);
        for (int i = 0; i < AchNames.Length; i++)
            AppendMenu(m, GRAY | ((ach & (1 << i)) != 0 ? CHECK : 0), UIntPtr.Zero, AchNames[i] + "  -  " + AchHow[i]);
    }

    static void SaveProgress()
    {
        try
        {
            File.WriteAllText(ProgressPath, "level=" + level + "\r\nxp=" + xp + "\r\ncloses=" + closes + "\r\npats=" + pats + "\r\nfocus=" + focusDone
                + "\r\nreminders=" + remDone + "\r\nclipboard=" + clipActs + "\r\nclaude=" + claudeDone + "\r\nstreak=" + streak + "\r\nbest=" + bestStreak
                + "\r\nlastday=" + (lastDay == DateTime.MinValue ? "" : lastDay.ToString("yyyy-MM-dd", Inv)) + "\r\nowl=" + (nightOwl ? 1 : 0) + "\r\nach=" + ach
                + "\r\nmet=" + (met == DateTime.MinValue ? "" : met.ToString("yyyy-MM-dd", Inv))
                + "\r\nday=" + (todayDate == DateTime.MinValue ? "" : EncodeDay(todayDate, today)) + "\r\nhist=" + string.Join(";", hist.ToArray()) + "\r\nscreen=" + screenDay.ToString("yyyyMMdd", Inv) + ":" + screenToday + "\r\n");
        }
        catch { }
    }

    static void LoadProgress()
    {
        try
        {
            string text = File.ReadAllText(ProgressPath).Trim();
            if (!text.Contains("=")) { var p = text.Split(' '); level = Math.Max(1, int.Parse(p[0])); xp = int.Parse(p[1]); return; }   // old "level xp" file
            foreach (var line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim(); int n; int.TryParse(v, NumberStyles.Integer, Inv, out n);
                switch (k)
                {
                    case "level": level = Math.Max(1, n); break; case "xp": xp = Math.Max(0, n); break;
                    case "closes": closes = n; break; case "pats": pats = n; break; case "focus": focusDone = n; break;
                    case "reminders": remDone = n; break; case "clipboard": clipActs = n; break; case "claude": claudeDone = n; break;
                    case "streak": streak = n; break; case "best": bestStreak = n; break; case "owl": nightOwl = n == 1; break; case "ach": ach = n; break;
                    case "lastday": DateTime d; if (DateTime.TryParseExact(v, "yyyy-MM-dd", Inv, DateTimeStyles.None, out d)) lastDay = d; break;
                    case "met": DateTime m; if (DateTime.TryParseExact(v, "yyyy-MM-dd", Inv, DateTimeStyles.None, out m)) met = m; break;
                    case "day": DateTime dd; int[] t; if (DecodeDay(v, out dd, out t)) { todayDate = dd; today = t; } break;
                    case "screen": if (v.Length > 9 && v.StartsWith(DateTime.Today.ToString("yyyyMMdd", Inv) + ":")) { int.TryParse(v.Substring(9), out screenToday); screenDay = DateTime.Today; } break;
                    case "hist": foreach (var e in v.Split(';')) { DateTime hd; int[] ht; if (DecodeDay(e, out hd, out ht)) hist.Add(e); } break;
                }
            }
        }
        catch { }
    }

    // Registers the shortcut the user asked for. Another app may own it, so a couple of spare combos are
    // tried before giving up; the one that stuck is shown in the menu.
    static void TakeHotkey(int id, string wanted, string setting, string[] fallbacks, out string text)
    {
        text = "";
        var tries = new List<string>();
        if (wanted != null) tries.Add(wanted.Trim());
        foreach (var f in fallbacks) tries.Add(f);
        string first = tries.Count > 0 ? tries[0] : "";
        if (first == "off" || first == "none" || first == "no") return;          // deliberately disabled
        foreach (var combo in tries)
        {
            uint mods, vk;
            if (!TextTools.ParseHotkey(combo, out mods, out vk)) continue;
            if (!RegisterHotKey(hwnd, id, mods | 0x4000, vk)) continue;          // MOD_NOREPEAT
            text = TextTools.HotkeyText(mods, vk);
            if (!combo.Equals(first, StringComparison.OrdinalIgnoreCase))
                Say(first + " is taken, so I'm using " + text + ". Change it with " + setting + " in the settings.", 7);
            return;
        }
        Say("Couldn't register a shortcut for " + setting + ": every choice is taken. Pick another in the settings.", 7);
    }

    // ------------------------------------------------------------------ costumes: a hat and a prop for the moment
    // 1 explorer + telescope, 2 detective, 3 lab flask, 4 wizard, 5 parachute, 6 briefcase, 7 coffee, 8 map, 9 thinking, 10 ideas
    static int Costume()
    {
        if (!cfg.Costumes || state == HELD || alert != null || sign != null || drinkT > 0 || stretchT > 0 || clockT > 0) return 0;
        if (paraT > 0 && state == AIR) return 5;
        if (wizardT > 0) return 4;
        if (askT > 0) return 9;
        if (clipOfferT > 0 && clipText != null) return 10;
        if (detectiveT > 0 && (state == SIT || state == WALK)) return 2;
        if (eyeingNow) return 1;
        if (state != SIT) return 0;                                              // the rest are sitting-still props
        if (agentBusy) return 3;
        if (focusPhase == 1) return 7;
        if (doing == "study") return 8;
        if (doing == "work") return 6;
        return 0;
    }

    static void DrawCostume(int dy, int body, int c)
    {
        int f = facing > 0 ? 1 : -1;
        double side = f > 0 ? 11.6 : -1.6;                                       // where a held prop sits
        switch (c)
        {
            case 1:                                                              // explorer hat and a telescope
                Px(1.6, dy - 0.7, 10.8, 0.6, PAPER); Px(3.4, dy - 2.2, 7.2, 1.6, PAPER); Px(3.4, dy - 1.1, 7.2, 0.45, ANGRY);
                Px(f > 0 ? 9.8 : -1.8, dy + 2.1, 6, 1.1, DARK);
                Px(f > 0 ? 15.4 : -2.6, dy + 1.9, 0.9, 1.5, PAPER);
                break;
            case 2:                                                              // detective hat and magnifying glass
                Px(2.6, dy - 1.5, 8.8, 1.2, DARK); Px(1.4, dy - 0.4, 11.2, 0.5, DARK);
                Px(side, dy + 1.6, 2.8, 2.8, DARK); Px(side + 0.5, dy + 2.1, 1.8, 1.8, PAPER);
                Px(f > 0 ? side + 1 : side + 0.8, dy + 4.4, 0.6, 1.6, ROPE);
                break;
            case 3:                                                              // lab cap and a bubbling flask
                Px(3.2, dy - 1.5, 7.6, 1.5, WHITE); Px(3.8, dy - 2.1, 6.4, 0.7, WHITE);
                Px(side + 0.2, dy + 3.4, 2.4, 0.6, WHITE); Px(side + 0.5, dy + 2.6, 1.8, 0.9, PINK);
                Px(side + 1, dy + 1.4, 0.8, 1.3, WHITE);
                if ((int)(animT * 3) % 2 == 0) Px(side + 1.1, dy + 0.4, 0.6, 0.6, PINK);
                break;
            case 4:                                                              // wizard hat and wand
                Px(6.2, dy - 4, 1.6, 1, CAP); Px(5.4, dy - 3, 3.2, 1, CAP); Px(4.6, dy - 2, 4.8, 1, CAP);   // a proper point
                Px(3.4, dy - 1, 7.2, 0.7, PAPER);
                Px(6.8, dy - 2.8, 0.7, 0.7, STAR);                                   // a star on the hat
                Px(f > 0 ? 11.8 : -1.4, dy + 1.6, 2.6, 0.45, ROPE);
                Px(f > 0 ? 14.2 : -1.8, dy + 0.9, 1, 1, STAR);
                break;
            case 5:                                                              // parachute
                Px(4.2, dy - 8, 5.6, 1, CAP); Px(2.8, dy - 7, 8.4, 1, CAP); Px(1.8, dy - 6, 10.4, 1, CAP);   // domed canopy
                Px(4.4, dy - 7, 1.5, 1, WHITE); Px(8.1, dy - 7, 1.5, 1, WHITE);
                Px(2.2, dy - 5, 0.35, 4.2, DARK); Px(7, dy - 5, 0.35, 4.2, DARK); Px(11.5, dy - 5, 0.35, 4.2, DARK);
                break;
            case 6: Px(side, dy + 4.2, 2.6, 2, DARK); Px(side + 0.8, dy + 3.6, 1, 0.6, DARK); break;   // briefcase
            case 7:                                                              // coffee
                Px(side, dy + 3.6, 2, 1.9, WHITE); Px(side + 2, dy + 4, 0.5, 0.9, WHITE); Px(side + 0.25, dy + 3.75, 1.5, 0.5, ROPE);
                if ((int)(animT * 2) % 2 == 0) Px(side + 0.8, dy + 2.6, 0.5, 0.7, WHITE);
                break;
            case 8:                                                              // a map held up
                Px(3.2, dy + 3.2, 7.6, 3.6, PAPER); Px(6.8, dy + 3.2, 0.3, 3.6, INK);
                Px(4.2, dy + 4.2, 0.8, 0.8, ANGRY); Px(8.6, dy + 5.4, 0.8, 0.8, GREEN); Px(5.4, dy + 5.6, 0.7, 0.7, CAP);
                break;
            case 9: Px(f > 0 ? 9.6 : 2.6, dy + 3.4, 1.8, 1.3, body); break;      // paw to the chin, thinking
            case 10:                                                             // ideas popping overhead
                Bulb(2.4, dy - 2.6, STAR); Bulb(6.4, dy - 3.4, GREEN); Bulb(10.2, dy - 2.6, PINK);
                break;
        }
    }

    static void Bulb(double col, double row, int colour)
    {
        Px(col, row, 1.2, 1.3, colour); Px(col + 0.3, row + 1.3, 0.6, 0.4, DARK);
    }

    // ------------------------------------------------------------------ quiet mode
    static void Chirp(string text, double seconds) { if (!cfg.Quiet) Say(text, seconds); }   // optional chit-chat; `quiet = yes` drops it

    // ------------------------------------------------------------------ memories and habits ("grows from your days")
    static void Remember(string text)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv) + " | " + text.Replace('\r', ' ').Replace('\n', ' ');
        memTail.Add(line);
        if (memTail.Count > 12) memTail.RemoveAt(0);
        try
        {
            if (!File.Exists(MemPath)) File.WriteAllText(MemPath, "# PixelPet Focus memory book: one memory per line, newest at the bottom.\r\n");
            File.AppendAllText(MemPath, line + "\r\n");
        }
        catch { }
    }

    static void InitMemories()
    {
        try
        {
            if (File.Exists(MemPath))
            {
                string[] all = File.ReadAllLines(MemPath);
                if (all.Length > 600) { var keep = new string[500]; Array.Copy(all, all.Length - 500, keep, 0, 500); File.WriteAllLines(MemPath, keep); all = keep; }
                for (int i = Math.Max(0, all.Length - 12); i < all.Length; i++) if (!all[i].StartsWith("#") && all[i].Trim().Length > 0) memTail.Add(all[i]);
            }
        }
        catch { }
        if (met == DateTime.MinValue) { met = DateTime.Today; Remember("We met. Hi, I'm your new pet!"); }
        RollDay();
        ComputeTraits(false); traitsReady = true;
        SaveProgress();
    }

    // At the first activity of a new day (or once a minute), yesterday becomes a memory.
    static void RollDay()
    {
        DateTime d = DateTime.Today;
        if (todayDate == d) return;
        if (todayDate != DateTime.MinValue)
        {
            string sum = DaySummary(todayDate, today);
            if (sum != null) { hist.Add(EncodeDay(todayDate, today)); Remember(sum); }
            while (hist.Count > 13) hist.RemoveAt(0);
        }
        today = new int[7]; todayDate = d;
        if (traitsReady) { ComputeTraits(true); SaveProgress(); }
    }

    static string EncodeDay(DateTime d, int[] t)
    {
        var sb = new StringBuilder(d.ToString("yyyyMMdd", Inv)).Append(':');
        for (int i = 0; i < t.Length; i++) { if (i > 0) sb.Append(','); sb.Append(t[i].ToString(Inv)); }
        return sb.ToString();
    }

    static bool DecodeDay(string e, out DateTime d, out int[] t)
    {
        t = new int[7]; d = DateTime.MinValue;
        int colon = e.IndexOf(':');
        if (colon != 8 || !DateTime.TryParseExact(e.Substring(0, 8), "yyyyMMdd", Inv, DateTimeStyles.None, out d)) return false;
        var f = e.Substring(9).Split(',');
        for (int i = 0; i < f.Length && i < 7; i++) int.TryParse(f[i], NumberStyles.Integer, Inv, out t[i]);
        return true;
    }

    static string DaySummary(DateTime d, int[] t)
    {
        var bits = new List<string>();
        if (t[0] > 0) bits.Add("closed " + t[0] + (t[0] == 1 ? " doomscroll tab" : " doomscroll tabs"));
        if (t[2] > 0) bits.Add(t[2] + (t[2] == 1 ? " focus session" : " focus sessions"));
        if (t[3] > 0) bits.Add(t[3] + (t[3] == 1 ? " reminder done" : " reminders done"));
        if (t[5] > 0) bits.Add("agents finished " + t[5] + (t[5] == 1 ? " task" : " tasks"));
        if (t[1] > 0) bits.Add(t[1] + (t[1] == 1 ? " pat" : " pats"));
        if (t[4] > 0) bits.Add(t[4] + (t[4] == 1 ? " clipboard help" : " clipboard helps"));
        if (bits.Count == 0) return null;
        return d.ToString("MMM d", Inv) + ": " + string.Join(", ", bits.ToArray()) + "." + (t[6] > 0 ? " Stayed up late." : "");
    }

    // Personality from the last 7 days. New traits become memories, and two of them show on the pet.
    static void ComputeTraits(bool announce)
    {
        int f = today[2], p = today[1], c = today[0], k = today[5], late = today[6];
        foreach (var e in hist)
        {
            DateTime d; int[] t;
            if (!DecodeDay(e, out d, out t) || (DateTime.Today - d).TotalDays > 6) continue;
            f += t[2]; p += t[1]; c += t[0]; k += t[5]; late += t[6];
        }
        Trait(ref traitFocused, f >= 5, "Focused", "lots of focus sessions this week (look, glasses)", announce);
        Trait(ref traitLoved, p >= 40, "Loved", "so many pats this week (rosy cheeks!)", announce);
        Trait(ref traitOwl, late >= 3, "a Night owl", "we stayed up late a lot this week", announce);
        Trait(ref traitGuard, c >= 10, "a Guardian", "closed 10+ doomscroll tabs this week", announce);
        Trait(ref traitBuddy, k >= 20, "an Agent buddy", "agents finished 20+ tasks this week", announce);
    }

    static void Trait(ref bool field, bool now, string name, string why, bool announce)
    {
        if (now && !field && announce) { Remember("Became " + name + ": " + why); Chirp("I think I'm becoming " + name + ".", 3); }
        field = now;
    }

    static string Traits()
    {
        var t = new List<string>();
        if (traitFocused) t.Add("Focused"); if (traitLoved) t.Add("Loved"); if (traitOwl) t.Add("Night owl");
        if (traitGuard) t.Add("Guardian"); if (traitBuddy) t.Add("Agent buddy");
        return t.Count == 0 ? "still getting to know you" : string.Join(", ", t.ToArray());
    }

    static void DrawTraits(int dy)
    {
        if (traitLoved) { Px(2.2, dy + 4.6, 1.3, 0.6, PINK); Px(10.5, dy + 4.6, 1.3, 0.6, PINK); }   // rosy cheeks, just under the glasses
        if (traitFocused)                                                        // study glasses
        {
            Px(3, dy + 1.4, 3, 0.35, DARK); Px(3, dy + 4.1, 3, 0.35, DARK); Px(3, dy + 1.4, 0.35, 3, DARK); Px(5.65, dy + 1.4, 0.35, 3, DARK);
            Px(8, dy + 1.4, 3, 0.35, DARK); Px(8, dy + 4.1, 3, 0.35, DARK); Px(8, dy + 1.4, 0.35, 3, DARK); Px(10.65, dy + 1.4, 0.35, 3, DARK);
            Px(6, dy + 2.2, 2, 0.35, DARK);
        }
    }

    static void AddMemoryItems(IntPtr m)
    {
        int shown = 0;
        for (int i = memTail.Count - 1; i >= 0 && shown < 10; i--, shown++)
        {
            string line = memTail[i]; DateTime when; int bar = line.IndexOf(" | ");
            string label = bar == 16 && DateTime.TryParseExact(line.Substring(0, 16), "yyyy-MM-dd HH:mm", Inv, DateTimeStyles.None, out when)
                ? when.ToString("MMM d", Inv) + "  -  " + line.Substring(19) : line;
            AppendMenu(m, GRAY, UIntPtr.Zero, Menuish(label.Length > 90 ? label.Substring(0, 90) + "..." : label));
        }
        if (shown == 0) AppendMenu(m, GRAY, UIntPtr.Zero, "No memories yet");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        AppendMenu(m, 0, (UIntPtr)66, "Open memory book...");
    }

    // ------------------------------------------------------------------ tools and a quick system check
    static string BatteryText()
    {
        SYSTEM_POWER_STATUS ps;
        if (!GetSystemPowerStatus(out ps) || (ps.BatteryFlag & 128) != 0 || ps.BatteryLifePercent > 100) return "No battery";
        return "Battery " + ps.BatteryLifePercent + "%" + (ps.ACLineStatus == 1 ? " (charging)" : "");
    }

    static bool DiskInfo(out double freeGb, out double totalGb)
    {
        ulong avail, total, free; freeGb = totalGb = 0;
        if (!GetDiskFreeSpaceEx("C:\\", out avail, out total, out free)) return false;
        freeGb = avail / 1073741824.0; totalGb = total / 1073741824.0;
        return totalGb > 0;
    }

    static int RamLoad() { var ms = new MEMORYSTATUSEX(); ms.dwLength = 64; return GlobalMemoryStatusEx(ref ms) ? (int)ms.dwMemoryLoad : -1; }

    static string UptimeText() { var t = TimeSpan.FromMilliseconds(GetTickCount64()); return "Up for " + (t.Days > 0 ? t.Days + "d " : "") + t.Hours + "h " + t.Minutes + "m"; }

    static void AddToolItems(IntPtr m)
    {
        double free, total; int ram = RamLoad();
        AppendMenu(m, GRAY, UIntPtr.Zero, BatteryText());
        AppendMenu(m, GRAY, UIntPtr.Zero, DiskInfo(out free, out total) ? "Disk C: " + free.ToString("0", Inv) + " GB free of " + total.ToString("0", Inv) + " GB" : "Disk C: unknown");
        if (ram >= 0) AppendMenu(m, GRAY, UIntPtr.Zero, "Memory in use: " + ram + "%");
        AppendMenu(m, GRAY, UIntPtr.Zero, UptimeText());
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        AppendMenu(m, 0, (UIntPtr)60, "System check");
        AppendMenu(m, 0, (UIntPtr)61, "Screenshot (snip)");
        AppendMenu(m, 0, (UIntPtr)62, "Task Manager");
        AppendMenu(m, 0, (UIntPtr)63, "Calculator");
        AppendMenu(m, 0, (UIntPtr)64, "Notepad");
        AppendMenu(m, 0, (UIntPtr)65, "Lock PC");
    }

    static void SystemCheck()
    {
        double free, total; int ram = RamLoad(); var worries = new List<string>();
        bool disk = DiskInfo(out free, out total);
        if (disk && (free < 5 || free / total < 0.08)) worries.Add("disk C: is nearly full");
        if (ram >= 90) worries.Add("memory is almost used up");
        if (GetTickCount64() > 7UL * 24 * 3600 * 1000) worries.Add("a restart soon would help");
        SYSTEM_POWER_STATUS ps;
        if (GetSystemPowerStatus(out ps) && (ps.BatteryFlag & 128) == 0 && ps.BatteryLifePercent <= 20 && ps.ACLineStatus != 1) worries.Add("battery is low");
        string report = BatteryText() + "\n" + (disk ? "C: " + free.ToString("0", Inv) + " GB free" : "C: ?") + (ram >= 0 ? " \u00B7 RAM " + ram + "%" : "") + "\n" + UptimeText();
        Say(report + "\n" + (worries.Count == 0 ? "All good!" : "Hmm: " + string.Join(", ", worries.ToArray()) + "."), 7);
        if (worries.Count == 0) { joyT = 1.5; Hearts(2); } else angryT = 1;
    }

    static void DiskCheck()
    {
        double free, total;
        if (diskWarned == DateTime.Today || !DiskInfo(out free, out total) || (free >= 5 && free / total >= 0.08)) return;
        diskWarned = DateTime.Today;
        Say("Disk C: is almost full: " + free.ToString("0.0", Inv) + " GB left.", 5); angryT = 1;
        Remember("Warned you: disk C: had only " + free.ToString("0.0", Inv) + " GB left");
    }

    // ------------------------------------------------------------------ windows as furniture
    // Windows the pet may shove: normal, visible, not maximised/fullscreen, not topmost/tool/click-through, not ours.
    static bool Pushable(IntPtr h, out RECT r)
    {
        r = new RECT();
        if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h) || IsZoomed(h) || Cloaked(h) || !Frame(h, out r)) return false;
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid == GetCurrentProcessId()) return false;
        int ex = GetWindowLong(h, -20);
        if ((ex & 0x8) != 0 || (ex & 0x80) != 0 || (ex & 0x20) != 0) return false;   // topmost, tool window, click-through
        if (r.R - r.L >= wa.R - wa.L - 4 && r.B - r.T >= wa.B - wa.T - 4) return false;
        clsBuf.Length = 0; GetClassName(h, clsBuf, 64);
        string cls = clsBuf.ToString();
        return cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd" && cls != "Progman" && cls != "WorkerW";
    }

    static bool Cloaked(IntPtr h) { int c; return DwmGetWindowAttribute(h, 14, out c, 4) == 0 && c != 0; }   // hidden UWP / other desktops

    static bool CollectCb(IntPtr h, IntPtr l) { enumList.Add(h); return true; }

    // The top-most real window at a point (our own layered windows don't count).
    static IntPtr TopWindowAt(int px, int py)
    {
        enumList.Clear(); EnumWindows(collectCb, IntPtr.Zero);
        uint me = GetCurrentProcessId();
        foreach (var h in enumList)
        {
            if (!IsWindowVisible(h) || IsIconic(h) || Cloaked(h) || (GetWindowLong(h, -20) & 0x20) != 0) continue;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == me) continue;
            RECT r;
            if (Frame(h, out r) && px >= r.L && px < r.R && py >= r.T && py < r.B) return h;
        }
        return IntPtr.Zero;
    }

    // Always async (a hung app can't freeze the pet), and always leaves a grabbable strip on screen.
    static bool MoveWindowBy(IntPtr h, int dx)
    {
        RECT wr;
        if (dx == 0 || !GetWindowRect(h, out wr)) return false;
        int w = wr.R - wr.L, nl = Clamp(wr.L + dx, wa.L - w + (int)(120 * S), wa.R - (int)(120 * S));
        if (nl == wr.L) return false;
        SetWindowPos(h, IntPtr.Zero, nl, wr.T, 0, 0, 0x4215);                   // NOSIZE|NOZORDER|NOACTIVATE|NOOWNERZORDER|ASYNCWINDOWPOS
        return true;
    }

    // Thrown sideways into a window: it gets knocked over and the pet bounces off.
    static void BumpWindow(double dt)
    {
        int dir = vx > 0 ? 1 : -1;
        double lead = x + dir * 7 * U, prevLead = lead - vx * dt;
        IntPtr h = TopWindowAt((int)lead, (int)(y - 4 * U)); RECT r;
        if (!Pushable(h, out r)) return;
        double edge = dir > 0 ? r.L : r.R;
        if (dir > 0 ? (prevLead > edge + 1 || lead < edge) : (prevLead < edge - 1 || lead > edge)) return;   // not its side we just crossed
        MoveWindowBy(h, (int)Clamp(vx * 0.06, -120 * S, 120 * S));
        x = edge - dir * 7 * U; vx = -vx * 0.35; sqK = 0.6; sqT = 0.2;
        parts.Add(new Part { X = edge, Y = y - 7 * U, VY = -30 * S, Life = 0.8, Text = "bonk!" });
    }

    // Now and then: walk up to a window resting on the taskbar and shove it a little way along.
    static bool PlanPush()
    {
        DateTime now = DateTime.Now;
        if (!cfg.Push || perch != IntPtr.Zero || orient != 0 || focusPhase == 1 || doing == "work" || doing == "study" || now - lastPush < TimeSpan.FromMinutes(10)) return false;
        IntPtr fg = GetForegroundWindow(), best = IntPtr.Zero; RECT br = new RECT(); double bestD = double.MaxValue, bestAt = 0; int bestDir = 0;
        enumList.Clear(); EnumWindows(collectCb, IntPtr.Zero);
        var cands = enumList.ToArray();
        foreach (var h in cands)
        {
            RECT r;
            if (h == fg || !Pushable(h, out r) || r.B < wa.B - 8 * U || r.B > wa.B + 4 || r.R - r.L < 200 * S) continue;
            bool fromLeft = Math.Abs(x - r.L) <= Math.Abs(x - r.R);
            for (int pass = 0; pass < 2; pass++, fromLeft = !fromLeft)
            {
                double at = fromLeft ? r.L - 7 * U : r.R + 7 * U; int dir = fromLeft ? 1 : -1;
                double room = dir > 0 ? wa.R - r.R : r.L - wa.L;
                if (at < MinX() || at > MaxX() || room < 60 * S) continue;
                if (TopWindowAt(fromLeft ? r.L + 3 : r.R - 4, (int)(r.B - 3 * U)) != h) continue;   // that corner is under another window
                double dist = Math.Abs(at - x);
                if (dist < bestD) { bestD = dist; best = h; br = r; bestAt = at; bestDir = dir; }
                break;
            }
        }
        if (best == IntPtr.Zero) return false;
        pushTarget = best; pushRect = br; pushDir = bestDir;
        Walk(bestAt); pushAt = walkTarget;
        return true;
    }

    static void StartPush()
    {
        RECT r; IntPtr h = pushTarget;
        if (!Pushable(h, out r) || h == GetForegroundWindow() || r.L != pushRect.L || r.B != pushRect.B) { pushTarget = IntPtr.Zero; SetState(SIT, 2); return; }
        facing = pushDir; pushLeft = Rand(40, 90) * S; pushAcc = 0; pushed = 0; lastPush = DateTime.Now;
        SetState(PUSH, 0);
        Chirp("Hnngh!", 1);
    }

    static void PushTick(double dt)
    {
        if (stateT < 0.5) return;                                               // brace first
        RECT r;
        if (pushLeft <= 0 || !Pushable(pushTarget, out r) || pushTarget == GetForegroundWindow()) { EndPush(); return; }
        pushAcc += 30 * S * dt;
        int move = (int)pushAcc;
        if (move == 0) return;
        pushAcc -= move;
        if (!MoveWindowBy(pushTarget, pushDir * move)) { EndPush(); return; }
        x += pushDir * move; pushLeft -= move; pushed += move;
    }

    static void EndPush()
    {
        pushTarget = IntPtr.Zero;
        if (pushed > 0) { Chirp(PushLines[rnd.Next(PushLines.Length)], 2); joyT = 1; }
        SetState(SIT, Rand(2, 4));
    }

    // ------------------------------------------------------------------ stretch nudge (idea from AgentPet's break reminder)
    static uint IdleMs() { var li = new LASTINPUTINFO(); li.cbSize = 8; GetLastInputInfo(ref li); return (uint)Environment.TickCount - li.dwTime; }

    // Break buddy: counts non-stop use (a 5-minute pause resets it), sends you to drink and to stretch, and now and
    // then says how long you've been at it. Nudges wait for a free moment: not in a focus session, a movie, a meeting
    // or a presentation, and never over something it's already saying.
    static void Wellness()
    {
        if (screenDay != DateTime.Today) { screenDay = DateTime.Today; screenToday = 0; }
        if (IdleMs() > 5 * 60 * 1000) { useSec = waterSec = stretchSec = lastTold = 0; return; }
        useSec++; waterSec++; stretchSec++; sinceNudge++; screenToday++;
        if (screenToday % 300 == 0) SaveProgress();
        if (focusPhase == 1 || MovieOn || alert != null || sign != null || bubbleT > 0 || state == HELD || state == AIR || state == SWING || Busy()) return;
        bool water = cfg.Water > 0 && waterSec >= cfg.Water * 60, stretch = cfg.Stretch > 0 && stretchSec >= cfg.Stretch * 60;
        int every = cfg.ScreenTime * 60;
        if (stretch || water)
        {
            string line = TextTools.Span(useSec) + (stretch ? " without a break. Stand up and stretch" + (water ? ", and have some water!" : "!") : " on screen. Time for a sip of water!");
            if (stretch) stretchSec = 0;
            if (water) waterSec = 0;
            sinceNudge = 0; MessageBeep(0x40);
            if (hidden) { Tray(1, stretch ? "Stretch break" : "Water break", line); return; }
            Say(line, 7); pushTarget = IntPtr.Zero;
            if (orient != 0) LetGo(null); else SetState(SIT, 6);
            if (stretch) stretchT = 5; else drinkT = 4.5;
        }
        else if (every > 0 && useSec / every > lastTold && sinceNudge > 600 && !cfg.Quiet && !hidden)
        {
            lastTold = useSec / every; sinceNudge = 0; clockT = 4;
            Say(TextTools.Span(useSec) + " on screen without a break" + (screenToday > useSec + 300 ? " (" + TextTools.Span(screenToday) + " today)." : "."), 5);
            if (orient == 0) SetState(SIT, 4);
        }
    }

    static bool Busy() { int q; return (SHQueryUserNotificationState(out q) == 0 && q >= 2 && q <= 4) || TextTools.Activity(fgProc, fgTitle, fgUrl) == "meeting"; }

    // Now and then it goes web-slinging: two or three quick swings across the screen, then carries on.
    static bool WebTrip()
    {
        if (focusPhase == 1 || doing == "work" || doing == "study" || MovieOn || grooving) return false;
        webHops = rnd.Next(1, 3);
        if (WebHop()) { Chirp("Thwip!", 1); return true; }
        webHops = 0; return false;
    }

    static bool WebHop()
    {
        double tx = x;
        for (int i = 0; i < 4 && Math.Abs(tx - x) < (wa.R - wa.L) * 0.25; i++) tx = Rand(wa.L + 12 * U, wa.R - 12 * U);   // somewhere a good way off
        return StartSwing((int)tx, false, true);
    }

    // ------------------------------------------------------------------ curious visits
    // Every few minutes it walks (or jumps) to the window you're using and reacts to what it is,
    // from the app name, title and URL the watcher already reads. Nothing is captured or sent anywhere.
    static bool CuriousVisit()
    {
        DateTime now = DateTime.Now;
        if (doing.Length > 0 && now >= doingUntil) doing = "";
        if (!cfg.Curious || curiousT > 0 || orient != 0 || alert != null || sign != null || focusPhase == 1 || doing == "leave") return false;
        IntPtr h; string proc, title, url;
        lock (Sync) { h = fgHwnd; proc = fgProc; title = fgTitle; url = fgUrl; }
        curiousT = doing == "work" || doing == "study" ? Rand(900, 1500) : doing == "break" ? Rand(90, 200) : Rand(180, 420);
        if (h == IntPtr.Zero || h == hwnd || h != GetForegroundWindow()) return false;
        string act = TextTools.Activity(proc, title, url);
        if (act == "meeting") return false;                                     // never chirp during a call
        bool changed = act != lastActivity; lastActivity = act;
        curiousAsk = !cfg.Quiet && doing.Length == 0 && (act.Length == 0 || (changed && rnd.NextDouble() < 0.5));
        curiousLine = curiousAsk ? "What are you up to?  (click me)" : TextTools.Comment(act, doing, rnd);
        if (curiousLine == null) return false;
        curiousAt = now; detectiveT = 8;                                         // out comes the magnifying glass
        if (!JumpOntoWindow())                                                  // hop onto its title bar, or walk underneath it
        {
            RECT r;
            if (!Frame(h, out r)) { curiousLine = null; return false; }
            Walk((r.L + r.R) / 2);
        }
        return true;
    }

    static void DeliverCurious()
    {
        string line = curiousLine; curiousLine = null;
        if ((DateTime.Now - curiousAt).TotalSeconds > 20 || alert != null || sign != null) return;   // took too long getting there
        lookY = -1;
        if (curiousAsk)
        {
            Say(line, 12); askT = 12;
            parts.Add(new Part { X = x + facing * 3 * U, Y = y - 14 * U, VY = -14 * S, Life = 1.6, Text = "?" });
        }
        else Chirp(line, 3.5);
    }

    // Movie night: reacts to how loud the scene gets (jump scares, explosions, laughs), in the flavour of the title.
    static string Pick(params string[] a) { return a[rnd.Next(a.Length)]; }
    static void MovieTick(double dt)
    {
        if (!MovieOn) { if (movieGenre.Length > 0) movieGenre = ""; scaredT = 0; snackKind = 0; return; }
        scaredT = Math.Max(0, scaredT - dt); movieCool -= dt; movieChat -= dt;
        string g = TextTools.Genre(fgTitle); if (g.Length > 0) movieGenre = g;       // sticky: the title may change to "Netflix" in fullscreen
        double pk = cfg.Audio ? audioPeak : 0;
        bool calm = alert == null && (state == SIT || state == TYPE) && sign == null;
        bool spike = pk > 0.12 && pk > movieAvg * 2.5 && movieCool <= 0 && movieLoud > 20;   // needs 20 s of sound first: no scare on the opening dialogue
        movieAvg += (pk - movieAvg) * Math.Min(1, dt * 0.5);
        if (pk > 0.01) { movieQuiet = 0; movieLoud += dt; }
        else if ((movieQuiet += dt) > 20 && movieLoud > 1800) { movieLoud = 0; Chirp("Credits! Was it good?", 3); Hearts(3); joyT = 1.5; }
        if (!calm) { snackKind = 0; return; }
        Snacks(dt);
        if (spike) { movieCool = 7; Startle(); }
        else if (movieChat <= 0) { movieChat = Rand(35, 70); Vibe(); }
    }

    // Between reactions it keeps helping itself: a handful of popcorn, then a sip through the straw.
    static void Snacks(double dt)
    {
        if (snackKind != 0)
        {
            snackLeft -= dt;
            if (snackLeft > 0) return;
            snackKind = 0; snackT = Rand(6, 14);
            return;
        }
        if ((snackT -= dt) > 0) return;
        if (rnd.NextDouble() < 0.6)
        {
            snackKind = 1; snackLeft = Rand(1.8, 3);                             // a few handfuls
            parts.Add(new Part { X = x + Rand(-1, 2) * U, Y = y - 7 * U, VY = -26 * S, Life = 0.8, Text = "•" });
        }
        else { snackKind = 2; snackLeft = Rand(1.6, 2.4); parts.Add(new Part { X = x - 5 * U, Y = y - 7 * U, VY = -22 * S, Life = 1, Text = "slurp" }); }
    }

    static void Startle()
    {
        if (MovieOn && snackKind == 0 && rnd.NextDouble() < 0.7)                  // jumped: the popcorn goes flying
            for (int i = 0; i < 5; i++)
                parts.Add(new Part { X = x + Rand(-2, 3) * U, Y = y - 7 * U, VY = -Rand(40, 80) * S, Life = Rand(0.7, 1.2), Text = "•" });
        sqK = 0.8; sqT = 0.25;
        parts.Add(new Part { X = x, Y = y - 12 * U, VY = -30 * S, Life = 0.8, Text = "!" });
        bool ground = orient == 0 && perch == IntPtr.Zero;
        switch (movieGenre)
        {
            case "horror": scaredT = 2.5; Chirp(Pick("AAAH!", "Nope nope nope!", "Who turned off the lights?!"), 2); if (ground) Hop(0, -420 * S); break;
            case "comedy": joyT = 1.5; Chirp(Pick("HAHA!", "Good one!", "Hehehe!"), 1.8); break;
            case "romance": joyT = 1.5; Hearts(4); Chirp(Pick("Awww!", "Kiss! Kiss!", "So sweet!"), 2); break;
            case "action": Chirp(Pick("BOOM!", "Whoa!", "Did you see that?!"), 1.8); if (ground) Hop(0, -300 * S); break;
            case "anim": joyT = 1.5; Chirp(Pick("Wheee!", "Yay!", "Again, again!"), 1.8); break;
            default: Chirp(Pick("Whoa!", "Did that just happen?!"), 1.8); break;
        }
    }

    static void Vibe()
    {
        switch (movieGenre)
        {
            case "horror": scaredT = 2; Chirp(Pick("Is it safe to look?", "Tell me when it's over.", "I'm not scared. You're scared."), 2.5); break;
            case "romance": Hearts(3); Chirp(Pick("Aww, they're so cute together.", "*sniff*", "Kiss already!"), 2.5); break;
            case "comedy": joyT = 1.2; Chirp(Pick("Ha ha ha!", "I can't breathe!", "*giggles*"), 2); break;
            case "action": Chirp(Pick("Go go go!", "Behind you!", "Epic!"), 2); break;
            case "anim": joyT = 1.2; Hearts(2); Chirp(Pick("So pretty!", "I love this one.", "Sing along!"), 2.5); break;
            default: Chirp(Pick("*munch munch*", "Shh, best part!", "No spoilers!", "Pass the popcorn."), 2); break;
        }
        parts.Add(new Part { X = x + Rand(-3, 3) * U, Y = y - 8 * U, VY = -45 * S, Life = 0.9, Text = "\u2022" });   // a kernel pops
    }

    static void AddDoingItems(IntPtr m)
    {
        string now = doing.Length == 0 ? "" : "  (now: " + doing + ")";
        AppendMenu(m, GRAY, UIntPtr.Zero, "What are you doing?" + now);
        AppendMenu(m, doing == "work" ? CHECK : 0, (UIntPtr)40, "Working");
        AppendMenu(m, 0, (UIntPtr)41, "Working, start a focus timer");
        AppendMenu(m, doing == "study" ? CHECK : 0, (UIntPtr)42, "Studying");
        AppendMenu(m, doing == "break" ? CHECK : 0, (UIntPtr)43, "Taking a break");
        AppendMenu(m, doing == "browse" ? CHECK : 0, (UIntPtr)44, "Just browsing");
        AppendMenu(m, doing == "leave" ? CHECK : 0, (UIntPtr)45, "Leave me alone (1 hour)");
        AppendMenu(m, MovieOn ? CHECK : 0, (UIntPtr)46, "Movie night (3 hours, pauses patrol)");
    }

    static void AskMenu()
    {
        askT = 0;
        IntPtr m = CreatePopupMenu();
        AddDoingItems(m);
        IntPtr prev; int cmd = Popup(m, out prev);
        Restore(prev);
        if (cmd >= 40 && cmd <= 46) Answer(cmd);
    }

    static void Answer(int cmd)
    {
        DateTime now = DateTime.Now; askT = 0; bubbleT = 0;
        if (doing == "movie" && cmd != 46) lock (Sync) snoozeUntil = DateTime.MinValue;   // leaving movie night ends its patrol pause
        switch (cmd)
        {
            case 40: doing = "work"; doingUntil = now.AddMinutes(45); curiousT = Rand(900, 1500); Say("Got it. I'll keep it down.", 2.5); break;
            case 41:
                doing = "work"; doingUntil = now.AddMinutes(cfg.Focus + cfg.Break); curiousT = Rand(900, 1500);
                if (focusPhase == 0) { focusPhase = 1; focusEnd = now.AddMinutes(cfg.Focus); }
                Say("Focus mode. I'm watching.", 2.5);
                break;
            case 42: doing = "study"; doingUntil = now.AddMinutes(45); curiousT = Rand(900, 1500); Say("Study hard! I'll guard you.", 2.5); break;
            case 43: doing = "break"; doingUntil = now.AddMinutes(Math.Max(5, cfg.Break)); curiousT = Rand(60, 120); Say("Break time! Stretch those paws.", 2.5); Hearts(3); break;
            case 44: doing = "browse"; doingUntil = now.AddMinutes(30); curiousT = Rand(240, 480); Say("Have fun. No doomscrolling!", 2.5); break;
            case 45: doing = "leave"; doingUntil = now.AddHours(1); curiousT = 3600; Say("Okay. Quiet for an hour.", 2); return;
            case 46:
                if (MovieOn) { doing = ""; lock (Sync) snoozeUntil = DateTime.MinValue; Say("Movie's over. Back on patrol.", 2.5); return; }
                doing = "movie"; doingUntil = now.AddHours(3); curiousT = 3 * 3600;
                movieGenre = ""; movieAvg = movieLoud = movieQuiet = scaredT = 0; movieChat = 15; movieCool = 0;
                lock (Sync) snoozeUntil = doingUntil;
                if (state == WALK || state == SLEEP) SetState(SIT, 10);
                Say("Movie night! Popcorn ready.", 3); Hearts(3); return;
        }
        joyT = 1.2;
    }

    static void Walk(double t) { walkTarget = Clamp(t, MinX(), MaxX()); climbAfterWalk = false; SetState(WALK, 0); }

    // ------------------------------------------------------------------ climbing the screen edges
    static void GoClimb()
    {
        if (perch != IntPtr.Zero) return;
        bool left = x - wa.L < wa.R - x;
        Walk(left ? MinX() : MaxX());
        climbAfterWalk = true;
    }

    static void StartClimb(bool left)
    {
        perch = IntPtr.Zero; orient = left ? 1 : 2;
        x = left ? wa.L : wa.R; y = wa.B - 7 * U;
        bool toTop = rnd.NextDouble() < 0.5;
        climbTarget = toTop ? wa.T + 7 * U : Rand(wa.T + 20 * U, wa.B - 25 * U);
        climbNext = toTop ? 1 : 0;
        SetState(CLIMB, 0);
    }

    static void Climb(double dt)
    {
        double speed = 45 * S;
        if (orient == 3)
        {
            if (!Step(climbTarget, speed, dt)) return;
            if (climbNext == 2)                                                 // round the top corner and head down
            {
                orient = x < (wa.L + wa.R) / 2 ? 1 : 2; x = orient == 1 ? wa.L : wa.R;
                y = wa.T + 7 * U; climbTarget = wa.B - 7 * U; climbNext = 3;
            }
            else SetState(HANG, Rand(2, 5));
            return;
        }
        double d = climbTarget - y;
        climbUp = d < 0;
        if (Math.Abs(d) > speed * dt) { y += (d > 0 ? 1 : -1) * speed * dt; return; }
        y = climbTarget;
        bool left = orient == 1;
        if (climbNext == 1)                                                     // over the top edge, upside down
        {
            orient = 3; x = left ? wa.L + 7 * U : wa.R - 7 * U; y = wa.T; facing = left ? 1 : -1;
            climbTarget = Rand(wa.L + 25 * U, wa.R - 25 * U); climbNext = 0;
        }
        else if (climbNext == 3)                                                // back on the floor
        {
            orient = 0; climbNext = 0; x = left ? wa.L + 8 * U : wa.R - 8 * U; y = wa.B;
            SetState(SIT, Rand(2, 4));
        }
        else SetState(HANG, Rand(2, 5));
    }

    static void HangNext()
    {
        hangSleep = false;
        double r = rnd.NextDouble();
        if (orient == 3)
        {
            if (r < 0.35) { climbTarget = Rand(wa.L + 20 * U, wa.R - 20 * U); climbNext = 0; SetState(CLIMB, 0); }
            else if (r < 0.55 && cfg.Sleepy) { hangSleep = true; SetState(HANG, Rand(10, 20)); }   // bat nap
            else if (r < 0.8) LetGo("Wheee!");
            else { climbTarget = x < (wa.L + wa.R) / 2 ? wa.L + 7 * U : wa.R - 7 * U; climbNext = 2; SetState(CLIMB, 0); }
            return;
        }
        if (r < 0.3) { climbTarget = wa.T + 7 * U; climbNext = 1; SetState(CLIMB, 0); }
        else if (r < 0.5) { climbTarget = Rand(wa.T + 7 * U, wa.B - 20 * U); climbNext = 0; SetState(CLIMB, 0); }
        else if (r < 0.75) { climbTarget = wa.B - 7 * U; climbNext = 3; SetState(CLIMB, 0); }
        else LetGo("Wheee!");
    }

    // Let go of a wall or the top edge: back to upright, then gravity does the rest.
    static void LetGo(string say)
    {
        if (orient == 1) x = wa.L + 8 * U; else if (orient == 2) x = wa.R - 8 * U; else if (orient == 3) y = wa.T + 10 * U;
        vx = orient == 1 ? 120 * S : orient == 2 ? -120 * S : 0; vy = 0;
        orient = 0; climbNext = 0; hangSleep = false; climbAfterWalk = false;
        if (say != null) Say(say, 1.2);
        SetState(AIR, 0);
    }

    // Thrown hard enough into a screen edge, it grabs on instead of bouncing.
    static bool GrabEdge(double minX, double maxX)
    {
        bool left = x < minX && vx < -700 * S, right = x > maxX && vx > 700 * S, top = y < wa.T + 10 * U && vy < -900 * S;
        if (!cfg.Climb || (!left && !right && !top)) return false;
        sqK = 0.6; sqT = 0.25; vx = vy = 0; climbNext = 0; perch = IntPtr.Zero;
        if (top && !left && !right) { orient = 3; x = Clamp(x, wa.L + 7 * U, wa.R - 7 * U); y = wa.T; }
        else { orient = left ? 1 : 2; x = left ? wa.L : wa.R; y = Clamp(y - 5 * U, wa.T + 7 * U, wa.B - 7 * U); }
        Say("Gotcha!", 1.2);
        SetState(HANG, Rand(1.5, 3));
        return true;
    }
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
        if (GrabEdge(minX, maxX)) return;
        if (cfg.Push && Math.Abs(vx) > 500 * S) BumpWindow(dt);
        if (x < minX) { x = minX; vx = -vx * 0.5; }
        if (x > maxX) { x = maxX; vx = -vx * 0.5; }
        if (y < wa.T + 10 * U) { y = wa.T + 10 * U; if (vy < 0) vy = 0; }
        if (cfg.Costumes && vy > 700 * S && wa.B - y > 22 * U) paraT = 4;        // long drop: out comes the parachute
        if (paraT > 0 && vy > 240 * S) vy = 240 * S;                             // ...and it floats down
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

    static void Land(bool webby = false)
    {
        if (webby)
        {
            // a cute, deliberate landing rather than the usual fall: a solid squash, a happy line, a
            // scatter of dust and a couple of hearts, then a short cooldown before another swing.
            sqK = 0.85; sqT = 0.25; vx = 0; vy = 0; webCool = 1.0;
            if (alert == null) { joyT = 1.2; if (webHops == 0) { Chirp(LandingLines[rnd.Next(LandingLines.Length)], 1.8); Hearts(2); } }
            for (int i = 0; i < 5; i++)
                parts.Add(new Part { X = x + Rand(-9, 9) * U, Y = y - Rand(0, 1.5) * U, VY = -Rand(18, 40) * S, Life = Rand(0.5, 0.9), Text = "\u00B7" });
            if (alert != null)
            {
                if (alert.CenterX == int.MinValue || Math.Abs(alert.CenterX - x) < 60 * S) StartGlare(); else SetState(RUN, 0);
            }
            else SetState(SIT, webHops > 0 ? 0.2 : Rand(1.5, 3));               // mid-trip: straight on to the next swing
            return;
        }
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
        orient = 0; climbNext = 0; hangSleep = false; climbAfterWalk = false;   // picked off the wall: upright again
        pushTarget = IntPtr.Zero; webHops = 0;
        curiousLine = null;
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
        if (askT > 0) { AskMenu(); return; }
        if (state == SLEEP) { angryT = 1.5; Say("Hmph. I was napping.", 2); SetState(SIT, 2); return; }
        if (hangSleep) { hangSleep = false; angryT = 1.5; Say("Hmph. Bat nap ruined.", 2); SetState(HANG, 2); return; }
        joyT = 1.6; Hearts(3);
        if (state != AIR && state != TYPE && orient == 0) Hop(0, -420 * S);
        if ((DateTime.Now - lastPat).TotalSeconds >= 4) { lastPat = DateTime.Now; Award(5); Did(ref pats, 1); }
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
        SaveProgress();
        parts.Add(new Part { X = x, Y = y - 11 * U, VY = -40 * S, Life = 1.4, Text = "+" + n + " XP" });
        if (gained > 0)
        {
            string was = Rank(level - gained);
            Say(Rank(level) != was ? "Level " + level + "! Evolved into a " + Rank(level) + "!" : "Level " + level + "!", 3.5);
            wizardT = 5;                                                         // wizard hat for the level-up sparkle
            joyT = 3; Hearts(6);
            Remember("Grew to level " + level + (Rank(level) != was ? " and became a " + Rank(level) : ""));
            CheckAch();
        }
    }

    // hooks for the Settings window (Settings.cs runs on its own thread; cfg is volatile, patrol is volatile)
    internal static void OpenSettings() { Settings.Show(RulesPath, cfg, ApplySaved); }
    static void ApplySaved(Cfg c) { cfg = c; }
    internal static bool PatrolFlag(bool? set) { if (set.HasValue) patrol = set.Value; return patrol; }
    internal static bool AutoStartFlag(bool? set) { return AutoStart(set); }
    internal static Rule[] DefaultRuleSet() { return Parse(DefaultRules.Split('\n')).Rules; }

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
            focusPhase = 2; focusEnd = DateTime.Now.AddMinutes(cfg.Break); Did(ref focusDone, 2);
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
        AppendMenu(m, GRAY, UIntPtr.Zero, "Level " + level + " " + Rank(level) + "   " + xp + " / " + Cost(level) + " XP");
        AppendMenu(m, GRAY, UIntPtr.Zero, "Screen time: " + TextTools.Span(useSec) + " without a break, " + TextTools.Span(screenToday) + " today");
        IntPtr stats = CreatePopupMenu();
        AddStatsItems(stats);
        AppendMenu(m, POPUP, Sub(stats), "Stats && achievements");
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
        if (cfg.Climb) AppendMenu(m, 0, (UIntPtr)26, orient != 0 ? "Come down" : "Climb the wall");
        if (cfg.WebTravel) AppendMenu(m, 0, (UIntPtr)27, "Web-swing here" + (swingKeyText.Length > 0 ? "  (" + swingKeyText + ")" : ""));
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
        IntPtr dm = CreatePopupMenu();
        AddDoingItems(dm);
        AppendMenu(m, POPUP, Sub(dm), "What I'm doing");
        IntPtr cm = CreatePopupMenu();
        AddAgentItems(cm);
        AppendMenu(m, POPUP, Sub(cm), "AI agents");
        IntPtr tm = CreatePopupMenu();
        AddToolItems(tm);
        AppendMenu(m, POPUP, Sub(tm), "Tools");
        IntPtr mm = CreatePopupMenu();
        AddMemoryItems(mm);
        AppendMenu(m, POPUP, Sub(mm), "Memories");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        AppendMenu(m, 0, (UIntPtr)20, "Settings...");
        AppendMenu(m, auto ? CHECK : 0, (UIntPtr)21, "Start with Windows");
        AppendMenu(m, 0, (UIntPtr)22, hidden ? "Show pet" : "Hide pet");
        AppendMenu(m, SEP, UIntPtr.Zero, null);
        AppendMenu(m, 0, (UIntPtr)23, "Quit");
        IntPtr prev; int cmd = Popup(m, out prev);
        if (cmd != 20 && cmd != 24 && (cmd < 61 || cmd > 66)) Restore(prev);   // launched apps and Notepad take focus themselves
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
            case 20: OpenSettings(); break;
            case 26:
                if (orient != 0) { if (state == CLIMB || state == HANG) LetGo("Okay, okay."); }
                else if (perch != IntPtr.Zero) HopDown();
                else if (state == SIT || state == WALK || state == SLEEP) { GoClimb(); Say("To the wall!", 1.5); }
                break;
            case 27: StartSwing(menuOpenPt.X); break;
            case 21: AutoStart(!auto); break;
            case 22: ToggleHidden(); break;
            case 23: DestroyWindow(hwnd); break;
            case 24: LoadReminders(); ShellExecute(IntPtr.Zero, "open", "notepad.exe", "\"" + RemPath + "\"", null, 1); break;
            case 25: pauseRecurring = !pauseRecurring; Say(pauseRecurring ? "Recurring reminders paused." : "Recurring reminders on.", 2); break;
            case 60: SystemCheck(); break;
            case 61: ShellExecute(IntPtr.Zero, "open", "ms-screenclip:", null, null, 1); break;
            case 62: ShellExecute(IntPtr.Zero, "open", "taskmgr.exe", null, null, 1); break;
            case 63: ShellExecute(IntPtr.Zero, "open", "calc.exe", null, null, 1); break;
            case 64: ShellExecute(IntPtr.Zero, "open", "notepad.exe", null, null, 1); break;
            case 65: LockWorkStation(); break;
            case 66: ShellExecute(IntPtr.Zero, "open", "notepad.exe", "\"" + MemPath + "\"", null, 1); break;
            case 70: case 71: case 72: case 73: case 74: case 75:
                string[] sp = AgentDefs.All[cmd - 70];
                ShellExecute(IntPtr.Zero, "open", System.Reflection.Assembly.GetEntryAssembly().Location,
                    (AgentDefs.Connected(sp) ? "--disconnect " : "--connect ") + sp[0], null, 1);
                break;
            case 40: case 41: case 42: case 43: case 44: case 45: case 46: Answer(cmd); break;
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
        menuOpenPt = c;                                                          // before the mouse moves onto the menu itself
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
        if (!hintShown) { hintShown = true; Chirp("Click me for options", 2.5); }
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
        if (clipKeyText.Length > 0) AppendMenu(m, GRAY, UIntPtr.Zero, "Shortcut: " + clipKeyText);
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
        joyT = 1; Did(ref clipActs, 4);
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
        Did(ref remDone, 3);
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
        alert = b; bubbleT = 0; curiousLine = null; askT = 0; webHops = 0;
        if (state == SWING) return;                                             // Land() sees the alert and carries on from there
        if (cfg.Wander && b.CenterX != int.MinValue && Math.Abs(Clamp((double)b.CenterX, wa.L + 8 * U, wa.R - 8 * U) - x) > 150 * S && StartSwing(b.CenterX, true)) return;
        if (orient != 0) LetGo(null);                                           // drops, then Land() sends it running
        else if (perch != IntPtr.Zero) HopDown();
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
            askFocus.WaitOne(1000);
            try
            {
                bool ask; lock (Sync) { ask = focusAsk; focusAsk = false; }
                if (ask) { int fx, fy; bool found = reader.FocusPoint(out fx, out fy); lock (Sync) { focusGot = true; focusX = found ? fx : int.MinValue; focusY = fy; } }
                DateTime st = File.GetLastWriteTimeUtc(RulesPath);
                if (st != stamp) { cfg = Parse(File.ReadAllLines(RulesPath)); stamp = st; }

                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero || h == hwnd) continue;
                if (h != lastH) { lastH = h; pendingKey = ""; }
                string title = Title(h);
                uint pid; GetWindowThreadProcessId(h, out pid);
                string proc = ProcName(pid);
                string url = Array.IndexOf(Browsers, proc) >= 0 ? reader.Read(h) : "";
                lock (Sync) { fgHwnd = h; fgProc = proc; fgTitle = title; fgUrl = url; }

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

    // one sprite cell: 14 columns x 9 rows, feet on row 9, squashed about the contact point (cx, by).
    // orient rotates the whole grid in 90-degree steps, so pixel art stays crisp on walls and upside down.
    static void Px(double col, double row, double w, double h, int color)
    {
        double a0 = (col - 7) * U * sx, a1 = (col + w - 7) * U * sx;           // across the body
        double d0 = (9 - row) * U * sy, d1 = (9 - row - h) * U * sy;           // away from the surface
        double X0, X1, Y0, Y1;
        switch (orient)
        {
            case 1: X0 = cx + d0; X1 = cx + d1; Y0 = by - a0; Y1 = by - a1; break;   // left edge
            case 2: X0 = cx - d0; X1 = cx - d1; Y0 = by + a0; Y1 = by + a1; break;   // right edge
            case 3: X0 = cx + a0; X1 = cx + a1; Y0 = by + d0; Y1 = by + d1; break;   // top edge
            default: X0 = cx + a0; X1 = cx + a1; Y0 = by - d0; Y1 = by - d1; break;  // floor
        }
        int l = (int)Math.Round(Math.Min(X0, X1)), t = (int)Math.Round(Math.Min(Y0, Y1));
        Box(l, t, (int)Math.Round(Math.Max(X0, X1)) - l, (int)Math.Round(Math.Max(Y0, Y1)) - t, color);
    }

    static void Render()
    {
        if (hidden || mdc == IntPtr.Zero) return;
        Box(0, 0, W, H, KEY);
        cx = x - winLeft; by = y - winTop;
        double k = sqT > 0 ? sqK * sqT / 0.25 : 0;
        sx = 1 + 0.16 * k; sy = 1 - 0.22 * k;
        if (state == HELD) { sx = 0.94; sy = 1.06; }
        bool vibing = lastKind == 1 && grooving && (state == SIT || state == TYPE) && danceLevel > 0.04;
        if (vibing) { double d = Math.Min(1, danceLevel * 1.6); sx *= 1 + 0.07 * d; sy *= 1 - 0.11 * d; }
        double reach = stretchT > 0 && orient == 0 && state == SIT ? Math.Min(1, Math.Min((5 - stretchT) / 0.6, stretchT / 0.6)) : 0;   // stretch: eases up, holds, eases down
        if (reach > 0) { sx *= 1 - 0.06 * reach; sy *= 1 + 0.16 * reach; }
        bool drinking = drinkT > 0 && state == SIT && orient == 0 && alert == null, clock = clockT > 0 && state == SIT && orient == 0 && alert == null;
        bool phonesOn = wasPhones || (cfg.Audio && lastKind == 1);                // its own headset while the music plays

        bool sleeping = state == SLEEP || hangSleep;
        bool angry = angryT > 0 || state == RUN || state == GLARE || state == SWIPE || (state == SWING && alert != null);
        bool joy = (joyT > 0 || (vibing && state == SIT) || state == SWING) && !angry;
        int body = angry ? ANGRY : onAC && chargeT > 2.3 && (int)(animT * 10) % 2 == 0 ? STAR : CORAL;   // zap flash on plug-in
        bool typing = state == TYPE;
        int dy = state == SLEEP || typing ? 2 : 0;

        if (dy == 0)
        {
            bool walking = state == WALK || state == RUN || state == CLIMB || state == SWING || state == PUSH;
            int phase = walking ? (int)(animT * (state == RUN ? 14 : 8)) % 2 : -1;
            for (int i = 0; i < 4; i++) Px(Legs[i], 7, 1, state == HELD ? 3 : (i % 2 == phase ? 1 : 2), body);
        }
        Px(2, dy, 10, 7, body);

        bool up = joy || state == HELD || scaredT > 0 || (sign != null && state != TYPE);   // holding up the reminder sign
        bool swipeRaised = state == SWIPE && stateT < 0.25, swipeStrike = state == SWIPE && stateT >= 0.25;
        bool roping = ropeT >= 0; bool swinging = state == SWING; bool pushing = state == PUSH;
        bool leftBusy = pushing || (facing < 0 && (swipeRaised || swipeStrike || roping || swinging)), rightBusy = pushing || (facing > 0 && (swipeRaised || swipeStrike || roping || swinging));
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
        if (state == CLIMB) { leftUp = (int)(animT * 8) % 2 == 0; rightUp = !leftUp; }   // paw over paw
        if (vibing && joyT <= 0 && state == SIT) { leftUp = (int)(animT * 2.5) % 2 == 0; rightUp = !leftUp; }   // arms sway to the music
        bool notes = lastKind == 2 && state == SIT && alert == null;
        if (notes) leftBusy = true;                                             // left paw holds the notepad
        if (reach > 0)                                                          // both paws reaching for the ceiling, one then the other
        {
            leftBusy = rightBusy = true; bool alt = (int)(animT * 1.5) % 2 == 0;
            Px(0.6, alt ? -2.6 : -2, 1.6, 3.4, body); Px(11.8, alt ? -2 : -2.6, 1.6, 3.4, body);
        }
        if (drinking || clock) { if (facing > 0) rightBusy = true; else leftBusy = true; }
        if (drinking)                                                           // a glass of water: raised, sipped, lowered
        {
            double lift = Clamp(Math.Min((4.5 - drinkT) / 0.5, drinkT / 0.5), 0, 1), level = Clamp(drinkT / 4, 0.2, 1);
            double gx = facing > 0 ? 10.2 : 1.4, gy = 4.6 - 1.8 * lift;
            Px(facing > 0 ? 11.8 : 0.2, gy + 1.2, 2, 1.4, body);
            Px(gx, gy, 2.4, 3, WHITE); Px(gx + 0.3, gy + 0.3 + 2.4 * (1 - level), 1.8, 2.4 * level, CUP);
        }
        if (clock)                                                              // holds up a little clock: screen time
        {
            double kx = facing > 0 ? 11.4 : 0.2;
            Px(facing > 0 ? 12 : 0, 4.2 + dy, 2, 1.6, body);
            Px(kx, 2.2 + dy, 2.4, 2.4, WHITE); Px(kx + 1.05, 2.6 + dy, 0.3, 1.1, DARK); Px(kx + 1.05, 3.4 + dy, 0.9, 0.3, DARK);
        }
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
        if (swinging) Px(facing > 0 ? 12 : 0, dy, 2, 3, body);
        if (pushing)                                                             // both paws on the window, shoving
        {
            double shove = (int)(animT * 4) % 2 == 0 ? 0 : 0.3;
            Px(facing > 0 ? 11.5 + shove : -0.5 - shove, 3.4 + dy, 3, 1.4, body);
            Px(facing > 0 ? 11 + shove : 0 - shove, 5.1 + dy, 3, 1.4, body);
        }
        if (swipeStrike) Px(facing > 0 ? 12 : -2, 3, 4, 2, body);
        if (state == SIT && alert == null && sign == null && orient == 0 && MovieOn)   // popcorn in one paw, a drink in the other
        {
            Px(4.5, 4.8, 5, 3.2, ANGRY); Px(5.6, 4.8, 0.8, 3.2, WHITE); Px(7.6, 4.8, 0.8, 3.2, WHITE);   // striped bucket
            Px(5, 4.1, 1.1, 1, WHITE); Px(6.3, 3.8, 1.3, 1.2, STAR); Px(7.6, 4.1, 1.1, 1, WHITE);        // kernels heaped on top
            bool munching = snackKind == 1, sipping = snackKind == 2;

            double cupTop = sipping ? 4.2 : 4.7;                                 // the cup lifts a little to drink
            Px(0.5, cupTop + 0.4, 2.8, 3.4 - (sipping ? 0.5 : 0), WHITE);        // cup
            Px(1.3, cupTop + 0.4, 0.5, 3.4 - (sipping ? 0.5 : 0), ANGRY); Px(2.3, cupTop + 0.4, 0.5, 3.4 - (sipping ? 0.5 : 0), ANGRY);
            Px(0.3, cupTop, 3.2, 0.5, DARK);                                     // lid
            Px(2.2, cupTop - 2.2, 0.45, 2.3, STAR);                              // straw
            Px(2.2, cupTop - 2.2, sipping ? 1.7 : 1.1, 0.45, STAR);              // bent toward the mouth while sipping
            if (sipping && (int)(animT * 6) % 2 == 0) Px(3.9, cupTop - 2.4, 0.5, 0.5, WHITE);   // a little slurp mark

            double paw = munching ? (animT * 2.5) % 1 : 0;                        // paw dips into the bucket and comes back up
            Px(3.3, (munching ? 4.4 - paw * 1.6 : 4.6), 1.7, 1.5, body);
            if (munching && paw > 0.75) Px(4.6, 3.2, 0.6, 0.6, STAR);            // a kernel on the way to its mouth
        }

        if (sleeping || reach > 0.5 || drinking || (blinkOn > 0 && !angry && !joy)) { Px(3.8, 3 + dy, 1.4, 0.5, EYE); Px(8.8, 3 + dy, 1.4, 0.5, EYE); }
        else if (joy) { Px(3, 3 + dy, 1, 1, EYE); Px(4, 2 + dy, 1, 1, EYE); Px(5, 3 + dy, 1, 1, EYE); Px(8, 3 + dy, 1, 1, EYE); Px(9, 2 + dy, 1, 1, EYE); Px(10, 3 + dy, 1, 1, EYE); }
        else if (angry) { Px(3.5 + lookX, 2 + dy, 1, 1, EYE); Px(4.5 + lookX, 3 + dy, 1, 1, EYE); Px(9.5 + lookX, 2 + dy, 1, 1, EYE); Px(8.5 + lookX, 3 + dy, 1, 1, EYE); }
        else { Px(4 + lookX, 2 + lookY + dy, 1, 2, EYE); Px(9 + lookX, 2 + lookY + dy, 1, 2, EYE); }

        int costume = Costume();
        DrawRank(dy, phonesOn || costume != 0);
        DrawCostume(dy, body, costume);
        DrawTraits(dy);
        if (phonesOn)
        {
            Px(2.4, dy - 1.2, 9.2, 0.7, DARK);                                      // band over the head
            Px(1.7, dy - 0.8, 0.8, 1.8, DARK); Px(11.5, dy - 0.8, 0.8, 1.8, DARK);
            Px(1, dy + 0.8, 2, 2.6, CUP); Px(11, dy + 0.8, 2, 2.6, CUP);            // ear cups
            Px(1.4, dy + 1.3, 0.8, 1.6, CUPDARK); Px(11.8, dy + 1.3, 0.8, 1.6, CUPDARK);
            if (lastKind == 2) { Px(12.2, dy + 3.3, 0.5, 1.4, DARK); Px(9.4, dy + 4.4, 3.3, 0.5, DARK); Px(8.7, dy + 4.1, 1, 1.1, DARK); }   // mic boom
        }

        sx = sy = 1;
        if (chargeT > 0 && onAC && dy == 0 && orient == 0) { Px(-9, 8.4, 8, 0.6, DARK); Px(-1.6, 7.6, 1.8, 1.5, INK); Px(0.2, 7.9, 0.7, 0.3, STAR); Px(0.2, 8.5, 0.7, 0.3, STAR); }   // plugged in
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

        double top = orient == 3 ? H - 2 : orient != 0 ? by - 8 * U : by - (dy > 0 ? 10 : 12) * U - 6 * S;
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
        int fails = 0, checkNo = 0;
        var log = new StringBuilder();
        // The exit code is the failure count; this names which checks failed, since a winexe has no console.
        Action<bool> ok = delegate (bool b) { checkNo++; if (!b) { fails++; log.Append("check #").Append(checkNo).Append(" failed\r\n"); } };
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
        ok(TextTools.Activity("Code", "main.py - Visual Studio Code", "") == "code");
        ok(TextTools.Activity("chrome", "A Talk - YouTube", "youtube.com/watch?v=a") == "video");
        ok(TextTools.Genre("The Conjuring (2013) - VLC media player") == "horror" && TextTools.Genre("Titanic 1997 1080p.mkv") == "romance" && TextTools.Genre("Toy Story 3 - Netflix") == "anim");
        ok(TextTools.Genre("Netflix") == "" && TextTools.Genre(null) == "" && TextTools.Genre("Avengers Endgame - YouTube") == "action");
        ok(TextTools.Activity("chrome", "YouTube Music", "music.youtube.com/watch?v=a") == "music");
        ok(TextTools.Activity("msedge", "Pull request #3", "github.com/a/b/pull/3") == "github");
        ok(TextTools.Activity("chrome", "Meet - abc", "meet.google.com/abc-defg") == "meeting" && TextTools.Activity("Zoom", "Zoom Meeting", "") == "meeting");
        ok(TextTools.Activity("explorer", "Downloads", "") == "files" && TextTools.Activity("explorer", "", "") == "");
        ok(TextTools.Activity("chrome", "Some page", "example.org/x") == "browse" && TextTools.Activity("someapp", "Thing", "") == "");
        ok(TextTools.Comment("video", "work", rnd) == "Weren't you working?" && TextTools.Comment("", "", rnd) == null && TextTools.Comment("code", "", rnd) != null);
        ok(TextTools.JsonField("{\"session_id\":\"abc\", \"hook_event_name\" : \"Stop\"}", "hook_event_name") == "Stop");
        ok(TextTools.JsonField("{\"cwd\":\"C:\\\\Users\\\\me\\\\proj\"}", "cwd") == "C:\\Users\\me\\proj");
        ok(TextTools.JsonField("{\"prompt\":\"say \\\"cwd\\\" here\",\"cwd\":\"D:/x\"}", "cwd") == "D:/x" && TextTools.JsonField("{}", "cwd") == "");
        ok(TextTools.ProjectName("C:\\Users\\me\\pixelpet\\") == "pixelpet" && TextTools.ProjectName("/home/me/api") == "api" && TextTools.ProjectName("") == "");
        ok(TextTools.Pretty("{\"a\":[1,{\"b\":\"x,y\"}],\"c\":{}}") == "{\n  \"a\": [\n    1,\n    {\n      \"b\": \"x,y\"\n    }\n  ],\n  \"c\": {}\n}");
        ok(Rank(1) == "Hatchling" && Rank(5) == "Companion" && Rank(19) == "Scout" && Rank(35) == "Legend" && NextRank(35) == 0);
        ok(AchNames.Length == AchHow.Length && AchNames.Length <= 31);
        DateTime dd0; int[] tt0;
        ok(DecodeDay(EncodeDay(new DateTime(2026, 9, 18), new[] { 3, 5, 2, 1, 0, 4, 1 }), out dd0, out tt0) && dd0 == new DateTime(2026, 9, 18) && tt0[0] == 3 && tt0[5] == 4 && tt0[6] == 1);
        ok(!DecodeDay("junk", out dd0, out tt0));
        ok(DaySummary(new DateTime(2026, 9, 18), new[] { 3, 0, 2, 0, 0, 1, 1 }) == "Sep 18: closed 3 doomscroll tabs, 2 focus sessions, agents finished 1 task. Stayed up late.");
        ok(DaySummary(new DateTime(2026, 9, 18), new int[7]) == null);
        cfg = new Cfg();
        ok(Costume() == 0);                                                      // nothing on by default at rest
        wizardT = 1; ok(Costume() == 4); wizardT = 0;
        askT = 1; ok(Costume() == 9); askT = 0;
        eyeingNow = true; ok(Costume() == 1); eyeingNow = false;
        cfg.Costumes = false; wizardT = 1; ok(Costume() == 0); wizardT = 0; cfg.Costumes = true;
        uint mk, kk;
        ok(TextTools.ParseHotkey("ctrl+alt+shift+c", out mk, out kk) && mk == 7 && kk == 0x43 && TextTools.HotkeyText(mk, kk) == "Ctrl+Alt+Shift+C");
        ok(TextTools.ParseHotkey("Ctrl+Shift+F10", out mk, out kk) && mk == 6 && kk == 0x79 && TextTools.HotkeyText(mk, kk) == "Ctrl+Shift+F10");
        ok(TextTools.ParseHotkey("win+space", out mk, out kk) && mk == 8 && kk == 0x20);
        ok(!TextTools.ParseHotkey("r", out mk, out kk) && !TextTools.ParseHotkey("ctrl+f25", out mk, out kk));
        ok(!TextTools.ParseHotkey("ctrl+a+b", out mk, out kk) && !TextTools.ParseHotkey("off", out mk, out kk) && !TextTools.ParseHotkey(null, out mk, out kk));
        ok(TextTools.Span(45 * 60) == "45 min" && TextTools.Span(90 * 60) == "1h 30m" && TextTools.Span(7200) == "2h" && c.Water == 45 && c.ScreenTime == 60 && c.Stretch == 90);
        ok(AgentDefs.All.Length == 6 && AgentDefs.Find("antigravity")[3] == "antigravity" && AgentDefs.Find("nope") == null);
        foreach (var sp in AgentDefs.All)
        {
            ok(sp.Length == 5 && sp[0].Length > 0 && sp[1].Length > 0 && sp[2].Contains(".") && sp[4].Contains("=done"));
            foreach (var pair in sp[4].Split(';'))
            {
                string st = pair.Substring(pair.IndexOf('=') + 1);
                ok(st == "working" || st == "waiting" || st == "done" || st == "end");
            }
        }

        double px, py;
        SwingPos(0, 100, 50, 0, 200, 100, 0, out px, out py);
        ok(px == 0 && py == 100);                                              // u=0: exactly the start
        SwingPos(0, 100, 50, 0, 200, 100, 1, out px, out py);
        ok(px == 200 && py == 100);                                            // u=1: exactly the target
        SwingPos(0, 100, 50, 0, 200, 100, 0.5, out px, out py);
        ok(Math.Abs(px - 75) < 0.001 && py < 100);                             // midpoint: pulled toward the overhead anchor (bezier, not a straight line)
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pixelpet-selftest.txt"), fails == 0 ? "all " + checkNo + " checks passed\r\n" : log.ToString()); } catch { }
        return fails;
    }

    // ------------------------------------------------------------------ Win32
    delegate IntPtr WndProcD(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [StructLayout(LayoutKind.Sequential)] struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);   // W, to match the Unicode class (the A version cut the title to "P")
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
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
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [StructLayout(LayoutKind.Sequential)] struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX ms);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetDiskFreeSpaceEx(string dir, out ulong avail, out ulong total, out ulong free);
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("shell32.dll")] static extern bool Shell_NotifyIcon(int op, ref NID data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ExtractIcon(IntPtr inst, string file, int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ShellExecute(IntPtr h, string op, string file, string args, string dir, int show);
}

// ---------------------------------------------------------------------- the agent CLIs we can hook into
// key | display name | config file under the user profile | file shape | events as "Name=state;..."
// The state is baked into each hook command, so the pet never has to map event names at runtime.
static class AgentDefs
{
    public static readonly string[][] All =
    {
        new[] { "claude", "Claude Code", @".claude\settings.json", "nested", "UserPromptSubmit=working;Notification=waiting;Stop=done;SessionEnd=end" },
        new[] { "codex", "Codex", @".codex\hooks.json", "nested", "UserPromptSubmit=working;PermissionRequest=waiting;Stop=done" },
        new[] { "gemini", "Gemini CLI", @".gemini\settings.json", "nested", "BeforeAgent=working;Notification=waiting;AfterAgent=done;SessionEnd=end" },
        new[] { "antigravity", "Antigravity", @".gemini\config\hooks.json", "antigravity", "PreInvocation=working;Stop=done" },
        new[] { "cursor", "Cursor", @".cursor\hooks.json", "cursor", "beforeSubmitPrompt=working;stop=done;sessionEnd=end" },
        new[] { "windsurf", "Windsurf", @".codeium\windsurf\hooks.json", "windsurf", "pre_user_prompt=working;post_cascade_response=done" },
    };

    public static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
    public static string PathOf(string[] spec) { return Path.Combine(Home, spec[2]); }
    public static bool Installed(string[] spec) { return Directory.Exists(Path.Combine(Home, spec[2].Split('\\')[0])); }

    public static string[] Find(string key)
    {
        foreach (var a in All) if (a[0] == key) return a;
        return null;
    }

    public static bool Connected(string[] spec)
    {
        try { string p = PathOf(spec); return File.Exists(p) && File.ReadAllText(p).Contains("--agent-hook"); }
        catch { return false; }
    }
}

// ---------------------------------------------------------------------- hook installer (runs as `--connect <agent>`, its own process)
// Adds or removes our entries in the agent's config. Entries are ours when their command mentions
// "--agent-hook" (or the older "--claude-hook"), so other tools' hooks are never touched, and the
// file is backed up first. This is the only code that needs a JSON parser, and it only ever runs in
// this separate process, so the pet itself never loads that assembly.
static class AgentSetup
{
    public static int Run(string key, bool connect)
    {
        string[] spec = AgentDefs.Find(key);
        if (spec == null) return 3;
        string path = AgentDefs.PathOf(spec), exe = System.Reflection.Assembly.GetEntryAssembly().Location;
        string low = exe.ToLowerInvariant();
        // Hooks store this exe's full path. From a temp folder they break as soon as that folder is cleaned up.
        if (connect && (low.Contains("\\temp\\") || low.Contains("\\scratch-workspaces\\") || low.Contains("\\downloads\\")) &&
            MessageBox(IntPtr.Zero, "PixelPetFocus.exe is running from\n" + Path.GetDirectoryName(exe) +
            "\n\nThat looks like a temporary or downloads folder. The hooks point at this exact path, so they break if it is moved or cleaned up. " +
            "Move the exe somewhere permanent first, then connect again.\n\nConnect anyway?", "Connect " + spec[1], 0x40021) != 1) return 1;
        if (connect && MessageBox(IntPtr.Zero, "PixelPet will add hooks to\n" + path + "\n\nso it can react when " + spec[1] +
            " is working, needs you, or finishes. A backup of the current file is saved next to it. If you move PixelPetFocus.exe later, connect again.\n\nContinue?",
            "Connect " + spec[1], 0x40021) != 1) return 1;
        try
        {
            Apply(key, path, spec[3], spec[4], connect, exe);
            MessageBox(IntPtr.Zero, connect ? "Connected. New " + spec[1] + " sessions will tell PixelPet when they work, need you, or finish."
                                            : "Disconnected. PixelPet's hooks were removed from " + Path.GetFileName(path) + ".", spec[1], 0x40040);
            return 0;
        }
        catch (Exception e)
        {
            MessageBox(IntPtr.Zero, "Couldn't update " + path + ":\n" + e.Message + "\n\nNothing was changed.", spec[1], 0x40030);
            return 2;
        }
    }

    // The file edit itself, no dialogs (also what the test harness calls on copies).
    public static void Apply(string key, string path, string style, string eventsSpec, bool connect, string exe)
    {
        var js = new JavaScriptSerializer();
        bool fresh = !File.Exists(path);
        var root = fresh ? new Dictionary<string, object>() : js.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
        if (root == null) throw new InvalidDataException(Path.GetFileName(path) + " is not a JSON object");
        string container = style == "antigravity" ? "pixelpet" : "hooks";
        object got; Dictionary<string, object> map;
        if (root.TryGetValue(container, out got))
        {
            map = got as Dictionary<string, object>;
            if (map == null) throw new InvalidDataException("\"" + container + "\" is not an object");
        }
        else map = new Dictionary<string, object>();

        foreach (var pair in eventsSpec.Split(';'))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            string ev = pair.Substring(0, eq), state = pair.Substring(eq + 1);
            string cmd = "\"" + exe.Replace('\\', '/') + "\" --agent-hook " + key + " " + state;
            var kept = new List<object>(); object cur;
            if (map.TryGetValue(ev, out cur) && cur is object[]) foreach (var e in (object[])cur) if (!IsOurs(e)) kept.Add(e);
            if (connect) kept.Add(Entry(style, ev, cmd));
            if (kept.Count > 0) map[ev] = kept.ToArray(); else map.Remove(ev);
        }

        if (map.Count > 0) root[container] = map; else root.Remove(container);
        if (connect && style == "cursor" && fresh) root["version"] = 1;         // only when we create the file, so disconnect restores it exactly
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if (File.Exists(path)) File.Copy(path, path + ".pixelpet-backup", true);
        File.WriteAllText(path, TextTools.Pretty(js.Serialize(root)) + "\n");
    }

    // Claude/Codex/Gemini wrap handlers in a group; Antigravity does that only for tool events; Cursor and
    // Windsurf list handlers directly.
    static object Entry(string style, string ev, string cmd)
    {
        var handler = new Dictionary<string, object>();
        if (style != "windsurf") handler["type"] = "command";
        handler["command"] = cmd;
        if (style == "cursor" || style == "windsurf" || (style == "antigravity" && ev != "PreToolUse" && ev != "PostToolUse")) return handler;
        var group = new Dictionary<string, object>();
        if (style == "antigravity") group["matcher"] = "*";
        group["hooks"] = new object[] { handler };
        return group;
    }

    static bool IsOurs(object entry)
    {
        var d = entry as Dictionary<string, object>;
        if (d == null) return false;
        object c;
        if (d.TryGetValue("command", out c) && c is string && Mine((string)c)) return true;
        object inner;
        if (!d.TryGetValue("hooks", out inner) || !(inner is object[])) return false;
        foreach (var x in (object[])inner)
        {
            var hd = x as Dictionary<string, object>;
            if (hd != null && hd.TryGetValue("command", out c) && c is string && Mine((string)c)) return true;
        }
        return false;
    }

    static bool Mine(string command) { return command.Contains("--agent-hook") || command.Contains("--claude-hook"); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBox(IntPtr h, string text, string caption, uint type);
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

    // One string field out of a small JSON object (Claude Code hook payloads). Handles the usual escapes.
    public static string JsonField(string json, string key)
    {
        int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return "";
        i += key.Length + 2;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != ':') return "";
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return "";
        var sb = new StringBuilder();
        for (i++; i < json.Length && sb.Length < 1000; i++)
        {
            char c = json[i];
            if (c == '"') return sb.ToString();
            if (c != '\\' || i + 1 >= json.Length) { sb.Append(c); continue; }
            char n = json[++i]; int code;
            if (n == 'n') sb.Append('\n');
            else if (n == 't') sb.Append('\t');
            else if (n == 'r') { }
            else if (n == 'u' && i + 4 < json.Length && int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber, Inv, out code)) { sb.Append((char)code); i += 4; }
            else sb.Append(n);
        }
        return sb.ToString();
    }

    // "45 min", "1h 30m", "2h": how long, for the break buddy.
    public static string Span(int seconds)
    {
        int m = seconds / 60;
        return m < 60 ? m + " min" : m / 60 + "h" + (m % 60 > 0 ? " " + m % 60 + "m" : "");
    }

    public static string ProjectName(string cwd)
    {
        string t = (cwd ?? "").TrimEnd('\\', '/');
        int cut = Math.Max(t.LastIndexOf('\\'), t.LastIndexOf('/'));
        string name = cut >= 0 ? t.Substring(cut + 1) : t;
        return name.Length > 24 ? name.Substring(0, 24) : name;
    }

    // Indents compact JSON (2 spaces), leaving string contents alone.
    public static string Pretty(string json)
    {
        var sb = new StringBuilder(); int ind = 0; bool str = false, esc = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (str) { sb.Append(c); if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') str = false; continue; }
            switch (c)
            {
                case '"': str = true; sb.Append(c); break;
                case '{': case '[':
                    if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[i + 1]); i++; break; }
                    sb.Append(c).Append('\n').Append(' ', ++ind * 2); break;
                case '}': case ']': sb.Append('\n').Append(' ', --ind * 2).Append(c); break;
                case ',': sb.Append(",\n").Append(' ', ind * 2); break;
                case ':': sb.Append(": "); break;
                default: if (!char.IsWhiteSpace(c)) sb.Append(c); break;
            }
        }
        return sb.ToString().Replace("\\u003c", "<").Replace("\\u003e", ">").Replace("\\u0026", "&").Replace("\\u0027", "'");
    }

    // A movie's flavour from a window title (players show the file name, YouTube the film's name). "" = no idea.
    public static string Genre(string title)
    {
        string t = (title ?? "").ToLowerInvariant();
        if (Any(t, "horror", "conjuring", "insidious", "scream", "annabelle", "exorcis", "haunt", "ghost", "paranormal", "zombie", "sinister", "evil dead", "nightmare", "the nun", "poltergeist", "possess", "bhoot")) return "horror";
        if (Any(t, "romance", "romantic", "love", "wedding", "notebook", "titanic", "valentine", "kiss", "pyaar", "ishq", "mohabbat", "rom-com")) return "romance";
        if (Any(t, "comedy", "funny", "hangover", "stand-up", "standup", "sitcom", "bloopers", "laugh", "hilarious", "mr bean", "mr. bean")) return "comedy";
        if (Any(t, "action", "fast & furious", "fast and furious", "avengers", "john wick", "mission impossible", "batman", "superman", "spider-man", "spiderman", "mad max", "transformers", "terminator", "rambo", "james bond", "kgf", "pushpa", "thriller", "heist", "marvel", "gladiator")) return "action";
        if (Any(t, "animation", "animated", "pixar", "frozen", "toy story", "minions", "shrek", "kung fu panda", "cartoon", "anime", "naruto", "ghibli", "doraemon", "moana", "encanto", "bluey", "tom and jerry")) return "anim";
        return "";
    }

    // "ctrl+alt+shift+c", "win+f9", "ctrl+space": modifiers in any order, then one key.
    public static bool ParseHotkey(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (s == null) return false;
        foreach (var raw in s.ToLowerInvariant().Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0) return false;
            if (part == "ctrl" || part == "control") { mods |= 2; continue; }     // MOD_CONTROL
            if (part == "alt") { mods |= 1; continue; }                           // MOD_ALT
            if (part == "shift") { mods |= 4; continue; }                         // MOD_SHIFT
            if (part == "win" || part == "windows" || part == "super" || part == "cmd") { mods |= 8; continue; }
            if (vk != 0) return false;                                            // only one non-modifier key
            if (part.Length == 1 && part[0] >= 'a' && part[0] <= 'z') vk = (uint)(part[0] - 'a' + 0x41);
            else if (part.Length == 1 && part[0] >= '0' && part[0] <= '9') vk = (uint)(part[0] - '0' + 0x30);
            else if (part[0] == 'f' && part.Length <= 3)
            {
                int n;
                if (!int.TryParse(part.Substring(1), NumberStyles.None, Inv, out n) || n < 1 || n > 24) return false;
                vk = (uint)(0x70 + n - 1);
            }
            else
            {
                switch (part)
                {
                    case "space": vk = 0x20; break; case "enter": case "return": vk = 0x0D; break;
                    case "tab": vk = 0x09; break; case "insert": vk = 0x2D; break; case "delete": vk = 0x2E; break;
                    case "home": vk = 0x24; break; case "end": vk = 0x23; break;
                    case "pageup": case "pgup": vk = 0x21; break; case "pagedown": case "pgdn": vk = 0x22; break;
                    case "up": vk = 0x26; break; case "down": vk = 0x28; break; case "left": vk = 0x25; break; case "right": vk = 0x27; break;
                    case "backspace": vk = 0x08; break; case "`": case "backtick": vk = 0xC0; break;
                    default: return false;
                }
            }
        }
        return vk != 0 && mods != 0;                                              // a bare key would swallow normal typing
    }

    public static string HotkeyText(uint mods, uint vk)
    {
        var sb = new StringBuilder();
        if ((mods & 2) != 0) sb.Append("Ctrl+");
        if ((mods & 1) != 0) sb.Append("Alt+");
        if ((mods & 4) != 0) sb.Append("Shift+");
        if ((mods & 8) != 0) sb.Append("Win+");
        if (vk >= 0x41 && vk <= 0x5A) sb.Append((char)vk);
        else if (vk >= 0x30 && vk <= 0x39) sb.Append((char)vk);
        else if (vk >= 0x70 && vk <= 0x87) sb.Append('F').Append(vk - 0x70 + 1);
        else
        {
            switch (vk)
            {
                case 0x20: sb.Append("Space"); break; case 0x0D: sb.Append("Enter"); break; case 0x09: sb.Append("Tab"); break;
                case 0x2D: sb.Append("Insert"); break; case 0x2E: sb.Append("Delete"); break; case 0x24: sb.Append("Home"); break;
                case 0x23: sb.Append("End"); break; case 0x21: sb.Append("PageUp"); break; case 0x22: sb.Append("PageDown"); break;
                case 0x26: sb.Append("Up"); break; case 0x28: sb.Append("Down"); break; case 0x25: sb.Append("Left"); break;
                case 0x27: sb.Append("Right"); break; case 0x08: sb.Append("Backspace"); break; case 0xC0: sb.Append('`'); break;
                default: sb.Append('?'); break;
            }
        }
        return sb.ToString();
    }

    // What the foreground window is, from its process name, title and URL. "" = no idea.
    public static string Activity(string proc, string title, string url)
    {
        proc = (proc ?? "").ToLowerInvariant();
        string t = (title ?? "").ToLowerInvariant(), u = (url ?? "").ToLowerInvariant(), hay = u + " | " + t;
        if (Any(u, "meet.google.com", "zoom.us/", "teams.microsoft.com/l/meetup") || proc == "zoom" || proc == "cpthost"
            || ((proc == "ms-teams" || proc == "teams") && Any(t, "meeting", "call"))) return "meeting";
        if (Any(hay, "music.youtube.com", "open.spotify.com", "jiosaavn", "gaana.com")) return "music";
        if (Any(u, "github.com", "gitlab.com", "bitbucket.org")) return "github";
        if (Any(u, "stackoverflow.com", "stackexchange.com")) return "stackoverflow";
        if (Any(u, "chatgpt.com", "claude.ai", "gemini.google.com", "copilot.microsoft.com", "perplexity.ai")) return "ai";
        if (Any(u, "leetcode.com", "coursera.org", "udemy.com", "khanacademy.org", "geeksforgeeks.org", "w3schools.com", "wikipedia.org", "nptel", "hackerrank.com", "developer.mozilla.org")) return "learn";
        if (Any(u, "mail.google.com", "outlook.live.com", "outlook.office.com")) return "mail";
        if (Any(u, "docs.google.com/document", "notion.so")) return "docs";
        if (Any(u, "docs.google.com/spreadsheets")) return "sheets";
        if (Any(u, "docs.google.com/presentation", "canva.com")) return "slides";
        if (Any(u, "web.whatsapp.com", "discord.com", "slack.com", "web.telegram.org", "messenger.com")) return "chat";
        if (Any(u, "amazon.", "flipkart.com", "myntra.com", "meesho.com", "ebay.", "aliexpress")) return "shop";
        if (Any(u, "linkedin.com", "x.com/", "twitter.com", "instagram.com", "facebook.com", "reddit.com", "threads.net")) return "social";
        if (Any(u, "youtube.com", "netflix.com", "primevideo.com", "hotstar.com", "twitch.tv", "crunchyroll")) return "video";
        switch (proc)
        {
            case "code": case "cursor": case "windsurf": case "devenv": case "pycharm64": case "idea64": case "webstorm64": case "rider64":
            case "clion64": case "studio64": case "sublime_text": case "notepad++": case "zed": case "antigravity": case "arduino ide": return "code";
            case "windowsterminal": case "wt": case "cmd": case "powershell": case "pwsh": case "conhost": case "mintty": case "openconsole": return "terminal";
            case "winword": case "notepad": case "obsidian": case "onenote": case "wordpad": case "acrord32": case "acrobat": return "docs";
            case "excel": return "sheets";
            case "powerpnt": return "slides";
            case "outlook": case "olk": case "thunderbird": return "mail";
            case "whatsapp": case "whatsapp.root": case "discord": case "slack": case "telegram": case "ms-teams": case "teams": case "signal": return "chat";
            case "spotify": return "music";
            case "vlc": case "potplayer": case "mpc-hc64": case "obs64": return "video";
            case "figma": case "photoshop": case "illustrator": case "blender": case "gimp-2.10": case "krita": case "inkscape": return "design";
            case "steam": case "steamwebhelper": case "epicgameslauncher": case "riotclientservices": case "valorant": case "robloxplayerbeta": case "minecraft": return "game";
            case "explorer": return t.Length > 0 ? "files" : "";
        }
        if (Array.IndexOf(new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "chromium", "librewolf", "zen", "opera_gx" }, proc) >= 0) return "browse";
        return "";
    }

    static bool Any(string hay, params string[] needles)
    {
        foreach (var n in needles) if (hay.Contains(n)) return true;
        return false;
    }

    // One short reaction for an activity. Knows when you said you were working or studying.
    public static string Comment(string act, string doing, Random rnd)
    {
        if ((doing == "work" || doing == "study") && (act == "video" || act == "social" || act == "shop" || act == "game"))
            return doing == "study" ? "Weren't you studying?" : "Weren't you working?";
        string[] lines;
        switch (act)
        {
            case "code": lines = new[] { "Writing code? Ship it!", "Need a rubber duck? I'm here.", "Remember to commit!" }; break;
            case "terminal": lines = new[] { "Hacker mode: on.", "Careful with rm -rf!", "May your tests be green." }; break;
            case "github": lines = new[] { "GitHub! Star something nice.", "Reviewing a pull request?" }; break;
            case "stackoverflow": lines = new[] { "Stack Overflow to the rescue!", "Copy, paste... understand?" }; break;
            case "ai": lines = new[] { "Asking an AI? I have opinions too.", "Say please to the robot." }; break;
            case "learn": lines = new[] { "Learning something new? Proud of you.", "Study mode!" }; break;
            case "docs": lines = new[] { "Writing something great?", "The words are flowing!" }; break;
            case "sheets": lines = new[] { "Spreadsheet wizardry!", "=SUM(snacks)" }; break;
            case "slides": lines = new[] { "Slide deck time. Fancy!", "Big presentation coming?" }; break;
            case "mail": lines = new[] { "Inbox zero today?", "Emails, emails..." }; break;
            case "chat": lines = new[] { "Say hi from me!", "Chatting? Don't forget your task." }; break;
            case "video": lines = new[] { "Ooh, what are we watching?", "Popcorn time?" }; break;
            case "music": lines = new[] { "Nice tunes!", "Turn it up!" }; break;
            case "design": lines = new[] { "Ooh, pretty pixels!", "Make it pop!" }; break;
            case "game": lines = new[] { "Game time! Have fun.", "Go win one for me!" }; break;
            case "shop": lines = new[] { "Adding to cart again?", "Need it, or want it?" }; break;
            case "social": lines = new[] { "Just a quick peek, right?", "Scrolling? Stay focused!" }; break;
            case "files": lines = new[] { "Tidying up files?", "Looking for something?" }; break;
            case "browse": lines = new[] { "What are you reading?", "Interesting page?" }; break;
            default: return null;
        }
        return lines[rnd.Next(lines.Length)];
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

    // The focused field, for the Enter rope: its middle, or 200 px in from the left of a long one, where typing sits.
    public bool FocusPoint(out int x, out int y)
    {
        x = y = 0;
        if (auto == null) return false;
        IUIAutomationElement e = null;
        try
        {
            e = auto.GetFocusedElement();
            var r = e == null ? null : e.GetCurrentPropertyValue(30001) as double[];   // BoundingRectangle: left, top, width, height
            if (r == null || r.Length < 4 || r[2] < 4 || r[3] < 4 || r[3] > 400) return false;   // nothing, or a whole page
            x = (int)(r[0] + Math.Min(r[2] / 2, 200)); y = (int)(r[1] + r[3] / 2);
            return true;
        }
        catch { return false; }
        finally { if (e != null) try { Marshal.ReleaseComObject(e); } catch { } }
    }

    void Drop() { if (bar != null) { try { Marshal.ReleaseComObject(bar); } catch { } } bar = null; }
}

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomation
{
    void _0(); void _1(); void _2();
    IUIAutomationElement ElementFromHandle(IntPtr hwnd);
    void _4(); IUIAutomationElement GetFocusedElement(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
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
