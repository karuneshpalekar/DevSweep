import Foundation

extension RuntimeStep {
    /// A zsh script that lists what will run and waits for Return before
    /// doing anything, then runs the step. Opened in Terminal by the app.
    public var terminalScript: String {
        func quote(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: #"'\''"#) + "'" }
        var lines = ["#!/bin/zsh -l", "clear", "echo \(quote("DevSweep: \(title)"))", "echo"]
        if kind == .guided {
            lines += ["echo 'This runs a guided script. It stops and asks before every change.'"]
        } else {
            lines += ["echo 'These commands will run:'"]
            lines += commands.map { "echo \(quote("  $ \($0)"))" }
        }
        if needsAdmin { lines += ["echo", "echo 'Some commands need your Mac password (sudo).'"] }
        lines += ["echo", #"read "?Press Return to continue, or close this window to cancel. ""#, "echo"]
        if kind == .guided {
            lines += commands
        } else {
            lines += ["set -e"]
            for c in commands { lines += ["echo \(quote("$ \(c)"))", c] }
        }
        lines += ["echo", "echo 'Finished. You can close this window, then scan again in DevSweep.'"]
        return lines.joined(separator: "\n") + "\n"
    }
}
