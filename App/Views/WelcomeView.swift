import SwiftUI

/// Shown once, before the first scan, so the permission prompts macOS
/// shows afterwards make sense.
@MainActor
struct WelcomeView: View {
    @Environment(AppModel.self) private var model
    @State private var giveFullAccess = true

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            HStack(spacing: 12) {
                Image(systemName: "wand.and.stars").font(.system(size: 30)).foregroundStyle(Color.accentColor)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Welcome to DevSweep").font(.title2.weight(.semibold))
                    Text("Before the first scan, here's exactly what it does.").foregroundStyle(.secondary)
                }
            }
            HStack(alignment: .top, spacing: 22) {
                column("It looks at", [
                    "Caches and settings in your Library folder",
                    "Developer tool folders like ~/.npm and ~/.gradle",
                    "Project folders you choose",
                    "Which apps and tools are installed",
                ])
                column("It never", [
                    "Changes anything without your review",
                    "Reads the contents of your documents",
                    "Sends anything about your Mac anywhere",
                    "Asks for your password to scan",
                ])
                column("macOS may ask", [
                    "To access Downloads, Documents or Desktop, for project folders",
                    "To access data from other apps, for leftovers",
                ])
            }
            VStack(spacing: 10) {
                option(true, "Give Full Disk Access", "Recommended. One switch in System Settings, then no more prompts and nothing is missed.")
                option(false, "Continue without it", "macOS asks for each folder, and a few leftovers may be missed. You can change this later in Settings.")
            }
            Spacer(minLength: 0)
            HStack {
                Text("Project folders can be changed in Settings.").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button(giveFullAccess ? "Open System Settings" : "Start scanning") {
                    if giveFullAccess { FullDiskAccess.openSettings() }
                    model.finishWelcome()
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(.horizontal, 40).padding(.top, 36).padding(.bottom, 28)
        .frame(width: 760, height: 500)
        .interactiveDismissDisabled()
    }

    private func column(_ title: String, _ items: [String]) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).fontWeight(.semibold)
            ForEach(items, id: \.self) { item in
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text("•").foregroundStyle(.secondary)
                    Text(item).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                .font(.callout)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func option(_ value: Bool, _ title: String, _ detail: String) -> some View {
        let selected = giveFullAccess == value
        return Button { giveFullAccess = value } label: {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .foregroundStyle(selected ? Color.accentColor : .secondary)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).fontWeight(.semibold)
                    Text(detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
            .padding(12)
            .contentShape(Rectangle())
            .background(selected ? Color.accentColor.opacity(0.07) : .clear, in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(selected ? Color.accentColor : Color.secondary.opacity(0.25)))
        }
        .buttonStyle(.plain)
    }
}
