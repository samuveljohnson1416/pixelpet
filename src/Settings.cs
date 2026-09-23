// The settings window: native Win32 controls (no WinForms), on its own thread so the pet keeps
// animating while it is open. Saving writes the same rules.txt the file format has always used,
// so hand-editing still works and the watcher's hot-reload still applies changes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class Settings
{
    const int ID_PATROL = 101, ID_CLOSE = 102, ID_NAG = 103, ID_COUNT = 104, ID_SNOOZE = 105,
              ID_WANDER = 106, ID_SLEEPY = 107, ID_TYPING = 108, ID_ROPE = 109, ID_AUDIO = 110,
              ID_SIZE = 111, ID_FOCUS = 112, ID_BREAK = 113, ID_AUTOSTART = 114, ID_LIST = 115,
              ID_RLABEL = 116, ID_RSECS = 117, ID_RPATS = 118, ID_ADD = 119, ID_APPLY = 120,
              ID_DEL = 121, ID_DEFAULTS = 122, ID_NEVER = 123, ID_SAVE = 124, ID_CLOSEBTN = 125,
              ID_OPENTXT = 126, ID_UP = 127, ID_DOWN = 128, ID_CLIP = 129, ID_HOTKEY = 130, ID_CLIMB = 131, ID_WEB = 132, ID_CLIPKEY = 133, ID_SWINGKEY = 134;

    static readonly double[] Sizes = { 0.75, 1, 1.25, 1.5, 2 };
    static readonly string[] SizeNames = { "0.75x", "1x", "1.25x", "1.5x", "2x" };

    static IntPtr hwnd, font, list, status;
    static readonly Dictionary<int, IntPtr> ctl = new Dictionary<int, IntPtr>();
    static readonly List<Rule> rules = new List<Rule>();
    static Cfg cfg;
    static string rulesPath;
    static Action<Cfg> onSave;
    static double S = 1;
    static readonly WndProcD keep = Proc;
    static bool classReady;

    internal static void Show(string path, Cfg current, Action<Cfg> apply)
    {
        rulesPath = path; onSave = apply;
        if (hwnd != IntPtr.Zero) { ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); return; }   // SW_RESTORE
        cfg = Copy(current);
        var t = new System.Threading.Thread(Run);
        t.IsBackground = true; t.Start();
    }

    static Cfg Copy(Cfg c)
    {
        var o = new Cfg();
        o.Countdown = c.Countdown; o.Snooze = c.Snooze; o.Focus = c.Focus; o.Break = c.Break;
        o.Nag = c.Nag; o.Wander = c.Wander; o.Sleepy = c.Sleepy; o.Typing = c.Typing;
        o.EnterRope = c.EnterRope; o.Climb = c.Climb; o.WebTravel = c.WebTravel; o.Audio = c.Audio; o.Clipboard = c.Clipboard; o.Hotkey = c.Hotkey; o.Curious = c.Curious; o.Claude = c.Claude; o.Stretch = c.Stretch; o.Push = c.Push; o.Quiet = c.Quiet; o.ClipKey = c.ClipKey; o.SwingKey = c.SwingKey; o.Size = c.Size;
        o.Never = (string[])c.Never.Clone();
        o.Rules = c.Rules;
        return o;
    }

    static void Run()
    {
        var icc = new INITCOMMONCONTROLSEX(); icc.dwSize = Marshal.SizeOf(typeof(INITCOMMONCONTROLSEX)); icc.dwICC = 0x1;  // ICC_LISTVIEW_CLASSES
        InitCommonControlsEx(ref icc);
        S = GetDpiForSystem() / 96.0;

        if (!classReady)
        {
            var wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(keep);
            wc.hInstance = GetModuleHandle(null);
            wc.hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512);                  // IDC_ARROW
            wc.hbrBackground = GetSysColorBrush(15);                              // COLOR_BTNFACE
            wc.lpszClassName = "PixelPetSettings";
            wc.hIcon = ExtractIcon(GetModuleHandle(null), System.Reflection.Assembly.GetEntryAssembly().Location, 0);
            RegisterClassEx(ref wc);
            classReady = true;
        }

        int w = D(676), h = D(752);
        var scr = new RECT(); SystemParametersInfo(0x30, 0, ref scr, 0);
        int px = (scr.L + scr.R - w) / 2, py = (scr.T + scr.B - h) / 2;
        // WS_OVERLAPPED|CAPTION|SYSMENU|MINIMIZEBOX, sized so the client area fits the layout
        hwnd = CreateWindowEx(0, "PixelPetSettings", "PixelPet Focus - Settings", 0x00CA0000, px, py, w, h,
                              IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        font = CreateFont(-(int)(12 * S), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        Build();
        Load();
        ShowWindow(hwnd, 5);
        SetForegroundWindow(hwnd);

        MSG m;
        while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
        {
            if (hwnd != IntPtr.Zero && IsDialogMessage(hwnd, ref m)) continue;     // Tab / Esc / Enter navigation
            TranslateMessage(ref m); DispatchMessage(ref m);
        }
    }

    static int D(int v) { return (int)Math.Round(v * S); }

    static IntPtr Mk(string cls, string text, int style, int x, int y, int w, int h, int id, int ex = 0)
    {
        IntPtr c = CreateWindowEx(ex, cls, text, (uint)(0x50000000 | style), D(x), D(y), D(w), D(h), hwnd, (IntPtr)id, IntPtr.Zero, IntPtr.Zero);
        SendMessage(c, 0x0030, font, (IntPtr)1);                                   // WM_SETFONT
        if (id != 0) ctl[id] = c;
        return c;
    }

    static void Group(string title, int x, int y, int w, int h) { Mk("BUTTON", title, 0x7, x, y, w, h, 0); }         // BS_GROUPBOX
    static void Label(string text, int x, int y, int w) { Mk("STATIC", text, 0, x, y, w, 18, 0); }
    static void Check(string text, int x, int y, int w, int id) { Mk("BUTTON", text, 0x10003, x, y, w, 22, id); }    // WS_TABSTOP|BS_AUTOCHECKBOX
    static IntPtr Edit(int x, int y, int w, int h, int style, int id) { return Mk("EDIT", "", 0x10080 | style, x, y, w, h, id, 0x200); }   // WS_TABSTOP|ES_AUTOHSCROLL, sunken themed border
    static void Push(string text, int x, int y, int w, int id) { Mk("BUTTON", text, 0x10000, x, y, w, 26, id); }     // WS_TABSTOP|BS_PUSHBUTTON

    static void Build()
    {
        Group("Patrol", 12, 6, 312, 148);
        Check("On patrol (watch for doomscrolling)", 24, 26, 280, ID_PATROL);
        Mk("BUTTON", "Close the tab", 0x30009, 24, 52, 130, 22, ID_CLOSE);          // WS_GROUP|BS_AUTORADIOBUTTON
        Mk("BUTTON", "Only complain", 0x9, 160, 52, 140, 22, ID_NAG);
        Label("Countdown before closing (s)", 24, 82, 200);
        Edit(230, 80, 60, 22, 0x2000, ID_COUNT);                                    // ES_NUMBER
        Label("Snooze when you click it (min)", 24, 112, 200);
        Edit(230, 110, 60, 22, 0x2000, ID_SNOOZE);

        Group("The pet", 12, 162, 312, 228);
        Check("Wander around the screen", 24, 182, 280, ID_WANDER);
        Check("Take naps", 24, 206, 280, ID_SLEEPY);
        Check("Pull out a laptop while you type", 24, 230, 280, ID_TYPING);
        Check("Throw a rope when you press Enter", 24, 254, 280, ID_ROPE);
        Check("Headphones, music and class reactions", 24, 278, 290, ID_AUDIO);
        Check("Climb up the screen edges", 24, 302, 280, ID_CLIMB);
        Check("Ctrl+Alt+G web-swings to the cursor", 24, 326, 290, ID_WEB);
        Label("Size", 24, 358, 40);
        Mk("COMBOBOX", "", 0x10003, 66, 356, 100, 200, ID_SIZE);                    // CBS_DROPDOWNLIST|WS_TABSTOP
        Label("(applies on restart)", 174, 358, 140);

        Group("Focus timer", 12, 394, 312, 92);
        Label("Focus session (min)", 24, 416, 140);
        Edit(170, 414, 60, 22, 0x2000, ID_FOCUS);
        Label("Break (min)", 24, 446, 140);
        Edit(170, 444, 60, 22, 0x2000, ID_BREAK);

        Group("Clipboard && Windows", 12, 494, 312, 162);
        Check("React to copied text (links, sums, reminders)", 24, 514, 290, ID_CLIP);
        Check("Shortcut keys (restart to apply)", 24, 538, 290, ID_HOTKEY);
        Label("Clipboard menu", 36, 566, 118);
        Edit(158, 564, 152, 22, 0, ID_CLIPKEY);
        Label("Web-swing", 36, 594, 118);
        Edit(158, 592, 152, 22, 0, ID_SWINGKEY);
        Label("e.g. ctrl+alt+shift+c, win+f9, or off", 36, 620, 280);
        Check("Start with Windows", 24, 640, 280, ID_AUTOSTART);

        Group("What counts as doomscrolling", 334, 6, 328, 450);
        list = Mk("SysListView32", "", 0x1000D, 344, 26, 308, 172, ID_LIST, 0x200); // WS_TABSTOP|LVS_REPORT|SINGLESEL|SHOWSELALWAYS
        SendMessage(list, 0x1000 + 54, IntPtr.Zero, (IntPtr)0x21);                  // LVM_SETEXTENDEDLISTVIEWSTYLE: FULLROWSELECT|GRIDLINES
        Col(0, "Rule", 104); Col(1, "Sec", 38); Col(2, "Phrases", 140);
        Push("Move up", 344, 202, 76, ID_UP);
        Push("Move down", 424, 202, 86, ID_DOWN);
        Push("Remove", 514, 202, 70, ID_DEL);
        Push("Defaults", 588, 202, 64, ID_DEFAULTS);
        Label("First match wins: keep specific rules on top.", 344, 234, 310);
        Label("Rule name", 344, 256, 120);
        Edit(344, 274, 308, 22, 0, ID_RLABEL);
        Label("Seconds allowed before it acts", 344, 302, 220);
        Edit(570, 300, 82, 22, 0x2000, ID_RSECS);
        Label("Phrases, comma separated (URL, title or app name)", 344, 328, 310);
        Edit(344, 346, 308, 48, 0x4 | 0x40, ID_RPATS);                              // ES_MULTILINE|ES_AUTOVSCROLL (wraps)
        Push("Add as new rule", 344, 402, 130, ID_ADD);
        Push("Update selected", 480, 402, 130, ID_APPLY);

        Group("Never touch (one phrase per line)", 334, 464, 328, 122);
        Edit(344, 486, 308, 88, 0x4 | 0x40 | 0x1000 | 0x200000, ID_NEVER);          // MULTILINE|AUTOVSCROLL|WANTRETURN|WS_VSCROLL

        Push("Open rules.txt", 12, 620, 120, ID_OPENTXT);
        status = Mk("STATIC", "", 0, 140, 626, 290, 18, 0);
        Push("Save", 452, 618, 96, ID_SAVE);
        Push("Close", 556, 618, 96, ID_CLOSEBTN);

        for (int i = 0; i < SizeNames.Length; i++) SendMessageStr(ctl[ID_SIZE], 0x0143, IntPtr.Zero, SizeNames[i]);   // CB_ADDSTRING
    }

    static void Col(int i, string title, int width)
    {
        var c = new LVCOLUMN(); c.mask = 2 | 4 | 8; c.cx = D(width); c.iSubItem = i;   // WIDTH|TEXT|SUBITEM
        IntPtr p = Marshal.StringToHGlobalUni(title); c.pszText = p;
        SendMessageCol(list, 0x1000 + 97, (IntPtr)i, ref c);                            // LVM_INSERTCOLUMNW
        Marshal.FreeHGlobal(p);
    }

    static void SetCheck(int id, bool on) { SendMessage(ctl[id], 0x00F1, (IntPtr)(on ? 1 : 0), IntPtr.Zero); }
    static bool GetCheck(int id) { return SendMessage(ctl[id], 0x00F2, IntPtr.Zero, IntPtr.Zero) == (IntPtr)1; }
    static void SetText(int id, string t) { SetWindowText(ctl[id], t); }

    static string GetText(int id)
    {
        var sb = new StringBuilder(4096);
        GetWindowText(ctl[id], sb, sb.Capacity);
        return sb.ToString();
    }

    static int GetNum(int id, int min, int max, int fallback)
    {
        int v; string t = GetText(id).Trim();
        if (!int.TryParse(t, out v)) return fallback;
        return v < min ? min : v > max ? max : v;
    }

    static void Load()
    {
        SetCheck(ID_PATROL, App.PatrolFlag(null));
        SetCheck(ID_CLOSE, !cfg.Nag); SetCheck(ID_NAG, cfg.Nag);
        SetText(ID_COUNT, cfg.Countdown.ToString()); SetText(ID_SNOOZE, cfg.Snooze.ToString());
        SetCheck(ID_WANDER, cfg.Wander); SetCheck(ID_SLEEPY, cfg.Sleepy);
        SetCheck(ID_TYPING, cfg.Typing); SetCheck(ID_ROPE, cfg.EnterRope); SetCheck(ID_AUDIO, cfg.Audio);
        SetCheck(ID_CLIP, cfg.Clipboard); SetCheck(ID_HOTKEY, cfg.Hotkey); SetCheck(ID_CLIMB, cfg.Climb); SetCheck(ID_WEB, cfg.WebTravel);
        SetText(ID_CLIPKEY, cfg.ClipKey); SetText(ID_SWINGKEY, cfg.SwingKey);
        int pick = 1;
        for (int i = 0; i < Sizes.Length; i++) if (Math.Abs(Sizes[i] - cfg.Size) < 0.01) pick = i;
        SendMessage(ctl[ID_SIZE], 0x014E, (IntPtr)pick, IntPtr.Zero);                   // CB_SETCURSEL
        SetText(ID_FOCUS, cfg.Focus.ToString()); SetText(ID_BREAK, cfg.Break.ToString());
        SetCheck(ID_AUTOSTART, App.AutoStartFlag(null));
        SetText(ID_NEVER, string.Join("\r\n", cfg.Never));
        rules.Clear();
        foreach (var r in cfg.Rules) rules.Add(new Rule { Label = r.Label, Grace = r.Grace, Any = (string[])r.Any.Clone() });
        Fill(-1);
    }

    // A typed shortcut, kept only if it parses; "off" disables it.
    static string Key(int id, string current)
    {
        string t = GetText(id).Trim().ToLowerInvariant();
        uint mods, vk;
        if (t == "off" || t == "none" || TextTools.ParseHotkey(t, out mods, out vk)) return t;
        Status("\"" + t + "\" isn't a shortcut I understand, so I kept " + current);
        return current;
    }

    static void Fill(int select)
    {
        SendMessage(list, 0x1009, IntPtr.Zero, IntPtr.Zero);                            // LVM_DELETEALLITEMS
        for (int i = 0; i < rules.Count; i++)
        {
            var it = new LVITEM(); it.mask = 1; it.iItem = i;
            IntPtr p = Marshal.StringToHGlobalUni(rules[i].Label); it.pszText = p;
            SendMessageItem(list, 0x1000 + 77, IntPtr.Zero, ref it);                    // LVM_INSERTITEMW
            Marshal.FreeHGlobal(p);
            Sub(i, 1, rules[i].Grace.ToString());
            Sub(i, 2, string.Join(", ", rules[i].Any));
        }
        if (select >= 0 && select < rules.Count)
        {
            var st = new LVITEM(); st.mask = 8; st.state = 3; st.stateMask = 3;         // SELECTED|FOCUSED
            SendMessageItem(list, 0x102B, (IntPtr)select, ref st);                      // LVM_SETITEMSTATE
            SendMessage(list, 0x1013, (IntPtr)select, IntPtr.Zero);                     // LVM_ENSUREVISIBLE
        }
    }

    static void Sub(int row, int sub, string text)
    {
        var it = new LVITEM(); it.mask = 1; it.iItem = row; it.iSubItem = sub;
        IntPtr p = Marshal.StringToHGlobalUni(text); it.pszText = p;
        SendMessageItem(list, 0x1000 + 116, (IntPtr)row, ref it);                       // LVM_SETITEMTEXTW
        Marshal.FreeHGlobal(p);
    }

    static int Selected() { return SendMessage(list, 0x100C, (IntPtr)(-1), (IntPtr)2).ToInt32(); }   // LVM_GETNEXTITEM/SELECTED

    static void ShowDetail()
    {
        int i = Selected();
        if (i < 0 || i >= rules.Count) return;
        SetText(ID_RLABEL, rules[i].Label);
        SetText(ID_RSECS, rules[i].Grace.ToString());
        SetText(ID_RPATS, string.Join(", ", rules[i].Any));
    }

    static string[] Phrases()
    {
        var parts = GetText(ID_RPATS).Replace("\r\n", ",").Split(',');
        var o = new List<string>();
        foreach (var s in parts) { string t = s.Trim().ToLowerInvariant(); if (t.Length > 0) o.Add(t); }
        return o.ToArray();
    }

    static Rule FromFields()
    {
        string label = GetText(ID_RLABEL).Trim().Replace("|", "/");
        var pats = Phrases();
        if (label.Length == 0) { Status("Give the rule a name first."); return null; }
        if (pats.Length == 0) { Status("A rule needs at least one phrase."); return null; }
        return new Rule { Label = label, Grace = GetNum(ID_RSECS, 1, 86400, 30), Any = pats };
    }

    static void Status(string text) { SetWindowText(status, text); }

    static void Save()
    {
        cfg.Nag = GetCheck(ID_NAG);
        cfg.Countdown = GetNum(ID_COUNT, 0, 120, 3);
        cfg.Snooze = GetNum(ID_SNOOZE, 1, 600, 5);
        cfg.Wander = GetCheck(ID_WANDER); cfg.Sleepy = GetCheck(ID_SLEEPY);
        cfg.Typing = GetCheck(ID_TYPING); cfg.EnterRope = GetCheck(ID_ROPE); cfg.Audio = GetCheck(ID_AUDIO);
        cfg.Clipboard = GetCheck(ID_CLIP); cfg.Hotkey = GetCheck(ID_HOTKEY); cfg.Climb = GetCheck(ID_CLIMB); cfg.WebTravel = GetCheck(ID_WEB);
        cfg.ClipKey = Key(ID_CLIPKEY, cfg.ClipKey); cfg.SwingKey = Key(ID_SWINGKEY, cfg.SwingKey);
        cfg.Focus = GetNum(ID_FOCUS, 1, 600, 25); cfg.Break = GetNum(ID_BREAK, 1, 600, 5);
        int sel = SendMessage(ctl[ID_SIZE], 0x0147, IntPtr.Zero, IntPtr.Zero).ToInt32();   // CB_GETCURSEL
        cfg.Size = sel >= 0 && sel < Sizes.Length ? Sizes[sel] : 1;

        var never = new List<string>();
        foreach (var line in GetText(ID_NEVER).Replace("\r\n", "\n").Split('\n'))
        { string t = line.Trim().ToLowerInvariant(); if (t.Length > 0) never.Add(t); }
        cfg.Never = never.ToArray();
        cfg.Rules = rules.ToArray();

        App.PatrolFlag(GetCheck(ID_PATROL));
        App.AutoStartFlag(GetCheck(ID_AUTOSTART));
        try
        {
            File.WriteAllText(rulesPath, Serialize(cfg));
            if (onSave != null) onSave(Copy(cfg));
            Status("Saved. " + DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (Exception e) { Status("Could not save: " + e.Message); }
    }

    static string Serialize(Cfg c)
    {
        var sb = new StringBuilder();
        sb.Append("# PixelPet Focus rules. The Settings window writes this file; hand edits are picked up within a second.\r\n\r\n");
        sb.Append("countdown = ").Append(c.Countdown).Append("\r\n");
        sb.Append("snooze = ").Append(c.Snooze).Append("\r\n");
        sb.Append("mode = ").Append(c.Nag ? "nag" : "close").Append("\r\n");
        sb.Append("wander = ").Append(YN(c.Wander)).Append("\r\n");
        sb.Append("sleepy = ").Append(YN(c.Sleepy)).Append("\r\n");
        sb.Append("typing = ").Append(YN(c.Typing)).Append("\r\n");
        sb.Append("enterrope = ").Append(YN(c.EnterRope)).Append("\r\n");
        sb.Append("climb = ").Append(YN(c.Climb)).Append("\r\n");
        sb.Append("webtravel = ").Append(YN(c.WebTravel)).Append("\r\n");
        sb.Append("audio = ").Append(YN(c.Audio)).Append("\r\n");
        sb.Append("clipboard = ").Append(YN(c.Clipboard)).Append("\r\n");
        sb.Append("hotkey = ").Append(YN(c.Hotkey)).Append("\r\n");
        sb.Append("clipkey = ").Append(c.ClipKey).Append("\r\n");
        sb.Append("swingkey = ").Append(c.SwingKey).Append("\r\n");
        sb.Append("size = ").Append(c.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("focus = ").Append(c.Focus).Append("\r\n");
        sb.Append("break = ").Append(c.Break).Append("\r\n");
        // keep settings this window doesn't know yet (added by a newer build or by hand), so Save never drops them
        var written = sb.ToString();
        try
        {
            foreach (var raw in File.ReadAllLines(rulesPath))
            {
                string line = raw.Trim(); int eq = line.IndexOf('=');
                if (line.StartsWith("#") || line.Contains("|") || eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                if (written.IndexOf("\n" + key + " = ", StringComparison.Ordinal) < 0 && !written.StartsWith(key + " = ")) sb.Append(line).Append("\r\n");
            }
        }
        catch { }
        sb.Append("\r\n");
        sb.Append("never | ").Append(string.Join(", ", c.Never)).Append("\r\n\r\n");
        sb.Append("# label | seconds allowed | phrases (any one matches). First match wins.\r\n");
        foreach (var r in c.Rules) sb.Append(r.Label).Append(" | ").Append(r.Grace).Append(" | ").Append(string.Join(", ", r.Any)).Append("\r\n");
        return sb.ToString();
    }

    static string YN(bool b) { return b ? "yes" : "no"; }

    static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case 0x0111:                                                            // WM_COMMAND
                Command(w.ToInt32() & 0xFFFF);
                return IntPtr.Zero;
            case 0x004E:                                                            // WM_NOTIFY
                var nm = (NMHDR)Marshal.PtrToStructure(l, typeof(NMHDR));
                if (nm.code == -101) ShowDetail();                                  // LVN_ITEMCHANGED
                return IntPtr.Zero;
            case 0x0010: DestroyWindow(h); return IntPtr.Zero;                       // WM_CLOSE
            case 0x0002:                                                            // WM_DESTROY
                hwnd = IntPtr.Zero; ctl.Clear();
                if (font != IntPtr.Zero) { DeleteObject(font); font = IntPtr.Zero; }
                PostQuitMessage(0);                                                  // ends this thread's loop only
                return IntPtr.Zero;
        }
        return DefWindowProc(h, m, w, l);
    }

    static void Command(int id)
    {
        int sel = Selected();
        switch (id)
        {
            case ID_SAVE: Save(); break;
            case ID_CLOSEBTN: case 2: DestroyWindow(hwnd); break;                    // 2 = IDCANCEL (Esc)
            case ID_OPENTXT: ShellExecute(IntPtr.Zero, "open", "notepad.exe", "\"" + rulesPath + "\"", null, 1); break;
            case ID_ADD:
                var add = FromFields();
                if (add != null) { rules.Add(add); Fill(rules.Count - 1); Status("Rule added. Press Save to keep it."); }
                break;
            case ID_APPLY:
                if (sel < 0) { Status("Pick a rule in the list first."); break; }
                var upd = FromFields();
                if (upd != null) { rules[sel] = upd; Fill(sel); Status("Rule updated. Press Save to keep it."); }
                break;
            case ID_DEL:
                if (sel < 0) { Status("Pick a rule in the list first."); break; }
                rules.RemoveAt(sel); Fill(Math.Min(sel, rules.Count - 1)); Status("Rule removed. Press Save to keep it.");
                break;
            case ID_UP:
                if (sel > 0) { var r = rules[sel]; rules[sel] = rules[sel - 1]; rules[sel - 1] = r; Fill(sel - 1); }
                break;
            case ID_DOWN:
                if (sel >= 0 && sel < rules.Count - 1) { var r = rules[sel]; rules[sel] = rules[sel + 1]; rules[sel + 1] = r; Fill(sel + 1); }
                break;
            case ID_DEFAULTS:
                rules.Clear();
                foreach (var r in App.DefaultRuleSet()) rules.Add(r);
                Fill(0); Status("Default rules restored. Press Save to keep them.");
                break;
        }
    }

    // ------------------------------------------------------------------ interop
    delegate IntPtr WndProcD(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public int cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)] struct INITCOMMONCONTROLSEX { public int dwSize, dwICC; }
    [StructLayout(LayoutKind.Sequential)] struct NMHDR { public IntPtr hwndFrom; public IntPtr idFrom; public int code; }
    [StructLayout(LayoutKind.Sequential)] struct LVCOLUMN { public uint mask; public int fmt, cx; public IntPtr pszText; public int cchTextMax, iSubItem, iImage, iOrder, cxMin, cxDefault, cxIdeal; }
    [StructLayout(LayoutKind.Sequential)]
    struct LVITEM
    {
        public uint mask; public int iItem, iSubItem; public uint state, stateMask;
        public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam;
        public int iIndent, iGroupId; public uint cColumns; public IntPtr puColumns, piColFmt; public int iGroup;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool IsDialogMessage(IntPtr h, ref MSG m);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageStr(IntPtr h, uint m, IntPtr w, string l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessageItem(IntPtr h, uint m, IntPtr w, ref LVITEM l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessageCol(IntPtr h, uint m, IntPtr w, ref LVCOLUMN l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr h, string t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr id);
    [DllImport("user32.dll")] static extern IntPtr GetSysColorBrush(int index);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint p, ref RECT r, uint winIni);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("comctl32.dll")] static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ExtractIcon(IntPtr inst, string file, int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ShellExecute(IntPtr h, string op, string file, string args, string dir, int show);
}
