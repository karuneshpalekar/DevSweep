import AppKit
import DevSweepCore
import ServiceManagement
import UserNotifications

/// Keeps DevSweep alive in the menu bar after its window closes, so scheduled
/// scans can run.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// Clicking the Dock icon or opening the app again brings the window back.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { true }
}

/// "Open at login", through macOS's own login-item service.
enum LoginItem {
    enum State { case on, off, needsApproval, unavailable }

    static var state: State {
        switch SMAppService.mainApp.status {
        case .enabled: return .on
        case .notRegistered: return .off
        case .requiresApproval: return .needsApproval
        default: return .unavailable
        }
    }

    static func set(_ on: Bool) throws {
        if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
    }

    static func openSettings() { SMAppService.openSystemSettingsLoginItems() }
}

/// Notifications for alerts. Only works for a real app bundle, so a plain
/// command-line run of the binary skips them instead of crashing.
@MainActor
enum Notifier {
    static var available: Bool { Bundle.main.bundleURL.pathExtension == "app" }
    private static let delegate = NotificationDelegate()

    static func install(onOpen: @escaping @MainActor (AlertTarget) -> Void) {
        guard available else { return }
        delegate.onOpen = onOpen
        UNUserNotificationCenter.current().delegate = delegate
    }

    static func status() async -> UNAuthorizationStatus {
        guard available else { return .denied }
        return await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
    }

    /// Asks macOS for permission the first time; afterwards just reports the answer.
    @discardableResult
    static func requestPermission() async -> Bool {
        guard available else { return false }
        let granted = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])
        return granted ?? false
    }

    static func send(_ event: AlertEvent) async {
        await send(title: event.title, body: event.body, target: event.target)
    }

    static func send(title: String, body: String, target: AlertTarget) async {
        guard available else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        content.userInfo = ["target": target.rawValue]
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        try? await UNUserNotificationCenter.current().add(request)
    }

    static func openNotificationSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension") {
            NSWorkspace.shared.open(url)
        }
    }
}

final class NotificationDelegate: NSObject, UNUserNotificationCenterDelegate {
    @MainActor var onOpen: (@MainActor (AlertTarget) -> Void)?

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        guard let raw = response.notification.request.content.userInfo["target"] as? String,
              let target = AlertTarget(rawValue: raw) else { return }
        await MainActor.run { onOpen?(target) }
    }

    /// Show the banner even when DevSweep is the frontmost app.
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async
        -> UNNotificationPresentationOptions { [.banner, .sound] }
}
