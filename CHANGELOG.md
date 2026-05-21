# Changelog

All notable changes to LAN Messenger are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

**How to add an entry:** under `[Unreleased]` add bullet points in the
appropriate section (`### Added`, `### Fixed`, `### Changed`, `### Removed`).
When CI cuts a release it extracts the matching `## [version]` section and
includes it in the GitHub release body — that text is what both apps display
in their Software Update screen.

## [Unreleased]

## [1.5.6] - 2026-05-20

### Fixed
- Windows: corrected test exception types; removed unnecessary sudo in smoke tests.

## [1.5.5] - 2026-05-20

### Added
- macOS installer is now a signed `.pkg` (replaces the previous `.dmg`).
  Double-click to install to `/Applications`; compatible with MDM/JAMF/Munki.

### Fixed
- macOS: file transfer could freeze when sending large files.
- macOS: replies to unsaved contacts now work correctly.
- macOS: dock icon, log output, and general UX polish.
- macOS: `MARKETING_VERSION` in the package now correctly uses the full
  `MAJOR.MINOR.PATCH` version string.
- Version hook now writes the full `MAJOR.MINOR.PATCH` version to `project.yml`.

## [1.5.4] - 2026-04-15

_Earlier release — changelog entry not yet filled in._
