import XCTest
import SwiftUI
import AppKit
@testable import LanMessenger

// The two-stage grant, and what the prompt that opens it says.
//
// PROTOCOL.md makes the staging a protocol requirement rather than an interface
// nicety: accepting an invite grants viewing, and control is a separate prompt.
// The two are wildly different in consequence and identical in how they arrive,
// so a single "accept" that quietly includes keyboard and mouse is how a
// screen-share feature becomes a remote-access one without anybody deciding to
// build that.
final class RemoteConsentTests: XCTestCase {

    private let sessionID = "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e"
    private let realKey = Data(repeating: 7, count: 32).base64EncodedString()

    private func request(kind: RemoteConsentRequest.Kind = .viewing,
                         trust: PeerKeyTrust = .pinned(username: "Dave"),
                         key: String? = nil,
                         expiresIn: TimeInterval = 45,
                         canInject: Bool = true,
                         from origin: Date = Date(timeIntervalSince1970: 1_000_000))
    -> RemoteConsentRequest {
        RemoteConsentRequest(
            sessionID: sessionID, kind: kind, peerName: "Dave", peerIP: "10.0.0.5",
            peerPublicKeyB64: key ?? realKey, trust: trust,
            expiresAt: origin.addingTimeInterval(expiresIn),
            canInject: canInject)
    }

    // MARK: - The grant ladder

    func testControlIsUnreachableWithoutViewing() {
        // The invariant the whole two-stage design exists for. There must be no
        // code path that grants input to a session nobody agreed to watch.
        var state = RemoteGrantState()
        XCTAssertFalse(state.grantControl(), "control was granted from a cold start")
        XCTAssertEqual(state.grant, .none)
        XCTAssertFalse(state.acceptsInput)
    }

    func testAcceptGrantsViewingAndNothingElse() {
        var state = RemoteGrantState()
        XCTAssertTrue(state.accept())
        XCTAssertEqual(state.grant, .viewing)
        XCTAssertFalse(state.acceptsInput,
                       "accepting an invite must not arm the input channel")
    }

    func testControlIsASecondDeliberateStep() {
        var state = RemoteGrantState()
        XCTAssertTrue(state.accept())
        XCTAssertTrue(state.grantControl())
        XCTAssertEqual(state.grant, .control)
        XCTAssertTrue(state.acceptsInput)
    }

    func testAcceptingTwiceChangesNothing() {
        var state = RemoteGrantState()
        XCTAssertTrue(state.accept())
        XCTAssertFalse(state.accept(), "a duplicate accept must not re-open anything")
        XCTAssertEqual(state.grant, .viewing)
    }

    func testRevokeDropsInputButKeepsTheSession() {
        var state = RemoteGrantState()
        _ = state.accept()
        _ = state.grantControl()
        XCTAssertTrue(state.revokeControl())
        XCTAssertEqual(state.grant, .viewing)
        XCTAssertFalse(state.acceptsInput)
        XCTAssertTrue(state.isLive, "revoking control must not end the session")
    }

    func testRevokingWhatWasNeverGrantedIsRefused() {
        var state = RemoteGrantState()
        _ = state.accept()
        XCTAssertFalse(state.revokeControl())
        XCTAssertEqual(state.grant, .viewing)
    }

    func testEndIsTerminal() {
        // Reconnect means a new session_id, new ephemerals and new keys — so it
        // means a new state, not this one quietly coming back to life.
        var state = RemoteGrantState()
        _ = state.accept()
        _ = state.grantControl()
        state.end()

        XCTAssertEqual(state.grant, .none)
        XCTAssertFalse(state.isLive)
        XCTAssertFalse(state.acceptsInput)
        XCTAssertFalse(state.accept(), "an ended session must not resume on an accept")
        XCTAssertFalse(state.grantControl())
    }

    func testOnlyControlAcceptsInput() {
        for grant in RemoteGrant.allCases {
            XCTAssertEqual(grant.acceptsInput, grant == .control,
                           "\(grant) disagreed about the input channel")
        }
    }

    func testTheLadderIsOrdered() {
        // Comparable so a check reads as the ordering it is, and a downgrade
        // cannot be mistaken for an upgrade by a `>` that should have been `>=`.
        XCTAssertLessThan(RemoteGrant.none, RemoteGrant.viewing)
        XCTAssertLessThan(RemoteGrant.viewing, RemoteGrant.control)
        XCTAssertEqual(RemoteGrant.allCases.sorted(), [.none, .viewing, .control])
    }

    // MARK: - Decline tokens

    func testDeclineReasonsAreTheWireTokensFromTheSpec() {
        // These are machine-readable values the peer parses, not display
        // strings. A token invented on one platform fails loudly nowhere — it
        // just shows a generic message on the other, forever.
        XCTAssertEqual(Set(RemoteDeclineReason.allCases.map(\.rawValue)),
                       ["declined", "busy", "unsupported", "disabled", "no_encoder", "timeout"])
        XCTAssertEqual(RemoteDeclineReason.noEncoder.rawValue, "no_encoder",
                       "camelCase must not leak onto the wire")
    }

    // MARK: - What the prompt says

    func testTheHeadlineNamesThePeerAndTheAsk() {
        // "Someone wants to view your screen" is not a sentence anybody can act
        // on, and viewing and control must never read the same.
        XCTAssertEqual(request(kind: .viewing).title, "Dave wants to view your screen")
        XCTAssertEqual(request(kind: .control).title, "Dave wants to control your screen")
        XCTAssertNotEqual(request(kind: .viewing).explanation,
                          request(kind: .control).explanation)
        XCTAssertEqual(request(kind: .viewing).acceptButtonTitle, "Allow Viewing")
        XCTAssertEqual(request(kind: .control).acceptButtonTitle, "Allow Control")
    }

    func testAPinnedKeyGetsNoWarningChromeAtAll() {
        // A green "verified" badge on every prompt trains people to look for the
        // badge instead of the words, and makes the one prompt that matters look
        // like all the others.
        XCTAssertNil(request(trust: .pinned(username: "Dave")).warning)
    }

    func testAChangedKeyWarnsAndNamesWhoItShouldHaveBeen() {
        let warning = try? XCTUnwrap(
            request(trust: .changedAtKnownAddress(username: "Dave",
                                                  previousPublicKeyB64: "old")).warning)
        XCTAssertTrue(warning?.contains("Dave") ?? false,
                      "the warning must say whose key it is not")
    }

    func testAnUnknownKeyWarnsToo() {
        XCTAssertNotNil(request(trust: .unknown).warning)
    }

    func testAControlPromptSaysWhenThisMacCannotActuallyInject() {
        // Input needs the **Accessibility** grant, which is a different TCC
        // permission from the Screen Recording one the session already has by
        // this point — being given one says nothing about the other. Without
        // it `CGEvent.post` fails silently: no error, no exception, nothing
        // happening, and a user who has just granted control watching a pointer
        // that does not move cannot tell that from a dead network.
        //
        // This is the moment they are deciding, so this is where it is said.
        let blocked = request(kind: .control, canInject: false)
        guard let notice = blocked.systemNotice else {
            return XCTFail("a control prompt that cannot inject said nothing about it")
        }
        XCTAssertTrue(notice.contains("Accessibility"), notice)
        XCTAssertTrue(notice.contains("System Settings"),
                      "a notice with no way to act on it is just bad news: \(notice)")
    }

    func testNothingIsSaidWhenThereIsNothingToSay() {
        // Not on the viewing prompt, which does not depend on the grant at all,
        // and not when the grant is present — a permanent notice is one nobody
        // reads, including on the one prompt where it matters.
        XCTAssertNil(request(kind: .control, canInject: true).systemNotice)
        XCTAssertNil(request(kind: .viewing, canInject: false).systemNotice)
        XCTAssertNil(request(kind: .viewing, canInject: true).systemNotice)
    }

    func testTheSystemNoticeIsNotDressedAsATrustWarning() {
        // `warning` is about the PEER and gets orange chrome. This is about our
        // own permissions, and borrowing that treatment teaches people to
        // dismiss the one that matters.
        XCTAssertNil(request(kind: .control, canInject: false).warning,
                     "a pinned key must still get no warning chrome")
    }

    func testTheFingerprintIsShownOrItsAbsenceIs() {
        // Never falls back to something reassuring: a key that cannot be parsed
        // has no fingerprint, and the dialog must say so rather than show a
        // plausible-looking blank.
        let real = request().fingerprint
        XCTAssertEqual(real, RemoteSessionCrypto.fingerprint(publicKeyB64: realKey))

        let broken = request(key: "not-a-key").fingerprint
        XCTAssertEqual(broken, "unreadable key")
        XCTAssertFalse(broken.isEmpty)
    }

    // MARK: - The countdown

    func testTheCountdownRunsDownAndStopsAtZero() {
        let origin = Date(timeIntervalSince1970: 1_000_000)
        let prompt = request(expiresIn: 45, from: origin)

        XCTAssertEqual(prompt.secondsRemaining(at: origin), 45)
        XCTAssertEqual(prompt.secondsRemaining(at: origin.addingTimeInterval(44.2)), 1)
        XCTAssertEqual(prompt.secondsRemaining(at: origin.addingTimeInterval(45)), 0)
        XCTAssertEqual(prompt.secondsRemaining(at: origin.addingTimeInterval(600)), 0,
                       "a countdown must never show a negative number")
    }

    func testExpiryIsInclusiveOfTheDeadline() {
        let origin = Date(timeIntervalSince1970: 1_000_000)
        let prompt = request(expiresIn: 45, from: origin)

        XCTAssertFalse(prompt.hasExpired(at: origin.addingTimeInterval(44.9)))
        XCTAssertTrue(prompt.hasExpired(at: origin.addingTimeInterval(45)))
        XCTAssertTrue(prompt.hasExpired(at: origin.addingTimeInterval(45.1)))
    }

    func testTheDefaultTimeoutIsLongEnoughToReadAndShortEnoughToMatter() {
        // Not an arbitrary number: long enough that somebody glancing over can
        // actually read a fingerprint, short enough that a peer is not left
        // staring at a spinner wondering whether the host is dead.
        XCTAssertGreaterThanOrEqual(RemoteConsentRequest.defaultTimeout, 30)
        XCTAssertLessThanOrEqual(RemoteConsentRequest.defaultTimeout, 120)
    }

    func testTheTwoPromptsForOneSessionAreDistinctRequests() {
        // The viewing prompt and the control prompt share a session but must not
        // share an identity, or the presenter's "already showing" check would
        // swallow the escalation.
        XCTAssertNotEqual(request(kind: .viewing).id, request(kind: .control).id)
        XCTAssertTrue(request(kind: .viewing).id.hasPrefix(sessionID))
        XCTAssertTrue(request(kind: .control).id.hasPrefix(sessionID))
    }
}

// MARK: - Rendering

/// Renders the prompt to PNGs so its layout can actually be looked at.
///
/// `screencapture` is unavailable in this development environment, so a running
/// app cannot be photographed — but `ImageRenderer` will draw a SwiftUI tree
/// headlessly, which covers everything static about the dialog: wrapping, the
/// warning block, the fingerprint not truncating, both colour schemes. Motion
/// and focus behaviour it cannot show.
///
/// Skipped unless `LANMSG_RENDER_UI=<dir>` is set — it is a generator, not an
/// assertion, in the same spirit as the H.264 fixture emitter.
@MainActor
final class RemoteConsentRenderTests: XCTestCase {

    func testRenderConsentPromptsForReview() throws {
        guard let directory = ProcessInfo.processInfo.environment["LANMSG_RENDER_UI"] else {
            throw XCTSkip("set LANMSG_RENDER_UI=<dir> to regenerate the prompt renders")
        }
        let key = Data(repeating: 7, count: 32).base64EncodedString()

        let cases: [(String, RemoteConsentRequest, ColorScheme)] = [
            ("pinned-viewing-light", Self.request(.viewing, .pinned(username: "Dave Felzy"), key), .light),
            ("pinned-viewing-dark", Self.request(.viewing, .pinned(username: "Dave Felzy"), key), .dark),
            ("changed-control-light",
             Self.request(.control, .changedAtKnownAddress(username: "Dave Felzy",
                                                           previousPublicKeyB64: "old"), key), .light),
            ("unknown-viewing-dark", Self.request(.viewing, .unknown, key), .dark),
            ("unreadable-key-light", Self.request(.viewing, .pinned(username: "Dave Felzy"), "nope"), .light),
        ]

        for (name, request, scheme) in cases {
            let view = RemoteConsentView(request: request, secondsRemaining: 42,
                                         onAccept: {}, onDecline: {})
                .environment(\.colorScheme, scheme)
            let renderer = ImageRenderer(content: view)
            renderer.scale = 2
            let image = try XCTUnwrap(renderer.nsImage, "\(name) rendered nothing")
            let url = URL(fileURLWithPath: directory).appendingPathComponent("\(name).png")
            try XCTUnwrap(Self.png(from: image)).write(to: url)
            print("wrote \(url.path) \(Int(image.size.width))x\(Int(image.size.height))")
        }
    }

    private static func request(_ kind: RemoteConsentRequest.Kind,
                                _ trust: PeerKeyTrust,
                                _ key: String) -> RemoteConsentRequest {
        RemoteConsentRequest(
            sessionID: "9f2c4a6e8b0d1f3a5c7e9b1d3f5a7c9e", kind: kind,
            peerName: "Dave Felzy", peerIP: "192.168.68.24", peerPublicKeyB64: key,
            trust: trust, expiresAt: Date().addingTimeInterval(42))
    }

    private static func png(from image: NSImage) -> Data? {
        guard let tiff = image.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }
}
