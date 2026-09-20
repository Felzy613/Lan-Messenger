import XCTest
@testable import LanMessenger

// Guards the in-app changelog: an update that skips versions has to show the
// notes for every version it skips, not just the newest one. See
// UpdateService.mergedReleaseNotes.
final class UpdateNotesTests: XCTestCase {

    // A per-platform release body as CI writes it: categorized sections, then
    // a "---" rule and the artifact table that belongs on the release page.
    private func platformBody(_ line: String) -> String {
        """
        ### 🐛 Bug Fixes

        - \(line)

        ---

        **macOS v9.9.9** — platform pre-release

        | Format | Use case |
        |--------|----------|
        | `.pkg` | Standard install |
        """
    }

    // MARK: - Stripping

    func testStripsEverythingFromTheHorizontalRule() {
        let stripped = UpdateService.stripReleasePageSections(platformBody("Fixed the thing"))
        XCTAssertEqual(stripped, "### 🐛 Bug Fixes\n\n- Fixed the thing")
    }

    func testStripsDownloadsAndInstallHeadings() {
        let body = """
        ### ✨ New Features

        - Something new

        ## Downloads

        | Platform | Installer |
        """
        XCTAssertEqual(
            UpdateService.stripReleasePageSections(body),
            "### ✨ New Features\n\n- Something new"
        )
    }

    // The combined release opens with "## What's New"; the per-platform one
    // does not. Dropping it keeps both shapes rendering identically under a
    // version heading.
    func testDropsLeadingWhatsNewHeading() {
        let body = "## What's New\n\n### 🐛 Bug Fixes\n\n- Fixed it"
        XCTAssertEqual(
            UpdateService.stripReleasePageSections(body),
            "### 🐛 Bug Fixes\n\n- Fixed it"
        )
    }

    func testKeepsWhatsNewHeadingThatIsNotLeading() {
        let body = "Intro line\n\n## What's New\n\n- Fixed it"
        XCTAssertTrue(
            UpdateService.stripReleasePageSections(body).contains("## What's New")
        )
    }

    // MARK: - Merging

    func testMergesEveryVersionBetweenInstalledAndOffered() {
        let releases = [
            (version: "1.1.3", body: platformBody("Newest fix")),
            (version: "1.1.2", body: platformBody("Middle fix")),
            (version: "1.1.1", body: platformBody("Already installed")),
        ]
        let notes = UpdateService.mergedReleaseNotes(
            from: releases, after: "1.1.1", through: "1.1.3"
        )

        XCTAssertTrue(notes.contains("## Version 1.1.3"))
        XCTAssertTrue(notes.contains("Newest fix"))
        XCTAssertTrue(notes.contains("## Version 1.1.2"))
        XCTAssertTrue(notes.contains("Middle fix"))
        // The version the user is already running must not be listed.
        XCTAssertFalse(notes.contains("## Version 1.1.1"))
        XCTAssertFalse(notes.contains("Already installed"))
        // Newest first.
        XCTAssertLessThan(
            notes.range(of: "## Version 1.1.3")!.lowerBound,
            notes.range(of: "## Version 1.1.2")!.lowerBound
        )
    }

    func testSingleHopHasNoVersionHeading() {
        let releases = [(version: "1.1.2", body: platformBody("Only fix"))]
        let notes = UpdateService.mergedReleaseNotes(
            from: releases, after: "1.1.1", through: "1.1.2"
        )
        XCTAssertFalse(notes.contains("## Version"))
        XCTAssertEqual(notes, "### 🐛 Bug Fixes\n\n- Only fix")
    }

    // A release newer than the asset we are actually offering (a combined
    // release whose macOS ZIP has not been published yet) must not advertise
    // changes the download does not contain.
    func testIgnoresVersionsNewerThanTheOfferedBuild() {
        let releases = [
            (version: "1.1.4", body: platformBody("Not in this download")),
            (version: "1.1.3", body: platformBody("Newest fix")),
        ]
        let notes = UpdateService.mergedReleaseNotes(
            from: releases, after: "1.1.1", through: "1.1.3"
        )
        XCTAssertFalse(notes.contains("Not in this download"))
        XCTAssertTrue(notes.contains("Newest fix"))
    }

    // The same build appears twice — once as macos-vX.Y.Z, once inside the
    // combined release — and must produce one section, not two.
    func testDeduplicatesRepeatedVersions() {
        let releases = [
            (version: "1.1.2", body: "## What's New\n\n### 🐛 Bug Fixes\n\n- Combined copy"),
            (version: "1.1.2", body: platformBody("Platform copy")),
        ]
        let notes = UpdateService.mergedReleaseNotes(
            from: releases, after: "1.1.1", through: "1.1.2"
        )
        XCTAssertTrue(notes.contains("Combined copy"))
        XCTAssertFalse(notes.contains("Platform copy"))
    }

    // An empty duplicate must not shadow the copy that still has content.
    func testEmptyDuplicateLosesToTheOneWithContent() {
        let releases = [
            (version: "1.1.2", body: "---\n\n## Downloads\n\n| Platform |"),
            (version: "1.1.2", body: platformBody("Platform copy")),
        ]
        let notes = UpdateService.mergedReleaseNotes(
            from: releases, after: "1.1.1", through: "1.1.2"
        )
        XCTAssertTrue(notes.contains("Platform copy"))
    }

    func testUnparseableTagsAreSkipped() {
        let releases = [
            (version: "", body: platformBody("No version")),
            (version: "1.1.2", body: platformBody("Real fix")),
        ]
        let notes = UpdateService.mergedReleaseNotes(
            from: releases, after: "1.1.1", through: "1.1.2"
        )
        XCTAssertFalse(notes.contains("No version"))
        XCTAssertTrue(notes.contains("Real fix"))
    }

    // The two platforms version independently, so a Windows-only tag must not
    // be read as a macOS version — its changelog would otherwise show up in
    // the macOS update panel under a version macOS never had.
    func testTagVersionsAreAttributedToTheRightPlatform() {
        XCTAssertEqual(UpdateService.extractVersion(fromTag: "macos-v1.8.4"), "1.8.4")
        XCTAssertEqual(UpdateService.extractVersion(fromTag: "release-win1.7.2-mac1.8.4"), "1.8.4")
        XCTAssertEqual(UpdateService.extractVersion(fromTag: "windows-v1.9.0"), "")
        // A tag naming neither platform still falls back to its first number.
        XCTAssertEqual(UpdateService.extractVersion(fromTag: "v2.0.1"), "2.0.1")
    }

    func testForeignPlatformReleasesAreLeftOutOfTheMerge() {
        let releases = [
            (version: UpdateService.extractVersion(fromTag: "windows-v1.9.0"),
             body: platformBody("Windows-only change")),
            (version: UpdateService.extractVersion(fromTag: "macos-v1.1.2"),
             body: platformBody("Mac fix")),
        ]
        let notes = UpdateService.mergedReleaseNotes(
            from: releases, after: "1.1.1", through: "1.1.2"
        )
        XCTAssertFalse(notes.contains("Windows-only change"))
        XCTAssertTrue(notes.contains("Mac fix"))
    }

    func testNoNewerReleasesYieldsEmptyNotes() {
        let releases = [(version: "1.1.1", body: platformBody("Installed"))]
        XCTAssertEqual(
            UpdateService.mergedReleaseNotes(from: releases, after: "1.1.1", through: "1.1.1"),
            ""
        )
    }

    // Version ordering must be numeric, not lexicographic: 1.1.10 is newer
    // than 1.1.9, and both belong in a merge that starts at 1.1.8.
    func testOrdersNumericallyNotLexicographically() {
        let releases = [
            (version: "1.1.9",  body: platformBody("Nine")),
            (version: "1.1.10", body: platformBody("Ten")),
        ]
        let notes = UpdateService.mergedReleaseNotes(
            from: releases, after: "1.1.8", through: "1.1.10"
        )
        XCTAssertLessThan(
            notes.range(of: "## Version 1.1.10")!.lowerBound,
            notes.range(of: "## Version 1.1.9")!.lowerBound
        )
    }
}
