# PixelPet Focus

A tiny pixel pet for your Windows desktop. It walks over and closes your doomscrolling tab.

PixelPet Focus is one small `.exe` (about 115 KB). It has no installer and no dependencies. It is written in C# against raw Win32 and GDI, and compiled with the C# compiler that ships with Windows.

## What it does

**Doomscroll patrol**
- Once a second it checks the window you're using: its title, its process name and, for browsers (Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Arc, Chromium, LibreWolf, Zen), the address-bar URL, which it reads through UI Automation.
- `rules.txt` gives each site its own grace time. By default that's 6 s for YouTube Shorts, Instagram Reels and TikTok, 45 s for Instagram, 90 s for Reddit and 4 min for YouTube videos. Swiping to the next reel doesn't restart the clock.
- In the last few seconds it turns to stare at the window. When the grace time runs out, it runs over, glares through a short countdown and closes the tab: Ctrl+W for browsers, a normal close request for other apps. It only does this if the same window is still in front, so switching back to work in time saves you.
- Click the pet during the countdown to snooze it (5 min by default). Set `mode = nag` to make it complain without closing anything.
- Meetings (Zoom, Teams, Google Meet) are on a "never touch" list.

**A pet that lives on your screen**
- It walks along the taskbar, jumps onto the title bar of the window you're using and rides along when you move that window. It falls off when the window is minimised, maximised, closed or covered.
- You can drag it and throw it. It falls with gravity, bounces off the screen edges and can land on windows.
- It climbs the screen edges and walks upside-down along the top.
- **Web-swing:** press **Ctrl+Alt+G** (or pick **Web-swing here** in the menu) and it shoots a web to your mouse pointer, swings over on an arc, and lands with a little squash, a puff of dust and hearts.
- It takes naps and follows your cursor with its eyes.
- **Curious visits:** every few minutes it hops onto (or walks under) the window you're using and reacts to what it is: code editors, terminals, GitHub, docs, mail, chat, video, music, games, shopping and more. Sometimes it asks "What are you up to?". Click it to answer from a quick menu (Working, Studying, Taking a break, Just browsing, Leave me alone). If you say you're working it gets quieter, and it nudges you if you drift to video or social media. It stays silent during meetings and focus sessions. It only uses the app name, window title and URL it already reads; nothing is captured or sent anywhere (`curious = no` turns it off).
- Clicking it counts as a pat and gives XP; closing a doomscroll tab, finishing reminders and Claude Code finishing a task give XP too. Your level is saved.
- **It evolves as it levels up:** Hatchling, then Companion at level 5 (a sprout), Scout at 10 (an explorer cap), Hero at 20 (a headband) and Legend at 35 (a crown). Hats step aside when it's wearing headphones.
- **14 achievements and a stats card.** **Stats & achievements** in the menu shows tabs closed, pats, focus sessions, reminders done, clipboard actions, Claude tasks and your daily streak, plus which achievements you've unlocked.
- **Stretch nudge:** after 90 minutes of non-stop activity (`stretch = 90`) it suggests a break. A 5-minute pause resets the clock, and it stays quiet during focus sessions, which have their own breaks.
- When you type, it pulls out a laptop and taps along. It spots typing without a keyboard hook and never records which keys you press (see [Privacy](#privacy)).
- Press Enter and it throws a rope at your text caret, or at the mouse pointer if the app doesn't expose a caret.
- On a laptop it reacts to the charger: a spark and a charging battery when you plug in, a note when you unplug, and warnings at 20 % and 10 % battery. Desktops without a battery see none of this.
- When headphones or a headset are connected it wears headphones. It looks at which apps are playing sound and at their window titles. If it's music, the pet bops along with notes floating up. If it's a class or a call (Zoom, Teams, lectures, courses and so on), it takes notes and stays put, with a headset mic if headphones are on.

**Focus timer**
- A Pomodoro timer: 25 min of focus, then a 5 min break (both configurable). The pet shows the countdown and celebrates when each phase ends.

**Clipboard helper**
- When you copy something useful, a `!` pops up over the pet. Click the pet within 8 seconds to see the options. You can also open the menu at any time with **Ctrl+Alt+R** or through the tray menu.
- Turn a copied phrase with a time in it into a reminder: "call mom in 20 min", "pay rent at 5pm", "standup tomorrow", "gym tomorrow at 7:30am". You can also remind yourself about any copied text in 10 min, 30 min, 1 hour, 3 hours or tomorrow at 9:00.
- Copy a clean link: it strips tracking parameters (`utm_*`, `fbclid`, `gclid`, `msclkid` and others, plus `si` on YouTube and Spotify links).
- Copy a math result: `12*7.5` becomes `90`, `(2+3)^2 - 1` becomes `24`. Dates and phone numbers aren't treated as math.
- Tidy whitespace: rejoins lines broken mid-paragraph (for example in PDF copies) and collapses runs of spaces.
- Change case: UPPER, lower or Title.
- It ignores anything that looks like a password, one-time code or API key, and anything a password manager marks as private (see [Privacy](#privacy)).

**Reminders**
- You write them in `reminders.txt`: repeating (`every 45m`), daily, weekdays only, or once at a date and time.
- When a reminder is due, the pet holds up a sign and beeps. If the pet is hidden, you get a tray notification. Click the pet to mark it done. Right-click to snooze it (5 min, 15 min, 1 hour or tomorrow at 9:00).
- `every` reminders skip while you've been away for more than 5 minutes. During a focus session they wait for your break.
- No reminder pops up while a fullscreen app, game or presentation is running. They wait until it ends.

**Claude Code companion**
- Choose **Claude Code > Connect Claude Code...** in the menu once. After you confirm, it adds four hooks (`UserPromptSubmit`, `Notification`, `Stop`, `SessionEnd`) to `~/.claude/settings.json`, backing the file up first to `settings.json.pixelpet-backup`. It never touches other hooks, and **Disconnect** removes only its own entries.
- While Claude works, a `Claude working 3:12` tag shows over the pet. When Claude needs your permission or is waiting for you, the pet runs to your cursor, waves and beeps. When a task finishes it cheers and earns 10 XP. If the pet is hidden, you get tray notifications instead. The menu lists each session with its project folder and state.
- The hook is `PixelPetFocus.exe --claude-hook`. It reads the event from Claude Code, hands it to the running pet through a window message, prints nothing and exits within a moment. If the pet isn't running it simply does nothing.

**Settings**
- **Settings...** in the menu opens a small native window: add, edit, reorder or remove site rules, edit the never-touch list, and flip every toggle. Launching the exe a second time also opens it.
- Everything is still saved as plain text (`rules.txt`), so you can hand-edit it too. `reminders.txt` opens in Notepad from the Reminders menu. Changes apply within a second of saving. Changing `size` or `hotkey` needs a restart.

## Install

1. Download `PixelPetFocus.exe` from the [Releases](../../releases) page. (If there's no release yet, see [Build from source](#build-from-source).)
2. Put it somewhere permanent, for example `Documents\PixelPet`, and double-click it. There is no installer.
3. The exe isn't code-signed, so Windows SmartScreen may say "Windows protected your PC". Click **More info**, then **Run anyway**.

It needs 64-bit Windows 10 (version 1607 or later) or Windows 11. The .NET Framework 4 it runs on is already part of Windows.

Settings and progress are saved in `%APPDATA%\PixelPet Focus`. To have it start when you log in, tick **Start with Windows** in its menu. This adds a value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

**To uninstall:** untick **Start with Windows**, choose **Quit**, then delete the exe and the `%APPDATA%\PixelPet Focus` folder.

## How to use

| Action | What happens |
| --- | --- |
| Left-click the pet | Pat it: hearts and XP. It also wakes it from a nap, snoozes the patrol during a countdown, marks a reminder sign as done, or opens the clipboard options while a `!` is showing. |
| Drag the pet | Pick it up. Let go to throw it. |
| Right-click the pet or the tray icon | Opens the menu. |
| Double-click the tray icon | Hides or shows the pet. |
| Ctrl+Alt+R | Opens the clipboard menu. |
| Enter | Throws the rope at your caret (`enterrope = yes`). |
| Ctrl+Alt+G | Web-swing: the pet shoots a web at your cursor and swings there (`webtravel = yes`). Also **Web-swing here** in the menu. |

The menu shows your level, battery and audio status. It also has: start/stop the focus timer, snooze or resume the patrol, **On patrol** (turns closing on or off), nap now, the **Clipboard** and **Reminders** submenus, **Settings...**, **Start with Windows**, hide/show the pet, and **Quit**.

### rules.txt

Anything after a `#` is a comment.

| Line | Meaning |
| --- | --- |
| `key = value` | A setting (see below). |
| `never \| phrase, phrase` | Never act on a window that contains any of these phrases. |
| `Label \| seconds \| phrase, phrase` | A rule: if any phrase matches, you get `seconds` before the pet closes the window. |

Phrases are matched case-insensitively as plain substrings against `URL | window title | process name`. The process name is the exe name without `.exe`, for example `chrome`. The first matching rule wins, so put specific rules above general ones:

```
never           | zoom meeting, microsoft teams, google meet
YouTube Shorts  | 6   | youtube.com/shorts
Twitch          | 120 | twitch.tv
YouTube         | 240 | youtube.com/watch, - youtube
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `countdown` | `3` | Seconds of warning before it closes the tab |
| `snooze` | `5` | Minutes a click on the pet buys you |
| `mode` | `close` | `close`, or `nag` to only complain |
| `wander` | `yes` | Walk around and jump on windows |
| `sleepy` | `yes` | Take naps |
| `typing` | `yes` | Laptop animation while you type |
| `enterrope` | `yes` | Rope on Enter |
| `clipboard` | `yes` | Clipboard helper |
| `hotkey` | `yes` | Ctrl+Alt+R (restart to apply) |
| `audio` | `yes` | Headphones and music/class reactions |
| `climb` | `yes` | Climb the screen edges |
| `webtravel` | `yes` | Ctrl+Alt+G web-swing to the cursor (restart to apply) |
| `curious` | `yes` | Visit your window, comment, ask what you're doing |
| `claude` | `yes` | React to Claude Code (after connecting it from the menu) |
| `stretch` | `90` | Minutes of non-stop activity before a stretch nudge (`0` = off) |
| `size` | `1` | Pet size, 0.5 to 4 (restart to apply) |
| `focus` / `break` | `25` / `5` | Focus timer minutes |

### reminders.txt

Write one reminder per line as `when | message`. Lines starting with `#` are ignored.

| When | Example |
| --- | --- |
| `every <n>m` or `every <n>h` (at least 5 min) | `every 45m \| Drink some water` |
| `every ...` with a time window | `every 20m 09:00-18:00 \| Look 20 feet away` |
| `daily HH:MM` | `daily 13:00 \| Lunch break` |
| `weekdays HH:MM` (Mon to Fri) | `weekdays 09:25 \| Standup` |
| `YYYY-MM-DD HH:MM` (once) | `2026-09-18 17:40 \| Call mom` |

When you mark a one-time reminder as done, its line is deleted. Snoozing one rewrites its line with the new time. If you save the file, running `every` timers carry on and don't restart. If the pet can't understand a line, it tells you the line number.

## Build from source

You need only Windows. There's no SDK, NuGet or Visual Studio. `build.cmd` calls the .NET Framework 4 C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`):

```
build.cmd
```

This produces `PixelPetFocus.exe` in the repo root. The code is a single C# 5 file, `src/PixelPetFocus.cs`. `app.ico` is committed. If you delete it, `build.cmd` regenerates it with `src/make_icon.py` (standard-library Python 3).

Self-test (rule matching, time parsing, secret detection, math, link cleaning, reminder parsing, activity detection, Claude hook payloads, ranks). The exit code is the number of failed checks, so `0` means everything passed:

```powershell
(Start-Process .\PixelPetFocus.exe -ArgumentList '--selftest' -Wait -PassThru).ExitCode
```

GitHub Actions builds every push the same way, runs the self-test and attaches the exe to a Release when a `v*` tag is pushed.

## Privacy

- **No network access at all.** The code has no networking APIs: no `System.Net`, sockets, WinINet or WinHTTP. Its P/Invoke calls go only to `user32`, `gdi32`, `kernel32`, `shell32`, `dwmapi` and `ole32`. The only COM components it uses are UI Automation and Core Audio. There's no telemetry and no update checks.
- **What it looks at:** the foreground window's title and process name, and the address bar in the browsers listed above. It checks these once a second in memory, only to match your rules. For audio it reads device names, output levels and the titles of the windows playing sound. It never reads the sound itself.
- **No keylogging.** There's no keyboard hook. To detect typing it checks that new input arrived, that the mouse didn't move and that some text key is held down right now. All that feeds is an "is typing" level. It never records or stores which keys you press. The Enter rope only checks whether Enter is down.
- **Clipboard:** it only looks at text copies of up to 2,000 characters. It skips anything marked private by password managers or clipboard-history exclusions, anything that looks like a key or token (`sk-`, `ghp_`, `github_pat_`, `AKIA`, `xox`, JWTs, PEM blocks), 4 to 8 digit codes, and single words that mix letters, digits and symbols like a password. Clipboard text stays in memory and is never written to disk. The one exception is when you pick a **Remind me** option: then the reminder's short message is saved to `reminders.txt`.
- **Claude Code:** the hook passes only the event name, the session id, the project folder and Claude's notification text to the pet. It sends them through a local window message, and nothing leaves your PC. `~/.claude/settings.json` is changed only when you click **Connect** or **Disconnect**. That runs as a separate short-lived process, so the pet itself never loads a JSON library.
- **What it writes:** `rules.txt`, `reminders.txt` and `progress.txt` (level, XP, counters, streak and achievements) in `%APPDATA%\PixelPet Focus`, plus the Run registry value if you tick **Start with Windows**.

## Resource use

Measured on a Windows 11 laptop: **3-5 MB private working set** and **about 0-1 % CPU**. The pet is a layered window drawn with GDI at 30 fps. There's no WinForms, WPF or WebView.

## Credits

PixelPet Focus is a native rewrite inspired by [BadKat / FocusCat](https://github.com/X-DIABLO-X/badkat) by X-DIABLO-X (MIT licensed). The default rule set and the approach of matching URL, title and process with a grace time per site come from that project. Its license notice is included in [LICENSE](LICENSE).

The Claude Code status, evolution stages, achievements and break reminder were inspired by [AgentPet](https://github.com/ntd4996/agentpet) by Nguyễn Thành Đạt (MIT licensed). The features were reimplemented from scratch; no AgentPet code is included.

The pixel pet's look is a fan tribute to the Claude Code mascot. This is an unofficial personal project and is not affiliated with or endorsed by Anthropic.

## License

[MIT](LICENSE) © 2026 Samuvel Johnson
