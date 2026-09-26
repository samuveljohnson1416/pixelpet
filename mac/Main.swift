// Entry point. Self-test and agent-hook runs return before AppKit starts, so a hook costs milliseconds.
import AppKit

@main
enum PixelPetMain {
    @MainActor static func main() {
        let args = CommandLine.arguments
        if args.count > 1 && args[1] == "--selftest" { exit(Int32(runSelfTest())) }
        if args.count > 3 && args[1] == "--agent-hook" { forwardHook(args[2], args[3]); return }
        if args.count > 1 && args[1] == "--claude-hook" { forwardHook("claude", ""); return }   // hooks installed by older builds
        let id = Bundle.main.bundleIdentifier ?? "io.github.samuveljohnson1416.pixelpet"
        if NSRunningApplication.runningApplications(withBundleIdentifier: id).contains(where: { $0.processIdentifier != getpid() }) {
            DistributedNotificationCenter.default().postNotificationName(openNote, object: nil, userInfo: nil, deliverImmediately: true)
            return                                                               // already running: that one waves instead
        }
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)                                      // no Dock icon; the menu bar icon is the tray
        let pet = Pet()
        if args.count > 2 && args[1] == "--probe" { pet.probeSecs = Double(args[2]) ?? 20 }
        Pet.shared = pet
        app.delegate = pet
        app.run()
    }
}
