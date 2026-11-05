# Storage provider fixture tests
Run from the repository with `dotnet run --project library-storage-tests/Threadbook.LibraryStorageTests.csproj` using the installed .NET 10 SDK and cleared package feeds. No third-party dependencies. Fixtures are synthetic, created under this directory, and retained for inspection. The parent work/run-library-storage-tests.ps1 uses the verified DevTools runner and records exit codes/logs.

Providers inherit the repository MIT license. Implementation is original; System.Text.Json, ZipArchive, SHA256 and the existing private-file provider supply serialization, ZIP and authenticated storage. No network, shell execution of attachments, default user-data reads or desktop startup.

Caller example: construct LibraryStore(owner), then LibraryService(store, () => app.Snapshot), then AttachmentStore(owner, service). Stage(note.Id, input, displayName, mediaType), then AddToNote(stage); Discard an abandoned stage. OpenRead(id,note.Id) enforces note binding. Header image checks bound PNG/JPEG dimensions to 16 MP; desktop must still use a bounded decoder and never execute opaque files.

PrepareMarkdownImport/PrepareJsonAppend return a complete candidate snapshot. Commit through the application's ordinary ReplaceAll service exactly once; these helpers never change storage. Portable restore requires a seekable input and a NEW directory. Encrypted restores require the password and authenticate every private payload; no plaintext files are written. Reset preserves damaged metadata first and callers recreate LibraryService after successful reset.
