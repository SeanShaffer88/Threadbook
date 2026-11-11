# Changelog

## [Unreleased]

## [0.4.0-beta.1] - 2026-11-11

### Added

- Built-in catalog of MCP servers, skills, prompt packs and guides, opened from the sidebar or the Notebook menu. Each entry shows its source, license, compatible clients, a plain statement of what it can touch (files, commands, network, API key), and a last-verified date. Install commands and config snippets can be copied, and an entry can be saved into a notebook as a note with its facts and room for personal notes. The catalog is bundled and read-only; Threadbook downloads and installs nothing from it.
- Visible borders: the sidebar, note list, reading pane and layout view are now framed panels with a clear edge and a small gap between them, note cards in the list carry a full border, and each Kanban lane is a bordered column, so the window's regions read at a glance in both themes.
- The catalog grew: the official MCP spec and docs, the official clients list, and the official Python, TypeScript, Rust, Go and Java SDKs join the C# SDK, plus five discovery directories (Awesome MCP Servers, MCP.so, Glama, Smithery and PulseMCP) — each labelled discovery-only, with a reminder to verify every listing at its own source.

### Fixed

- The Catalog menu command had no stable command ID, which stopped the app from starting; it now registers as `catalog.open`, and the packaged smoke check runs in CI as well as in the release build so this class of failure is caught before a release.

## [0.3.0-beta.1] - 2026-11-10

### Added

- Prompt variables: write `{{name}}` or `{{name = default}}` in any note body. **Run prompt…** in the writer opens a fill-in sheet with one field per variable, a live final-prompt preview, and a Copy button that stays off until every variable has a value or a default. `\{{name}}` stays literal text.
- Slash menu in the writer: type `/` at the start of a word and pick a prompt part — Role, Task, Context, Input, Steps, Examples, Constraints, Output format, Tone, Variable, or Ask first — with the arrow keys and Enter, or a double-click. Inserted parts land on their first variable so it can be typed over at once.

### Changed

- The theme now follows TesselGUI revision 2 (the GitHub Dark scheme): dark surfaces, text, muted text, and edges use the revision 2 tokens, selection uses the blue selection indicator, keyboard focus uses the focus blue, and primary actions use the green action family with white captions. Light mode keeps the Tessel light surfaces with the GitHub Light accent family. The orange accent and orange flair pills are gone; flair pills now use the deep accent with its matching caption color.

## [0.2.0-beta.2] - 2026-11-04

- Add a per-user installer and portable ZIP with explicit storage selection and manual .NET 10 Desktop Runtime prerequisite.
- Keep automated modes restricted to explicit fixture directories; portable errors never fall back to the default library.
- Add headless startup and package inventory tests. Installed lifecycle and read-only ACL qualification remain pending; see docs/packaging.md.

## 0.2.0-beta.1 — release candidate

Release availability, date, source commit, and artifact checksums are recorded on the GitHub release page.

### Added

- History and Trash with explicit restore, permanent removal, capacity handling, and pending-operation recovery.
- Mouse and keyboard pane resizing with widths retained per layout.
- Main-window command palette for commands, notes, and notebooks.
- Inline Markdown note editing with shared writer tools and a separate advanced editor.
- Local title and stable-ID wiki links, ambiguity selection, broken-link states, and backlinks.
- Windows headless CI and public build/verification instructions.

### Compatibility

Existing Snapshot, Library, and Recovery schema versions remain 1. Optional archive and UI sidecars extend the library. History keeps previous content; version restore preserves current replies and metadata. Trash keeps complete deleted content until you explicitly remove it. Back up the complete directory before using important data with this beta.

### Earlier local alpha

The local 0.1.0-alpha line established the offline notebook, nested replies, Markdown writer, six layouts, folders, tags, templates, search, tasks, attachment previews, imports/exports, recovery, and optional encrypted library copies. It was a local development distribution rather than a public release. Its recorded checks do not substitute for beta artifact verification.
