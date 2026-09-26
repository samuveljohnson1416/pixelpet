// PixelPet Focus for macOS: the part of the brain that needs no screen. Rules, clipboard text helpers,
// reminders, progress bookkeeping and the agent-hook installer. Pure Foundation, so `--selftest` checks all
// of it headless in CI. Same logic as src/PixelPetFocus.cs; keep the two in step.
import Foundation

let cal: Calendar = { var c = Calendar(identifier: .gregorian); c.timeZone = .current; return c }()
let posix = Locale(identifier: "en_US_POSIX")

func isDigit(_ c: Character) -> Bool { c >= "0" && c <= "9" }
func trimmed(_ s: String) -> String { s.trimmingCharacters(in: .whitespacesAndNewlines) }
func hasNewline(_ s: String) -> Bool { s.contains(where: { $0.isNewline }) }
func clamp(_ v: Double, _ a: Double, _ b: Double) -> Double { v < a ? a : v > b ? b : v }
func rand(_ a: Double, _ b: Double) -> Double { a + Double.random(in: 0..<1) * (b - a) }
func pick(_ a: [String]) -> String { a[Int.random(in: 0..<a.count)] }
func firstNonEmpty(_ v: String...) -> String { v.first(where: { !$0.isEmpty }) ?? "" }

var formatters: [String: DateFormatter] = [:]
func formatter(_ f: String) -> DateFormatter {
    if let d = formatters[f] { return d }
    let d = DateFormatter(); d.locale = posix; d.calendar = cal; d.timeZone = .current; d.dateFormat = f
    formatters[f] = d
    return d
}
func fmt(_ d: Date, _ f: String) -> String { formatter(f).string(from: d) }
func parseDate(_ s: String, _ f: String) -> Date? { formatter(f).date(from: s) }
func dayStart(_ d: Date) -> Date { cal.startOfDay(for: d) }
func addDays(_ d: Date, _ n: Int) -> Date { cal.date(byAdding: .day, value: n, to: d) ?? d }
func atTime(_ day: Date, _ h: Int, _ m: Int) -> Date {
    var c = cal.dateComponents([.year, .month, .day], from: day)
    c.hour = h; c.minute = m; c.second = 0
    return cal.date(from: c) ?? day
}
func mk(_ y: Int, _ mo: Int, _ d: Int, _ h: Int = 0, _ mi: Int = 0) -> Date {
    cal.date(from: DateComponents(year: y, month: mo, day: d, hour: h, minute: mi)) ?? Date()
}

// ---------------------------------------------------------------------- settings file
struct Rule { var label: String; var grace: Int; var any: [String] }

final class Cfg {
    var countdown = 3, snooze = 5, focus = 25, brk = 5, stretch = 90, water = 45, screenTime = 60
    var agents = true, push = true, quiet = false, costumes = true
    var clipKey = "ctrl+alt+shift+c", swingKey = "ctrl+alt+shift+w"
    var nag = false, wander = true, sleepy = true, typing = true, enterRope = true, climb = true
    var audio = true, clipboard = true, hotkey = true, curious = true, webTravel = true
    var size = 1.0
    var never: [String] = []
    var rules: [Rule] = []
}

let defaultRules = """
# PixelPet Focus rules. Saved changes apply within a second (size and shortcuts need a restart).
countdown = 3      # seconds of warning before it closes the tab
snooze = 5         # minutes a click on the pet buys you
mode = close       # close | nag  (nag only complains, never closes)
wander = yes       # walk around, jump on windows
sleepy = yes       # take naps
typing = yes       # pulls out a laptop while you type (never records keys)
enterrope = yes    # throws a rope at your text cursor when you press Return
climb = yes        # climbs the screen edges and walks upside-down along the top
webtravel = yes    # shoots a web to the pointer and swings the pet there (swingkey below)
clipboard = yes    # notices useful copied text (times, links, sums); ignores passwords
hotkey = yes       # turn the two shortcut keys below on or off (restart to apply)
clipkey = ctrl+alt+shift+c    # opens the clipboard menu. ctrl / alt (option) / shift / cmd + a letter, digit or F-key; off = none
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
never | zoom meeting, microsoft teams, google meet, facetime

# label | seconds allowed | phrases matched against URL, title or app name (any one)
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

"""

let defaultReminders = """
# PixelPet Focus reminders, one per line:   when | message
# Saved changes apply within a second. Remove the # to turn an example on.
# Recurring reminders pause while you are away, wait for your break during a focus
# session, and stay quiet during presentations and fullscreen apps.
#
# every 45m | Drink some water
# every 20m 09:00-18:00 | 20-20-20: look at something 20 feet away
# daily 13:00 | Lunch break
# weekdays 09:25 | Standup
#
# One-shot reminders look like the line below. Copy text like "call mom in 20 min"
# and click the pet: it adds these for you and deletes them when you click Done.
# 2026-09-18 17:40 | Call mom

"""

func splitList(_ s: String) -> [String] {
    s.split(separator: ",").map { trimmed(String($0)).lowercased() }.filter { !$0.isEmpty }
}

func parseCfg(_ lines: [String]) -> Cfg {
    let c = Cfg(); var rules: [Rule] = []
    for raw in lines {
        var line = raw
        if let hash = line.firstIndex(of: "#") { line = String(line[..<hash]) }
        line = trimmed(line)
        if line.isEmpty { continue }
        if line.contains("|") {
            let p = line.components(separatedBy: "|")
            let pats = splitList(p[p.count - 1])
            if trimmed(p[0]).lowercased() == "never" { c.never = pats }
            else if p.count >= 3, let g = Int(trimmed(p[1])), !pats.isEmpty { rules.append(Rule(label: trimmed(p[0]), grace: g, any: pats)) }
        } else if let eq = line.firstIndex(of: "=") {
            let k = trimmed(String(line[..<eq])).lowercased(), v = trimmed(String(line[line.index(after: eq)...])).lowercased()
            let n = Int(v) ?? 0, yes = v == "yes" || v == "true" || v == "on"
            switch k {
            case "countdown": c.countdown = max(0, n)
            case "snooze": c.snooze = max(1, n)
            case "focus": c.focus = max(1, n)
            case "break": c.brk = max(1, n)
            case "stretch": c.stretch = max(0, n)
            case "water": c.water = max(0, n)
            case "screentime": c.screenTime = max(0, n)
            case "claude", "agents": c.agents = yes
            case "push": c.push = yes
            case "quiet": c.quiet = yes
            case "costumes": c.costumes = yes
            case "mode": c.nag = v == "nag"
            case "wander": c.wander = yes
            case "sleepy": c.sleepy = yes
            case "typing": c.typing = yes
            case "enterrope": c.enterRope = yes
            case "climb": c.climb = yes
            case "webtravel": c.webTravel = yes
            case "clipboard": c.clipboard = yes
            case "hotkey": c.hotkey = yes
            case "clipkey": c.clipKey = v
            case "swingkey": c.swingKey = v
            case "curious": c.curious = yes
            case "audio": c.audio = yes
            case "size": if let d = Double(v) { c.size = max(0.5, min(4, d)) }
            default: break
            }
        }
    }
    c.rules = rules
    return c
}

func matchRule(_ c: Cfg, _ url: String, _ title: String, _ proc: String) -> Rule? {
    if title.isEmpty && url.isEmpty { return nil }
    let hay = (url + " | " + title + " | " + proc).lowercased()
    for n in c.never where hay.contains(n) { return nil }
    for r in c.rules { for p in r.any where hay.contains(p) { return r } }
    return nil
}

// Browsers prepend unread counts "(8) " and tick them mid-countdown; that must not look like a new page.
func stripBadge(_ t0: String) -> String {
    let t = String(t0.drop(while: { $0.isWhitespace }))
    guard t.hasPrefix("("), let close = t.firstIndex(of: ")") else { return t }
    let inner = t[t.index(after: t.startIndex)..<close]
    if !inner.isEmpty && inner.allSatisfy(isDigit) { return String(t[t.index(after: close)...].drop(while: { $0.isWhitespace })) }
    return t
}

// ---------------------------------------------------------------------- progress, ranks, achievements, days
func cost(_ level: Int) -> Int { max(5, Int((50 * pow(1.28, Double(level - 1)) / 5).rounded(.toNearestOrEven)) * 5) }
// "45 min", "1h 30m", "2h": how long, for the break buddy.
func span(_ seconds: Int) -> String { let m = seconds / 60; return m < 60 ? "\(m) min" : "\(m / 60)h" + (m % 60 > 0 ? " \(m % 60)m" : "") }
func rank(_ lv: Int) -> String { lv >= 35 ? "Legend" : lv >= 20 ? "Hero" : lv >= 10 ? "Scout" : lv >= 5 ? "Companion" : "Hatchling" }
func nextRank(_ lv: Int) -> Int { lv < 5 ? 5 : lv < 10 ? 10 : lv < 20 ? 20 : lv < 35 ? 35 : 0 }

let achNames = ["First patrol", "Doomscroll slayer", "Deep focus", "Focus machine", "Remembered!", "Clipboard pro",
                "Agent buddy", "Pair programmer", "On a roll", "Week warrior", "Night owl", "Companion", "Hero", "Legend"]
let achHow = ["close a doomscroll tab", "close 25 of them", "finish a focus session", "finish 25 focus sessions",
              "finish a reminder", "use 10 clipboard actions", "a coding agent finishes a task", "agents finish 100 tasks",
              "3-day streak", "7-day streak", "be active after midnight", "reach level 5", "reach level 20", "reach level 35"]

// today: closes, pats, focus, reminders, clipboard, agent tasks, late
func encodeDay(_ d: Date, _ t: [Int]) -> String { fmt(d, "yyyyMMdd") + ":" + t.map { String($0) }.joined(separator: ",") }

func decodeDay(_ e: String) -> (Date, [Int])? {
    let parts = e.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)
    guard parts.count == 2, parts[0].count == 8, parts[0].allSatisfy(isDigit), let d = parseDate(String(parts[0]), "yyyyMMdd") else { return nil }
    var t = [Int](repeating: 0, count: 7)
    for (i, f) in parts[1].split(separator: ",", omittingEmptySubsequences: false).enumerated() where i < 7 { t[i] = Int(f) ?? 0 }
    return (d, t)
}

func daySummary(_ d: Date, _ t: [Int]) -> String? {
    var bits: [String] = []
    if t[0] > 0 { bits.append("closed \(t[0]) " + (t[0] == 1 ? "doomscroll tab" : "doomscroll tabs")) }
    if t[2] > 0 { bits.append("\(t[2]) " + (t[2] == 1 ? "focus session" : "focus sessions")) }
    if t[3] > 0 { bits.append("\(t[3]) " + (t[3] == 1 ? "reminder done" : "reminders done")) }
    if t[5] > 0 { bits.append("agents finished \(t[5]) " + (t[5] == 1 ? "task" : "tasks")) }
    if t[1] > 0 { bits.append("\(t[1]) " + (t[1] == 1 ? "pat" : "pats")) }
    if t[4] > 0 { bits.append("\(t[4]) " + (t[4] == 1 ? "clipboard help" : "clipboard helps")) }
    if bits.isEmpty { return nil }
    return fmt(d, "MMM d") + ": " + bits.joined(separator: ", ") + "." + (t[6] > 0 ? " Stayed up late." : "")
}

// A quadratic bezier from start to target through an overhead anchor, eased like a real swing.
func swingPos(_ sx0: Double, _ sy0: Double, _ ax: Double, _ ay: Double, _ tx: Double, _ ty: Double, _ u: Double) -> (Double, Double) {
    let e = u * u * (3 - 2 * u)
    let a1 = (1 - e) * (1 - e), b1 = 2 * e * (1 - e), c1 = e * e
    return (a1 * sx0 + b1 * ax + c1 * tx, a1 * sy0 + b1 * ay + c1 * ty)
}

// ---------------------------------------------------------------------- audio words
let talkWords = ["zoom", "teams", "meet", "webex", "skype", "discord", "facetime", "lecture", "class", "course", "lesson", "tutorial", "webinar", "seminar", "udemy", "coursera", "nptel", "edx", "unacademy", "khan academy", "podcast", "interview"]
let musicWords = ["spotify", "music", "song", "songs", "lyrics", "playlist", "album", "soundcloud", "jiosaavn", "gaana", "wynk", "deezer", "tidal", "lofi", "lo-fi", "remix", "official video", "official audio", "mix"]
let phoneWords = ["headphone", "headset", "earphone", "earbud", "buds", "airpods", "beats", "hands-free", "wh-", "wf-"]

func hasWord(_ text: String, _ w: String) -> Bool {
    var search = text.startIndex..<text.endIndex
    while let r = text.range(of: w, range: search) {
        let before = r.lowerBound == text.startIndex || !text[text.index(before: r.lowerBound)].isLetter
        let after = r.upperBound == text.endIndex || !text[r.upperBound].isLetter
        if before && after { return true }
        search = text.index(after: r.lowerBound)..<text.endIndex
    }
    return false
}

func classify(_ text: String) -> Int {
    for w in talkWords where hasWord(text, w) { return 2 }
    for w in musicWords where hasWord(text, w) { return 1 }
    return 0
}

func isHeadphones(_ name: String) -> Bool { phoneWords.contains(where: { name.contains($0) }) }

// ---------------------------------------------------------------------- reminders
final class Reminder {
    var line = "", kind = "", msg = ""
    var at = Date.distantPast, next = Date.distantPast
    var tod = 0, every = 0, from = -1, to = -1
}

func tod(_ s: String) -> Int? {
    let p = s.split(separator: ":", omittingEmptySubsequences: false)
    guard p.count == 2, (1...2).contains(p[0].count), p[1].count == 2, p[0].allSatisfy(isDigit), p[1].allSatisfy(isDigit),
          let h = Int(p[0]), let m = Int(p[1]), h < 24, m < 60 else { return nil }
    return h * 60 + m
}

func isWeekend(_ d: Date) -> Bool { let w = cal.component(.weekday, from: d); return w == 1 || w == 7 }

func nextDaily(_ r: Reminder, _ after: Date) -> Date {
    var d = atTime(after, r.tod / 60, r.tod % 60)
    while d <= after || (r.kind == "weekdays" && isWeekend(d)) { d = atTime(addDays(d, 1), r.tod / 60, r.tod % 60) }
    return d
}

func parseReminder(_ line: String, _ now: Date) -> Reminder? {
    guard let bar = line.firstIndex(of: "|") else { return nil }
    let when = trimmed(String(line[..<bar])).lowercased(), msg = trimmed(String(line[line.index(after: bar)...]))
    if msg.isEmpty { return nil }
    let r = Reminder()
    r.line = trimmed(line); r.msg = msg.count > 60 ? String(msg.prefix(60)) + "..." : msg
    let parts = when.split(separator: " ").map { String($0) }
    if parts.count >= 2 && parts.count <= 3 && parts[0] == "every" {
        let p = parts[1], digits = p.dropLast()
        guard let unit = p.last, unit == "m" || unit == "h", !digits.isEmpty, digits.allSatisfy(isDigit), let n = Int(digits), n > 0 else { return nil }
        r.kind = "every"; r.every = max(5, unit == "h" ? n * 60 : n)
        if parts.count == 3 {
            let win = parts[2].split(separator: "-").map { String($0) }
            guard win.count == 2, let a = tod(win[0]), let b = tod(win[1]) else { return nil }
            r.from = a; r.to = b
        }
        r.next = now.addingTimeInterval(Double(r.every) * 60)
    } else if parts.count == 2 && (parts[0] == "daily" || parts[0] == "weekdays"), let t = tod(parts[1]) {
        r.kind = parts[0]; r.tod = t; r.next = nextDaily(r, now)
    } else if let at = parseDate(when, "yyyy-MM-dd HH:mm") ?? parseDate(when, "yyyy-MM-dd H:mm") {
        r.kind = "once"; r.at = at; r.next = at
    } else { return nil }
    return r
}

// ---------------------------------------------------------------------- global shortcuts
// "ctrl+alt+shift+c", "cmd+f9", "ctrl+space": modifiers in any order, then one key. Carbon modifier bits and
// virtual key codes, written out here so this file stays plain Foundation.
struct Hotkey { var mods: UInt32; var code: UInt32; var text: String }

enum Keys {
    static let control: UInt32 = 0x1000, option: UInt32 = 0x0800, shift: UInt32 = 0x0200, command: UInt32 = 0x0100
    static let codes: [String: UInt32] = [
        "a": 0x00, "b": 0x0B, "c": 0x08, "d": 0x02, "e": 0x0E, "f": 0x03, "g": 0x05, "h": 0x04, "i": 0x22, "j": 0x26,
        "k": 0x28, "l": 0x25, "m": 0x2E, "n": 0x2D, "o": 0x1F, "p": 0x23, "q": 0x0C, "r": 0x0F, "s": 0x01, "t": 0x11,
        "u": 0x20, "v": 0x09, "w": 0x0D, "x": 0x07, "y": 0x10, "z": 0x06,
        "0": 0x1D, "1": 0x12, "2": 0x13, "3": 0x14, "4": 0x15, "5": 0x17, "6": 0x16, "7": 0x1A, "8": 0x1C, "9": 0x19,
        "f1": 0x7A, "f2": 0x78, "f3": 0x63, "f4": 0x76, "f5": 0x60, "f6": 0x61, "f7": 0x62, "f8": 0x64, "f9": 0x65, "f10": 0x6D,
        "f11": 0x67, "f12": 0x6F, "f13": 0x69, "f14": 0x6B, "f15": 0x71, "f16": 0x6A, "f17": 0x40, "f18": 0x4F, "f19": 0x50, "f20": 0x5A,
        "space": 0x31, "return": 0x24, "enter": 0x24, "tab": 0x30, "backspace": 0x33, "delete": 0x75,
        "home": 0x73, "end": 0x77, "pageup": 0x74, "pgup": 0x74, "pagedown": 0x79, "pgdn": 0x79,
        "left": 0x7B, "right": 0x7C, "down": 0x7D, "up": 0x7E, "`": 0x32, "backtick": 0x32]

    static func parse(_ s: String?) -> Hotkey? {
        guard let s = s else { return nil }
        var mods: UInt32 = 0, code: UInt32? = nil, name = ""
        for raw in s.lowercased().split(separator: "+", omittingEmptySubsequences: false) {
            let part = trimmed(String(raw))
            if part.isEmpty { return nil }
            switch part {
            case "ctrl", "control": mods |= control; continue
            case "alt", "option", "opt": mods |= option; continue
            case "shift": mods |= shift; continue
            case "cmd", "command", "win", "windows", "super": mods |= command; continue
            default: break
            }
            if code != nil { return nil }                                        // only one non-modifier key
            guard let c = codes[part] else { return nil }
            code = c
            name = part.count == 1 ? part.uppercased() : part.hasPrefix("f") && part.dropFirst().allSatisfy(isDigit) ? part.uppercased() : part.capitalized
        }
        guard let k = code, mods != 0 else { return nil }                        // a bare key would swallow normal typing
        var t = ""
        if mods & control != 0 { t += "\u{2303}" }
        if mods & option != 0 { t += "\u{2325}" }
        if mods & shift != 0 { t += "\u{21E7}" }
        if mods & command != 0 { t += "\u{2318}" }
        return Hotkey(mods: mods, code: k, text: t + name)
    }
}

// ---------------------------------------------------------------------- clipboard text helpers
enum TextTools {
    static let trackers = ["fbclid", "gclid", "dclid", "msclkid", "yclid", "twclid", "igsh", "igshid", "mc_cid", "mc_eid", "ref_src", "_hsenc", "_hsmktg"]
    static let secretPrefixes = ["sk-", "ghp_", "github_pat_", "AKIA", "xox", "eyJ"]
    static let leads = ["remind me to ", "remind me ", "don't forget to ", "dont forget to ", "to "]

    // Passwords, one-time codes, API keys: never previewed, stored or offered.
    static func looksSecret(_ s: String) -> Bool {
        let t = trimmed(s)
        if t.contains("-----BEGIN") { return true }
        if t.contains(" ") || t.contains("\t") || hasNewline(t) { return false }
        for p in secretPrefixes where t.hasPrefix(p) { return true }
        if !t.isEmpty && t.allSatisfy(isDigit) { return t.count >= 4 && t.count <= 8 }
        if t.count < 8 || t.count > 128 || isUrl(t) || calc(t) != nil { return false }
        var lower = 0, upper = 0, digit = 0, sym = 0
        for ch in t { if ch.isLowercase { lower = 1 } else if ch.isUppercase { upper = 1 } else if isDigit(ch) { digit = 1 } else { sym = 1 } }
        return lower + upper + digit + sym >= 3
    }

    static func isUrl(_ t: String) -> Bool {
        let l = t.lowercased()
        return !t.contains(" ") && (l.hasPrefix("http://") || l.hasPrefix("https://"))
    }

    static func cleanLink(_ text: String) -> (String, Int) {
        let url = trimmed(text)
        if !isUrl(url) || hasNewline(url) { return (text, 0) }
        var head = url, frag = ""
        if let h = url.firstIndex(of: "#") { head = String(url[..<h]); frag = String(url[h...]) }
        guard let q = head.firstIndex(of: "?") else { return (text, 0) }
        let base = String(head[..<q]), host = base.lowercased()
        let siHost = host.contains("youtube.com") || host.contains("youtu.be") || host.contains("spotify.com")
        var kept: [String] = [], removed = 0
        for pair in head[head.index(after: q)...].split(separator: "&") {
            let key = String(pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false).first ?? "").lowercased()
            if key.hasPrefix("utm_") || trackers.contains(key) || (siHost && key == "si") { removed += 1; continue }
            kept.append(String(pair))
        }
        if removed == 0 { return (text, 0) }
        return (base + (kept.isEmpty ? "" : "?" + kept.joined(separator: "&")) + frag, removed)
    }

    // + - * / % ^ ( ) and x-ish symbols. Refuses dates (2026-09-18, 18/09/2026) and phone numbers.
    static func calc(_ s0: String) -> Double? {
        let s = trimmed(s0)
        if s.isEmpty || s.count > 100 { return nil }
        var slashes = 0, digit = false, strongOp = false
        for ch in s {
            if isDigit(ch) { digit = true } else if !"+-*/%^(). \u{00D7}\u{00F7}".contains(ch) { return nil }
            if ch == "/" { slashes += 1 }
            if "*^+%\u{00D7}\u{00F7}".contains(ch) { strongOp = true }
        }
        let op = strongOp || slashes == 1 || s.contains(" - ")
        if !digit || !op || (slashes >= 2 && !strongOp) { return nil }
        let e = Array(s.replacingOccurrences(of: " ", with: "").replacingOccurrences(of: "\u{00D7}", with: "*").replacingOccurrences(of: "\u{00F7}", with: "/"))
        var p = 0
        guard let v = try? expr(e, &p), p == e.count, v.isFinite else { return nil }
        return v
    }

    struct Bad: Error {}

    static func expr(_ e: [Character], _ p: inout Int) throws -> Double {
        var v = try term(e, &p)
        while p < e.count && (e[p] == "+" || e[p] == "-") {
            let o = e[p]; p += 1
            let r = try term(e, &p)
            v = o == "+" ? v + r : v - r
        }
        return v
    }

    static func term(_ e: [Character], _ p: inout Int) throws -> Double {
        var v = try power(e, &p)
        while p < e.count && (e[p] == "*" || e[p] == "/" || e[p] == "%") {
            let o = e[p]; p += 1
            let r = try power(e, &p)
            v = o == "*" ? v * r : o == "/" ? v / r : v.truncatingRemainder(dividingBy: r)
        }
        return v
    }

    static func power(_ e: [Character], _ p: inout Int) throws -> Double {
        let v = try unary(e, &p)
        if p < e.count && e[p] == "^" {
            p += 1
            let r = try power(e, &p)
            return pow(v, r)
        }
        return v
    }

    static func unary(_ e: [Character], _ p: inout Int) throws -> Double {
        if p < e.count && e[p] == "-" { p += 1; let u = try unary(e, &p); return -u }
        if p < e.count && e[p] == "+" { p += 1; return try unary(e, &p) }
        if p < e.count && e[p] == "(" {
            p += 1
            let v = try expr(e, &p)
            if p >= e.count || e[p] != ")" { throw Bad() }
            p += 1
            return v
        }
        let st = p
        while p < e.count && (isDigit(e[p]) || e[p] == ".") { p += 1 }
        if st == p { throw Bad() }
        guard let d = Double(String(e[st..<p])) else { throw Bad() }
        return d
    }

    // Joins lines broken mid-paragraph (PDF copies), collapses runs of spaces, keeps blank-line paragraphs.
    static func tidy(_ s: String) -> String {
        var paras: [String] = [], para = ""
        for raw in s.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            let line = trimmed(raw)
            if line.isEmpty { if !para.isEmpty { paras.append(para); para = "" }; continue }
            if !para.isEmpty { para += " " }
            para += line
        }
        if !para.isEmpty { paras.append(para) }
        var out = "", prevSpace = false
        for ch in paras.joined(separator: "\n\n") {
            let sp = ch == " " || ch == "\t"
            if sp && prevSpace { continue }
            out.append(sp ? " " : ch); prevSpace = sp
        }
        return out
    }

    // "in 20 min", "in 1.5h", "in an hour", "at 5pm", "at 17:30", "tomorrow", "tomorrow at 9am", bare "5:30pm"/"17:30".
    static func parseWhen(_ text: String?, _ now: Date) -> (Date, String)? {
        guard let text = text, text.count <= 200 else { return nil }
        var line = trimmed(text)
        if let nl = line.firstIndex(where: { $0.isNewline }) { line = String(line[..<nl]) }
        let chars = Array(line)
        var words: [String] = [], starts: [Int] = [], ends: [Int] = []
        var i = 0
        while i < chars.count {
            while i < chars.count && chars[i].isWhitespace { i += 1 }
            let st = i
            while i < chars.count && !chars[i].isWhitespace { i += 1 }
            if i > st {
                var w = String(chars[st..<i]).lowercased()
                while let tail = w.last, ",.!?;".contains(tail) { w.removeLast() }
                words.append(w); starts.append(st); ends.append(i)
            }
        }
        var from = -1, last = -1, when = now
        var k = 0
        while k < words.count && from < 0 {
            let w = words[k]
            if w == "in" && k + 1 < words.count {
                var n = 0.0, unit = "", used = 1
                if words[k + 1] == "an" || words[k + 1] == "a" { n = 1; unit = k + 2 < words.count ? words[k + 2] : ""; used = 2 }
                else if let sn = splitNumber(words[k + 1]) {
                    n = sn.0; unit = sn.1
                    if unit.isEmpty { unit = k + 2 < words.count ? words[k + 2] : ""; used = 2 }
                } else { k += 1; continue }
                let total = n * unitMinutes(unit)
                if total < 1 || total > 30 * 24 * 60 { k += 1; continue }
                when = now.addingTimeInterval(total.rounded(.toNearestOrEven) * 60); from = k; last = k + used
            } else if w == "tomorrow" {
                let j = k + 1 < words.count && words[k + 1] == "at" ? k + 2 : k + 1
                var h = 9, m = 0
                if j < words.count, let c = clock(words, j) { h = c.0; m = c.1; last = j + c.2 - 1 } else { last = k }
                when = atTime(addDays(now, 1), h, m); from = k
            } else if w == "at" && k + 1 < words.count, let c = clock(words, k + 1) {
                when = atTime(now, c.0, c.1)
                if when <= now { when = addDays(when, 1) }
                from = k; last = k + c.2
            }
            k += 1
        }
        if from < 0 && chars.count <= 80 {
            var j = 0
            while j < words.count && from < 0 {
                if let c = clock(words, j) {
                    when = atTime(now, c.0, c.1)
                    if when <= now { when = addDays(when, 1) }
                    from = j; last = j + c.2 - 1
                }
                j += 1
            }
        }
        if from < 0 { return nil }

        var rest = trimmed(String(chars[0..<starts[from]]) + " " + String(chars[ends[last]...]))
        let lower = rest.lowercased()
        for lead in leads where lower.hasPrefix(lead) { rest = String(rest.dropFirst(lead.count)); break }
        var sb = "", sp = false
        for ch in rest { let isSp = ch.isWhitespace; if isSp && sp { continue }; sb.append(isSp ? " " : ch); sp = isSp }
        rest = sb.trimmingCharacters(in: CharacterSet(charactersIn: " ,.;:-"))
        if rest.isEmpty { rest = "Reminder" }
        return (when, String(rest.prefix(60)))
    }

    static func splitNumber(_ w: String) -> (Double, String)? {
        let c = Array(w)
        var k = 0
        while k < c.count && (isDigit(c[k]) || c[k] == ".") { k += 1 }
        guard k > 0, let n = Double(String(c[0..<k])) else { return nil }
        return (n, String(c[k...]))
    }

    static func unitMinutes(_ u: String) -> Double {
        switch u {
        case "m", "min", "mins", "minute", "minutes": return 1
        case "h", "hr", "hrs", "hour", "hours": return 60
        case "d", "day", "days": return 1440
        default: return 0
        }
    }

    // "5pm", "5 pm", "5:30pm", "17:30". A bare "5" is not a time. Returns hour, minute, words used.
    static func clock(_ w: [String], _ j: Int) -> (Int, Int, Int)? {
        var t = w[j], suffix = "", used = 1
        if t.hasSuffix("am") || t.hasSuffix("pm") { suffix = String(t.suffix(2)); t = String(t.dropLast(2)) }
        else if j + 1 < w.count && (w[j + 1] == "am" || w[j + 1] == "pm") { suffix = w[j + 1]; used = 2 }
        let colon = t.firstIndex(of: ":")
        if colon == nil && suffix.isEmpty { return nil }
        let hs = colon.map { String(t[..<$0]) } ?? t, ms = colon.map { String(t[t.index(after: $0)...]) } ?? "0"
        if hs.isEmpty || hs.count > 2 || (colon != nil && ms.count != 2) { return nil }
        guard hs.allSatisfy(isDigit), ms.allSatisfy(isDigit), var h = Int(hs), let m = Int(ms), m <= 59 else { return nil }
        if !suffix.isEmpty {
            if h < 1 || h > 12 { return nil }
            if h == 12 { h = 0 }
            if suffix == "pm" { h += 12 }
        } else if h > 23 { return nil }
        return (h, m, used)
    }

    // One string field out of a small JSON object (agent hook payloads). Handles the usual escapes.
    static func jsonField(_ json: String, _ key: String) -> String {
        let u = Array(json.utf16), k = Array(("\"" + key + "\"").utf16)
        guard !k.isEmpty, u.count >= k.count else { return "" }
        var start = -1
        var i = 0
        while i <= u.count - k.count {
            if u[i] == k[0] && Array(u[i..<(i + k.count)]) == k { start = i; break }
            i += 1
        }
        if start < 0 { return "" }
        i = start + k.count
        func ws(_ c: UInt16) -> Bool { c == 32 || c == 9 || c == 10 || c == 13 }
        while i < u.count && ws(u[i]) { i += 1 }
        guard i < u.count && u[i] == 58 else { return "" }                      // ':'
        i += 1
        while i < u.count && ws(u[i]) { i += 1 }
        guard i < u.count && u[i] == 34 else { return "" }                      // '"'
        var out: [UInt16] = []
        i += 1
        while i < u.count && out.count < 1000 {
            let c = u[i]
            if c == 34 { break }
            if c != 92 || i + 1 >= u.count { out.append(c); i += 1; continue }
            i += 1
            let n = u[i]
            if n == 110 { out.append(10) }                                     // \n
            else if n == 116 { out.append(9) }                                 // \t
            else if n == 114 { }                                               // \r
            else if n == 117 && i + 4 < u.count, let code = UInt16(String(decoding: u[(i + 1)...(i + 4)], as: UTF16.self), radix: 16) { out.append(code); i += 4 }
            else { out.append(n) }
            i += 1
        }
        return String(decoding: out, as: UTF16.self)
    }

    static func projectName(_ cwd: String) -> String {
        var t = cwd
        while let l = t.last, l == "/" || l == "\\" { t.removeLast() }
        let name = t.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map { String($0) } ?? ""
        return String(name.prefix(24))
    }

    // A movie's flavour from a window title (players show the file name, YouTube the film's name). "" = no idea.
    static func genre(_ title: String?) -> String {
        let t = (title ?? "").lowercased()
        if any(t, "horror", "conjuring", "insidious", "scream", "annabelle", "exorcis", "haunt", "ghost", "paranormal", "zombie", "sinister", "evil dead", "nightmare", "the nun", "poltergeist", "possess", "bhoot") { return "horror" }
        if any(t, "romance", "romantic", "love", "wedding", "notebook", "titanic", "valentine", "kiss", "pyaar", "ishq", "mohabbat", "rom-com") { return "romance" }
        if any(t, "comedy", "funny", "hangover", "stand-up", "standup", "sitcom", "bloopers", "laugh", "hilarious", "mr bean", "mr. bean") { return "comedy" }
        if any(t, "action", "fast & furious", "fast and furious", "avengers", "john wick", "mission impossible", "batman", "superman", "spider-man", "spiderman", "mad max", "transformers", "terminator", "rambo", "james bond", "kgf", "pushpa", "thriller", "heist", "marvel", "gladiator") { return "action" }
        if any(t, "animation", "animated", "pixar", "frozen", "toy story", "minions", "shrek", "kung fu panda", "cartoon", "anime", "naruto", "ghibli", "doraemon", "moana", "encanto", "bluey", "tom and jerry") { return "anim" }
        return ""
    }

    static let browserNames = ["safari", "google chrome", "chrome", "firefox", "microsoft edge", "brave browser", "arc", "opera", "vivaldi", "chromium", "zen", "zen browser", "orion", "librewolf", "google chrome canary"]

    // What the window in front is, from its app name, title and URL. "" = no idea.
    static func activity(_ proc0: String?, _ title: String?, _ url: String?) -> String {
        let proc = (proc0 ?? "").lowercased(), t = (title ?? "").lowercased(), u = (url ?? "").lowercased(), hay = u + " | " + t
        let teams = proc == "microsoft teams" || proc == "microsoft teams (work or school)" || proc == "teams"
        if any(u, "meet.google.com", "zoom.us/", "teams.microsoft.com/l/meetup") || proc == "zoom.us" || proc == "facetime" || proc == "webex"
            || (teams && any(t, "meeting", "call")) { return "meeting" }
        if any(hay, "music.youtube.com", "open.spotify.com", "jiosaavn", "gaana.com") { return "music" }
        if any(u, "github.com", "gitlab.com", "bitbucket.org") { return "github" }
        if any(u, "stackoverflow.com", "stackexchange.com") { return "stackoverflow" }
        if any(u, "chatgpt.com", "claude.ai", "gemini.google.com", "copilot.microsoft.com", "perplexity.ai") { return "ai" }
        if any(u, "leetcode.com", "coursera.org", "udemy.com", "khanacademy.org", "geeksforgeeks.org", "w3schools.com", "wikipedia.org", "nptel", "hackerrank.com", "developer.mozilla.org", "developer.apple.com") { return "learn" }
        if any(u, "mail.google.com", "outlook.live.com", "outlook.office.com", "icloud.com/mail") { return "mail" }
        if any(u, "docs.google.com/document", "notion.so") { return "docs" }
        if any(u, "docs.google.com/spreadsheets") { return "sheets" }
        if any(u, "docs.google.com/presentation", "canva.com") { return "slides" }
        if any(u, "web.whatsapp.com", "discord.com", "slack.com", "web.telegram.org", "messenger.com") { return "chat" }
        if any(u, "amazon.", "flipkart.com", "myntra.com", "meesho.com", "ebay.", "aliexpress") { return "shop" }
        if any(u, "linkedin.com", "x.com/", "twitter.com", "instagram.com", "facebook.com", "reddit.com", "threads.net") { return "social" }
        if any(u, "youtube.com", "netflix.com", "primevideo.com", "hotstar.com", "twitch.tv", "crunchyroll", "tv.apple.com") { return "video" }
        if teams { return "chat" }
        switch proc {
        case "code", "visual studio code", "cursor", "windsurf", "xcode", "intellij idea", "intellij idea ce", "pycharm", "pycharm ce", "webstorm",
             "android studio", "sublime text", "zed", "nova", "bbedit", "antigravity", "clion", "rider", "goland", "arduino ide": return "code"
        case "terminal", "iterm2", "iterm", "warp", "ghostty", "alacritty", "kitty", "wezterm", "hyper", "tabby": return "terminal"
        case "pages", "microsoft word", "textedit", "notes", "obsidian", "notion", "bear", "preview", "craft", "ulysses", "microsoft onenote": return "docs"
        case "numbers", "microsoft excel": return "sheets"
        case "keynote", "microsoft powerpoint": return "slides"
        case "mail", "microsoft outlook", "spark", "spark desktop", "thunderbird", "airmail": return "mail"
        case "messages", "whatsapp", "discord", "slack", "telegram", "signal": return "chat"
        case "spotify", "music": return "music"
        case "tv", "quicktime player", "vlc", "iina", "infuse", "obs": return "video"
        case "figma", "sketch", "blender", "gimp", "krita", "inkscape", "affinity designer", "affinity photo", "pixelmator pro": return "design"
        case "steam", "minecraft", "epic games launcher", "battle.net": return "game"
        case "finder": return t.isEmpty ? "" : "files"
        default: break
        }
        if proc.contains("photoshop") || proc.contains("illustrator") { return "design" }
        return browserNames.contains(proc) ? "browse" : ""
    }

    static func any(_ hay: String, _ needles: String...) -> Bool { needles.contains(where: { hay.contains($0) }) }

    // One short reaction for an activity. Knows when you said you were working or studying.
    static func comment(_ act: String, _ doing: String) -> String? {
        if (doing == "work" || doing == "study") && (act == "video" || act == "social" || act == "shop" || act == "game") {
            return doing == "study" ? "Weren't you studying?" : "Weren't you working?"
        }
        let lines: [String]
        switch act {
        case "code": lines = ["Writing code? Ship it!", "Need a rubber duck? I'm here.", "Remember to commit!"]
        case "terminal": lines = ["Hacker mode: on.", "Careful with rm -rf!", "May your tests be green."]
        case "github": lines = ["GitHub! Star something nice.", "Reviewing a pull request?"]
        case "stackoverflow": lines = ["Stack Overflow to the rescue!", "Copy, paste... understand?"]
        case "ai": lines = ["Asking an AI? I have opinions too.", "Say please to the robot."]
        case "learn": lines = ["Learning something new? Proud of you.", "Study mode!"]
        case "docs": lines = ["Writing something great?", "The words are flowing!"]
        case "sheets": lines = ["Spreadsheet wizardry!", "=SUM(snacks)"]
        case "slides": lines = ["Slide deck time. Fancy!", "Big presentation coming?"]
        case "mail": lines = ["Inbox zero today?", "Emails, emails..."]
        case "chat": lines = ["Say hi from me!", "Chatting? Don't forget your task."]
        case "video": lines = ["Ooh, what are we watching?", "Popcorn time?"]
        case "music": lines = ["Nice tunes!", "Turn it up!"]
        case "design": lines = ["Ooh, pretty pixels!", "Make it pop!"]
        case "game": lines = ["Game time! Have fun.", "Go win one for me!"]
        case "shop": lines = ["Adding to cart again?", "Need it, or want it?"]
        case "social": lines = ["Just a quick peek, right?", "Scrolling? Stay focused!"]
        case "files": lines = ["Tidying up files?", "Looking for something?"]
        case "browse": lines = ["What are you reading?", "Interesting page?"]
        default: return nil
        }
        return pick(lines)
    }
}

// ---------------------------------------------------------------------- the agent CLIs we can hook into
// key | display name | config file under the home folder | file shape | events as "Name=state;..."
// The state is baked into each hook command, so the pet never has to map event names at runtime.
enum AgentDefs {
    static let all: [[String]] = [
        ["claude", "Claude Code", ".claude/settings.json", "nested", "UserPromptSubmit=working;Notification=waiting;Stop=done;SessionEnd=end"],
        ["codex", "Codex", ".codex/hooks.json", "nested", "UserPromptSubmit=working;PermissionRequest=waiting;Stop=done"],
        ["gemini", "Gemini CLI", ".gemini/settings.json", "nested", "BeforeAgent=working;Notification=waiting;AfterAgent=done;SessionEnd=end"],
        ["antigravity", "Antigravity", ".gemini/config/hooks.json", "antigravity", "PreInvocation=working;Stop=done"],
        ["cursor", "Cursor", ".cursor/hooks.json", "cursor", "beforeSubmitPrompt=working;stop=done;sessionEnd=end"],
        ["windsurf", "Windsurf", ".codeium/windsurf/hooks.json", "windsurf", "pre_user_prompt=working;post_cascade_response=done"],
    ]

    static func path(_ s: [String]) -> String { NSHomeDirectory() + "/" + s[2] }
    static func find(_ key: String) -> [String]? { all.first(where: { $0[0] == key }) }
    static func installed(_ s: [String]) -> Bool {
        var dir: ObjCBool = false
        let top = NSHomeDirectory() + "/" + (s[2].split(separator: "/").first.map { String($0) } ?? "")
        return FileManager.default.fileExists(atPath: top, isDirectory: &dir) && dir.boolValue
    }
    static func connected(_ s: [String]) -> Bool { (try? String(contentsOfFile: path(s), encoding: .utf8))?.contains("--agent-hook") ?? false }
}

// Adds or removes our entries in an agent's config. Entries are ours when their command mentions "--agent-hook"
// (or the older "--claude-hook"), so other tools' hooks are never touched, and the file is backed up first.
enum AgentSetup {
    struct Failure: LocalizedError { let text: String; var errorDescription: String? { text } }

    static func apply(key: String, path: String, style: String, events: String, connect: Bool, exe: String) throws {
        let fm = FileManager.default
        let fresh = !fm.fileExists(atPath: path)
        let name = (path as NSString).lastPathComponent
        var root: [String: Any] = [:]
        if !fresh {
            let data = try Data(contentsOf: URL(fileURLWithPath: path))
            if !trimmed(String(decoding: data, as: UTF8.self)).isEmpty {
                guard let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw Failure(text: name + " is not a JSON object") }
                root = obj
            }
        }
        let container = style == "antigravity" ? "pixelpet" : "hooks"
        var map: [String: Any] = [:]
        if let got = root[container] {
            guard let m = got as? [String: Any] else { throw Failure(text: "\"" + container + "\" is not an object") }
            map = m
        }
        for pair in events.split(separator: ";") {
            guard let eq = pair.firstIndex(of: "=") else { continue }
            let ev = String(pair[..<eq]), state = String(pair[pair.index(after: eq)...])
            let cmd = "\"" + exe + "\" --agent-hook " + key + " " + state
            var kept: [Any] = []
            if let cur = map[ev] as? [Any] { for e in cur where !isOurs(e) { kept.append(e) } }
            if connect { kept.append(entry(style, ev, cmd)) }
            if kept.isEmpty { map.removeValue(forKey: ev) } else { map[ev] = kept }
        }
        if map.isEmpty { root.removeValue(forKey: container) } else { root[container] = map }
        if connect && style == "cursor" && fresh { root["version"] = 1 }    // only when we create the file, so disconnect restores it
        try fm.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        if !fresh {
            let backup = path + ".pixelpet-backup"
            try? fm.removeItem(atPath: backup)
            try fm.copyItem(atPath: path, toPath: backup)
        }
        let out = try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .withoutEscapingSlashes])
        try (String(decoding: out, as: UTF8.self) + "\n").write(toFile: path, atomically: true, encoding: .utf8)
    }

    // Claude/Codex/Gemini wrap handlers in a group; Antigravity does that only for tool events; Cursor and
    // Windsurf list handlers directly.
    static func entry(_ style: String, _ ev: String, _ cmd: String) -> [String: Any] {
        var handler: [String: Any] = ["command": cmd]
        if style != "windsurf" { handler["type"] = "command" }
        if style == "cursor" || style == "windsurf" || (style == "antigravity" && ev != "PreToolUse" && ev != "PostToolUse") { return handler }
        var group: [String: Any] = ["hooks": [handler]]
        if style == "antigravity" { group["matcher"] = "*" }
        return group
    }

    static func isOurs(_ entry: Any) -> Bool {
        guard let d = entry as? [String: Any] else { return false }
        if let c = d["command"] as? String, mine(c) { return true }
        guard let inner = d["hooks"] as? [Any] else { return false }
        for x in inner { if let h = x as? [String: Any], let c = h["command"] as? String, mine(c) { return true } }
        return false
    }

    static func mine(_ command: String) -> Bool { command.contains("--agent-hook") || command.contains("--claude-hook") }
}

// ---------------------------------------------------------------------- self-check: PixelPetFocus --selftest ; exit code 0 = pass
func runSelfTest() -> Int {
    var fails = 0, checkNo = 0
    func ok(_ b: Bool, _ line: Int = #line) { checkNo += 1; if !b { fails += 1; print("::error title=selftest::check #\(checkNo) failed (Core.swift line \(line))") } }

    let c = parseCfg(defaultRules.components(separatedBy: "\n"))
    ok(c.countdown == 3 && c.snooze == 5 && !c.nag && c.rules.count == 9 && c.never.count == 4)
    ok(matchRule(c, "instagram.com/reels/abc/", "(8) Instagram", "google chrome")?.label == "Instagram Reels")
    ok(matchRule(c, "", "Crunchyroll - Season 3", "safari")?.label == "Streaming")
    ok(matchRule(c, "youtube.com/shorts/x", "Some Short - YouTube", "google chrome")?.label == "YouTube Shorts")
    ok(matchRule(c, "youtube.com/watch?v=a", "A Talk - YouTube", "google chrome")?.grace == 240)
    ok(matchRule(c, "github.com/foo", "foo", "safari") == nil)
    ok(matchRule(c, "tiktok.com", "standup - google meet", "google chrome") == nil)
    ok(stripBadge("(8) Instagram") == stripBadge("(9) Instagram") && stripBadge("(Draft) Report") == "(Draft) Report")
    ok(cost(1) == 50 && cost(2) == 65 && cost(3) == 80)
    ok(classify("google chrome lecture 5: thermodynamics - youtube") == 2 && classify("zoom.us") == 2)
    ok(classify("spotify daft punk") == 1 && classify("google chrome classic rock playlist - youtube") == 1)
    ok(classify("google chrome some random video") == 0)
    ok(!isHeadphones("macbook pro speakers") && isHeadphones("samuvel's airpods pro") && isHeadphones("wh-1000xm4"))

    let n0 = mk(2026, 9, 17, 18, 0)                                           // a Thursday, 18:00
    var r = TextTools.parseWhen("call mom in 20 min", n0)
    ok(r != nil && r!.0 == n0.addingTimeInterval(1200) && r!.1 == "call mom")
    r = TextTools.parseWhen("remind me to pay rent at 5pm", n0)
    ok(r != nil && r!.0 == mk(2026, 9, 18, 17, 0) && r!.1 == "pay rent")
    r = TextTools.parseWhen("standup tomorrow", n0)
    ok(r != nil && r!.0 == mk(2026, 9, 18, 9, 0) && r!.1 == "standup")
    r = TextTools.parseWhen("gym tomorrow at 7:30am", n0)
    ok(r != nil && r!.0 == mk(2026, 9, 18, 7, 30) && r!.1 == "gym")
    r = TextTools.parseWhen("stretch in 1.5h", n0)
    ok(r != nil && r!.0 == n0.addingTimeInterval(5400) && r!.1 == "stretch")
    r = TextTools.parseWhen("Deploy 20:15", n0)
    ok(r != nil && r!.0 == mk(2026, 9, 17, 20, 15) && r!.1 == "Deploy")
    ok(TextTools.parseWhen("meet at 5", n0) == nil && TextTools.parseWhen("the cat sat in the hat", n0) == nil)
    ok(TextTools.looksSecret("Hunter2!xYz9") && TextTools.looksSecret("482913") && TextTools.looksSecret("ghp_abcdef123"))
    ok(!TextTools.looksSecret("call mom in 20 min") && !TextTools.looksSecret("https://example.com/a?b=1") && !TextTools.looksSecret("12*7.5"))
    ok(TextTools.calc("12*7.5") == 90 && TextTools.calc("(2+3)^2 - 1") == 24)
    ok(TextTools.calc("2026-09-18") == nil && TextTools.calc("18/09/2026") == nil && TextTools.calc("hello 2+2") == nil)
    var cl = TextTools.cleanLink("https://x.com/p?utm_source=a&id=3&fbclid=z#top")
    ok(cl.0 == "https://x.com/p?id=3#top" && cl.1 == 2)
    cl = TextTools.cleanLink("https://youtu.be/abc?si=XYZ")
    ok(cl.0 == "https://youtu.be/abc" && cl.1 == 1)
    ok(TextTools.tidy("  a  line\nbroken\n\nnext   para ") == "a line broken\n\nnext para")
    let r1 = parseReminder("every 45m | Drink water", n0)
    ok(r1 != nil && r1!.kind == "every" && r1!.next == n0.addingTimeInterval(2700))
    let r2 = parseReminder("weekdays 09:25 | Standup", mk(2026, 9, 18, 10, 0))  // Friday after standup
    ok(r2 != nil && r2!.next == mk(2026, 9, 21, 9, 25))                         // skips the weekend
    let r3 = parseReminder("2026-09-18 17:40 | call mom", n0)
    ok(r3 != nil && r3!.kind == "once" && r3!.next == mk(2026, 9, 18, 17, 40))
    ok(parseReminder("sometime | nope", n0) == nil && parseReminder("every 20m 09:00-18:00 | eyes", n0)?.from == 540)
    ok(TextTools.activity("Code", "main.py - PixelPet", "") == "code" && TextTools.activity("Terminal", "zsh", "") == "terminal")
    ok(TextTools.activity("Google Chrome", "A Talk - YouTube", "youtube.com/watch?v=a") == "video")
    ok(TextTools.activity("Safari", "YouTube Music", "music.youtube.com/watch?v=a") == "music")
    ok(TextTools.activity("Safari", "Pull request #3", "github.com/a/b/pull/3") == "github")
    ok(TextTools.activity("Google Chrome", "Meet - abc", "meet.google.com/abc-defg") == "meeting" && TextTools.activity("zoom.us", "Zoom Meeting", "") == "meeting")
    ok(TextTools.activity("Finder", "Downloads", "") == "files" && TextTools.activity("Finder", "", "") == "")
    ok(TextTools.activity("Safari", "Some page", "example.org/x") == "browse" && TextTools.activity("SomeApp", "Thing", "") == "")
    ok(TextTools.comment("video", "work") == "Weren't you working?" && TextTools.comment("", "") == nil && TextTools.comment("code", "") != nil)
    ok(TextTools.genre("The Conjuring (2013) - IINA") == "horror" && TextTools.genre("Titanic 1997 1080p.mkv") == "romance" && TextTools.genre("Toy Story 3 - Netflix") == "anim")
    ok(TextTools.genre("Netflix") == "" && TextTools.genre(nil) == "" && TextTools.genre("Avengers Endgame - YouTube") == "action")
    ok(TextTools.jsonField("{\"session_id\":\"abc\", \"hook_event_name\" : \"Stop\"}", "hook_event_name") == "Stop")
    ok(TextTools.jsonField("{\"cwd\":\"C:\\\\Users\\\\me\\\\proj\"}", "cwd") == "C:\\Users\\me\\proj")
    ok(TextTools.jsonField("{\"prompt\":\"say \\\"cwd\\\" here\",\"cwd\":\"/Users/me/x\"}", "cwd") == "/Users/me/x" && TextTools.jsonField("{}", "cwd") == "")
    ok(TextTools.jsonField("{\"message\":\"caf\\u00e9\"}", "message") == "caf\u{e9}")
    ok(TextTools.projectName("/Users/me/pixelpet/") == "pixelpet" && TextTools.projectName("C:\\Users\\me\\api") == "api" && TextTools.projectName("") == "")
    ok(rank(1) == "Hatchling" && rank(5) == "Companion" && rank(19) == "Scout" && rank(35) == "Legend" && nextRank(35) == 0)
    ok(achNames.count == achHow.count && achNames.count <= 31)
    ok(span(45 * 60) == "45 min" && span(90 * 60) == "1h 30m" && span(7200) == "2h" && c.water == 45 && c.screenTime == 60 && c.stretch == 90)
    let dd = decodeDay(encodeDay(mk(2026, 9, 18), [3, 5, 2, 1, 0, 4, 1]))
    ok(dd != nil && dd!.0 == mk(2026, 9, 18) && dd!.1[0] == 3 && dd!.1[5] == 4 && dd!.1[6] == 1)
    ok(decodeDay("junk") == nil)
    ok(daySummary(mk(2026, 9, 18), [3, 0, 2, 0, 0, 1, 1]) == "Sep 18: closed 3 doomscroll tabs, 2 focus sessions, agents finished 1 task. Stayed up late.")
    ok(daySummary(mk(2026, 9, 18), [Int](repeating: 0, count: 7)) == nil)
    var hk = Keys.parse("ctrl+alt+shift+c")
    ok(hk != nil && hk!.mods == 0x1A00 && hk!.code == 0x08 && hk!.text == "\u{2303}\u{2325}\u{21E7}C")
    hk = Keys.parse("Cmd+Shift+F10")
    ok(hk != nil && hk!.mods == 0x0300 && hk!.code == 0x6D && hk!.text == "\u{21E7}\u{2318}F10")
    ok(Keys.parse("win+space")?.code == 0x31 && Keys.parse("r") == nil && Keys.parse("ctrl+f25") == nil)
    ok(Keys.parse("ctrl+a+b") == nil && Keys.parse("off") == nil && Keys.parse(nil) == nil)
    ok(AgentDefs.all.count == 6 && AgentDefs.find("antigravity")?[3] == "antigravity" && AgentDefs.find("nope") == nil)
    for sp in AgentDefs.all {
        ok(sp.count == 5 && !sp[0].isEmpty && !sp[1].isEmpty && sp[2].contains(".") && sp[4].contains("=done"))
        for pair in sp[4].split(separator: ";") {
            let st = pair.split(separator: "=").last.map { String($0) } ?? ""
            ok(st == "working" || st == "waiting" || st == "done" || st == "end")
        }
    }
    var p = swingPos(0, 100, 50, 0, 200, 100, 0)
    ok(p.0 == 0 && p.1 == 100)
    p = swingPos(0, 100, 50, 0, 200, 100, 1)
    ok(p.0 == 200 && p.1 == 100)
    p = swingPos(0, 100, 50, 0, 200, 100, 0.5)
    ok(abs(p.0 - 75) < 0.001 && p.1 < 100)

    // Hook installer round trip, per file shape, on throwaway copies: connecting twice leaves one entry per event,
    // other keys survive, and disconnecting takes ours back out.
    let tmp = NSTemporaryDirectory() + "pixelpet-selftest-\(getpid())"
    try? FileManager.default.createDirectory(atPath: tmp, withIntermediateDirectories: true)
    for sp in AgentDefs.all {
        let path = tmp + "/" + sp[0] + ".json"
        let other: [String: Any] = sp[3] == "antigravity" ? ["theme": "dark"] : ["theme": "dark", "hooks": ["Other": [["command": "echo hi"]]]]
        try? JSONSerialization.data(withJSONObject: other).write(to: URL(fileURLWithPath: path))
        do {
            try AgentSetup.apply(key: sp[0], path: path, style: sp[3], events: sp[4], connect: true, exe: "/Applications/PixelPet Focus.app/Contents/MacOS/PixelPetFocus")
            try AgentSetup.apply(key: sp[0], path: path, style: sp[3], events: sp[4], connect: true, exe: "/Applications/PixelPet Focus.app/Contents/MacOS/PixelPetFocus")
            let on = try String(contentsOfFile: path, encoding: .utf8)
            let events = sp[4].split(separator: ";").count
            ok(on.components(separatedBy: "--agent-hook").count - 1 == events && on.contains("\"theme\"") && on.contains("PixelPet Focus.app"))
            try AgentSetup.apply(key: sp[0], path: path, style: sp[3], events: sp[4], connect: false, exe: "x")
            let off = try String(contentsOfFile: path, encoding: .utf8)
            ok(!off.contains("--agent-hook") && off.contains("\"theme\"") && (sp[3] == "antigravity" || off.contains("echo hi")))
            ok(FileManager.default.fileExists(atPath: path + ".pixelpet-backup"))
        } catch { ok(false) }
    }
    try? FileManager.default.removeItem(atPath: tmp)

    print(fails == 0 ? "::notice title=Mac self-test::all \(checkNo) checks passed" : "::error title=Mac self-test::\(fails) of \(checkNo) checks failed")
    return fails
}
