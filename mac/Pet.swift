// PixelPet Focus for macOS: the pet itself. One borderless panel above the Dock (clicks pass through its
// transparent pixels), drawn with plain rectangle fills, plus a second click-through panel for the rope and web.
// Same behaviour as the Windows build (src/PixelPetFocus.cs). Where macOS guards something behind a permission
// (window titles, moving other apps' windows, key state), the pet asks through its Permissions menu and simply
// skips that reaction until it's allowed.
import AppKit
import Carbon.HIToolbox
import QuartzCore
import CoreAudio
import IOKit.ps
import UserNotifications

let agentNote = Notification.Name("io.github.samuveljohnson1416.pixelpet.agent")
let openNote = Notification.Name("io.github.samuveljohnson1416.pixelpet.open")

struct R {
    var l = 0.0, t = 0.0, r = 0.0, b = 0.0
    var w: Double { r - l }
    var h: Double { b - t }
    func has(_ x: Double, _ y: Double) -> Bool { x >= l && x < r && y >= t && y < b }
}
struct Pt { var x = 0.0, y = 0.0 }
struct Win { let id: CGWindowID; let pid: pid_t; let f: R }

final class Part {
    var x: Double, y: Double, vy: Double, life: Double, text: String?
    init(_ x: Double, _ y: Double, _ vy: Double, _ life: Double, _ text: String?) { self.x = x; self.y = y; self.vy = vy; self.life = life; self.text = text }
}

final class AgentSess { var state = "", project = "", who = "Agent"; var since = Date(), seen = Date(), lastPing = Date.distantPast }

struct Bust { var pid: pid_t; var win: CGWindowID; var title: String; var bundle: String; var label: String; var scold = ""; var centerX: Double? }

func readLines(_ p: String) -> [String] { (try? String(contentsOfFile: p, encoding: .utf8))?.components(separatedBy: "\n") ?? [] }
func mtime(_ p: String) -> Date { ((try? FileManager.default.attributesOfItem(atPath: p))?[.modificationDate] as? Date) ?? .distantPast }
func appendLine(_ path: String, _ line: String) {
    guard let data = (line + "\n").data(using: .utf8) else { return }
    if let h = FileHandle(forWritingAtPath: path) { h.seekToEndOfFile(); h.write(data); h.closeFile() }
    else { try? data.write(to: URL(fileURLWithPath: path)) }
}
func fourCC(_ s: String) -> UInt32 { s.utf8.reduce(0) { ($0 << 8) | UInt32($1) } }

// Run by an agent CLI on its events as `PixelPetFocus --agent-hook <agent> <state>`; the state is baked into the
// command at install time. This short-lived process forwards it to the running pet and exits.
func forwardHook(_ key: String, _ state0: String) {
    final class Box { var s = "" }
    let box = Box(), done = DispatchSemaphore(value: 0)
    Thread.detachNewThread {
        let d = FileHandle.standardInput.readDataToEndOfFile()
        box.s = String(decoding: d.prefix(200_000), as: UTF8.self)
        done.signal()
    }
    _ = done.wait(timeout: .now() + 1.5)                                       // never hold the agent up
    let json = box.s
    var state = state0
    if state.isEmpty { state = TextTools.jsonField(json, "hook_event_name") }  // old --claude-hook installs send the event name
    if state.isEmpty { return }
    let sid = firstNonEmpty(TextTools.jsonField(json, "session_id"), TextTools.jsonField(json, "sessionId"),
                            TextTools.jsonField(json, "conversation_id"), TextTools.jsonField(json, "conversationId"))
    let cwd = firstNonEmpty(TextTools.jsonField(json, "cwd"), TextTools.jsonField(json, "workspace_path"), TextTools.jsonField(json, "workspaceRoot"))
    let fields = [state, sid, cwd, TextTools.jsonField(json, "message"), AgentDefs.find(key)?[1] ?? key]
    let data = fields.map { $0.replacingOccurrences(of: "\n", with: " ") }.joined(separator: "\n")
    DistributedNotificationCenter.default().postNotificationName(agentNote, object: data, userInfo: nil, deliverImmediately: true)
}

// Carbon calls this for our registered hot keys; it hops to the main actor where the pet lives.
func installHotkeyHandler() {
    var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    InstallEventHandler(GetApplicationEventTarget(), { _, event, _ -> OSStatus in
        var hk = EventHotKeyID()
        GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
                          MemoryLayout<EventHotKeyID>.size, nil, &hk)
        let id = Int(hk.id)
        Task { @MainActor in Pet.shared?.hotkey(id) }
        return noErr
    }, 1, &spec, nil, nil)
}

final class PetPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }   // may hang off the top edge
}

final class PetView: NSView {
    weak var pet: Pet?
    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func draw(_ dirtyRect: NSRect) { pet?.render() }
    override func mouseDown(with event: NSEvent) { pet?.mouseDown() }
    override func mouseDragged(with event: NSEvent) { pet?.mouseDragged() }
    override func mouseUp(with event: NSEvent) { pet?.leftUp(event) }
    override func rightMouseDown(with event: NSEvent) { pet?.rightClick(event) }
}

// The rope and the web: one click-through panel over the main screen that never resizes and holds no bitmap.
// Each frame only swaps the path of two shape layers (resizing a drawn window every frame made macOS keep
// megabytes of old buffers).
final class RopeView: NSView {
    let rope = CAShapeLayer(), star = CAShapeLayer()
    override init(frame: NSRect) {
        super.init(frame: frame)
        let root = CALayer()
        root.isGeometryFlipped = true                                            // y down, like the pet's coordinates
        layer = root; wantsLayer = true                                          // layer-hosting: AppKit draws nothing here
        star.lineWidth = 2
        root.addSublayer(rope); root.addSublayer(star)
    }
    required init?(coder: NSCoder) { nil }
    func show(_ pts: [CGPoint], _ starPts: [CGPoint]?, _ scale: CGFloat) {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        for l in [rope, star] { l.frame = bounds; l.contentsScale = scale }
        rope.path = shape(pts)
        star.path = starPts.map { shape($0) }
        CATransaction.commit()
    }
    func shape(_ pts: [CGPoint]) -> CGPath {
        let p = CGMutablePath()
        if pts.count > 2 { p.addLines(between: pts); p.closeSubpath() }
        return p
    }
}

@MainActor
final class Pet: NSObject, NSApplicationDelegate, NSMenuDelegate {
    static var shared: Pet?

    let SIT = 0, WALK = 1, SLEEP = 2, AIR = 3, HELD = 4, RUN = 5, GLARE = 6, SWIPE = 7, TYPE = 8, CLIMB = 9, HANG = 10, SWING = 11, PUSH = 12
    // colours written as in the Windows build (0xBBGGRR), so both draw the same pet
    let CORAL = 0x5777D9, ANGRY = 0x4058E8, EYE = 0x141414, DARK = 0x282828, WHITE = 0xFFFFFF, PINK = 0x875FFF
    let ROPE = 0x325A8C, STAR = 0x30D8FF, GREEN = 0x50C850, SIGN = 0x9CF0FF
    let LID = 0x4E4646, BASE = 0x322D2D, LOGO = 0xB48C64, GLOW = 0xFFDC8C
    let CUP = 0xC8D25A, CUPDARK = 0x8C9637, PAPER = 0xF0F5F5, INK = 0xB4AAA0, CAP = 0xC86E3C, HEADBAND = 0x3C3CDC

    let scolds = ["Shorts. Again.", "That's enough of that.", "Closing this one.", "You said you'd stop.", "Nope."]
    let pushLines = ["There. Much better.", "Rearranging the furniture.", "Tidy desk, tidy mind.", "Hnngh... done!"]
    let codes = ["{ }", "01", ";", "</>", "#", "=>", "()"]
    let landingLines = ["Ta-da!", "Stuck the landing!", "Whee, made it!", "Web-slinging pro!", "Boop!"]
    let clipFallbacks = ["ctrl+alt+shift+c", "ctrl+alt+cmd+c", "ctrl+shift+f10"]
    let swingFallbacks = ["ctrl+alt+shift+w", "ctrl+alt+cmd+w", "ctrl+shift+f11"]
    let legs: [Double] = [3, 5, 8, 10]
    // browsers: 1 = Chromium family (AppleScript "active tab"), 2 = Safari ("current tab"), 3 = no scripting (title only)
    let chromium = ["com.google.Chrome", "com.google.Chrome.beta", "com.google.Chrome.canary", "com.brave.Browser", "com.microsoft.edgemac",
                    "com.vivaldi.Vivaldi", "com.operasoftware.Opera", "org.chromium.Chromium", "company.thebrowser.Browser"]
    let safari = ["com.apple.Safari", "com.apple.SafariTechnologyPreview"]
    let scriptless = ["org.mozilla.firefox", "org.mozilla.firefoxdeveloperedition", "org.mozilla.nightly", "app.zen-browser.zen", "org.mozilla.librewolf", "net.waterfox.waterfox"]

    var cfg = Cfg()
    var patrol = true
    var snoozeUntil = Date.distantPast, cooldownUntil = Date.distantPast
    var pendingBust: Bust?
    var watchRemaining = -1.0, watchX = 0.0
    var dir = "", rulesPath = "", progressPath = "", remPath = "", memPath = ""

    var panel: PetPanel!, view: PetView!, ropePanel: PetPanel?, ropeView: RopeView?
    var statusItem: NSStatusItem?
    var W = 260.0, H = 160.0, U = 5.0, S = 1.0, primaryH = 900.0
    var wa = R()
    var x = 0.0, y = 0.0, vx = 0.0, vy = 0.0, walkTarget = 0.0, facing = 1.0
    var state = 0, stateT = 0.0, stateDur = 2.0, animT = 0.0
    var perch: CGWindowID = 0, ignorePerch: CGWindowID = 0, perchRect = R()
    var joyT = 0.0, angryT = 0.0, sqT = 0.0, sqK = 0.0, blinkT = 3.0, blinkOn = 0.0, zT = 0.0
    var bubble: String?, bubbleT = 0.0
    var lookX = 0.0, lookY = 0.0
    var pressed = false, held = false, hidden = false, downPt = Pt(), grabDx = 0.0, grabDy = 0.0
    var alert: Bust?, glareLeft = 0, glareTick = 0.0, acted = false
    var parts: [Part] = []
    var winLeft = -1e9, winTop = -1e9, lastTick = 0.0
    var level = 1, xp = 0, lastPat = Date.distantPast
    var focusPhase = 0, focusEnd = Date()                                    // 0 off, 1 focus, 2 break
    var lapOpen = 0.0, lastKeyAgo = 99.0, keyBurst = 0.0, codeT = 0.0, lastKeySecs = 1e9
    var ropeT = -1.0, ropeTx = 0.0, ropeTy = 0.0, ropeCool = 0.0, enterDown = false
    var swingSX = 0.0, swingSY = 0.0, swingAX = 0.0, swingAY = 0.0, swingTX = 0.0, swingTY = 0.0, webCool = 0.0, webShown = false
    var menuOpenPt = Pt()
    var headphones = false, audioKind = 0, audioPeak = 0.0, audioDevice = "", audioRunning = false, audioSec = 0, kindCandidate = 0, kindSeen = 0
    var wasPhones = false, lastKind = 0, noteT = 0.0, danceLevel = 0.0
    var chargeT = 0.0, batteryPct = -1, lowWarned = 100, powerKnown = false, onAC = false
    var rems: [Reminder] = [], remStamp = Date.distantPast, sign: Reminder?, signText = "", signT = 0.0, pauseRecurring = false, bubbleSign = false
    var clipText: String?, clipMsg = "", clipWhen = Date(), clipOfferT = 0.0, ownCount = -1, lastCount = -1, clipPoll = 0.0, hintShown = false, clipNoticed = false
    var gcSec = 0, tagCache: String?
    var orient = 0, climbNext = 0, climbTarget = 0.0, climbUp = false, climbAfterWalk = false, hangSleep = false
    var fgPid: pid_t = 0, fgWin: CGWindowID = 0, fgProc = "", fgTitle = "", fgUrl = "", fgBundle = "", fgFrame: R?
    var lastFgPid: pid_t = 0, lastFgWin: CGWindowID = 0
    var curiousT = 90.0, askT = 0.0, curiousLine: String?, lastActivity = "", curiousAsk = false, curiousAt = Date()
    var doing = "", doingUntil = Date()
    var movieGenre = "", movieAvg = 0.0, movieCool = 0.0, movieChat = 15.0, movieLoud = 0.0, movieQuiet = 0.0, scaredT = 0.0
    var detectiveT = 0.0, wizardT = 0.0, paraT = 0.0, eyeingNow = false, agentBusy = false
    var clipKeyText = "", swingKeyText = "", hotRefs: [EventHotKeyRef?] = []
    var snackT = 5.0, snackLeft = 0.0, snackKind = 0
    var agents: [String: AgentSess] = [:], lastAgentXp = Date.distantPast
    var useSec = 0, waterSec = 0, stretchSec = 0, sinceNudge = 9999, lastTold = 0, screenToday = 0, screenDay = Date.distantPast   // break buddy
    var drinkT = 0.0, stretchT = 0.0, clockT = 0.0, grooveT = 0.0, grooving = false, webHops = 0
    var closes = 0, pats = 0, focusDone = 0, remDone = 0, clipActs = 0, claudeDone = 0, streak = 0, bestStreak = 0, ach = 0
    var lastDay = Date.distantPast, nightOwl = false, askedAX = false
    var memTail: [String] = [], today = [Int](repeating: 0, count: 7), todayDate = Date.distantPast, met = Date.distantPast, hist: [String] = []
    var traitFocused = false, traitLoved = false, traitOwl = false, traitGuard = false, traitBuddy = false, traitsReady = false
    var pushTarget: CGWindowID = 0, pushAX: AXUIElement?, pushAt = 0.0, pushLeft = 0.0, pushAcc = 0.0, pushed = 0.0, pushDir = 0.0, pushRect = R()
    var lastPush = Date.distantPast, diskWarned = Date.distantPast
    var secAcc = 0.0
    var matchId = "", pendingKey = "", matchSince = Date(), rulesStamp = Date.distantPast
    var urlFor = "", urlCache = "", urlReadAt = Date.distantPast, scripts: [String: NSAppleScript] = [:]
    var winCache: [Win]?
    var colors: [Int: NSColor] = [:]
    var probeSecs = 0.0, probeElapsed = 0, marks: [String] = [], drawnOnce = false, frameMarked = false
    var cx = 0.0, by = 0.0, sx = 1.0, sy = 1.0
    let bubbleFont = NSFont.boldSystemFont(ofSize: 12), smallFont = NSFont.boldSystemFont(ofSize: 12)

    var movieOn: Bool { doing == "movie" && Date() < doingUntil }

    // ------------------------------------------------------------------ start-up
    func applicationDidFinishLaunching(_ note: Notification) {
        mark("AppKit up")
        let fm = FileManager.default
        dir = ProcessInfo.processInfo.environment["PIXELPET_DIR"] ?? NSHomeDirectory() + "/Library/Application Support/PixelPet Focus"
        try? fm.createDirectory(atPath: dir, withIntermediateDirectories: true)
        rulesPath = dir + "/rules.txt"; progressPath = dir + "/progress.txt"; remPath = dir + "/reminders.txt"; memPath = dir + "/memories.txt"
        if !fm.fileExists(atPath: rulesPath) { try? defaultRules.write(toFile: rulesPath, atomically: true, encoding: .utf8) }
        cfg = parseCfg(readLines(rulesPath)); rulesStamp = mtime(rulesPath)
        loadProgress()
        initMemories()
        mark("files")

        U = max(2, (5 * S * cfg.size).rounded())
        W = max(260 * S, 22 * U); H = 12 * U + 100 * S                          // room for a 3-line reminder sign
        wa = workArea()
        x = (wa.l + wa.r) / 2; y = wa.b

        panel = makePanel(W, H)
        view = PetView(frame: NSRect(x: 0, y: 0, width: W, height: H)); view.pet = self
        panel.contentView = view
        mark("windows")


        loadReminders()
        lastCount = NSPasteboard.general.changeCount
        if cfg.hotkey {
            installHotkeyHandler()
            clipKeyText = takeHotkey(1, cfg.clipKey, "clipkey", clipFallbacks)
            if cfg.webTravel { swingKeyText = takeHotkey(2, cfg.swingKey, "swingkey", swingFallbacks) }
        }
        let dnc = DistributedNotificationCenter.default()
        dnc.addObserver(self, selector: #selector(onAgentNote(_:)), name: agentNote, object: nil, suspensionBehavior: .deliverImmediately)
        dnc.addObserver(self, selector: #selector(onOpenNote(_:)), name: openNote, object: nil, suspensionBehavior: .deliverImmediately)
        lastTick = ProcessInfo.processInfo.systemUptime
        let t = Timer(timeInterval: 1.0 / 30, target: self, selector: #selector(tickTimer), userInfo: nil, repeats: true)
        RunLoop.main.add(t, forMode: .common)                                   // keeps animating while a menu is open
        mark("hot keys + timers")
        panel.orderFrontRegardless()
        if bubbleT <= 0 { chirp("Hi! I'll keep you focused.", 3) }
        joyT = 2
        if probeSecs == 0 && !askedAX && !AXIsProcessTrusted() {                 // once, on the first run: window titles need Accessibility
            askedAX = true; saveProgress()
            say("Hi! Allow me in Accessibility so I can see window titles.", 6)
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary) }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { reopen(); return false }
    @objc func onOpenNote(_ n: Notification) { reopen() }
    func reopen() { if hidden { toggleHidden() }; say("I'm here! Right-click me for the menu.", 3); joyT = 1.5; hearts(2) }

    func makePanel(_ w: Double, _ h: Double) -> PetPanel {
        let p = PetPanel(contentRect: NSRect(x: 0, y: 0, width: w, height: h), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false; p.backgroundColor = .clear; p.hasShadow = false
        p.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.dockWindow)) + 1)   // just above the Dock
        p.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]
        p.hidesOnDeactivate = false; p.isMovable = false; p.isReleasedWhenClosed = false; p.animationBehavior = .none
        return p
    }

    // Pet coordinates are global with y growing down from the top of the main screen, like Windows and like
    // CGWindowList and Accessibility. AppKit's y-up only appears where windows are placed and the mouse is read.
    func workArea() -> R {
        guard let s = NSScreen.screens.first else { return wa }
        let v = s.visibleFrame
        primaryH = Double(s.frame.height)
        return R(l: Double(v.minX), t: primaryH - Double(v.maxY), r: Double(v.maxX), b: primaryH - Double(v.minY))
    }
    func cursor() -> Pt { let m = NSEvent.mouseLocation; return Pt(x: Double(m.x), y: primaryH - Double(m.y)) }

    // ------------------------------------------------------------------ the brain, 30 times a second
    @objc func tickTimer() { autoreleasepool { tick() } }

    func tick() {
        let nowT = ProcessInfo.processInfo.systemUptime
        let dt = min(0.1, max(0, nowT - lastTick)); lastTick = nowT
        winCache = nil
        animT += dt; stateT += dt
        chargeT = max(0, chargeT - dt)
        if drinkT > 0 { drinkT -= dt; if drinkT <= 0 { hearts(2) } }
        stretchT = max(0, stretchT - dt); clockT = max(0, clockT - dt)
        detectiveT = max(0, detectiveT - dt); wizardT = max(0, wizardT - dt); paraT = max(0, paraT - dt)
        clipOfferT -= dt; curiousT -= dt; askT -= dt
        joyT = max(0, joyT - dt); angryT = max(0, angryT - dt); sqT = max(0, sqT - dt); bubbleT -= dt
        blinkT -= dt; blinkOn -= dt
        if blinkT <= 0 { blinkOn = 0.13; blinkT = rand(2, 6) }

        secAcc += dt
        if secAcc >= 1 {
            secAcc = 0
            wa = workArea()
            if gcSec == 0 { mark("1 s of frames") }
            watch()
            if gcSec == 0 { mark("patrol check") }
            audioWatch()
            if gcSec == 0 { mark("Core Audio") }
            focusTimer()
            power()
            reminders()
            tagCache = nil
            if focusPhase != 0 {
                let left = max(0, Int(focusEnd.timeIntervalSinceNow))
                tagCache = (focusPhase == 1 ? "Focus " : "Break ") + "\(left / 60):" + String(format: "%02d", left % 60)
            }
            wellness()
            if gcSec % 60 == 0 { rollDay() }
            if gcSec % 600 == 30 { diskCheck() }
            let agentLine = agentTag()
            if tagCache == nil { tagCache = agentLine }
            gcSec += 1
            if gcSec % 10 == 0 { _ = malloc_zone_pressure_relief(nil, 0) }      // hand freed pages back to macOS, like GC.Collect on Windows
            if probeSecs > 0 { probe() }
        }

        let b = pendingBust; pendingBust = nil
        if let b = b, alert == nil, state != HELD { beginAlert(b) }
        if orient != 0 && sign != nil && state != AIR && state != HELD { letGo(nil) }   // come down to show the sign
        let eyeing = alert == nil && watchRemaining > 0 && watchRemaining <= 5          // it notices before it acts
        eyeingNow = eyeing
        if eyeing && state == WALK { setState(SIT, 2) }

        clipPoll -= dt
        if clipPoll <= 0 {
            clipPoll = 0.5
            let n = NSPasteboard.general.changeCount
            if n != lastCount { lastCount = n; if n != ownCount { readClipboard(true) } }
        }
        let c = cursor()
        detectTyping(dt)
        let enter = cfg.enterRope && (CGEventSource.keyState(.combinedSessionState, key: 0x24) || CGEventSource.keyState(.combinedSessionState, key: 0x4C))
        ropeCool -= dt; webCool -= dt
        if enter && !enterDown && !hidden && alert == nil && ropeT < 0 && ropeCool <= 0
            && (state == SIT || state == WALK || state == SLEEP || state == TYPE) { throwRope() }
        enterDown = enter
        if ropeT >= 0 { ropeT += dt; if ropeT > 0.6 { ropeT = -1; ropePanel?.orderOut(nil) } }

        let phones = cfg.audio && headphones, kind = cfg.audio ? audioKind : 0
        // macOS has no system-wide output level without a recording permission, so music gets a steady beat instead
        audioPeak = kind == 1 ? 0.1 + 0.15 * abs(sin(animT * .pi * 2.2)) : audioRunning ? 0.03 : 0
        if phones != wasPhones {
            wasPhones = phones
            if alert == nil { say(phones ? "Headphones on!" : "Headphones off.", 2.2); if phones { joyT = 1.5; hearts(2) } }
        }
        if kind != lastKind {
            lastKind = kind; grooving = kind == 1; grooveT = rand(15, 35)
            if alert == nil && kind == 1 { say("Ooh, music!", 2) }
            else if alert == nil && kind == 2 { say("Class time. Taking notes.", 2.5); if state == WALK || state == SLEEP { setState(SIT, 6) } }
        }
        if kind == 1 { grooveT -= dt; if grooveT <= 0 { grooving.toggle(); grooveT = grooving ? rand(15, 35) : rand(30, 70) } }   // dances a while, then a break
        danceLevel = danceLevel * 0.55 + (kind == 1 && grooving ? audioPeak : 0) * 0.45
        if kind == 1 && grooving && audioPeak > 0.05 && (state == SIT || state == TYPE || state == WALK) {
            noteT -= dt
            if noteT <= 0 { noteT = rand(0.6, 1.2); part(x + rand(-7, 6) * U, y - 11 * U, -35 * S, 1.5, Bool.random() ? "\u{266A}" : "\u{266B}") }
        }
        movieTick(dt)
        if keyBurst >= 3 && cfg.typing && alert == nil && (state == SIT || state == WALK || state == SLEEP) { setState(TYPE, 0) }
        lapOpen = state != TYPE ? 0 : clamp(lapOpen + (lastKeyAgo <= 2.5 ? dt : -dt) / 0.3, 0, 1)
        var lx = c.x, ly = c.y
        if let a = alert { lx = a.centerX ?? x; ly = y - 50 * U }
        else if eyeing { lx = watchX; ly = y - 50 * U }
        let near = alert != nil || eyeing || abs(lx - x) + abs(ly - y) < 450 * S
        lookX = !near ? (state == WALK ? facing : 0) : lx > x + 4 * U ? 1 : lx < x - 4 * U ? -1 : 0
        lookY = near && ly < y - 12 * U ? -1 : 0
        if state == TYPE { lookX = 0; lookY = 0 }                                // eyes on the screen
        if orient == 1 || orient == 2 {                                          // on a wall the face turns sideways
            let upward = state == CLIMB ? climbUp : c.y < y
            lookX = (orient == 1) == upward ? 1 : -1; lookY = 0
        } else if orient == 3 { lookY = c.y > y + 12 * U ? -1 : 0 }             // hanging: peek down at the cursor

        ride()
        switch state {
        case SIT:
            if curiousLine != nil && stateT >= 0.4 { deliverCurious() }         // arrived at your window: say it
            if stateT >= stateDur && sign == nil { chooseNext() }               // a reminder sign keeps it put
        case WALK:
            if step(walkTarget, 55 * S * max(0.5, cfg.size), dt) {
                if climbAfterWalk { climbAfterWalk = false; startClimb(walkTarget <= (wa.l + wa.r) / 2) }
                else if pushTarget != 0 && abs(walkTarget - pushAt) < 1 { startPush() }
                else { pushTarget = 0; setState(SIT, rand(2, 6)) }
            }
        case CLIMB: climb(dt)
        case HANG:
            if hangSleep {
                zT -= dt
                if zT <= 0 { zT = 1.3; part(x + (orient == 2 ? -12 : 12) * U, y + (orient == 3 ? 6 : -2) * U, -22 * S, 1.8, "z") }
            }
            if stateT >= stateDur && sign == nil { hangNext() }
        case SLEEP:
            zT -= dt
            if zT <= 0 { zT = 1.3; part(x + facing * 5 * U, y - 7 * U, -22 * S, 1.8, "z") }
            if stateT >= stateDur { setState(SIT, 2) }
        case AIR: physics(dt)
        case PUSH: pushTick(dt)
        case SWING:
            let u = clamp(stateT / max(0.01, stateDur), 0, 1)
            (x, y) = swingPos(swingSX, swingSY, swingAX, swingAY, swingTX, swingTY, u)
            if u >= 1 { x = swingTX; y = swingTY; land(true) }
        case TYPE:
            if lastKeyAgo > 2.5 && lapOpen <= 0 { setState(SIT, rand(1.5, 3)) }
            else if lastKeyAgo < 0.4 && lapOpen >= 1 {
                codeT -= dt
                if codeT <= 0 { codeT = rand(0.5, 1.1); part(x + rand(-4, 3) * U, y - 8 * U, -30 * S, 1.2, pick(codes)) }
            }
        case HELD:
            let px0 = x, py0 = y
            x = clamp(c.x + grabDx, wa.l + 8 * U, wa.r - 8 * U)
            y = clamp(c.y + grabDy, wa.t + 10 * U, wa.b)
            if dt > 0 { vx = vx * 0.5 + (x - px0) / dt * 0.5; vy = vy * 0.5 + (y - py0) / dt * 0.5 }
        case RUN:
            guard let a = alert else { setState(SIT, 1); break }
            let target = a.centerX == nil || !cfg.wander ? x : clamp(a.centerX!, minX(), maxX())
            if step(target, 240 * S, dt) { startGlare() }
        case GLARE:
            if cfg.nag { if stateT >= 2.6 { endAlert(1) }; break }
            glareTick += dt
            if glareTick >= 1 {
                glareTick -= 1; glareLeft -= 1
                if glareLeft <= 0 { setState(SWIPE, 0); acted = false }
                else if let a = alert { say(a.scold + "  \(glareLeft)s", 99) }
            }
        case SWIPE:
            if !acted && stateT >= 0.3, let a = alert {
                acted = true
                if let why = act(a) { say(why, 3) }
                else {
                    say("Closed. Back to work!", 2.4); joyT = 2.4
                    cooldownUntil = Date().addingTimeInterval(12)
                    award(25); closes += 1; did(0)
                }
            }
            if stateT >= 0.7 { endAlert(2.4) }
        default: break
        }

        var i = parts.count - 1
        while i >= 0 {
            let p = parts[i]; p.y += p.vy * dt; p.life -= dt
            if p.life <= 0 || parts.count > 40 { parts.remove(at: i) }
            i -= 1
        }

        let rx = x.rounded(), ry = y.rounded()
        var wl = orient == 1 ? rx - U : orient == 2 ? rx + U - W : rx - (W / 2).rounded(.down)
        let wt = orient == 1 || orient == 2 ? ry - (H / 2).rounded(.down) : orient == 3 ? ry - U : ry + U - H
        wl = clamp(wl, wa.l, wa.r - W)
        if wl != winLeft || wt != winTop { panel.setFrameOrigin(NSPoint(x: wl, y: primaryH - wt - H)); winLeft = wl; winTop = wt }
        if ropeT >= 0 { drawRope() } else if state == SWING { drawWeb() } else if webShown { ropePanel?.orderOut(nil); webShown = false }
        if !hidden { view.needsDisplay = true }
    }

    // ------------------------------------------------------------------ web-swing and the Return rope
    @discardableResult func startSwing(_ cursorX: Double, _ forAlert: Bool = false, _ trip: Bool = false) -> Bool {
        if !cfg.webTravel || held || hidden { return false }
        if forAlert { if state == SWING || state == AIR || state == HELD { return false } }
        else {
            if alert != nil || sign != nil || (webCool > 0 && !trip) { return false }
            if ![SIT, WALK, SLEEP, TYPE, HANG, CLIMB].contains(state) { return false }
        }
        let tx = clamp(cursorX, wa.l + 8 * U, wa.r - 8 * U)
        let dist = abs(tx - x)
        if dist < 40 * S { if !forAlert && !trip { say("Already here!", 1.2) }; return false }
        orient = 0; climbNext = 0; hangSleep = false; climbAfterWalk = false; perch = 0; ignorePerch = 0
        curiousLine = nil; askT = 0; bubbleT = 0; pushTarget = 0
        swingSX = x; swingSY = y; swingTX = tx; swingTY = wa.b
        let baseline = min(swingSY, swingTY)
        let rise = clamp(dist * 0.45, 80 * S, max(40 * S, baseline - (wa.t + 14 * U)))
        swingAX = swingSX + (swingTX - swingSX) * 0.5
        swingAY = baseline - rise
        facing = swingTX >= swingSX ? 1 : -1
        setState(SWING, clamp(dist / ((forAlert ? 1200 : 1000) * S), 0.35, 0.9))   // quick, and quicker still for an alert
        return true
    }

    func drawWeb() {
        webShown = true
        let hx = x + facing * 6 * U, hy = y - 9 * U, ax = swingAX, ay = swingAY
        let dist = ((ax - hx) * (ax - hx) + (ay - hy) * (ay - hy)).squareRoot()
        if dist < 3 { ropePanel?.orderOut(nil); webShown = false; return }
        let th = max(2, U * 0.4), nx = -(ay - hy) / dist * th / 2, ny = (ax - hx) / dist * th / 2
        showRope([Pt(x: hx + nx, y: hy + ny), Pt(x: ax + nx, y: ay + ny), Pt(x: ax - nx, y: ay - ny), Pt(x: hx - nx, y: hy - ny)], nil)
    }

    // Return: lasso the spot you just typed at. The text cursor when the app exposes one, else the mouse.
    func throwRope() {
        guard let front = NSWorkspace.shared.frontmostApplication, front.processIdentifier != getpid() else { return }
        var t = cursor()
        if AXIsProcessTrusted(), let p = caretPoint() { t = p }
        ropeTx = t.x; ropeTy = t.y; ropeT = 0; ropeCool = 1.2
        facing = ropeTx >= x ? 1 : -1
        if state == SLEEP || state == WALK { setState(SIT, 2) }
    }

    func drawRope() {
        let t = ropeT, e = t < 0.22 ? t / 0.22 : t < 0.38 ? 1 : max(0, 1 - (t - 0.38) / 0.22)
        let dy: Double = state == TYPE || state == SLEEP ? 2 : 0
        let hx = x + facing * 6 * U, hy = y - (9 - dy) * U                       // the raised paw
        let tx = hx + (ropeTx - hx) * e, ty = hy + (ropeTy - hy) * e
        let dist = ((tx - hx) * (tx - hx) + (ty - hy) * (ty - hy)).squareRoot()
        if dist < 3 { ropePanel?.orderOut(nil); return }
        let sag = min(90 * S, dist * 0.25) * (t < 0.22 ? 1 - e * 0.8 : 0.15)
        let mx = (hx + tx) / 2, my = (hy + ty) / 2 + sag, th = max(2, U * 0.45)
        let n = 16
        var bx = [Double](repeating: 0, count: n + 1), bY = [Double](repeating: 0, count: n + 1)
        for i in 0...n {
            let u = Double(i) / Double(n), a = (1 - u) * (1 - u), b = 2 * u * (1 - u), c = u * u
            bx[i] = a * hx + b * mx + c * tx; bY[i] = a * hy + b * my + c * ty
        }
        var pts = [Pt](repeating: Pt(), count: 2 * (n + 1))
        for i in 0...n {
            let dx = bx[min(i + 1, n)] - bx[max(i - 1, 0)], dyy = bY[min(i + 1, n)] - bY[max(i - 1, 0)]
            let len = max(0.001, (dx * dx + dyy * dyy).squareRoot()), nx = -dyy / len * th / 2, ny = dx / len * th / 2
            pts[i] = Pt(x: bx[i] + nx, y: bY[i] + ny)
            pts[2 * n + 1 - i] = Pt(x: bx[i] - nx, y: bY[i] - ny)
        }
        var star: [Pt]? = nil
        if t >= 0.2 && t < 0.48 {
            let r = U * (2.5 + 1.5 * sin((t - 0.2) / 0.28 * .pi))
            star = (0..<16).map { i -> Pt in
                let ang = Double(i) * .pi / 8 + t * 6, rr = i % 2 == 0 ? r : r * 0.45
                return Pt(x: tx + cos(ang) * rr, y: ty + sin(ang) * rr)
            }
        }
        showRope(pts, star)
    }

    func showRope(_ pts: [Pt], _ star: [Pt]?) {
        let screen = NSScreen.screens.first?.frame ?? NSRect(x: 0, y: 0, width: 1440, height: primaryH)
        if ropePanel == nil {                                                    // made the first time a rope or web is thrown
            let rp = makePanel(Double(screen.width), Double(screen.height)), rv = RopeView(frame: NSRect(origin: .zero, size: screen.size))
            rp.ignoresMouseEvents = true
            rv.rope.fillColor = color(ROPE).cgColor
            rv.star.fillColor = color(STAR).cgColor; rv.star.strokeColor = color(DARK).cgColor
            rp.contentView = rv
            ropePanel = rp; ropeView = rv
        }
        guard let rp = ropePanel, let rv = ropeView else { return }
        if rp.frame != screen { rp.setFrame(screen, display: false); rv.frame = NSRect(origin: .zero, size: screen.size) }   // resolution changed
        rv.show(pts.map { CGPoint(x: $0.x, y: $0.y) }, star?.map { CGPoint(x: $0.x, y: $0.y) }, NSScreen.screens.first?.backingScaleFactor ?? 2)
        if !rp.isVisible { rp.orderFrontRegardless() }
    }

    // Typing without ever knowing WHICH key: only how long ago the last key went down.
    func detectTyping(_ dt: Double) {
        lastKeyAgo += dt; keyBurst = max(0, keyBurst - dt * 1.5)
        let k = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: .keyDown)
        if k + 0.02 < lastKeySecs { lastKeyAgo = 0; keyBurst += 1 }
        lastKeySecs = k
    }

    func idleSecs() -> Double { CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!) }

    func setState(_ s: Int, _ dur: Double) { state = s; stateT = 0; stateDur = dur }
    func say(_ text: String, _ seconds: Double) { bubble = text; bubbleT = seconds; bubbleSign = false }
    func chirp(_ text: String, _ seconds: Double) { if !cfg.quiet { say(text, seconds) } }   // optional chit-chat; `quiet = yes` drops it
    func part(_ px: Double, _ py: Double, _ pvy: Double, _ life: Double, _ text: String? = nil) { parts.append(Part(px, py, pvy, life, text)) }
    func minX() -> Double { perch != 0 ? perchRect.l + 7 * U : wa.l + 8 * U }
    func maxX() -> Double { perch != 0 ? perchRect.r - 7 * U : wa.r - 8 * U }

    func step(_ target: Double, _ speed: Double, _ dt: Double) -> Bool {
        let d = target - x
        if abs(d) <= speed * dt { x = target; return true }
        facing = d > 0 ? 1 : -1; x += facing * speed * dt
        return false
    }

    func chooseNext() {
        var r = Double.random(in: 0..<1)
        if webHops > 0 { webHops -= 1; if webHop() { return }; webHops = 0 }        // the rest of a web-slinging trip
        if movieOn && r < 0.8 { setState(SIT, rand(10, 20)); return }            // watch with you, with the odd break
        if movieOn { chirp(pick(["Be right back!", "Popcorn refill!", "Stretching my legs."]), 2); r = Double.random(in: 0..<0.5) }   // a stroll or a nap
        if lastKind == 2 || (lastKind == 1 && grooving) { setState(SIT, rand(4, 8)); return }   // class: stay put; music: dance, between breaks
        if curiousVisit() { return }
        if perch != 0 {
            if r < 0.35 && cfg.wander { walk(rand(minX(), maxX())) }
            else if r < 0.5 && cfg.sleepy { setState(SLEEP, rand(12, 30)) }
            else if r < 0.8 { hopDown() }
            else { setState(SIT, rand(2, 5)) }
            return
        }
        if r < 0.35 && cfg.wander { walk(rand(minX(), maxX())) }
        else if r < 0.5 && cfg.sleepy { setState(SLEEP, rand(12, 30)) }
        else if r < 0.72 && cfg.wander && jumpOntoWindow() { }
        else if r < 0.82 && cfg.wander && cfg.climb { goClimb() }
        else if r < 0.86 && cfg.wander && planPush() { }
        else if r < 0.9 && cfg.wander && cfg.webTravel && webTrip() { }
        else if r < 0.93 && cfg.wander { walk(cursor().x) }                       // come see what you're doing
        else if r < 0.97 { hop(0, -520 * S) }
        else { setState(SIT, rand(2, 5)) }
    }

    // ------------------------------------------------------------------ coding-agent status (idea from AgentPet, MIT)
    @objc func onAgentNote(_ n: Notification) {
        guard let s = n.object as? String, s.utf16.count <= 8192 else { return }
        let f = s.components(separatedBy: "\n")
        if f.count >= 4 { onAgentEvent(f[0], f[1], f[2], f[3], f.count >= 5 && !f[4].isEmpty ? f[4] : "Claude Code") }
    }

    func onAgentEvent(_ ev: String, _ sid: String, _ cwd: String, _ msg: String, _ who: String) {
        if !cfg.agents { return }
        let now = Date()
        let st: String
        switch ev {
        case "working", "waiting", "done", "end": st = ev
        case "UserPromptSubmit", "PreToolUse": st = "working"                    // names from old --claude-hook installs
        case "Notification": st = "waiting"
        case "Stop": st = "done"
        case "SessionEnd": st = "end"
        default: return
        }
        let id = who + "|" + (sid.isEmpty ? cwd : sid)                           // two agents can share a project folder
        if st == "end" { agents.removeValue(forKey: id); return }
        let a = agents[id] ?? AgentSess()
        agents[id] = a
        a.project = TextTools.projectName(cwd); a.seen = now; a.who = who
        if a.state != st { a.state = st; a.since = now }
        let proj = a.project.isEmpty ? "" : " (" + a.project + ")"
        let free = alert == nil && sign == nil
        if st == "waiting" {
            if now.timeIntervalSince(a.lastPing) < 60 { return }                  // one nudge a minute per session
            a.lastPing = now
            let text = msg.lowercased().contains("permission") ? who + " needs your OK" + proj + "!" : who + " is waiting for you" + proj + "."
            beep()
            if hidden { notify(who, text) }
            if !free { return }
            say(text, 6); joyT = 3                                               // arms up, waving you over
            if orient == 0 && (state == SIT || state == WALK || state == SLEEP || state == TYPE) { walk(cursor().x) }
        } else if st == "done" {
            claudeDone += 1; did(5)
            if hidden { notify(who, who + " finished" + proj + ".") }
            if !free { return }
            say(who + " finished" + proj + "!", 4); hearts(4)
            if orient == 0 && (state == SIT || state == WALK || state == SLEEP) { hop(0, -520 * S) }
            if now.timeIntervalSince(lastAgentXp) >= 30 { lastAgentXp = now; award(10) }
        }
    }

    // A tag above the pet while an agent works or waits; also drops sessions that went quiet.
    func agentTag() -> String? {
        let now = Date()
        var since = now, working = 0, waiting = 0, who = "", waitWho = ""
        for (k, a) in agents {
            if now.timeIntervalSince(a.seen) > 3 * 3600 || (a.state == "done" && now.timeIntervalSince(a.since) > 15 * 60) { agents.removeValue(forKey: k); continue }
            if a.state == "working" && now.timeIntervalSince(a.seen) > 30 * 60 { a.state = "idle" }   // interrupted without a Stop
            if a.state == "working" { working += 1; if a.since <= since { since = a.since; who = a.who } }
            else if a.state == "waiting" && now.timeIntervalSince(a.since) < 180 { waiting += 1; waitWho = a.who }
        }
        agentBusy = working > 0
        if !cfg.agents { return nil }
        if waiting == 1 { return waitWho + " needs you" }
        if waiting > 1 { return "\(waiting) agents need you" }
        if working == 1 { return who + " working " + dur(now.timeIntervalSince(since)) }
        if working > 1 { return "\(working) agents working" }
        return nil
    }

    func dur(_ s: TimeInterval) -> String {
        let t = Int(max(0, s))
        return t >= 3600 ? "\(t / 3600)h" + String(format: "%02d", (t % 3600) / 60) : "\(t / 60):" + String(format: "%02d", t % 60)
    }

    func addAgentItems(_ m: NSMenu) {
        let now = Date()
        var shown = 0
        for a in agents.values where !a.state.isEmpty {
            let what = a.state == "waiting" ? "needs you" : a.state
            add(m, a.who + (a.project.isEmpty ? "" : "  -  " + a.project) + "  -  " + what + "  " + dur(now.timeIntervalSince(a.since)), gray: true)
            shown += 1
        }
        if shown == 0 { add(m, "No agent activity yet", gray: true) }
        m.addItem(.separator())
        var found = 0
        for (i, sp) in AgentDefs.all.enumerated() where AgentDefs.installed(sp) {
            found += 1
            let on = AgentDefs.connected(sp)
            add(m, (on ? "Disconnect " : "Connect ") + sp[1] + (on ? "" : "..."), 70 + i, check: on)
        }
        if found == 0 { add(m, "No agent CLIs found on this Mac", gray: true) }
    }

    // Hooks store this app's full path, so a copy running from Downloads, a disk image or macOS's quarantine
    // translocation folder would leave the agent calling a path that disappears.
    func connectAgent(_ i: Int) {
        let sp = AgentDefs.all[i], on = AgentDefs.connected(sp), path = AgentDefs.path(sp)
        let exe = Bundle.main.executablePath ?? CommandLine.arguments[0]
        NSApp.activate(ignoringOtherApps: true)
        if !on {
            if exe.contains("/AppTranslocation/") || exe.contains("/Downloads/") || exe.hasPrefix("/Volumes/") || exe.hasPrefix("/private/var/folders/") {
                if !confirm("Move PixelPet Focus to Applications first",
                            "It's running from\n" + (exe as NSString).deletingLastPathComponent + "\n\nThe hooks point at this exact path, so they break when it's moved or cleaned up. Drag PixelPet Focus into Applications, open it from there, then connect again.\n\nConnect anyway?") { return }
            }
            if !confirm("Connect " + sp[1], "PixelPet will add hooks to\n" + path + "\n\nso it can react when " + sp[1]
                        + " is working, needs you, or finishes. A backup of the current file is saved next to it. If you move PixelPet later, connect again.") { return }
        }
        do {
            try AgentSetup.apply(key: sp[0], path: path, style: sp[3], events: sp[4], connect: !on, exe: exe)
            inform(sp[1], !on ? "Connected. New " + sp[1] + " sessions will tell PixelPet when they work, need you, or finish."
                              : "Disconnected. PixelPet's hooks were removed from " + (path as NSString).lastPathComponent + ".")
        } catch {
            inform(sp[1], "Couldn't update " + path + ":\n" + error.localizedDescription + "\n\nNothing was changed.")
        }
    }

    func confirm(_ title: String, _ text: String) -> Bool {
        let a = NSAlert(); a.messageText = title; a.informativeText = text
        a.addButton(withTitle: "Continue"); a.addButton(withTitle: "Cancel")
        return a.runModal() == .alertFirstButtonReturn
    }
    func inform(_ title: String, _ text: String) { let a = NSAlert(); a.messageText = title; a.informativeText = text; _ = a.runModal() }

    // ------------------------------------------------------------------ progress, streaks, achievements, ranks
    // Level looks: a sprout, an explorer cap, a hero headband, a crown. Hats step aside for headphones.
    func drawRank(_ dy: Double, _ phones: Bool) {
        if level >= 35 { if !phones { px(4, dy - 1, 6, 1, STAR); px(4, dy - 2, 1, 1, STAR); px(6.5, dy - 2, 1, 1, STAR); px(9, dy - 2, 1, 1, STAR); px(6.7, dy - 0.8, 0.6, 0.6, PINK) } }
        else if level >= 20 { px(2, dy + 1, 10, 0.7, HEADBAND); px(12, dy + 1.1, 1.4, 0.5, HEADBAND); px(12.8, dy + 1.5, 1, 0.5, HEADBAND) }
        else if level >= 10 { if !phones { px(3, dy - 1, 8, 1, CAP); px(facing > 0 ? 10 : 1, dy - 0.35, 3, 0.4, CAP) } }
        else if level >= 5 { if !phones { px(6.7, dy - 1.2, 0.6, 1.2, GREEN); px(7.3, dy - 1.9, 1.4, 0.8, GREEN); px(5.4, dy - 1.6, 1.3, 0.6, GREEN) } }
    }

    // Callers bump their own counter first; this files it under today (see Core.swift's encodeDay order).
    func did(_ day: Int) {
        rollDay()
        today[day] += 1
        let hour = cal.component(.hour, from: Date())
        if hour < 4 { today[6] = 1; nightOwl = true }
        let d = dayStart(Date())
        if lastDay != d {
            streak = lastDay == addDays(d, -1) ? streak + 1 : 1; lastDay = d
            if streak > bestStreak { bestStreak = streak; if streak >= 3 { remember("New best streak: \(streak) days in a row") } }
        }
        checkAch()
        saveProgress()
    }

    func achMet(_ i: Int) -> Bool {
        switch i {
        case 0: return closes >= 1
        case 1: return closes >= 25
        case 2: return focusDone >= 1
        case 3: return focusDone >= 25
        case 4: return remDone >= 1
        case 5: return clipActs >= 10
        case 6: return claudeDone >= 1
        case 7: return claudeDone >= 100
        case 8: return streak >= 3
        case 9: return streak >= 7
        case 10: return nightOwl
        case 11: return level >= 5
        case 12: return level >= 20
        case 13: return level >= 35
        default: return false
        }
    }

    func checkAch() {
        for i in 0..<achNames.count where ach & (1 << i) == 0 && achMet(i) {
            ach |= 1 << i
            remember("Unlocked \"" + achNames[i] + "\": " + achHow[i])
            say("Achievement: " + achNames[i] + "!", 4); joyT = 2.5; hearts(5); beep(); wizardT = 5
            saveProgress()
            return                                                               // one at a time; the next shows on the next event
        }
    }

    func addStatsItems(_ m: NSMenu) {
        let next = nextRank(level), cur = lastDay >= addDays(dayStart(Date()), -1) ? streak : 0
        var got = 0
        for i in 0..<achNames.count where ach & (1 << i) != 0 { got += 1 }
        add(m, "Rank: " + rank(level) + (next > 0 ? "  (next rank at level \(next))" : "  (top rank)"), gray: true)
        add(m, "Tabs closed: \(closes)     Pats: \(pats)", gray: true)
        add(m, "Focus sessions: \(focusDone)     Reminders done: \(remDone)", gray: true)
        add(m, "Clipboard actions: \(clipActs)     Agent tasks: \(claudeDone)", gray: true)
        add(m, "Streak: \(cur)" + (cur == 1 ? " day" : " days") + "   (best \(bestStreak))", gray: true)
        let together = (cal.dateComponents([.day], from: met, to: dayStart(Date())).day ?? 0) + 1
        add(m, "Personality: " + traits() + "     Together for \(together) days", gray: true)
        m.addItem(.separator())
        add(m, "Achievements \(got) / \(achNames.count)", gray: true)
        for i in 0..<achNames.count { add(m, achNames[i] + "  -  " + achHow[i], gray: true, check: ach & (1 << i) != 0) }
    }

    func saveProgress() {
        let lines = ["level=\(level)", "xp=\(xp)", "closes=\(closes)", "pats=\(pats)", "focus=\(focusDone)", "reminders=\(remDone)",
                     "clipboard=\(clipActs)", "claude=\(claudeDone)", "streak=\(streak)", "best=\(bestStreak)",
                     "lastday=" + (lastDay == .distantPast ? "" : fmt(lastDay, "yyyy-MM-dd")), "owl=\(nightOwl ? 1 : 0)", "ach=\(ach)",
                     "met=" + (met == .distantPast ? "" : fmt(met, "yyyy-MM-dd")),
                     "day=" + (todayDate == .distantPast ? "" : encodeDay(todayDate, today)), "hist=" + hist.joined(separator: ";"),
                     "askedax=\(askedAX ? 1 : 0)", "screen=" + fmt(screenDay, "yyyyMMdd") + ":\(screenToday)"]
        try? (lines.joined(separator: "\n") + "\n").write(toFile: progressPath, atomically: true, encoding: .utf8)
    }

    func loadProgress() {
        for line in readLines(progressPath) {
            guard let eq = line.firstIndex(of: "=") else { continue }
            let k = trimmed(String(line[..<eq])), v = trimmed(String(line[line.index(after: eq)...])), n = Int(v) ?? 0
            switch k {
            case "level": level = max(1, n)
            case "xp": xp = max(0, n)
            case "closes": closes = n
            case "pats": pats = n
            case "focus": focusDone = n
            case "reminders": remDone = n
            case "clipboard": clipActs = n
            case "claude": claudeDone = n
            case "streak": streak = n
            case "best": bestStreak = n
            case "owl": nightOwl = n == 1
            case "ach": ach = n
            case "askedax": askedAX = n == 1
            case "screen": if v.hasPrefix(fmt(Date(), "yyyyMMdd") + ":") { screenToday = Int(v.dropFirst(9)) ?? 0; screenDay = dayStart(Date()) }
            case "lastday": if let d = parseDate(v, "yyyy-MM-dd") { lastDay = d }
            case "met": if let d = parseDate(v, "yyyy-MM-dd") { met = d }
            case "day": if let d = decodeDay(v) { todayDate = d.0; today = d.1 }
            case "hist": hist = v.split(separator: ";").map { String($0) }.filter { decodeDay($0) != nil }
            default: break
            }
        }
    }

    // ------------------------------------------------------------------ global shortcuts (Carbon hot keys: no permission needed)
    // Another app may own the combo the user asked for, so a couple of spares are tried before giving up.
    func takeHotkey(_ id: UInt32, _ wanted: String, _ setting: String, _ fallbacks: [String]) -> String {
        let tries = [trimmed(wanted)] + fallbacks
        let first = tries[0]
        if first == "off" || first == "none" || first == "no" { return "" }      // deliberately disabled
        for combo in tries {
            guard let k = Keys.parse(combo) else { continue }
            var ref: EventHotKeyRef?
            if RegisterEventHotKey(k.code, k.mods, EventHotKeyID(signature: OSType(0x50505446), id: id), GetApplicationEventTarget(), 0, &ref) != noErr { continue }
            hotRefs.append(ref)
            if combo.lowercased() != first.lowercased() { say(first + " is taken, so I'm using " + k.text + ". Change it with " + setting + " in the settings.", 7) }
            return k.text
        }
        say("Couldn't register a shortcut for " + setting + ": every choice is taken. Pick another in the settings.", 7)
        return ""
    }

    func hotkey(_ id: Int) { if id == 2 { startSwing(cursor().x) } else { clipMenu(nil) } }

    // ------------------------------------------------------------------ costumes: a hat and a prop for the moment
    // 1 explorer + telescope, 2 detective, 3 lab flask, 4 wizard, 5 parachute, 6 briefcase, 7 coffee, 8 map, 9 thinking, 10 ideas
    func costume() -> Int {
        if !cfg.costumes || state == HELD || alert != nil || sign != nil || drinkT > 0 || stretchT > 0 || clockT > 0 { return 0 }
        if paraT > 0 && state == AIR { return 5 }
        if wizardT > 0 { return 4 }
        if askT > 0 { return 9 }
        if clipOfferT > 0 && clipText != nil { return 10 }
        if detectiveT > 0 && (state == SIT || state == WALK) { return 2 }
        if eyeingNow { return 1 }
        if state != SIT { return 0 }                                             // the rest are sitting-still props
        if agentBusy { return 3 }
        if focusPhase == 1 { return 7 }
        if doing == "study" { return 8 }
        if doing == "work" { return 6 }
        return 0
    }

    func drawCostume(_ dy: Double, _ body: Int, _ c: Int) {
        let right = facing > 0
        let side = right ? 11.6 : -1.6                                           // where a held prop sits
        switch c {
        case 1:                                                                  // explorer hat and a telescope
            px(1.6, dy - 0.7, 10.8, 0.6, PAPER); px(3.4, dy - 2.2, 7.2, 1.6, PAPER); px(3.4, dy - 1.1, 7.2, 0.45, ANGRY)
            px(right ? 9.8 : -1.8, dy + 2.1, 6, 1.1, DARK)
            px(right ? 15.4 : -2.6, dy + 1.9, 0.9, 1.5, PAPER)
        case 2:                                                                  // detective hat and magnifying glass
            px(2.6, dy - 1.5, 8.8, 1.2, DARK); px(1.4, dy - 0.4, 11.2, 0.5, DARK)
            px(side, dy + 1.6, 2.8, 2.8, DARK); px(side + 0.5, dy + 2.1, 1.8, 1.8, PAPER)
            px(right ? side + 1 : side + 0.8, dy + 4.4, 0.6, 1.6, ROPE)
        case 3:                                                                  // lab cap and a bubbling flask
            px(3.2, dy - 1.5, 7.6, 1.5, WHITE); px(3.8, dy - 2.1, 6.4, 0.7, WHITE)
            px(side + 0.2, dy + 3.4, 2.4, 0.6, WHITE); px(side + 0.5, dy + 2.6, 1.8, 0.9, PINK)
            px(side + 1, dy + 1.4, 0.8, 1.3, WHITE)
            if Int(animT * 3) % 2 == 0 { px(side + 1.1, dy + 0.4, 0.6, 0.6, PINK) }
        case 4:                                                                  // wizard hat and wand
            px(6.2, dy - 4, 1.6, 1, CAP); px(5.4, dy - 3, 3.2, 1, CAP); px(4.6, dy - 2, 4.8, 1, CAP)
            px(3.4, dy - 1, 7.2, 0.7, PAPER)
            px(6.8, dy - 2.8, 0.7, 0.7, STAR)
            px(right ? 11.8 : -1.4, dy + 1.6, 2.6, 0.45, ROPE)
            px(right ? 14.2 : -1.8, dy + 0.9, 1, 1, STAR)
        case 5:                                                                  // parachute
            px(4.2, dy - 8, 5.6, 1, CAP); px(2.8, dy - 7, 8.4, 1, CAP); px(1.8, dy - 6, 10.4, 1, CAP)
            px(4.4, dy - 7, 1.5, 1, WHITE); px(8.1, dy - 7, 1.5, 1, WHITE)
            px(2.2, dy - 5, 0.35, 4.2, DARK); px(7, dy - 5, 0.35, 4.2, DARK); px(11.5, dy - 5, 0.35, 4.2, DARK)
        case 6: px(side, dy + 4.2, 2.6, 2, DARK); px(side + 0.8, dy + 3.6, 1, 0.6, DARK)   // briefcase
        case 7:                                                                  // coffee
            px(side, dy + 3.6, 2, 1.9, WHITE); px(side + 2, dy + 4, 0.5, 0.9, WHITE); px(side + 0.25, dy + 3.75, 1.5, 0.5, ROPE)
            if Int(animT * 2) % 2 == 0 { px(side + 0.8, dy + 2.6, 0.5, 0.7, WHITE) }
        case 8:                                                                  // a map held up
            px(3.2, dy + 3.2, 7.6, 3.6, PAPER); px(6.8, dy + 3.2, 0.3, 3.6, INK)
            px(4.2, dy + 4.2, 0.8, 0.8, ANGRY); px(8.6, dy + 5.4, 0.8, 0.8, GREEN); px(5.4, dy + 5.6, 0.7, 0.7, CAP)
        case 9: px(right ? 9.6 : 2.6, dy + 3.4, 1.8, 1.3, body)                  // paw to the chin, thinking
        case 10: bulb(2.4, dy - 2.6, STAR); bulb(6.4, dy - 3.4, GREEN); bulb(10.2, dy - 2.6, PINK)   // ideas popping overhead
        default: break
        }
    }

    func bulb(_ c: Double, _ row: Double, _ colour: Int) { px(c, row, 1.2, 1.3, colour); px(c + 0.3, row + 1.3, 0.6, 0.4, DARK) }

    // ------------------------------------------------------------------ memories and habits ("grows from your days")
    func remember(_ text: String) {
        let line = fmt(Date(), "yyyy-MM-dd HH:mm") + " | " + text.replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ")
        memTail.append(line)
        if memTail.count > 12 { memTail.removeFirst() }
        if !FileManager.default.fileExists(atPath: memPath) {
            try? "# PixelPet Focus memory book: one memory per line, newest at the bottom.\n".write(toFile: memPath, atomically: true, encoding: .utf8)
        }
        appendLine(memPath, line)
    }

    func initMemories() {
        var all = readLines(memPath).filter { !$0.isEmpty }
        if all.count > 600 { all = Array(all.suffix(500)); try? (all.joined(separator: "\n") + "\n").write(toFile: memPath, atomically: true, encoding: .utf8) }
        for l in all.suffix(12) where !l.hasPrefix("#") && !trimmed(l).isEmpty { memTail.append(l) }
        if met == .distantPast { met = dayStart(Date()); remember("We met. Hi, I'm your new pet!") }
        rollDay()
        computeTraits(false); traitsReady = true
        saveProgress()
    }

    // At the first activity of a new day (or once a minute), yesterday becomes a memory.
    func rollDay() {
        let d = dayStart(Date())
        if todayDate == d { return }
        if todayDate != .distantPast, let sum = daySummary(todayDate, today) { hist.append(encodeDay(todayDate, today)); remember(sum) }
        while hist.count > 13 { hist.removeFirst() }
        today = [Int](repeating: 0, count: 7); todayDate = d
        if traitsReady { computeTraits(true); saveProgress() }
    }

    // Personality from the last 7 days. New traits become memories, and two of them show on the pet.
    func computeTraits(_ announce: Bool) {
        var f = today[2], p = today[1], c = today[0], k = today[5], late = today[6]
        let start = dayStart(Date())
        for e in hist {
            guard let day = decodeDay(e), start.timeIntervalSince(day.0) <= 6.5 * 86400 else { continue }
            f += day.1[2]; p += day.1[1]; c += day.1[0]; k += day.1[5]; late += day.1[6]
        }
        traitFocused = trait(traitFocused, f >= 5, "Focused", "lots of focus sessions this week (look, glasses)", announce)
        traitLoved = trait(traitLoved, p >= 40, "Loved", "so many pats this week (rosy cheeks!)", announce)
        traitOwl = trait(traitOwl, late >= 3, "a Night owl", "we stayed up late a lot this week", announce)
        traitGuard = trait(traitGuard, c >= 10, "a Guardian", "closed 10+ doomscroll tabs this week", announce)
        traitBuddy = trait(traitBuddy, k >= 20, "an Agent buddy", "agents finished 20+ tasks this week", announce)
    }

    func trait(_ was: Bool, _ now: Bool, _ name: String, _ why: String, _ announce: Bool) -> Bool {
        if now && !was && announce { remember("Became " + name + ": " + why); chirp("I think I'm becoming " + name + ".", 3) }
        return now
    }

    func traits() -> String {
        var t: [String] = []
        if traitFocused { t.append("Focused") }
        if traitLoved { t.append("Loved") }
        if traitOwl { t.append("Night owl") }
        if traitGuard { t.append("Guardian") }
        if traitBuddy { t.append("Agent buddy") }
        return t.isEmpty ? "still getting to know you" : t.joined(separator: ", ")
    }

    func drawTraits(_ dy: Double) {
        if traitLoved { px(2.2, dy + 4.6, 1.3, 0.6, PINK); px(10.5, dy + 4.6, 1.3, 0.6, PINK) }   // rosy cheeks, just under the glasses
        if traitFocused {                                                        // study glasses
            px(3, dy + 1.4, 3, 0.35, DARK); px(3, dy + 4.1, 3, 0.35, DARK); px(3, dy + 1.4, 0.35, 3, DARK); px(5.65, dy + 1.4, 0.35, 3, DARK)
            px(8, dy + 1.4, 3, 0.35, DARK); px(8, dy + 4.1, 3, 0.35, DARK); px(8, dy + 1.4, 0.35, 3, DARK); px(10.65, dy + 1.4, 0.35, 3, DARK)
            px(6, dy + 2.2, 2, 0.35, DARK)
        }
    }

    func addMemoryItems(_ m: NSMenu) {
        var shown = 0
        for line in memTail.reversed() where shown < 10 {
            var label = line
            if line.count > 19, let when = parseDate(String(line.prefix(16)), "yyyy-MM-dd HH:mm") { label = fmt(when, "MMM d") + "  -  " + String(line.dropFirst(19)) }
            add(m, label.count > 90 ? String(label.prefix(90)) + "..." : label, gray: true)
            shown += 1
        }
        if shown == 0 { add(m, "No memories yet", gray: true) }
        m.addItem(.separator())
        add(m, "Open memory book...", 66)
    }

    // ------------------------------------------------------------------ tools and a quick system check
    func powerState() -> (ac: Bool, pct: Int)? {
        let info = IOPSCopyPowerSourcesInfo().takeRetainedValue()
        let list = IOPSCopyPowerSourcesList(info).takeRetainedValue() as Array
        for ps in list {
            guard let desc = IOPSGetPowerSourceDescription(info, ps as CFTypeRef)?.takeUnretainedValue() as NSDictionary?,
                  (desc[kIOPSTypeKey] as? String) == kIOPSInternalBatteryType,
                  let cur = desc[kIOPSCurrentCapacityKey] as? Int, let mx = desc[kIOPSMaxCapacityKey] as? Int, mx > 0 else { continue }
            return ((desc[kIOPSPowerSourceStateKey] as? String) == kIOPSACPowerValue, min(100, cur * 100 / mx))
        }
        return nil
    }

    func batteryText() -> String { guard let p = powerState() else { return "No battery" }; return "Battery \(p.pct)%" + (p.ac ? " (plugged in)" : "") }

    func diskInfo() -> (free: Double, total: Double)? {
        guard let v = try? URL(fileURLWithPath: "/").resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeTotalCapacityKey]),
              let free = v.volumeAvailableCapacityForImportantUsage, let total = v.volumeTotalCapacity, total > 0 else { return nil }
        return (Double(free) / 1e9, Double(total) / 1e9)                          // GB the way Finder counts them
    }

    func ramLoad() -> Int {
        var s = vm_statistics64_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64_data_t>.size / MemoryLayout<integer_t>.size)
        let kr = withUnsafeMutablePointer(to: &s) { $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count) } }
        guard kr == KERN_SUCCESS else { return -1 }
        let used = (Double(s.active_count) + Double(s.wire_count) + Double(s.compressor_page_count)) * Double(vm_kernel_page_size)
        return Int(used / Double(ProcessInfo.processInfo.physicalMemory) * 100)
    }

    // What Activity Monitor shows in its Memory column for this app.
    func footprintMB() -> Double {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
        let kr = withUnsafeMutablePointer(to: &info) { $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count) } }
        return kr == KERN_SUCCESS ? Double(info.phys_footprint) / 1_048_576 : -1
    }

    func uptimeText() -> String {
        let t = Int(ProcessInfo.processInfo.systemUptime)
        return "Up for " + (t >= 86400 ? "\(t / 86400)d " : "") + "\((t % 86400) / 3600)h \((t % 3600) / 60)m"
    }

    func addToolItems(_ m: NSMenu) {
        let ram = ramLoad()
        add(m, batteryText(), gray: true)
        if let d = diskInfo() { add(m, "Disk: " + String(format: "%.0f", d.free) + " GB free of " + String(format: "%.0f", d.total) + " GB", gray: true) }
        else { add(m, "Disk: unknown", gray: true) }
        if ram >= 0 { add(m, "Memory in use: \(ram)%", gray: true) }
        add(m, uptimeText(), gray: true)
        add(m, "PixelPet itself: " + String(format: "%.1f", footprintMB()) + " MB", gray: true)
        m.addItem(.separator())
        add(m, "System check", 60)
        add(m, "Screenshot", 61)
        add(m, "Activity Monitor", 62)
        add(m, "Calculator", 63)
        add(m, "TextEdit", 64)
        add(m, "Lock screen", 65)
    }

    func systemCheck() {
        let ram = ramLoad(), disk = diskInfo()
        var worries: [String] = []
        if let d = disk, d.free < 5 || d.free / d.total < 0.08 { worries.append("the disk is nearly full") }
        if ram >= 90 { worries.append("memory is almost used up") }
        if ProcessInfo.processInfo.systemUptime > 7 * 86400 { worries.append("a restart soon would help") }
        if let p = powerState(), p.pct <= 20 && !p.ac { worries.append("battery is low") }
        let report = batteryText() + "\n" + (disk.map { String(format: "%.0f", $0.free) + " GB free" } ?? "Disk ?") + (ram >= 0 ? " \u{00B7} RAM \(ram)%" : "") + "\n" + uptimeText()
        say(report + "\n" + (worries.isEmpty ? "All good!" : "Hmm: " + worries.joined(separator: ", ") + "."), 7)
        if worries.isEmpty { joyT = 1.5; hearts(2) } else { angryT = 1 }
    }

    func diskCheck() {
        guard diskWarned != dayStart(Date()), let d = diskInfo(), d.free < 5 || d.free / d.total < 0.08 else { return }
        diskWarned = dayStart(Date())
        say("Your disk is almost full: " + String(format: "%.1f", d.free) + " GB left.", 5); angryT = 1
        remember("Warned you: the disk had only " + String(format: "%.1f", d.free) + " GB left")
    }

    func openFile(_ p: String) { NSWorkspace.shared.open(URL(fileURLWithPath: p)) }

    // ------------------------------------------------------------------ windows as furniture
    // On-screen windows, front to back, from CGWindowList: positions need no permission (titles would).
    func wins() -> [Win] {
        if let c = winCache { return c }
        var out: [Win] = []
        let me = getpid()
        if let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] {
            for d in list {
                guard (d[kCGWindowLayer as String] as? Int) == 0, let pid = d[kCGWindowOwnerPID as String] as? Int32, pid != me,
                      let num = d[kCGWindowNumber as String] as? UInt32, let bd = d[kCGWindowBounds as String] as? NSDictionary,
                      let rect = CGRect(dictionaryRepresentation: bd as CFDictionary), rect.width > 40, rect.height > 40 else { continue }
                if let a = d[kCGWindowAlpha as String] as? Double, a < 0.05 { continue }
                out.append(Win(id: num, pid: pid, f: R(l: Double(rect.minX), t: Double(rect.minY), r: Double(rect.maxX), b: Double(rect.maxY))))
            }
        }
        winCache = out
        return out
    }

    func topWin(_ px: Double, _ py: Double) -> Win? { wins().first(where: { $0.f.has(px, py) }) }
    func frontWindow(_ pid: pid_t) -> Win? { wins().first(where: { $0.pid == pid }) }
    func frontWinId() -> CGWindowID { NSWorkspace.shared.frontmostApplication.flatMap { frontWindow($0.processIdentifier) }?.id ?? 0 }
    func filling(_ r: R) -> Bool { r.w >= wa.w - 4 && r.h >= wa.h - 4 }

    // Moving another app's window goes through Accessibility, which also tells us which window is which.
    func axWindow(_ w: Win) -> AXUIElement? {
        let app = AXUIElementCreateApplication(w.pid)
        AXUIElementSetMessagingTimeout(app, 0.25)
        guard let list = axAttr(app, kAXWindowsAttribute) as? [AXUIElement] else { return nil }
        for el in list {
            if let f = axFrame(el), abs(f.l - w.f.l) < 2, abs(f.t - w.f.t) < 2, abs(f.w - w.f.w) < 2, abs(f.h - w.f.h) < 2 { return el }
        }
        return nil
    }

    // Always leaves a grabbable strip on screen.
    func moveWindowBy(_ el: AXUIElement, _ dx: Double) -> Bool {
        guard dx != 0, let f = axFrame(el) else { return false }
        let nl = clamp(f.l + dx, wa.l - f.w + 120 * S, wa.r - 120 * S).rounded()
        if abs(nl - f.l) < 1 { return false }
        var p = CGPoint(x: nl, y: f.t)
        guard let v = AXValueCreate(.cgPoint, &p) else { return false }
        return AXUIElementSetAttributeValue(el, kAXPositionAttribute as CFString, v) == .success
    }

    // Thrown sideways into a window: it gets knocked over and the pet bounces off.
    func bumpWindow(_ dt: Double) {
        let dir: Double = vx > 0 ? 1 : -1
        let lead = x + dir * 7 * U, prevLead = lead - vx * dt
        guard AXIsProcessTrusted(), let w = topWin(lead, y - 4 * U), !filling(w.f) else { return }
        let edge = dir > 0 ? w.f.l : w.f.r
        let missed = dir > 0 ? (prevLead > edge + 1 || lead < edge) : (prevLead < edge - 1 || lead > edge)
        if missed { return }                                                     // not its side we just crossed
        guard let el = axWindow(w) else { return }
        _ = moveWindowBy(el, clamp(vx * 0.06, -120 * S, 120 * S))
        x = edge - dir * 7 * U; vx = -vx * 0.35; sqK = 0.6; sqT = 0.2
        part(edge, y - 7 * U, -30 * S, 0.8, "bonk!")
    }

    // Now and then: walk up to a window resting on the Dock line and shove it a little way along.
    func planPush() -> Bool {
        if !cfg.push || perch != 0 || orient != 0 || focusPhase == 1 || doing == "work" || doing == "study"
            || Date().timeIntervalSince(lastPush) < 600 || !AXIsProcessTrusted() { return false }
        let fg = frontWinId()
        var best: Win? = nil, bestD = Double.greatestFiniteMagnitude, bestAt = 0.0, bestDir = 0.0
        for w in wins() {
            let r = w.f
            if w.id == fg || filling(r) || r.b < wa.b - 8 * U || r.b > wa.b + 4 || r.w < 200 * S { continue }
            var fromLeft = abs(x - r.l) <= abs(x - r.r)
            for _ in 0..<2 {
                let at = fromLeft ? r.l - 7 * U : r.r + 7 * U, dir: Double = fromLeft ? 1 : -1
                let room = dir > 0 ? wa.r - r.r : r.l - wa.l
                if at < minX() || at > maxX() || room < 60 * S || topWin(fromLeft ? r.l + 3 : r.r - 4, r.b - 3 * U)?.id != w.id { fromLeft.toggle(); continue }
                let dist = abs(at - x)
                if dist < bestD { bestD = dist; best = w; bestAt = at; bestDir = dir }
                break
            }
        }
        guard let b = best else { return false }
        pushTarget = b.id; pushRect = b.f; pushDir = bestDir; pushAX = nil
        walk(bestAt); pushAt = walkTarget
        return true
    }

    func startPush() {
        guard let w = wins().first(where: { $0.id == pushTarget }), !filling(w.f), w.id != frontWinId(),
              w.f.l == pushRect.l, w.f.b == pushRect.b, let el = axWindow(w) else { pushTarget = 0; setState(SIT, 2); return }
        pushAX = el
        facing = pushDir; pushLeft = rand(40, 90) * S; pushAcc = 0; pushed = 0; lastPush = Date()
        setState(PUSH, 0)
        chirp("Hnngh!", 1)
    }

    func pushTick(_ dt: Double) {
        if stateT < 0.5 { return }                                               // brace first
        guard pushLeft > 0, let el = pushAX, frontWinId() != pushTarget else { endPush(); return }
        pushAcc += 30 * S * dt
        let move = pushAcc.rounded(.down)
        if move == 0 { return }
        pushAcc -= move
        if !moveWindowBy(el, pushDir * move) { endPush(); return }
        x += pushDir * move; pushLeft -= move; pushed += move
    }

    func endPush() {
        pushTarget = 0; pushAX = nil
        if pushed > 0 { chirp(pick(pushLines), 2); joyT = 1 }
        setState(SIT, rand(2, 4))
    }

    // ------------------------------------------------------------------ break buddy: water, stretching and screen time
    // Counts non-stop use (a 5-minute pause resets it), sends you to drink and to stretch, and now and then says how
    // long you've been at it. Nudges wait for a free moment: not in a focus session, a movie, a meeting or a
    // presentation, and never over something it's already saying.
    func wellness() {
        if screenDay != dayStart(Date()) { screenDay = dayStart(Date()); screenToday = 0 }
        if idleSecs() > 300 { useSec = 0; waterSec = 0; stretchSec = 0; lastTold = 0; return }
        useSec += 1; waterSec += 1; stretchSec += 1; sinceNudge += 1; screenToday += 1
        if screenToday % 300 == 0 { saveProgress() }
        if focusPhase == 1 || movieOn || alert != nil || sign != nil || bubbleT > 0 || state == HELD || state == AIR || state == SWING || busy() { return }
        let water = cfg.water > 0 && waterSec >= cfg.water * 60, stretch = cfg.stretch > 0 && stretchSec >= cfg.stretch * 60
        let every = cfg.screenTime * 60
        if stretch || water {
            let line = span(useSec) + (stretch ? " without a break. Stand up and stretch" + (water ? ", and have some water!" : "!") : " on screen. Time for a sip of water!")
            if stretch { stretchSec = 0 }
            if water { waterSec = 0 }
            sinceNudge = 0; beep()
            if hidden { notify(stretch ? "Stretch break" : "Water break", line); return }
            say(line, 7); pushTarget = 0
            if orient != 0 { letGo(nil) } else { setState(SIT, 6) }
            if stretch { stretchT = 5 } else { drinkT = 4.5 }
        } else if every > 0 && useSec / every > lastTold && sinceNudge > 600 && !cfg.quiet && !hidden {
            lastTold = useSec / every; sinceNudge = 0; clockT = 4
            say(span(useSec) + " on screen without a break" + (screenToday > useSec + 300 ? " (" + span(screenToday) + " today)." : "."), 5)
            if orient == 0 { setState(SIT, 4) }
        }
    }

    func presenting() -> Bool {
        guard let f = fgFrame, let s = NSScreen.screens.first else { return false }
        return f.l <= 0 && f.t <= 0 && f.w >= Double(s.frame.width) && f.h >= primaryH
    }

    func busy() -> Bool { presenting() || TextTools.activity(fgProc, fgTitle, fgUrl) == "meeting" }

    // Now and then it goes web-slinging: two or three quick swings across the screen, then carries on.
    func webTrip() -> Bool {
        if focusPhase == 1 || doing == "work" || doing == "study" || movieOn || grooving { return false }
        webHops = Int.random(in: 1...2)
        if webHop() { chirp("Thwip!", 1); return true }
        webHops = 0
        return false
    }

    func webHop() -> Bool {
        var tx = x
        for _ in 0..<4 where abs(tx - x) < (wa.r - wa.l) * 0.25 { tx = rand(wa.l + 12 * U, wa.r - 12 * U) }   // somewhere a good way off
        return startSwing(tx, false, true)
    }

    // ------------------------------------------------------------------ curious visits
    func curiousVisit() -> Bool {
        let now = Date()
        if !doing.isEmpty && now >= doingUntil { doing = "" }
        if !cfg.curious || curiousT > 0 || orient != 0 || alert != nil || sign != nil || focusPhase == 1 || doing == "leave" { return false }
        curiousT = doing == "work" || doing == "study" ? rand(900, 1500) : doing == "break" ? rand(90, 200) : rand(180, 420)
        guard fgWin != 0, frontWinId() == fgWin else { return false }
        let act = TextTools.activity(fgProc, fgTitle, fgUrl)
        if act == "meeting" { return false }                                     // never chirp during a call
        let changed = act != lastActivity
        lastActivity = act
        curiousAsk = !cfg.quiet && doing.isEmpty && (act.isEmpty || (changed && Double.random(in: 0..<1) < 0.5))
        curiousLine = curiousAsk ? "What are you up to?  (click me)" : TextTools.comment(act, doing)
        if curiousLine == nil { return false }
        curiousAt = now; detectiveT = 8                                          // out comes the magnifying glass
        if !jumpOntoWindow() {                                                   // hop onto its title bar, or walk underneath it
            guard let f = fgFrame else { curiousLine = nil; return false }
            walk((f.l + f.r) / 2)
        }
        return true
    }

    func deliverCurious() {
        guard let line = curiousLine else { return }
        curiousLine = nil
        if Date().timeIntervalSince(curiousAt) > 20 || alert != nil || sign != nil { return }   // took too long getting there
        lookY = -1
        if curiousAsk { say(line, 12); askT = 12; part(x + facing * 3 * U, y - 14 * U, -14 * S, 1.6, "?") }
        else { chirp(line, 3.5) }
    }

    // ------------------------------------------------------------------ movie night
    func movieTick(_ dt: Double) {
        if !movieOn { if !movieGenre.isEmpty { movieGenre = "" }; scaredT = 0; snackKind = 0; return }
        scaredT = max(0, scaredT - dt); movieCool -= dt; movieChat -= dt
        let g = TextTools.genre(fgTitle)
        if !g.isEmpty { movieGenre = g }                                         // sticky: the title may change to "Netflix" in fullscreen
        let pk = cfg.audio ? audioPeak : 0
        let calm = alert == nil && (state == SIT || state == TYPE) && sign == nil
        let spike = pk > 0.12 && pk > movieAvg * 2.5 && movieCool <= 0 && movieLoud > 20
        movieAvg += (pk - movieAvg) * min(1, dt * 0.5)
        if pk > 0.01 { movieQuiet = 0; movieLoud += dt }
        else {
            movieQuiet += dt
            if movieQuiet > 20 && movieLoud > 1800 { movieLoud = 0; chirp("Credits! Was it good?", 3); hearts(3); joyT = 1.5 }
        }
        if !calm { snackKind = 0; return }
        snacks(dt)
        if spike { movieCool = 7; startle() }
        else if movieChat <= 0 { movieChat = rand(35, 70); vibe() }
    }

    // Between reactions it keeps helping itself: a handful of popcorn, then a sip through the straw.
    func snacks(_ dt: Double) {
        if snackKind != 0 {
            snackLeft -= dt
            if snackLeft > 0 { return }
            snackKind = 0; snackT = rand(6, 14)
            return
        }
        snackT -= dt
        if snackT > 0 { return }
        if Double.random(in: 0..<1) < 0.6 { snackKind = 1; snackLeft = rand(1.8, 3); part(x + rand(-1, 2) * U, y - 7 * U, -26 * S, 0.8, "\u{2022}") }
        else { snackKind = 2; snackLeft = rand(1.6, 2.4); part(x - 5 * U, y - 7 * U, -22 * S, 1, "slurp") }
    }

    func startle() {
        if movieOn && snackKind == 0 && Double.random(in: 0..<1) < 0.7 {           // jumped: the popcorn goes flying
            for _ in 0..<5 { part(x + rand(-2, 3) * U, y - 7 * U, -rand(40, 80) * S, rand(0.7, 1.2), "\u{2022}") }
        }
        sqK = 0.8; sqT = 0.25
        part(x, y - 12 * U, -30 * S, 0.8, "!")
        let ground = orient == 0 && perch == 0
        switch movieGenre {
        case "horror": scaredT = 2.5; chirp(pick(["AAAH!", "Nope nope nope!", "Who turned off the lights?!"]), 2); if ground { hop(0, -420 * S) }
        case "comedy": joyT = 1.5; chirp(pick(["HAHA!", "Good one!", "Hehehe!"]), 1.8)
        case "romance": joyT = 1.5; hearts(4); chirp(pick(["Awww!", "Kiss! Kiss!", "So sweet!"]), 2)
        case "action": chirp(pick(["BOOM!", "Whoa!", "Did you see that?!"]), 1.8); if ground { hop(0, -300 * S) }
        case "anim": joyT = 1.5; chirp(pick(["Wheee!", "Yay!", "Again, again!"]), 1.8)
        default: chirp(pick(["Whoa!", "Did that just happen?!"]), 1.8)
        }
    }

    func vibe() {
        switch movieGenre {
        case "horror": scaredT = 2; chirp(pick(["Is it safe to look?", "Tell me when it's over.", "I'm not scared. You're scared."]), 2.5)
        case "romance": hearts(3); chirp(pick(["Aww, they're so cute together.", "*sniff*", "Kiss already!"]), 2.5)
        case "comedy": joyT = 1.2; chirp(pick(["Ha ha ha!", "I can't breathe!", "*giggles*"]), 2)
        case "action": chirp(pick(["Go go go!", "Behind you!", "Epic!"]), 2)
        case "anim": joyT = 1.2; hearts(2); chirp(pick(["So pretty!", "I love this one.", "Sing along!"]), 2.5)
        default: chirp(pick(["*munch munch*", "Shh, best part!", "No spoilers!", "Pass the popcorn."]), 2)
        }
        part(x + rand(-3, 3) * U, y - 8 * U, -45 * S, 0.9, "\u{2022}")          // a kernel pops
    }

    func addDoingItems(_ m: NSMenu) {
        add(m, "What are you doing?" + (doing.isEmpty ? "" : "  (now: " + doing + ")"), gray: true)
        add(m, "Working", 40, check: doing == "work")
        add(m, "Working, start a focus timer", 41)
        add(m, "Studying", 42, check: doing == "study")
        add(m, "Taking a break", 43, check: doing == "break")
        add(m, "Just browsing", 44, check: doing == "browse")
        add(m, "Leave me alone (1 hour)", 45, check: doing == "leave")
        add(m, "Movie night (3 hours, pauses patrol)", 46, check: movieOn)
    }

    func askMenu(_ e: NSEvent?) { askT = 0; let m = newMenu(); addDoingItems(m); popMenu(m, e) }

    func answer(_ cmd: Int) {
        let now = Date()
        askT = 0; bubbleT = 0
        if doing == "movie" && cmd != 46 { snoozeUntil = .distantPast }          // leaving movie night ends its patrol pause
        switch cmd {
        case 40: doing = "work"; doingUntil = now + 45 * 60; curiousT = rand(900, 1500); say("Got it. I'll keep it down.", 2.5)
        case 41:
            doing = "work"; doingUntil = now + Double(cfg.focus + cfg.brk) * 60; curiousT = rand(900, 1500)
            if focusPhase == 0 { focusPhase = 1; focusEnd = now + Double(cfg.focus) * 60 }
            say("Focus mode. I'm watching.", 2.5)
        case 42: doing = "study"; doingUntil = now + 45 * 60; curiousT = rand(900, 1500); say("Study hard! I'll guard you.", 2.5)
        case 43: doing = "break"; doingUntil = now + Double(max(5, cfg.brk)) * 60; curiousT = rand(60, 120); say("Break time! Stretch those paws.", 2.5); hearts(3)
        case 44: doing = "browse"; doingUntil = now + 30 * 60; curiousT = rand(240, 480); say("Have fun. No doomscrolling!", 2.5)
        case 45: doing = "leave"; doingUntil = now + 3600; curiousT = 3600; say("Okay. Quiet for an hour.", 2); return
        case 46:
            if movieOn { doing = ""; snoozeUntil = .distantPast; say("Movie's over. Back on patrol.", 2.5); return }
            doing = "movie"; doingUntil = now + 3 * 3600; curiousT = 3 * 3600
            movieGenre = ""; movieAvg = 0; movieLoud = 0; movieQuiet = 0; scaredT = 0; movieChat = 15; movieCool = 0
            snoozeUntil = doingUntil
            if state == WALK || state == SLEEP { setState(SIT, 10) }
            say("Movie night! Popcorn ready.", 3); hearts(3); return
        default: return
        }
        joyT = 1.2
    }

    func walk(_ t: Double) { walkTarget = clamp(t, minX(), maxX()); climbAfterWalk = false; setState(WALK, 0) }

    // ------------------------------------------------------------------ climbing the screen edges
    // orient: which surface the feet are on (0 floor, 1 left edge, 2 right edge, 3 top edge, upside down).
    func goClimb() {
        if perch != 0 { return }
        let left = x - wa.l < wa.r - x
        walk(left ? minX() : maxX())
        climbAfterWalk = true
    }

    func startClimb(_ left: Bool) {
        perch = 0; orient = left ? 1 : 2
        x = left ? wa.l : wa.r; y = wa.b - 7 * U
        let toTop = Bool.random()
        climbTarget = toTop ? wa.t + 7 * U : rand(wa.t + 20 * U, wa.b - 25 * U)
        climbNext = toTop ? 1 : 0
        setState(CLIMB, 0)
    }

    func climb(_ dt: Double) {
        let speed = 45 * S
        if orient == 3 {
            if !step(climbTarget, speed, dt) { return }
            if climbNext == 2 {                                                  // round the top corner and head down
                orient = x < (wa.l + wa.r) / 2 ? 1 : 2; x = orient == 1 ? wa.l : wa.r
                y = wa.t + 7 * U; climbTarget = wa.b - 7 * U; climbNext = 3
            } else { setState(HANG, rand(2, 5)) }
            return
        }
        let d = climbTarget - y
        climbUp = d < 0
        if abs(d) > speed * dt { y += (d > 0 ? 1 : -1) * speed * dt; return }
        y = climbTarget
        let left = orient == 1
        if climbNext == 1 {                                                      // over the top edge, upside down
            orient = 3; x = left ? wa.l + 7 * U : wa.r - 7 * U; y = wa.t; facing = left ? 1 : -1
            climbTarget = rand(wa.l + 25 * U, wa.r - 25 * U); climbNext = 0
        } else if climbNext == 3 {                                               // back on the floor
            orient = 0; climbNext = 0; x = left ? wa.l + 8 * U : wa.r - 8 * U; y = wa.b
            setState(SIT, rand(2, 4))
        } else { setState(HANG, rand(2, 5)) }
    }

    func hangNext() {
        hangSleep = false
        let r = Double.random(in: 0..<1)
        if orient == 3 {
            if r < 0.35 { climbTarget = rand(wa.l + 20 * U, wa.r - 20 * U); climbNext = 0; setState(CLIMB, 0) }
            else if r < 0.55 && cfg.sleepy { hangSleep = true; setState(HANG, rand(10, 20)) }   // bat nap
            else if r < 0.8 { letGo("Wheee!") }
            else { climbTarget = x < (wa.l + wa.r) / 2 ? wa.l + 7 * U : wa.r - 7 * U; climbNext = 2; setState(CLIMB, 0) }
            return
        }
        if r < 0.3 { climbTarget = wa.t + 7 * U; climbNext = 1; setState(CLIMB, 0) }
        else if r < 0.5 { climbTarget = rand(wa.t + 7 * U, wa.b - 20 * U); climbNext = 0; setState(CLIMB, 0) }
        else if r < 0.75 { climbTarget = wa.b - 7 * U; climbNext = 3; setState(CLIMB, 0) }
        else { letGo("Wheee!") }
    }

    // Let go of a wall or the top edge: back to upright, then gravity does the rest.
    func letGo(_ line: String?) {
        if orient == 1 { x = wa.l + 8 * U } else if orient == 2 { x = wa.r - 8 * U } else if orient == 3 { y = wa.t + 10 * U }
        vx = orient == 1 ? 120 * S : orient == 2 ? -120 * S : 0; vy = 0
        orient = 0; climbNext = 0; hangSleep = false; climbAfterWalk = false
        if let l = line { say(l, 1.2) }
        setState(AIR, 0)
    }

    // Thrown hard enough into a screen edge, it grabs on instead of bouncing.
    func grabEdge(_ lo: Double, _ hi: Double) -> Bool {
        let left = x < lo && vx < -700 * S, right = x > hi && vx > 700 * S, top = y < wa.t + 10 * U && vy < -900 * S
        if !cfg.climb || (!left && !right && !top) { return false }
        sqK = 0.6; sqT = 0.25; vx = 0; vy = 0; climbNext = 0; perch = 0
        if top && !left && !right { orient = 3; x = clamp(x, wa.l + 7 * U, wa.r - 7 * U); y = wa.t }
        else { orient = left ? 1 : 2; x = left ? wa.l : wa.r; y = clamp(y - 5 * U, wa.t + 7 * U, wa.b - 7 * U) }
        say("Gotcha!", 1.2)
        setState(HANG, rand(1.5, 3))
        return true
    }

    func hop(_ hvx: Double, _ hvy: Double) { vx = hvx; vy = hvy; perch = 0; setState(AIR, 0) }
    func hopDown() { ignorePerch = perch; hop((x < (wa.l + wa.r) / 2 ? 1 : -1) * 160 * S, -380 * S) }

    // ------------------------------------------------------------------ physics: throws, falls, window tops
    func physics(_ dt: Double) {
        let prevY = y, g = 2600 * S
        vy += g * dt; x += vx * dt; y += vy * dt
        let lo = wa.l + 8 * U, hi = wa.r - 8 * U
        if grabEdge(lo, hi) { return }
        if cfg.push && abs(vx) > 500 * S { bumpWindow(dt) }
        if x < lo { x = lo; vx = -vx * 0.5 }
        if x > hi { x = hi; vx = -vx * 0.5 }
        if y < wa.t + 10 * U { y = wa.t + 10 * U; if vy < 0 { vy = 0 } }
        if cfg.costumes && vy > 700 * S && wa.b - y > 22 * U { paraT = 4 }       // long drop: out comes the parachute
        if paraT > 0 && vy > 240 * S { vy = 240 * S }                            // ...and it floats down
        if vy <= 0 { return }
        if let w = topWin(x, y + U + 2), w.id != ignorePerch, ledge(w), w.f.t >= prevY - 1, w.f.t <= y + U + 2, x > w.f.l + 4 * U, x < w.f.r - 4 * U {
            y = w.f.t; perch = w.id; perchRect = w.f; land()
            return
        }
        if y >= wa.b { y = wa.b; perch = 0; ignorePerch = 0; land() }
    }

    func land(_ webby: Bool = false) {
        if webby {
            // a deliberate landing rather than the usual fall: a squash, a happy line, dust and hearts
            sqK = 0.85; sqT = 0.25; vx = 0; vy = 0; webCool = 1.0
            if alert == nil { joyT = 1.2; if webHops == 0 { chirp(pick(landingLines), 1.8); hearts(2) } }
            for _ in 0..<5 { part(x + rand(-9, 9) * U, y - rand(0, 1.5) * U, -rand(18, 40) * S, rand(0.5, 0.9), "\u{00B7}") }
            if let a = alert {
                if a.centerX == nil || abs(a.centerX! - x) < 60 * S { startGlare() } else { setState(RUN, 0) }
            } else { setState(SIT, webHops > 0 ? 0.2 : rand(1.5, 3)) }        // mid-trip: straight on to the next swing
            return
        }
        sqK = min(1, vy / (1400 * S)); sqT = 0.25; vx = 0; vy = 0
        if alert != nil { if perch != 0 { hopDown() } else { setState(RUN, 0) } }
        else { setState(SIT, rand(1.5, 4)) }
    }

    // Riding a window: move with it, fall off when it is minimised, closed, zoomed to fill the screen or covered.
    func ride() {
        if perch == 0 || state == AIR || state == HELD { return }
        guard let w = wins().first(where: { $0.id == perch }), !filling(w.f) else { fall(); return }
        x += w.f.l - perchRect.l; y = w.f.t; perchRect = w.f
        if x < w.f.l + 2 * U || x > w.f.r - 2 * U || topWin(x, w.f.t + U + 3)?.id != perch { fall() }
    }

    func fall() { perch = 0; vx = 0; vy = 0; setState(AIR, 0) }

    func jumpOntoWindow() -> Bool {
        guard let app = NSWorkspace.shared.frontmostApplication, app.processIdentifier != getpid(),
              let w = frontWindow(app.processIdentifier), ledge(w) else { return false }
        let r = w.f, g = 2600 * S
        let tx = clamp(x, r.l + 10 * U, r.r - 10 * U), dy = r.t - y
        if dy > -60 * S || topWin(tx, r.t + U + 3)?.id != w.id { return false }  // too low, or that spot is covered
        let T = max(0.7, (2 * -dy / g).squareRoot() * 1.25)
        ignorePerch = 0
        hop((tx - x) / T, (dy - 0.5 * g * T * T) / T)
        if vx != 0 { facing = vx > 0 ? 1 : -1 }
        return true
    }

    func ledge(_ w: Win) -> Bool {
        let r = w.f
        return !(r.t < wa.t + 30 * S || r.t > wa.b - 80 * S || r.w < 160 * S || filling(r))
    }

    // ------------------------------------------------------------------ interaction
    func mouseDown() { downPt = cursor(); pressed = true; grabDx = x - downPt.x; grabDy = y - downPt.y }

    func mouseDragged() {
        if !pressed || held { return }
        let c = cursor()
        if abs(c.x - downPt.x) + abs(c.y - downPt.y) > 6 * S { startHold() }
    }

    func startHold() {
        held = true; perch = 0; ignorePerch = 0; vx = 0; vy = 0
        orient = 0; climbNext = 0; hangSleep = false; climbAfterWalk = false   // picked off the wall: upright again
        pushTarget = 0; curiousLine = nil; webHops = 0
        if alert != nil { alert = nil; bubbleT = 0 }
        setState(HELD, 0)
        say("Wheee!", 1)
    }

    func leftUp(_ e: NSEvent) {
        if !pressed { return }
        pressed = false
        if held {
            held = false
            vx = clamp(vx, -2500 * S, 2500 * S); vy = clamp(vy, -2500 * S, 2500 * S)
            setState(AIR, 0)
            return
        }
        if alert != nil && (state == RUN || state == GLARE) {
            snoozeUntil = Date().addingTimeInterval(Double(cfg.snooze) * 60)
            alert = nil; joyT = 1.2
            say("Fine. \(cfg.snooze) minutes.", 2.5)
            setState(SIT, 2.5)
            return
        }
        if state == SWIPE { return }
        if sign != nil { reminderDone(); return }
        if clipOfferT > 0 && clipText != nil { clipMenu(e); return }
        if askT > 0 { askMenu(e); return }
        if state == SLEEP { angryT = 1.5; say("Hmph. I was napping.", 2); setState(SIT, 2); return }
        if hangSleep { hangSleep = false; angryT = 1.5; say("Hmph. Bat nap ruined.", 2); setState(HANG, 2); return }
        joyT = 1.6; hearts(3)
        if state != AIR && state != TYPE && orient == 0 { hop(0, -420 * S) }
        if Date().timeIntervalSince(lastPat) >= 4 { lastPat = Date(); award(5); pats += 1; did(1) }
    }

    func hearts(_ n: Int) { for _ in 0..<n { part(x + rand(-6, 6) * U, y - rand(9, 12) * U, -rand(35, 70) * S, rand(0.9, 1.5)) } }

    func award(_ n: Int) {
        xp += n
        var gained = 0
        while xp >= cost(level) && gained < 50 { xp -= cost(level); level += 1; gained += 1 }
        saveProgress()
        part(x, y - 11 * U, -40 * S, 1.4, "+\(n) XP")
        if gained > 0 {
            let was = rank(level - gained)
            say(rank(level) != was ? "Level \(level)! Evolved into a \(rank(level))!" : "Level \(level)!", 3.5)
            wizardT = 5                                                          // wizard hat for the level-up sparkle
            joyT = 3; hearts(6)
            remember("Grew to level \(level)" + (rank(level) != was ? " and became a " + rank(level) : ""))
            checkAch()
        }
    }

    func toggleHidden() {
        hidden.toggle()
        if hidden { panel.orderOut(nil); ropePanel?.orderOut(nil) } else { panel.orderFrontRegardless() }
        // The menu bar icon is there only while the pet is hidden: right-clicking the pet opens the same menu.
        if hidden && statusItem == nil {
            let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
            item.button?.image = petImage(18); item.button?.toolTip = "PixelPet Focus (hidden)"
            let menu = newMenu(); menu.delegate = self; item.menu = menu
            statusItem = item
        } else if !hidden, let item = statusItem { NSStatusBar.system.removeStatusItem(item); statusItem = nil }
    }

    // Charger in/out and low battery. Desktops (no battery) never trigger anything.
    func power() {
        guard let p = powerState() else { return }
        if !powerKnown { powerKnown = true; onAC = p.ac; batteryPct = p.pct; return }
        if alert != nil { return }                                               // an intervention owns the bubble; retry next second
        batteryPct = p.pct
        if p.ac && !onAC {
            onAC = true; lowWarned = 100; chargeT = 3; joyT = 2
            say("Charging! \(p.pct)%", 3)
            if state == SIT || state == WALK || state == SLEEP { hop(0, -560 * S) }
        } else if !p.ac && onAC { onAC = false; chargeT = 2.5; say("Unplugged. \(p.pct)% left.", 3) }
        else if !p.ac && p.pct <= 10 && lowWarned > 10 { lowWarned = 10; chargeT = 4; angryT = 1.5; say("Battery \(p.pct)%! Save your work.", 6) }
        else if !p.ac && p.pct <= 20 && lowWarned > 20 { lowWarned = 20; chargeT = 4; say("Battery \(p.pct)%. Plug me in?", 5) }
    }

    func drawBattery() {
        let t = max(2, (U / 3).rounded(.down)), bw = 6 * U, bh = 3 * U
        var bx = cx.rounded(.down) + 9 * U
        let bt = by.rounded(.down) - 8 * U
        if bx + bw + t > W { bx = cx.rounded(.down) - 9 * U - bw }              // no room on the right: other side
        let lv = onAC ? ((3 - chargeT) * 0.8).truncatingRemainder(dividingBy: 1) : Double(max(0, batteryPct)) / 100
        let fillC = batteryPct >= 0 && batteryPct <= 20 && !onAC ? ANGRY : GREEN
        let third = (bh / 3).rounded(.down)
        box(bx, bt, bw, bh, DARK); box(bx + bw, bt + third, t, third, DARK)
        box(bx + t, bt + t, bw - 2 * t, bh - 2 * t, WHITE)
        box(bx + t, bt + t, ((bw - 2 * t) * lv).rounded(.down), bh - 2 * t, fillC)
        if !onAC { return }
        let mx = bx + (bw / 2).rounded(.down), my = bt + (bh / 2).rounded(.down)
        let xs = [0.3, -0.7, -0.05, -0.3, 0.7, 0.05], ys = [-1.7, 0.2, 0.2, 1.7, -0.2, -0.2]
        let bolt = NSBezierPath()
        for i in 0..<6 {
            let p = NSPoint(x: mx + xs[i] * U, y: my + ys[i] * U)
            if i == 0 { bolt.move(to: p) } else { bolt.line(to: p) }
        }
        bolt.close()
        color(STAR).setFill(); bolt.fill(); color(DARK).setStroke(); bolt.lineWidth = 2 * S; bolt.stroke()
    }

    func focusTimer() {
        if focusPhase == 0 || Date() < focusEnd { return }
        if focusPhase == 1 {
            focusPhase = 2; focusEnd = Date() + Double(cfg.brk) * 60; focusDone += 1; did(2)
            say("Break time! Stretch a bit.", 6)
        } else { focusPhase = 0; say("Break's over. Ready?", 6) }
        joyT = 3; hearts(6)
        if state == SIT || state == WALK || state == SLEEP { hop(0, -650 * S) }
    }

    // ------------------------------------------------------------------ menus
    func newMenu() -> NSMenu { let m = NSMenu(); m.autoenablesItems = false; return m }

    func add(_ m: NSMenu, _ title: String, _ tag: Int = 0, gray: Bool = false, check: Bool = false) {
        let it = NSMenuItem(title: title, action: gray ? nil : #selector(menuPicked(_:)), keyEquivalent: "")
        it.target = self; it.tag = tag; it.isEnabled = !gray
        if check { it.state = .on }
        m.addItem(it)
    }

    func sub(_ m: NSMenu, _ title: String, _ s: NSMenu) { let it = NSMenuItem(title: title, action: nil, keyEquivalent: ""); it.submenu = s; m.addItem(it) }

    @objc func menuPicked(_ it: NSMenuItem) { handle(it.tag) }

    func menuNeedsUpdate(_ menu: NSMenu) { menu.removeAllItems(); fillMain(menu) }   // the menu bar icon

    func rightClick(_ e: NSEvent) { let m = newMenu(); fillMain(m); NSMenu.popUpContextMenu(m, with: e, for: view) }

    // A menu opened by a shortcut has no click to hang off, so the app steps forward for it and hands focus back after.
    func popMenu(_ m: NSMenu, _ e: NSEvent?) {
        if let e = e { NSMenu.popUpContextMenu(m, with: e, for: view); return }
        let prev = NSWorkspace.shared.frontmostApplication
        NSApp.activate(ignoringOtherApps: true)
        _ = m.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
        if let p = prev, p.processIdentifier != getpid() { _ = p.activate(options: []) }
    }

    func fillMain(_ m: NSMenu) {
        let now = Date(), snoozing = now < snoozeUntil, auto = autoStart(nil)
        menuOpenPt = cursor()                                                    // before the mouse moves onto the menu itself
        readClipboard(false)
        if let s = sign {
            add(m, "Done: " + s.msg, 30); add(m, "Snooze 5 min", 31); add(m, "Snooze 15 min", 32); add(m, "Snooze 1 hour", 33); add(m, "Tomorrow 9:00", 34)
            m.addItem(.separator())
        }
        add(m, "Level \(level) " + rank(level) + "   \(xp) / \(cost(level)) XP", gray: true)
        add(m, "Screen time: " + span(useSec) + " without a break, " + span(screenToday) + " today", gray: true)
        let stats = newMenu(); addStatsItems(stats); sub(m, "Stats & achievements", stats)
        if powerKnown && batteryPct >= 0 { add(m, "Battery \(batteryPct)%" + (onAC ? " (charging)" : ""), gray: true) }
        if cfg.audio && (headphones || audioKind != 0) {
            let dev = audioDevice.count > 36 ? String(audioDevice.prefix(36)) + "..." : audioDevice
            add(m, (headphones ? "Headphones: " + dev : "Speakers") + (audioKind == 1 ? "  (music)" : audioKind == 2 ? "  (class / call)" : ""), gray: true)
        }
        m.addItem(.separator())
        add(m, focusPhase == 0 ? "Start focus timer (\(cfg.focus) min)" : "Stop focus timer", 10)
        add(m, snoozing ? "Resume patrol (snoozed until " + fmt(snoozeUntil, "HH:mm") + ")" : "Snooze patrol \(cfg.snooze) min", 11)
        add(m, "On patrol", 12, check: patrol)
        add(m, state == SLEEP ? "Wake up" : "Nap now", 13)
        if cfg.climb { add(m, orient != 0 ? "Come down" : "Climb the wall", 26) }
        if cfg.webTravel { add(m, "Web-swing here" + (swingKeyText.isEmpty ? "" : "  (" + swingKeyText + ")"), 27) }
        m.addItem(.separator())
        let clip = newMenu(); addClipItems(clip); sub(m, "Clipboard", clip)
        let rm = newMenu()
        let upcoming = rems.sorted { $0.next < $1.next }
        for r in upcoming.prefix(5) {
            add(rm, r.kind == "every" ? "every \(r.every)m  " + r.msg + "  -  next " + fmt(r.next, "HH:mm") : whenText(r.next) + "  " + r.msg, gray: true)
        }
        if upcoming.isEmpty { add(rm, "None yet. Copy \"call mom in 20 min\" and click me.", gray: true) }
        rm.addItem(.separator())
        add(rm, "Pause recurring", 25, check: pauseRecurring)
        add(rm, "Edit reminders...", 24)
        sub(m, "Reminders", rm)
        let dm = newMenu(); addDoingItems(dm); sub(m, "What I'm doing", dm)
        let cm = newMenu(); addAgentItems(cm); sub(m, "AI agents", cm)
        let tm = newMenu(); addToolItems(tm); sub(m, "Tools", tm)
        let mm = newMenu(); addMemoryItems(mm); sub(m, "Memories", mm)
        let pm = newMenu(); addPermissionItems(pm); sub(m, "Permissions", pm)
        m.addItem(.separator())
        add(m, "Settings...", 20)
        add(m, "Open at login", 21, check: auto)
        add(m, hidden ? "Show pet" : "Hide pet", 22)
        m.addItem(.separator())
        add(m, "Quit PixelPet", 23)
    }

    func addPermissionItems(_ m: NSMenu) {
        let ax = AXIsProcessTrusted(), listen = CGPreflightListenEventAccess()
        add(m, ax ? "Accessibility: allowed" : "Allow Accessibility...", 80, gray: ax, check: ax)
        add(m, "    window titles, moving windows, closing apps", gray: true)
        add(m, listen ? "Input Monitoring: allowed" : "Allow Input Monitoring...", 81, gray: listen, check: listen)
        add(m, "    the typing laptop and the Return rope (keys are never recorded)", gray: true)
        add(m, "Browsers: macOS asks the first time I read a tab", gray: true)
    }

    func handle(_ cmd: Int) {
        if doClip(cmd) { return }
        let now = Date()
        switch cmd {
        case 10:
            if focusPhase == 0 { focusPhase = 1; focusEnd = now + Double(cfg.focus) * 60; say("Focus mode. I'm watching.", 2.5); joyT = 1.5 }
            else { focusPhase = 0; say("Timer stopped.", 1.5) }
        case 11:
            let snoozing = now < snoozeUntil
            snoozeUntil = snoozing ? .distantPast : now + Double(cfg.snooze) * 60
            say(snoozing ? "Back on patrol." : "Snoozing \(cfg.snooze) min.", 2)
        case 12: patrol.toggle(); say(patrol ? "On patrol." : "Off duty.", 2)
        case 13: if state == SLEEP { setState(SIT, 2) } else if state == SIT || state == WALK { setState(SLEEP, 45) }
        case 20: openFile(rulesPath); say("Saved changes apply within a second.", 3)
        case 21: _ = autoStart(!autoStart(nil))
        case 22: toggleHidden()
        case 23: NSApp.terminate(nil)
        case 24: loadReminders(); openFile(remPath)
        case 25: pauseRecurring.toggle(); say(pauseRecurring ? "Recurring reminders paused." : "Recurring reminders on.", 2)
        case 26:
            if orient != 0 { if state == CLIMB || state == HANG { letGo("Okay, okay.") } }
            else if perch != 0 { hopDown() }
            else if state == SIT || state == WALK || state == SLEEP { goClimb(); say("To the wall!", 1.5) }
        case 27: startSwing(menuOpenPt.x)
        case 30: reminderDone()
        case 31: reminderSnooze(now + 5 * 60)
        case 32: reminderSnooze(now + 15 * 60)
        case 33: reminderSnooze(now + 3600)
        case 34: reminderSnooze(atTime(addDays(now, 1), 9, 0))
        case 40...46: answer(cmd)
        case 60: systemCheck()
        case 61: openFile("/System/Applications/Utilities/Screenshot.app")
        case 62: openFile("/System/Applications/Utilities/Activity Monitor.app")
        case 63: openFile("/System/Applications/Calculator.app")
        case 64: openFile("/System/Applications/TextEdit.app")
        case 65: let p = Process(); p.executableURL = URL(fileURLWithPath: "/usr/bin/pmset"); p.arguments = ["displaysleepnow"]; try? p.run()
        case 66: openFile(memPath)
        case 70...75: connectAgent(cmd - 70)
        case 80:
            _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary)
            if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") { NSWorkspace.shared.open(u) }
        case 81:
            _ = CGRequestListenEventAccess()
            if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent") { NSWorkspace.shared.open(u) }
        default: break
        }
    }

    // ------------------------------------------------------------------ clipboard
    // Password managers mark their copies with the nspasteboard.org types; those are never read.
    func readClipboard(_ react: Bool) {
        if !cfg.clipboard { clipText = nil; return }
        let pb = NSPasteboard.general
        let types = pb.types ?? []
        let secret = types.contains(where: { ["org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType", "org.nspasteboard.AutoGeneratedType"].contains($0.rawValue) })
        var text: String? = nil
        if !secret, types.contains(.string), let s = pb.string(forType: .string), s.utf16.count <= 2000 { text = s }
        clipText = nil
        if react { clipOfferT = 0 }
        guard let t = text, !trimmed(t).isEmpty, !TextTools.looksSecret(t) else { return }
        clipText = t
        if !react || hidden || alert != nil { return }
        var useful = TextTools.parseWhen(t, Date()) != nil || TextTools.calc(t) != nil
        if !useful { useful = TextTools.cleanLink(t).1 > 0 }
        if !useful { return }
        clipOfferT = 8; clipNoticed = true                                       // click within 8 s for options; otherwise nothing happens
        part(x + facing * 3 * U, y - 14 * U, -14 * S, 1.6, "!")
        if !hintShown { hintShown = true; chirp("Click me for options", 2.5) }
    }

    func clipMenu(_ e: NSEvent?) {
        readClipboard(false)
        let m = newMenu(); addClipItems(m)
        popMenu(m, e)
        clipOfferT = 0
    }

    func addClipItems(_ m: NSMenu) {
        if !clipKeyText.isEmpty { add(m, "Shortcut: " + clipKeyText, gray: true) }
        guard let text = clipText else {
            add(m, cfg.clipboard ? "Nothing usable copied (or it looked private)" : "Clipboard features are off in rules.txt", gray: true)
            return
        }
        add(m, "\"" + firstLine(text, 40) + "\"", gray: true)
        add(m, "\(words(text)) words, \(text.count) chars", gray: true)
        m.addItem(.separator())
        if let w = TextTools.parseWhen(text, Date()) {
            clipWhen = w.0; clipMsg = w.1
            add(m, "Remind \"" + w.1 + "\" " + (cal.isDateInToday(w.0) ? "at " : "") + whenText(w.0), 100)
        }
        let later = newMenu()
        add(later, "In 10 min", 101); add(later, "In 30 min", 102); add(later, "In 1 hour", 103); add(later, "In 3 hours", 104); add(later, "Tomorrow 9:00", 105)
        sub(m, "Remind me about this", later)
        m.addItem(.separator())
        let removed = TextTools.cleanLink(text).1
        if removed > 0 { add(m, "Copy clean link (\(removed)" + (removed == 1 ? " tracker)" : " trackers)"), 110) }
        if let v = TextTools.calc(text) { add(m, "Copy result: " + num(v), 111) }
        add(m, "Tidy whitespace", 112)
        let cs = newMenu()
        add(cs, "UPPER CASE", 113); add(cs, "lower case", 114); add(cs, "Title Case", 115)
        sub(m, "Change case", cs)
    }

    func doClip(_ cmd: Int) -> Bool {
        if cmd < 100 || cmd > 115 { return false }
        guard let text = clipText else { return true }
        let now = Date(), about = firstLine(text, 60)
        switch cmd {
        case 100: addReminder(clipWhen, clipMsg)
        case 101: addReminder(now + 600, about)
        case 102: addReminder(now + 1800, about)
        case 103: addReminder(now + 3600, about)
        case 104: addReminder(now + 3 * 3600, about)
        case 105: addReminder(atTime(addDays(now, 1), 9, 0), about)
        case 110:
            let cl = TextTools.cleanLink(text)
            setClip(cl.0); say("Link cleaned. \(cl.1)" + (cl.1 == 1 ? " tracker gone." : " trackers gone."), 2.5)
        case 111: if let v = TextTools.calc(text) { setClip(num(v)); say("= " + num(v) + ". Copied.", 2.5) }
        case 112: setClip(TextTools.tidy(text)); say("Tidied.", 1.5)
        case 113: setClip(text.uppercased()); say("DONE.", 1.5)
        case 114: setClip(text.lowercased()); say("done.", 1.5)
        case 115: setClip(text.lowercased().capitalized); say("Done.", 1.5)
        default: break
        }
        joyT = 1; clipActs += 1; did(4)
        return true
    }

    func setClip(_ t: String) {
        let pb = NSPasteboard.general
        pb.clearContents(); pb.setString(t, forType: .string)
        ownCount = pb.changeCount; lastCount = ownCount                          // don't react to our own write
        clipText = t
    }

    func firstLine(_ s: String, _ limit: Int) -> String {
        var t = trimmed(s)
        if let nl = t.firstIndex(where: { $0.isNewline }) { t = String(t[..<nl]) }
        return t.count > limit ? String(t.prefix(limit)) + "..." : t
    }

    func words(_ s: String) -> Int { s.split(whereSeparator: { $0.isWhitespace }).count }

    func num(_ v: Double) -> String {
        if v == v.rounded() && abs(v) < 1e15 { return String(Int64(v)) }
        var s = String(format: "%.10f", v)
        while s.hasSuffix("0") { s.removeLast() }
        if s.hasSuffix(".") { s.removeLast() }
        return s
    }

    func whenText(_ t: Date) -> String { cal.isDateInToday(t) ? fmt(t, "HH:mm") : fmt(t, "EEE HH:mm") }

    // ------------------------------------------------------------------ reminders
    func loadReminders() {
        if !FileManager.default.fileExists(atPath: remPath) { try? defaultReminders.write(toFile: remPath, atomically: true, encoding: .utf8) }
        let st = mtime(remPath)
        if st == remStamp { return }
        guard let text = try? String(contentsOfFile: remPath, encoding: .utf8) else { return }   // mid-save: next second
        remStamp = st
        var list: [Reminder] = [], bad = 0
        let now = Date()
        for (i, raw) in text.components(separatedBy: "\n").enumerated() {
            let l = trimmed(raw)
            if l.isEmpty || l.hasPrefix("#") { continue }
            guard let r = parseReminder(l, now) else { if bad == 0 { bad = i + 1 }; continue }
            if let o = rems.first(where: { $0.line == r.line && $0.kind != "once" }) { r.next = o.next }   // saving must not restart running timers
            list.append(r)
        }
        rems = list
        if bad > 0 { say("reminders.txt line \(bad) isn't understood.", 3) }
    }

    func reminders() {
        loadReminders()
        let now = Date()
        if sign != nil {
            signT += 1
            if signT <= 300 && Int(signT) % 30 == 0 && state == SIT { hop(0, -480 * S) }
            if alert == nil && !bubbleSign && bubbleT <= 0 { showSign() }       // something else borrowed the bubble; take it back
            return
        }
        if alert != nil || state == HELD { return }
        let idle = idleSecs()
        let fullscreen = presenting()
        for r in rems {
            if now < r.next { continue }
            if r.kind == "every" {
                let mins = cal.component(.hour, from: now) * 60 + cal.component(.minute, from: now)
                let outside = r.from >= 0 && (r.from <= r.to ? (mins < r.from || mins >= r.to) : (mins < r.from && mins >= r.to))
                if pauseRecurring || idle > 300 || outside { r.next = now + Double(r.every) * 60; continue }
                if focusPhase == 1 { r.next = focusEnd + 3; continue }           // hold it for the break
            }
            if fullscreen { return }                                             // presentations and fullscreen video: later
            sign = r; signT = 0
            signText = (r.kind == "once" && now.timeIntervalSince(r.at) > 120 ? "Missed at " + fmt(r.at, "HH:mm") + ": " : "") + r.msg
            showSign()
            beep()
            if hidden { notify("Reminder", signText) }
            if state == SLEEP { setState(SIT, 2) }
            joyT = 1
            return
        }
    }

    func showSign() { say(signText + "\nclick = done  |  right-click = snooze", 99); bubbleSign = true }

    func findLive(_ r: Reminder) -> Reminder { rems.first(where: { $0.line == r.line }) ?? r }

    func reminderDone() {
        guard let r = sign else { return }
        sign = nil; bubbleT = 0; bubbleSign = false
        let now = Date()
        if r.kind == "once" { editReminderLine(r.line, nil) }
        else { findLive(r).next = r.kind == "every" ? now + Double(r.every) * 60 : nextDaily(r, now) }
        joyT = 1.5; hearts(3); say("Nice.", 1.5)
        if signT <= 120 { award(5) }
        remDone += 1; did(3)
    }

    func reminderSnooze(_ until: Date) {
        guard let r = sign else { return }
        sign = nil; bubbleSign = false
        if r.kind == "once", let bar = r.line.firstIndex(of: "|") { editReminderLine(r.line, fmt(until, "yyyy-MM-dd HH:mm") + " " + String(r.line[bar...])) }
        else { findLive(r).next = until }
        say("Okay, " + whenText(until) + ".", 2)
    }

    // Read-modify-write so edits you made in TextEdit since the last load survive.
    func editReminderLine(_ old: String, _ new: String?) {
        guard let text = try? String(contentsOfFile: remPath, encoding: .utf8) else { say("Couldn't update reminders.txt.", 2); return }
        var lines = text.components(separatedBy: "\n")
        guard let i = lines.firstIndex(where: { trimmed($0) == old }) else { return }
        if let n = new { lines[i] = n } else { lines.remove(at: i) }
        do {
            try lines.joined(separator: "\n").write(toFile: remPath, atomically: true, encoding: .utf8)
            remStamp = .distantPast; loadReminders()
        } catch { say("Couldn't update reminders.txt.", 2) }
    }

    func addReminder(_ when: Date, _ msg0: String) {
        var msg = trimmed(msg0.replacingOccurrences(of: "|", with: "/").replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " "))
        if msg.isEmpty { msg = "Reminder" }
        if msg.count > 120 { msg = String(msg.prefix(120)) }
        if !FileManager.default.fileExists(atPath: remPath) { try? defaultReminders.write(toFile: remPath, atomically: true, encoding: .utf8) }
        let existing = (try? String(contentsOfFile: remPath, encoding: .utf8)) ?? ""
        appendLine(remPath, (!existing.isEmpty && !existing.hasSuffix("\n") ? "\n" : "") + fmt(when, "yyyy-MM-dd HH:mm") + " | " + msg)
        remStamp = .distantPast; loadReminders()
        say("Got it. " + whenText(when) + " - " + msg, 3); joyT = 1.2
    }

    // A LaunchAgent that opens the app at login. No helper app, no permission prompt.
    var agentPlist: String { NSHomeDirectory() + "/Library/LaunchAgents/io.github.samuveljohnson1416.pixelpet.plist" }
    func autoStart(_ set: Bool?) -> Bool {
        let fm = FileManager.default
        if set == true, let exe = Bundle.main.executablePath {
            try? fm.createDirectory(atPath: (agentPlist as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            let plist: NSDictionary = ["Label": "io.github.samuveljohnson1416.pixelpet", "ProgramArguments": [exe], "RunAtLoad": true, "ProcessType": "Interactive"]
            _ = plist.write(toFile: agentPlist, atomically: true)
        } else if set == false { try? fm.removeItem(atPath: agentPlist) }
        return fm.fileExists(atPath: agentPlist)
    }

    // ------------------------------------------------------------------ the intervention
    func beginAlert(_ b0: Bust) {
        var b = b0
        b.scold = b.label.contains("Short") ? scolds[0] : scolds[1 + Int.random(in: 0..<(scolds.count - 1))]
        alert = b; bubbleT = 0; curiousLine = nil; askT = 0; webHops = 0
        if state == SWING { return }                                             // land() sees the alert and carries on from there
        if cfg.wander, let bx = b.centerX, abs(clamp(bx, wa.l + 8 * U, wa.r - 8 * U) - x) > 150 * S, startSwing(bx, true) { return }
        if orient != 0 { letGo(nil) }                                            // drops, then land() sends it running
        else if perch != 0 { hopDown() }
        else if state != AIR { setState(RUN, 0) }
    }

    func startGlare() {
        guard let a = alert else { setState(SIT, 1); return }
        if let c = a.centerX { facing = c > x ? 1 : -1 }
        setState(GLARE, 0); glareTick = 0
        if cfg.nag { say(a.label + "? Really?", 2.6); return }
        glareLeft = cfg.countdown
        if glareLeft <= 0 { setState(SWIPE, 0); acted = false }
        else { say(a.scold + "  \(glareLeft)s", 99) }
    }

    func endAlert(_ rest: Double) { alert = nil; if bubbleT > rest { bubbleT = rest }; setState(SIT, rest) }

    // Only acts if the same window is still in front: you may have switched back to real work meanwhile.
    // Returns nil when it closed the tab, or the line to say instead.
    func act(_ b: Bust) -> String? {
        guard let front = NSWorkspace.shared.frontmostApplication, front.processIdentifier == b.pid, frontWindow(b.pid)?.id ?? 0 == b.win else { return "You moved on. Good." }
        if AXIsProcessTrusted() {
            let now = stripBadge(focusedTitle(b.pid)), tail = String(stripBadge(b.title).suffix(40))
            if !now.isEmpty && !tail.isEmpty && !now.contains(tail) { return "The window moved on." }
        }
        if let k = browserKind(b.bundle) {
            if k != 3, runScript(tabScript(b.bundle, k, "close")) != nil { return nil }
            if postCmdW() { return nil }
            return "Let me close tabs: right-click me > Permissions."
        }
        if let w = focusedWindow(b.pid), let btn = axAttr(w, kAXCloseButtonAttribute), AXUIElementPerformAction(btn as! AXUIElement, kAXPressAction as CFString) == .success { return nil }
        return "Let me close it: right-click me > Permissions > Accessibility."
    }

    // Browsers without AppleScript tabs (Firefox and friends) get the same Cmd+W you would press.
    func postCmdW() -> Bool {
        guard AXIsProcessTrusted() else { return false }
        let src = CGEventSource(stateID: .combinedSessionState)
        guard let d = CGEvent(keyboardEventSource: src, virtualKey: 0x0D, keyDown: true), let u = CGEvent(keyboardEventSource: src, virtualKey: 0x0D, keyDown: false) else { return false }
        d.flags = .maskCommand; u.flags = .maskCommand
        d.post(tap: .cghidEventTap); u.post(tap: .cghidEventTap)
        return true
    }

    // ------------------------------------------------------------------ the patrol check, once a second
    func watch() {
        let st = mtime(rulesPath)
        if st != rulesStamp { rulesStamp = st; cfg = parseCfg(readLines(rulesPath)) }
        guard let app = NSWorkspace.shared.frontmostApplication, app.processIdentifier != getpid() else { return }
        let pid = app.processIdentifier, bundle = app.bundleIdentifier ?? "", name = app.localizedName ?? ""
        let win = frontWindow(pid), winId = win?.id ?? 0
        if pid != lastFgPid || winId != lastFgWin { lastFgPid = pid; lastFgWin = winId; pendingKey = "" }
        var title = AXIsProcessTrusted() ? focusedTitle(pid) : ""
        let isBrowser = browserKind(bundle) != nil
        let url = isBrowser ? readURL(bundle, title) : ""
        if title.isEmpty && !isBrowser { title = name }                         // without Accessibility the app's name still helps
        fgPid = pid; fgWin = winId; fgProc = name.lowercased(); fgTitle = title; fgUrl = url; fgBundle = bundle; fgFrame = win?.f
        let now = Date()
        let paused = !patrol || now < snoozeUntil || now < cooldownUntil
        guard !paused, let rule = matchRule(cfg, url, title, fgProc) else { matchId = ""; watchRemaining = -1; return }
        // the clock runs per rule+window: swiping to the next reel must not restart it
        let id = rule.label + "|\(pid)|\(winId)"
        if id != matchId { matchId = id; matchSince = now }
        let remaining = Double(rule.grace) - now.timeIntervalSince(matchSince)
        let center = win.map { ($0.f.l + $0.f.r) / 2 }
        if remaining > 0 { watchRemaining = remaining; watchX = center ?? x; return }
        watchRemaining = -1
        let key = "\(pid)|\(winId)|\(title)|\(url)"
        if key != pendingKey { pendingKey = key; pendingBust = Bust(pid: pid, win: winId, title: title, bundle: bundle, label: rule.label, centerX: center) }
    }

    func browserKind(_ bundle: String) -> Int? { chromium.contains(bundle) ? 1 : safari.contains(bundle) ? 2 : scriptless.contains(bundle) ? 3 : nil }

    func tabScript(_ bundle: String, _ kind: Int, _ verb: String) -> String {
        "with timeout of 2 seconds\ntell application id \"" + bundle + "\" to " + verb + " " + (kind == 2 ? "current tab" : "active tab") + " of front window\nend timeout"
    }

    func runScript(_ src: String) -> String? {
        let s: NSAppleScript
        if let c = scripts[src] { s = c } else { guard let n = NSAppleScript(source: src) else { return nil }; scripts[src] = n; s = n }
        var err: NSDictionary?
        let d = s.executeAndReturnError(&err)
        return err == nil ? (d.stringValue ?? "") : nil
    }

    // Titles never say "shorts" or "reels"; only the address does. macOS asks you once per browser.
    func readURL(_ bundle: String, _ title: String) -> String {
        let key = bundle + "|" + title
        if key == urlFor && Date().timeIntervalSince(urlReadAt) < 1.5 { return urlCache }
        urlFor = key; urlReadAt = Date()
        guard let k = browserKind(bundle), k != 3 else { urlCache = ""; return "" }
        urlCache = runScript(tabScript(bundle, k, "return URL of")) ?? ""
        return urlCache
    }

    // ------------------------------------------------------------------ Accessibility helpers
    func axAttr(_ el: AXUIElement, _ name: String) -> AnyObject? {
        var v: AnyObject?
        return AXUIElementCopyAttributeValue(el, name as CFString, &v) == .success ? v : nil
    }

    func focusedWindow(_ pid: pid_t) -> AXUIElement? {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.3)
        guard let w = axAttr(app, kAXFocusedWindowAttribute) else { return nil }
        return (w as! AXUIElement)
    }

    func focusedTitle(_ pid: pid_t) -> String { focusedWindow(pid).flatMap { axAttr($0, kAXTitleAttribute) as? String } ?? "" }

    func axFrame(_ el: AXUIElement) -> R? {
        guard let pv = axAttr(el, kAXPositionAttribute), let sv = axAttr(el, kAXSizeAttribute) else { return nil }
        var p = CGPoint.zero, s = CGSize.zero
        guard AXValueGetValue(pv as! AXValue, .cgPoint, &p), AXValueGetValue(sv as! AXValue, .cgSize, &s) else { return nil }
        return R(l: Double(p.x), t: Double(p.y), r: Double(p.x + s.width), b: Double(p.y + s.height))
    }

    func windowTitles(_ pid: pid_t) -> String {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.2)
        guard let list = axAttr(app, kAXWindowsAttribute) as? [AXUIElement] else { return "" }
        var s = ""
        for w in list {
            if let t = axAttr(w, kAXTitleAttribute) as? String { s += " " + t.lowercased() }
            if s.count > 4000 { break }
        }
        return s
    }

    func caretPoint() -> Pt? {
        let sys = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(sys, 0.2)
        guard let f = axAttr(sys, kAXFocusedUIElementAttribute) else { return nil }
        let el = f as! AXUIElement
        guard let range = axAttr(el, kAXSelectedTextRangeAttribute) else { return fieldPoint(el) }
        var b: AnyObject?
        guard AXUIElementCopyParameterizedAttributeValue(el, kAXBoundsForRangeParameterizedAttribute as CFString, range, &b) == .success, let bv = b else { return fieldPoint(el) }
        var r = CGRect.zero
        guard AXValueGetValue(bv as! AXValue, .cgRect, &r), r.height > 0 else { return fieldPoint(el) }
        return Pt(x: Double(r.minX), y: Double(r.midY))
    }

    // No caret to be had (many browsers and web apps): aim at the focused field itself, 200 pt into a long one.
    func fieldPoint(_ el: AXUIElement) -> Pt? {
        guard let f = axFrame(el), f.w >= 4, f.h >= 4, f.h <= 400 else { return nil }   // nothing, or a whole page
        return Pt(x: f.l + min(f.w / 2, 200), y: f.t + f.h / 2)
    }

    // ------------------------------------------------------------------ audio: headphones, music vs class
    // Core Audio, once a second: is the output headphones, and is anything playing? From macOS 14 it also says WHICH
    // apps are playing, and their window titles tell music from a lecture or a call.
    func audioWatch() {
        if !cfg.audio { headphones = false; audioKind = 0; audioRunning = false; return }
        audioSec += 1
        guard let dev = defaultOutput() else { headphones = false; audioDevice = ""; audioRunning = false; return }
        let name = stringProp(dev, kAudioObjectPropertyName) ?? ""
        let jack = u32Prop(dev, kAudioDevicePropertyDataSource, kAudioDevicePropertyScopeOutput) == fourCC("hdpn")
        headphones = jack || isHeadphones(name.lowercased())
        audioDevice = headphones ? (jack ? "Headphones" : name) : ""
        audioRunning = u32Prop(dev, kAudioDevicePropertyDeviceIsRunningSomewhere) == 1
        if audioSec % 2 == 0 {
            let kind = audioRunning ? classifyPlaying() : 0
            if kind == kindCandidate { kindSeen += 1 } else { kindCandidate = kind; kindSeen = 1 }
            if kindSeen >= 2 { audioKind = kindCandidate }                       // must hold for two checks: no flicker
        }
    }

    func defaultOutput() -> AudioObjectID? {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var id = AudioObjectID(0), size = UInt32(MemoryLayout<AudioObjectID>.size)
        let st = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &id)
        return st == noErr && id != 0 ? id : nil
    }

    func u32Prop(_ id: AudioObjectID, _ sel: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> UInt32? {
        var addr = AudioObjectPropertyAddress(mSelector: sel, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        var v: UInt32 = 0, size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &v) == noErr ? v : nil
    }

    func stringProp(_ id: AudioObjectID, _ sel: AudioObjectPropertySelector) -> String? {
        var addr = AudioObjectPropertyAddress(mSelector: sel, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var ref: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let st = withUnsafeMutablePointer(to: &ref) { AudioObjectGetPropertyData(id, &addr, 0, nil, &size, $0) }
        guard st == noErr, let r = ref else { return nil }
        return r.takeRetainedValue() as String
    }

    // The process list ('prs#'), each process's running-output flag ('piro'), PID ('ppid') and bundle id ('pbid')
    // arrived in macOS 14; on older systems the list is simply empty and the window in front is used instead.
    func classifyPlaying() -> Int {
        var texts: [String] = []
        let system = AudioObjectID(kAudioObjectSystemObject)
        var addr = AudioObjectPropertyAddress(mSelector: fourCC("prs#"), mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        if AudioObjectGetPropertyDataSize(system, &addr, 0, nil, &size) == noErr && size > 0 {
            var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
            if AudioObjectGetPropertyData(system, &addr, 0, nil, &size, &ids) == noErr {
                let apps = NSWorkspace.shared.runningApplications
                for p in ids where u32Prop(p, fourCC("piro")) == 1 {
                    let pid = pid_t(bitPattern: u32Prop(p, fourCC("ppid")) ?? 0)
                    let bid = stringProp(p, fourCC("pbid")) ?? ""
                    let owner = apps.first(where: { a -> Bool in                 // a browser plays sound from a helper process
                        if a.processIdentifier == pid { return true }
                        guard let b = a.bundleIdentifier, !b.isEmpty, !bid.isEmpty else { return false }
                        return bid.hasPrefix(b + ".") || (bid.hasPrefix("com.apple.WebKit") && b == "com.apple.Safari")
                    })
                    var text = (owner?.localizedName ?? bid).lowercased()
                    if let o = owner, AXIsProcessTrusted() { text += windowTitles(o.processIdentifier) }
                    texts.append(text)
                }
            }
        }
        if texts.isEmpty { texts.append(fgProc + " " + fgTitle.lowercased()) }
        var music = false
        for t in texts {
            let k = classify(t)
            if k == 2 { return 2 }
            if k == 1 { music = true }
        }
        return music ? 1 : 0
    }

    // ------------------------------------------------------------------ drawing
    func color(_ bgr: Int) -> NSColor {
        if let c = colors[bgr] { return c }
        let c = NSColor(srgbRed: CGFloat(bgr & 0xFF) / 255, green: CGFloat((bgr >> 8) & 0xFF) / 255, blue: CGFloat((bgr >> 16) & 0xFF) / 255, alpha: 1)
        colors[bgr] = c
        return c
    }

    func box(_ l: Double, _ t: Double, _ w: Double, _ h: Double, _ c: Int) {
        if w <= 0 || h <= 0 { return }
        color(c).setFill()
        NSRect(x: l, y: t, width: w, height: h).fill()
    }

    // One sprite cell: 14 columns x 9 rows, feet on row 9, squashed about the contact point (cx, by).
    // orient rotates the whole grid in 90-degree steps, so the pixel art stays crisp on walls and upside down.
    func px(_ col: Double, _ row: Double, _ w: Double, _ h: Double, _ c: Int) {
        let a0 = (col - 7) * U * sx, a1 = (col + w - 7) * U * sx                 // across the body
        let d0 = (9 - row) * U * sy, d1 = (9 - row - h) * U * sy                 // away from the surface
        var X0 = 0.0, X1 = 0.0, Y0 = 0.0, Y1 = 0.0
        switch orient {
        case 1: X0 = cx + d0; X1 = cx + d1; Y0 = by - a0; Y1 = by - a1           // left edge
        case 2: X0 = cx - d0; X1 = cx - d1; Y0 = by + a0; Y1 = by + a1           // right edge
        case 3: X0 = cx + a0; X1 = cx + a1; Y0 = by + d0; Y1 = by + d1           // top edge
        default: X0 = cx + a0; X1 = cx + a1; Y0 = by - d0; Y1 = by - d1          // floor
        }
        let l = min(X0, X1).rounded(), t = min(Y0, Y1).rounded()
        box(l, t, max(X0, X1).rounded() - l, max(Y0, Y1).rounded() - t, c)
    }

    func render() {
        guard let ctx = NSGraphicsContext.current else { return }
        if probeSecs > 0 && !drawnOnce { drawnOnce = true; mark("before first frame") }
        NSColor.clear.set(); NSRect(x: 0, y: 0, width: W, height: H).fill(using: .copy)
        if hidden { return }
        ctx.shouldAntialias = false
        cx = x - winLeft; by = y - winTop
        let k = sqT > 0 ? sqK * sqT / 0.25 : 0
        sx = 1 + 0.16 * k; sy = 1 - 0.22 * k
        if state == HELD { sx = 0.94; sy = 1.06 }
        let vibing = lastKind == 1 && grooving && (state == SIT || state == TYPE) && danceLevel > 0.04
        if vibing { let d = min(1, danceLevel * 1.6); sx *= 1 + 0.07 * d; sy *= 1 - 0.11 * d }
        let reach = stretchT > 0 && orient == 0 && state == SIT ? min(1, min((5 - stretchT) / 0.6, stretchT / 0.6)) : 0   // stretch: eases up, holds, eases down
        if reach > 0 { sx *= 1 - 0.06 * reach; sy *= 1 + 0.16 * reach }
        let drinking = drinkT > 0 && state == SIT && orient == 0 && alert == nil, clock = clockT > 0 && state == SIT && orient == 0 && alert == nil
        let phonesOn = wasPhones || (cfg.audio && lastKind == 1)                // its own headset while the music plays

        let sleeping = state == SLEEP || hangSleep
        let angry = angryT > 0 || state == RUN || state == GLARE || state == SWIPE || (state == SWING && alert != nil)
        let joy = (joyT > 0 || (vibing && state == SIT) || state == SWING) && !angry
        let body = angry ? ANGRY : onAC && chargeT > 2.3 && Int(animT * 10) % 2 == 0 ? STAR : CORAL   // zap flash on plug-in
        let typing = state == TYPE
        let dy: Double = state == SLEEP || typing ? 2 : 0

        if dy == 0 {
            let walking = state == WALK || state == RUN || state == CLIMB || state == SWING || state == PUSH
            let phase = walking ? Int(animT * (state == RUN ? 14 : 8)) % 2 : -1
            for i in 0..<4 { px(legs[i], 7, 1, state == HELD ? 3 : (i % 2 == phase ? 1 : 2), body) }
        }
        px(2, dy, 10, 7, body)

        var up = joy || state == HELD || scaredT > 0 || (sign != nil && state != TYPE)   // holding up the reminder sign
        let swipeRaised = state == SWIPE && stateT < 0.25, swipeStrike = state == SWIPE && stateT >= 0.25
        let roping = ropeT >= 0, swinging = state == SWING, pushing = state == PUSH
        let reaching = swipeRaised || swipeStrike || roping || swinging
        var leftBusy = pushing || (facing < 0 && reaching), rightBusy = pushing || (facing > 0 && reaching)
        if typing && lapOpen > 0 {
            let off = (1 - lapOpen) * 3.5                                        // the laptop rises out of the ground
            let tapping = lastKeyAgo < 0.4 && lapOpen >= 1
            let ph = Int(animT * 12) % 2
            px(3.5, 5.5 + off, 7, 3, LID)
            px(2, 8.4 + off, 10, 0.6, BASE)
            px(6.5, 6.5 + off, 1, 1, tapping && Int(animT * 6) % 2 == 0 ? GLOW : LOGO)
            if !(roping && facing < 0) { px(1.5, 6.5 + (tapping && ph == 0 ? 0.5 : 0), 2.5, 1.5, body) }
            if !(roping && facing > 0) { px(10, 6.5 + (tapping && ph == 1 ? 0.5 : 0), 2.5, 1.5, body) }
            up = false; leftBusy = true; rightBusy = true
        }
        var leftUp = up, rightUp = up
        if state == CLIMB { leftUp = Int(animT * 8) % 2 == 0; rightUp = !leftUp }                     // paw over paw
        if vibing && joyT <= 0 && state == SIT { leftUp = Int(animT * 2.5) % 2 == 0; rightUp = !leftUp }  // arms sway to the music
        let notes = lastKind == 2 && state == SIT && alert == nil
        if notes { leftBusy = true }                                             // left paw holds the notepad
        if reach > 0 {                                                          // both paws reaching for the ceiling, one then the other
            leftBusy = true; rightBusy = true
            let alt = Int(animT * 1.5) % 2 == 0
            px(0.6, alt ? -2.6 : -2, 1.6, 3.4, body); px(11.8, alt ? -2 : -2.6, 1.6, 3.4, body)
        }
        if drinking || clock { if facing > 0 { rightBusy = true } else { leftBusy = true } }
        if drinking {                                                           // a glass of water: raised, sipped, lowered
            let lift = clamp(min((4.5 - drinkT) / 0.5, drinkT / 0.5), 0, 1), level = clamp(drinkT / 4, 0.2, 1)
            let gx = facing > 0 ? 10.2 : 1.4, gy = 4.6 - 1.8 * lift
            px(facing > 0 ? 11.8 : 0.2, gy + 1.2, 2, 1.4, body)
            px(gx, gy, 2.4, 3, WHITE); px(gx + 0.3, gy + 0.3 + 2.4 * (1 - level), 1.8, 2.4 * level, CUP)
        }
        if clock {                                                              // holds up a little clock: screen time
            let kx = facing > 0 ? 11.4 : 0.2
            px(facing > 0 ? 12 : 0, 4.2 + dy, 2, 1.6, body)
            px(kx, 2.2 + dy, 2.4, 2.4, WHITE); px(kx + 1.05, 2.6 + dy, 0.3, 1.1, DARK); px(kx + 1.05, 3.4 + dy, 0.9, 0.3, DARK)
        }
        if !leftBusy { px(0, leftUp ? 1 : 4 + dy, 2, 2, body) }
        if !rightBusy { px(12, rightUp ? 1 : 4 + dy, 2, 2, body) }
        if notes {
            let wig = sin(animT * 9) * 0.6
            px(0.5, 4.6, 4.4, 3.8, PAPER); px(1, 5.4, 3.2, 0.3, INK); px(1, 6.3, 2.6, 0.3, INK); px(1, 7.2, 3, 0.3, INK)
            px(2.6 + wig, 3.9, 0.6, 2.2, STAR); px(2.6 + wig, 6.1, 0.6, 0.5, DARK)   // pencil scribbling
        }
        if swipeRaised { px(facing > 0 ? 12 : 0, 0, 2, 3, body) }
        if roping || swinging { px(facing > 0 ? 12 : 0, dy, 2, 3, body) }
        if pushing {                                                             // both paws on the window, shoving
            let shove = Int(animT * 4) % 2 == 0 ? 0.0 : 0.3
            px(facing > 0 ? 11.5 + shove : -0.5 - shove, 3.4 + dy, 3, 1.4, body)
            px(facing > 0 ? 11 + shove : 0 - shove, 5.1 + dy, 3, 1.4, body)
        }
        if swipeStrike { px(facing > 0 ? 12 : -2, 3, 4, 2, body) }
        if state == SIT && alert == nil && sign == nil && orient == 0 && movieOn {   // popcorn in one paw, a drink in the other
            px(4.5, 4.8, 5, 3.2, ANGRY); px(5.6, 4.8, 0.8, 3.2, WHITE); px(7.6, 4.8, 0.8, 3.2, WHITE)   // striped bucket
            px(5, 4.1, 1.1, 1, WHITE); px(6.3, 3.8, 1.3, 1.2, STAR); px(7.6, 4.1, 1.1, 1, WHITE)        // kernels heaped on top
            let munching = snackKind == 1, sipping = snackKind == 2
            let cupTop = sipping ? 4.2 : 4.7, cupH = 3.4 - (sipping ? 0.5 : 0)   // the cup lifts a little to drink
            px(0.5, cupTop + 0.4, 2.8, cupH, WHITE)
            px(1.3, cupTop + 0.4, 0.5, cupH, ANGRY); px(2.3, cupTop + 0.4, 0.5, cupH, ANGRY)
            px(0.3, cupTop, 3.2, 0.5, DARK)                                      // lid
            px(2.2, cupTop - 2.2, 0.45, 2.3, STAR)                               // straw
            px(2.2, cupTop - 2.2, sipping ? 1.7 : 1.1, 0.45, STAR)               // bent toward the mouth while sipping
            if sipping && Int(animT * 6) % 2 == 0 { px(3.9, cupTop - 2.4, 0.5, 0.5, WHITE) }
            let paw = munching ? (animT * 2.5).truncatingRemainder(dividingBy: 1) : 0   // dips into the bucket and back up
            px(3.3, munching ? 4.4 - paw * 1.6 : 4.6, 1.7, 1.5, body)
            if munching && paw > 0.75 { px(4.6, 3.2, 0.6, 0.6, STAR) }           // a kernel on the way to its mouth
        }

        if sleeping || reach > 0.5 || drinking || (blinkOn > 0 && !angry && !joy) { px(3.8, 3 + dy, 1.4, 0.5, EYE); px(8.8, 3 + dy, 1.4, 0.5, EYE) }
        else if joy {
            px(3, 3 + dy, 1, 1, EYE); px(4, 2 + dy, 1, 1, EYE); px(5, 3 + dy, 1, 1, EYE)
            px(8, 3 + dy, 1, 1, EYE); px(9, 2 + dy, 1, 1, EYE); px(10, 3 + dy, 1, 1, EYE)
        } else if angry {
            px(3.5 + lookX, 2 + dy, 1, 1, EYE); px(4.5 + lookX, 3 + dy, 1, 1, EYE); px(9.5 + lookX, 2 + dy, 1, 1, EYE); px(8.5 + lookX, 3 + dy, 1, 1, EYE)
        } else { px(4 + lookX, 2 + lookY + dy, 1, 2, EYE); px(9 + lookX, 2 + lookY + dy, 1, 2, EYE) }

        let outfit = costume()
        drawRank(dy, phonesOn || outfit != 0)
        drawCostume(dy, body, outfit)
        drawTraits(dy)
        if phonesOn {
            px(2.4, dy - 1.2, 9.2, 0.7, DARK)                                    // band over the head
            px(1.7, dy - 0.8, 0.8, 1.8, DARK); px(11.5, dy - 0.8, 0.8, 1.8, DARK)
            px(1, dy + 0.8, 2, 2.6, CUP); px(11, dy + 0.8, 2, 2.6, CUP)          // ear cups
            px(1.4, dy + 1.3, 0.8, 1.6, CUPDARK); px(11.8, dy + 1.3, 0.8, 1.6, CUPDARK)
            if lastKind == 2 { px(12.2, dy + 3.3, 0.5, 1.4, DARK); px(9.4, dy + 4.4, 3.3, 0.5, DARK); px(8.7, dy + 4.1, 1, 1.1, DARK) }   // mic boom
        }

        sx = 1; sy = 1
        if chargeT > 0 && onAC && dy == 0 && orient == 0 { px(-9, 8.4, 8, 0.6, DARK); px(-1.6, 7.6, 1.8, 1.5, INK); px(0.2, 7.9, 0.7, 0.3, STAR); px(0.2, 8.5, 0.7, 0.3, STAR) }   // plugged in
        if chargeT > 0 { drawBattery() }
        let hp = max(2, (U / 2).rounded(.down))
        for p in parts where p.text == nil {                                     // pixel hearts
            let qx = (p.x - winLeft).rounded(), qy = (p.y - winTop).rounded()
            box(qx - 2 * hp, qy, hp, hp, PINK); box(qx, qy, hp, hp, PINK)
            box(qx - 2 * hp, qy + hp, 3 * hp, hp, PINK); box(qx - hp, qy + 2 * hp, hp, hp, PINK)
            box(qx - 3 * hp, qy + hp, hp, hp, PINK); box(qx + hp, qy + hp, hp, hp, PINK)
        }
        ctx.shouldAntialias = true
        for p in parts { if let t = p.text { outlined(t, (p.x - winLeft).rounded(), (p.y - winTop).rounded()) } }

        let top = orient == 3 ? H - 2 : orient != 0 ? by - 8 * U : by - (dy > 0 ? 10 : 12) * U - 6 * S
        if bubbleT > 0, let b = bubble { pill(b, top, bubbleSign ? SIGN : WHITE, DARK) }
        else if let t = tagCache { pill(t, top, DARK, WHITE) }
        if probeSecs > 0 && !frameMarked { frameMarked = true; mark("after first frame") }
    }

    func pill(_ text: String, _ bottom: Double, _ fillC: Int, _ ink: Int) {
        let padX = 9 * S, padY = 5 * S
        let para = NSMutableParagraphStyle(); para.alignment = .center
        let s = NSAttributedString(string: text, attributes: [.font: bubbleFont, .foregroundColor: color(ink), .paragraphStyle: para])
        let r = s.boundingRect(with: NSSize(width: W - 24 * S, height: 1000), options: [.usesLineFragmentOrigin, .usesFontLeading])
        let tw = Double(r.width).rounded(.up), th = Double(r.height).rounded(.up)
        let bw = tw + 2 * padX, bh = th + 2 * padY
        let bx = clamp((cx - bw / 2).rounded(), 1, W - bw - 1), bt = max(1, bottom - bh)
        let shape = NSBezierPath(roundedRect: NSRect(x: bx, y: bt, width: bw, height: bh), xRadius: 6 * S, yRadius: 6 * S)
        color(fillC).setFill(); shape.fill()
        color(DARK).setStroke(); shape.lineWidth = 2 * S; shape.stroke()
        s.draw(with: NSRect(x: bx + padX, y: bt + padY, width: tw, height: th), options: [.usesLineFragmentOrigin, .usesFontLeading])
    }

    func outlined(_ s: String, _ qx: Double, _ qy: Double) {
        let ns = s as NSString
        let dark: [NSAttributedString.Key: Any] = [.font: smallFont, .foregroundColor: color(DARK)]
        for (ox, oy) in [(-1.0, 0.0), (1.0, 0.0), (0.0, -1.0), (0.0, 1.0)] { ns.draw(at: NSPoint(x: qx + ox, y: qy + oy), withAttributes: dark) }
        ns.draw(at: NSPoint(x: qx, y: qy), withAttributes: [.font: smallFont, .foregroundColor: color(WHITE)])
    }

    func petImage(_ size: Double) -> NSImage {
        let grid = ["..XXXXXXXXXX..", "..XXXXXXXXXX..", "..XXEXXXXEXX..", "..XXEXXXXEXX..", "XXXXXXXXXXXXXX", "XXXXXXXXXXXXXX", "..XXXXXXXXXX..", "...X.X..X.X...", "...X.X..X.X..."]
        let coral = color(CORAL), eye = color(EYE)
        return NSImage(size: NSSize(width: size, height: size), flipped: true) { _ in
            let k = size / 14, oy = (size - 9 * k) / 2
            for (j, row) in grid.enumerated() {
                for (i, ch) in row.enumerated() where ch != "." {
                    (ch == "E" ? eye : coral).setFill()
                    NSRect(x: Double(i) * k, y: oy + Double(j) * k, width: k, height: k).fill()
                }
            }
            return true
        }
    }

    func beep() { NSSound.beep() }

    // Shown only while the pet is hidden, the way the Windows build uses a tray balloon.
    func notify(_ title: String, _ body: String) {
        if probeSecs > 0 { return }
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
            guard granted else { return }
            let n = UNMutableNotificationContent(); n.title = title; n.body = body
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: n, trigger: nil)) { _ in }
        }
    }

    // ------------------------------------------------------------------ CI probe: PixelPetFocus --probe <seconds>
    // Runs the real pet on the build machine's screen, then prints what it measured as GitHub annotations:
    // memory, whether its window is on screen, an ASCII picture of what it drew, and whether a hook and a copy reached it.
    func mark(_ label: String) { if probeSecs > 0 { marks.append(label + " " + String(format: "%.1f", footprintMB())) } }

    func probe() {
        probeElapsed += 1
        if probeElapsed == 2 { mark("first second drawn") }
        if probeElapsed == 12 { startSwing(x < (wa.l + wa.r) / 2 ? wa.r - 100 : wa.l + 100) }
        if probeElapsed == 15 { mark("after a web-swing (" + (state == SWING ? "swinging" : "landed") + ")") }
        if probeElapsed == 16 { toggleHidden(); mark("hidden with menu bar icon") }
        if probeElapsed == 17 { toggleHidden(); mark("shown again") }
        if probeElapsed == 10 {
            let mine = ((CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]]) ?? []).filter { ($0[kCGWindowOwnerPID as String] as? Int32) == getpid() }
            print("::notice title=Mac memory::PixelPet Focus uses " + String(format: "%.1f", footprintMB()) + " MB (phys_footprint, the Activity Monitor number)")
            print("::notice title=Mac window::\(mine.count) window(s) on screen. Pet at x=\(Int(x)) y=\(Int(y)), floor y=\(Int(wa.b)), screen width \(Int(wa.r)), state \(state)")
            print("::notice title=Mac memory by stage (MB)::" + marks.joined(separator: ", "))
            print("::notice title=Mac sprite::" + spriteAscii().joined(separator: "%0A"))
        }
        if probeElapsed >= Int(probeSecs) {
            let tag = agentTag() ?? "none"
            print("::notice title=Mac hook and clipboard::agent tag: \(tag), sessions: \(agents.count). Clipboard offer: " + (clipNoticed ? "yes" : "no") + ", clipText: " + (clipText ?? "nil"))
            mark("at exit")
            print("::notice title=Mac memory, full run (MB)::" + marks.joined(separator: ", "))
            print("::notice title=Mac memory at exit::" + String(format: "%.1f", footprintMB()) + " MB after \(probeElapsed) s")
            exit(0)
        }
    }

    func spriteAscii() -> [String] {
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return ["no bitmap"] }
        view.cacheDisplay(in: view.bounds, to: rep)
        let scale = Double(rep.pixelsWide) / W
        var out: [String] = []
        var row = by - 13 * U + U / 2
        while row < by + U {
            var line = ""
            var col = cx - 9 * U + U / 2
            while col < cx + 9 * U {
                let qx = Int(col * scale), qy = Int(row * scale)
                line += qx < 0 || qy < 0 || qx >= rep.pixelsWide || qy >= rep.pixelsHigh ? " " : glyph(rep.colorAt(x: qx, y: qy))
                col += U
            }
            out.append(line)
            row += U
        }
        return out
    }

    func glyph(_ c0: NSColor?) -> String {
        guard let c = c0?.usingColorSpace(.sRGB), c.alphaComponent > 0.5 else { return "." }
        let r = Int(c.redComponent * 255), g = Int(c.greenComponent * 255), b = Int(c.blueComponent * 255)
        func near(_ bgr: Int) -> Bool { abs(r - (bgr & 0xFF)) + abs(g - ((bgr >> 8) & 0xFF)) + abs(b - ((bgr >> 16) & 0xFF)) < 60 }
        if near(CORAL) || near(ANGRY) { return "#" }
        if near(EYE) || near(DARK) { return "@" }
        if near(WHITE) || near(PAPER) { return "o" }
        return "+"
    }
}
