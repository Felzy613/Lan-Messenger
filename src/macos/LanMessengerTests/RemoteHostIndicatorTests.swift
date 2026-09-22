import XCTest
import SwiftUI
import AppKit
@testable import LanMessenger

// The strip a host sees while their screen is being shared.
//
// PROTOCOL.md requires it — "while a session is live the host must show a
// persistent indicator naming the viewer and the current grant level, with a
// stop control" — and it is a safety device rather than chrome: a host who
// cannot see it has no way to know they are being watched. So the tests are
// mostly about it being *there*, saying the right thing, and staying put.
final class RemoteHostIndicatorTests: XCTestCase {

    private let started = Date(timeIntervalSince1970: 1_000_000)

    private func model(_ grant: RemoteGrant, name: String = "Dave Felzy")
    -> RemoteHostIndicatorModel {
        RemoteHostIndicatorModel(peerName: name, grant: grant, startedAt: started)
    }

    // MARK: - What it says

    func testNothingIsShownWhenNothingIsShared() {
        XCTAssertFalse(model(.none).isVisible)
        XCTAssertTrue(model(.viewing).isVisible)
        XCTAssertTrue(model(.control).isVisible)
    }

    func testTheViewerIsNamed() {
        // "Someone is viewing your screen" does not let a host tell an expected
        // session from an unexpected one.
        XCTAssertTrue(model(.viewing).headline.contains("Dave Felzy"))
        XCTAssertTrue(model(.control).headline.contains("Dave Felzy"))
    }

    func testViewingAndControlNeverReadTheSame() {
        // They are one escalation apart and wildly different in consequence.
        XCTAssertNotEqual(model(.viewing).headline, model(.control).headline)
        XCTAssertTrue(model(.viewing).headline.contains("viewing"))
        XCTAssertTrue(model(.control).headline.contains("controlling"))
    }

    func testSeverityFollowsTheGrant() {
        XCTAssertEqual(model(.viewing).severity, .watching)
        XCTAssertEqual(model(.control).severity, .controlled)
    }

    func testTakingBackControlIsOfferedOnlyWhenThereIsControlToTakeBack() {
        // Revoking leaves the session running, which is usually what a host
        // actually wants — "stop touching things", not "get out".
        XCTAssertFalse(model(.viewing).showsRevokeControl)
        XCTAssertTrue(model(.control).showsRevokeControl)
        XCTAssertFalse(model(.none).showsRevokeControl)
    }

    // MARK: - Elapsed time

    func testElapsedTimeIsTheQuietPartThatCatchesAForgottenSession() {
        let m = model(.viewing)
        XCTAssertEqual(m.elapsed(at: started), "0:00")
        XCTAssertEqual(m.elapsed(at: started.addingTimeInterval(9)), "0:09")
        XCTAssertEqual(m.elapsed(at: started.addingTimeInterval(72)), "1:12")
        XCTAssertEqual(m.elapsed(at: started.addingTimeInterval(599)), "9:59")
    }

    func testTheHourAppearsOnlyOnceItMatters() {
        let m = model(.viewing)
        XCTAssertEqual(m.elapsed(at: started.addingTimeInterval(3599)), "59:59")
        XCTAssertEqual(m.elapsed(at: started.addingTimeInterval(3600)), "1:00:00")
        XCTAssertEqual(m.elapsed(at: started.addingTimeInterval(3767)), "1:02:47")
    }

    func testAClockThatWentBackwardsDoesNotShowNegativeTime() {
        // NTP corrections and sleep/wake both do this.
        XCTAssertEqual(model(.viewing).elapsed(at: started.addingTimeInterval(-30)), "0:00")
    }

    // MARK: - Where it sits

    private let screen = CGRect(x: 0, y: 0, width: 1920, height: 1080)

    func testItSitsTopCentre() {
        let size = CGSize(width: 360, height: 44)
        let origin = IndicatorPlacement.origin(panelSize: size, in: screen)

        XCTAssertEqual(origin.x, (1920 - 360) / 2, accuracy: 1)
        // AppKit's origin is bottom-left, so "near the top" is a high y.
        XCTAssertEqual(origin.y, 1080 - 44 - IndicatorPlacement.topInset, accuracy: 1)
    }

    func testItFollowsTheVisibleFrameNotTheWholeScreen() {
        // The menu bar and the notch live outside the visible frame; an
        // indicator drawn under either is one nobody can read or click.
        let visible = CGRect(x: 0, y: 0, width: 1920, height: 1055)
        let origin = IndicatorPlacement.origin(panelSize: CGSize(width: 360, height: 44),
                                               in: visible)
        XCTAssertLessThanOrEqual(origin.y + 44, visible.maxY)
    }

    func testItIsPlacedOnASecondDisplayRelativeToThatDisplay() {
        // A second monitor's frame does not start at zero, and an indicator
        // positioned in absolute coordinates lands on the wrong screen — or off
        // every screen.
        let secondary = CGRect(x: 1920, y: 300, width: 1280, height: 800)
        let origin = IndicatorPlacement.origin(panelSize: CGSize(width: 360, height: 44),
                                               in: secondary)
        XCTAssertGreaterThanOrEqual(origin.x, secondary.minX)
        XCTAssertLessThanOrEqual(origin.x + 360, secondary.maxX)
        XCTAssertGreaterThanOrEqual(origin.y, secondary.minY)
    }

    func testAStripWiderThanTheScreenIsStillOnIt() {
        // A very long peer name on a narrow display. Hanging off the edge is
        // how the Stop button becomes unreachable.
        let narrow = CGRect(x: 0, y: 0, width: 300, height: 600)
        let size = CGSize(width: 520, height: 44)
        let origin = IndicatorPlacement.clamped(
            origin: IndicatorPlacement.origin(panelSize: size, in: narrow),
            panelSize: size, in: narrow)

        XCTAssertGreaterThanOrEqual(origin.x, narrow.minX,
                                    "the left edge must stay on screen")
        XCTAssertGreaterThanOrEqual(origin.y, narrow.minY)
    }

    func testPlacementIsDerivedNotRemembered() {
        // The indicator is deliberately immovable: a viewer holding the mouse
        // could otherwise drag the host's own warning off the screen, and
        // nothing can tell an injected drag from a real one. So placement must
        // be a pure function of the screen — the same inputs always land in the
        // same place, with no stored offset to corrupt.
        let size = CGSize(width: 360, height: 44)
        let first = IndicatorPlacement.origin(panelSize: size, in: screen)
        let second = IndicatorPlacement.origin(panelSize: size, in: screen)
        XCTAssertEqual(first, second)
    }
}

/// Renders the indicator to PNGs for review, alongside the consent prompt.
/// Skipped unless `LANMSG_RENDER_UI=<dir>` is set.
@MainActor
final class RemoteHostIndicatorRenderTests: XCTestCase {

    func testRenderIndicatorForReview() throws {
        guard let directory = ProcessInfo.processInfo.environment["LANMSG_RENDER_UI"] else {
            throw XCTSkip("set LANMSG_RENDER_UI=<dir> to regenerate the indicator renders")
        }
        let started = Date()
        let cases: [(String, RemoteGrant, String, String)] = [
            ("indicator-viewing", .viewing, "4:12", "Dave Felzy"),
            ("indicator-control", .control, "1:02:47", "Dave Felzy"),
            ("indicator-long-name", .control, "0:07",
             "A Really Quite Long Machine Name"),
        ]

        for (name, grant, elapsed, peer) in cases {
            let view = RemoteHostIndicatorView(
                model: RemoteHostIndicatorModel(peerName: peer, grant: grant,
                                                startedAt: started),
                elapsed: elapsed, onRevokeControl: {}, onStop: {})
                .padding(24)
            let renderer = ImageRenderer(content: view)
            renderer.scale = 2
            let image = try XCTUnwrap(renderer.nsImage, "\(name) rendered nothing")
            guard let tiff = image.tiffRepresentation,
                  let rep = NSBitmapImageRep(data: tiff),
                  let png = rep.representation(using: .png, properties: [:]) else {
                XCTFail("could not encode \(name)"); return
            }
            let url = URL(fileURLWithPath: directory).appendingPathComponent("\(name).png")
            try png.write(to: url)
            print("wrote \(url.path) \(Int(image.size.width))x\(Int(image.size.height))")
        }
    }
}
