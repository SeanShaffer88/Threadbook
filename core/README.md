# Threadbook.Core

Package reference example: `<PackageReference Include="Threadbook.Core" Version="0.2.0-beta.2" />`.

Original MIT implementation using .NET 10 BCL only; no third-party dependencies. Dependency graph: desktop -> Storage -> Core; desktop -> Core. Core has no UI, filesystem, network, timers or import side effects.

NotebookService(ISnapshotStore, optional UTC clock) owns the canonical Snapshot schema 1. Execute(Command) returns Outcome; Query(NoteQuery) returns matching notes. Commands: PutNotebook/DeleteNotebook, PutNote/DeleteNote, ToggleSaved, PutReply/DeleteReply, ReplaceAll. Validate replacement snapshots before committing; failure preserves published state. Limits: 200 notebooks, 10,000 notes, 50,000 replies, 16 reply levels, 100,000 body characters. Caller serializes service operations and owns store lifetime.

LibraryService(ILibraryStore, Func<Snapshot>) publishes State, Warning and WritesBlocked. Execute(LibraryCommand) handles Put/DeleteLibraryFolder, AssignNotebookFolder, Put/DeleteLibraryTag, AssignNoteTags, Put/DeleteNoteTemplate, Put/DeleteLibraryTask, CompleteLibraryTask, SnoozeLibraryTask, Add/RemoveLibraryAttachment. IDs are generated for null create IDs. Successful commits increment sidecar revision once. Snapshot and LibraryState Freeze methods defensively copy collections. LibraryLogic.Validate validates internal metadata; Normalize projects stale canonical references with warning. Schema-1 sidecar metadata is independent of canonical note content; no multi-file transaction is promised.

```csharp
var notes = new NotebookService(snapshotStore);
var library = new LibraryService(libraryStore, () => notes.State);
var result = library.Execute(new PutLibraryFolder(null, null, "Projects"));
if (!result.Success) Console.WriteLine(result.Error?.Message);
var hits = LibrarySearch.Search(notes.State, library.State,
    new SearchRequest(Text: "project", HasTasks: true));
var body = Checklist.Toggle("- [ ] Review\n", 0, "- [ ] Review");
```

LibrarySearch.Search accepts cancellation and SearchRequest text/notebook/tag-AND/UTC updated-from inclusive/updated-before exclusive/saved/attachment/task filters. SearchResult has Hits and TotalCount; hits contain note/source/optional reply ID and bounded snippet. Results sort updated descending then GUID. Invalid filters throw DataException; cancellation throws OperationCanceledException. Checklist.Toggle uses zero-based physical source lines, rejects stale/non-task/fenced lines and preserves line endings. LibraryTemplates.BuiltIns supplies Meeting, Journal, Project and Checklist without persisting them automatically.

ISnapshotStore exposes Load and Commit(candidate,expectedRevision). ILibraryStore exposes Load(canonical) and Commit(candidate,expectedRevision,canonical). Failed commands return structured Outcome; validation helpers can throw DataException. Host owns confirmation, cancellation, task scheduling and reminder UI. Services have no process-wide singleton and do not start background work.

Verification: root tests, writing-tests and library-domain tests exercise source behavior. The expansion packaging helper builds actual nupkg files and runs work/expansion-consumer using PackageReference and a cleared local feed/cache, with no ProjectReference. Consult the expansion checks report for actual results; packaging alone is not behavior verification.

Archive domain: `CommandPreview` is side-effect free and uses NotebookService's single command engine. `CommitPreview` rejects a stale before-state; `Reload` refreshes the same service object. Schema 1 canonical and library models are unchanged. Archive models describe immutable references and restore requests without IO or background initialization.

CommitPreview validates and freezes a local candidate, requires the exact next revision, and shares that frozen candidate between persistence and in-memory state. Caller-owned collections cannot mutate committed state after return.

Wiki links use the same bounded Markdown tokenizer: `[[title]]` or
`WikiLinks.FormatStable(id, displayTitle)`. Backslash escapes backslash, pipe and
brackets. Display labels decode once and are never interpreted as Markdown.
Code, escaped openings, Markdown link/image spans and URL spans exclude wiki
recognition. `MarkdownInline.Wiki` carries the token; optional `Children` carries
styled composites containing links. Offsets always address the original UTF-16
source. HTML export displays encoded source syntax without active links or URLs.

```csharp
var parsed = WikiLinks.Parse(body, cancellationToken);
var index = WikiLinkIndex.Build(snapshot, previousIndex, cancellationToken);
foreach (var token in parsed.Tokens)
{
    var resolution = index.Resolve(sourceNotebookId, token);
    if (resolution.TargetId is Guid target)
        foreach (var source in index.GetBacklinks(target)) { /* host navigation */ }
    if (resolution.CandidateGroupId is int group)
        foreach (var candidate in index.GetCandidates(group, 0, 50)) { /* chooser */ }
}
```

All canonical live notes participate, including unbookmarked notes. Stable IDs
survive renames. Title matching trims and uses ordinal case-insensitive comparison;
a unique source-notebook match wins, then a globally unique match. Multiple
matches remain ambiguous. Group handles belong to the index that issued them;
retain that index while paging and discard handles when replacing it. Collections
are read-only; a previous index remains usable after a rebuild. Hosts own worker
scheduling, generation checks, cancellation, locks and navigation authorization.

Bounds: 100,000 UTF-16 characters per body, 1,000 tokens per note, 100,000
admitted aggregate tokens and a conservative 64 MiB owned-index estimate.
`IsComplete` and `Diagnostics` report omissions; never present incomplete backlinks
as exhaustive. Full source stays unchanged. `EstimatedOwnedBytes` includes cache,
maps, groups, dependency keys, token text and both edge directions; immutable source
body strings are shared by reference with the supplied snapshot, not copied.
`ParsedBodyCount`, `ParsedCharacterCount`, `ResolutionCount` and `LinkCount` describe
the current build. Exact bodies, title, notebook and membership determine reuse;
revision, timestamps, replies, flair and bookmarks alone do not trigger parsing.
No previous-index chain is retained. No IO or background work occurs on import.

The original wiki implementation uses .NET only, has no third-party source or
runtime dependencies, and is covered by the repository MIT license included in
the Core package. Focused verification: `dotnet run --project wiki-tests`; see
the controller's evidence for actual packed-package consumer verification.
