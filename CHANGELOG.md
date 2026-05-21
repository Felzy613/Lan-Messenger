# Changelog

All notable changes to LAN Messenger are documented here.

> **Release notes are now auto-generated from git commit messages.**
> The CI `release.yml` workflow builds the "What's New" section automatically
> by running `git log --no-merges` since the previous combined release tag.
> You no longer need to edit this file before shipping — just write clear
> commit subjects and they will appear in the release notes verbatim.
>
> Historical entries below are kept for reference.

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
