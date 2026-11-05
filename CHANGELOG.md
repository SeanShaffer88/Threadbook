# Changelog

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
