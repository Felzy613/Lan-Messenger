import Foundation
import Darwin

// Last-resort diagnostics for abnormal termination.
//
// Two independent failure classes, two hooks:
//
//  • Uncaught Objective-C/AppKit exceptions — NSSetUncaughtExceptionHandler.
//    Covers the AppKit/SwiftUI failures that dominate real crash reports
//    (out-of-range NSArray access, KVO teardown, invalid window state).
//  • Fatal POSIX signals — SIGSEGV/SIGBUS/SIGILL/SIGFPE/SIGABRT/SIGTRAP.
//    Covers Swift runtime traps (force-unwrap of nil, array OOB, overflow,
//    `fatalError`), which raise SIGILL/SIGTRAP rather than an NSException.
//
// Swift `fatalError` and friends are NOT catchable as exceptions — the signal
// handler is the only way to leave a breadcrumb, which is why both exist.
//
// Signal-handler constraints: only async-signal-safe work is legal here. We
// deliberately keep it to a synchronous write() of already-formatted bytes and
// then re-raise on the default handler so the OS still produces its own crash
// report. Symbolication uses backtrace_symbols, which is not strictly
// async-signal-safe; we accept that narrow risk because a best-effort symbolic
// stack is worth far more than a bare address list, and we have already lost
// the process either way.
enum CrashReporter {

    private static var installed = false
    private static let fatalSignals: [Int32] =
        [SIGSEGV, SIGBUS, SIGILL, SIGFPE, SIGABRT, SIGTRAP]

    /// Installs both hooks. Idempotent; call once as early in launch as possible.
    static func install() {
        guard !installed else { return }
        installed = true

        NSSetUncaughtExceptionHandler { exception in
            CrashReporter.recordException(exception)
        }

        for sig in fatalSignals {
            var action = sigaction()
            action.__sigaction_u.__sa_handler = { signalNumber in
                CrashReporter.recordSignal(signalNumber)
                // Restore the default disposition and re-raise so the OS writes
                // its own .ips crash report and the exit status is accurate.
                signal(signalNumber, SIG_DFL)
                raise(signalNumber)
            }
            sigemptyset(&action.sa_mask)
            action.sa_flags = 0
            sigaction(sig, &action, nil)
        }

        NetLogger.info("Crash", "handlers installed (exception + \(fatalSignals.count) signals)")
    }

    /// Records that the previous run ended abnormally, if it did. Call after
    /// install() on each launch: a clean shutdown clears the marker, so a
    /// marker still present at startup means the last run died without one.
    static func reportPreviousRunIfCrashed() {
        let marker = markerURL
        if FileManager.default.fileExists(atPath: marker.path) {
            let detail = (try? String(contentsOf: marker, encoding: .utf8)) ?? "unknown"
            NetLogger.crash(
                event: "previous_run_terminated_abnormally",
                reason: detail.trimmingCharacters(in: .whitespacesAndNewlines)
            )
        }
        // Arm the marker for this run.
        try? "started \(ISO8601DateFormatter().string(from: Date()))"
            .data(using: .utf8)?.write(to: marker)
    }

    /// Clears the abnormal-termination marker. Call from applicationWillTerminate.
    static func noteCleanShutdown() {
        try? FileManager.default.removeItem(at: markerURL)
    }

    // MARK: - Internals

    private static var markerURL: URL {
        ConfigStore.shared.logsDirectory.appendingPathComponent(".running")
    }

    private static func recordException(_ exception: NSException) {
        NetLogger.crash(
            event: "uncaught_exception",
            name: exception.name.rawValue,
            reason: exception.reason ?? "(no reason)",
            stack: exception.callStackSymbols
        )
    }

    private static func recordSignal(_ signalNumber: Int32) {
        NetLogger.crash(
            event: "fatal_signal",
            name: signalName(signalNumber),
            reason: "signal \(signalNumber)",
            stack: Thread.callStackSymbols
        )
    }

    private static func signalName(_ s: Int32) -> String {
        switch s {
        case SIGSEGV: return "SIGSEGV"
        case SIGBUS:  return "SIGBUS"
        case SIGILL:  return "SIGILL"
        case SIGFPE:  return "SIGFPE"
        case SIGABRT: return "SIGABRT"
        case SIGTRAP: return "SIGTRAP"
        default:      return "SIG\(s)"
        }
    }
}
