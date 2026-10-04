import SwiftUI

@main
@MainActor
struct DevSweepApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = AppModel()

    var body: some Scene {
        WindowGroup("DevSweep", id: "main") {
            ContentView()
                .environment(model)
                .frame(minWidth: 1040, minHeight: 640)
                .onAppear { AppearanceMode.current.apply() }
        }
        .defaultSize(width: 1280, height: 800)

        Settings {
            SettingsView()
                .environment(model)
        }

        MenuBarExtra {
            MenuBarView()
                .environment(model)
                .onAppear { AppearanceMode.current.apply() }
        } label: {
            Image(systemName: "wand.and.stars")
        }
        .menuBarExtraStyle(.window)
    }
}
