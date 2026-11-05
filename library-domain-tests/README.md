# Library domain tests

Zero-dependency .NET 10 console suite; synthetic in-memory canonical notes and fake metadata store only. Run from source root: `dotnet run --project library-domain-tests/Threadbook.LibraryDomainTests.csproj`. No application launch or live-data reads.

63 assertions cover collection/depth/byte boundaries, revision and commit failure semantics, stale projections, immutable collections, folder promotion collisions, literal content, stable-ID tasks, built-in templates, search beyond previews/replies/attachments/date ranges/tag AND/cancellation/determinism, and checklist fences/escapes/line endings/staleness. Existing canonical Models.cs and NotebookService.cs remain unchanged.

Uses only project Core and BCL APIs. Original implementation under repository MIT license; no copied third-party code. Consumption example: construct LibraryService with an ILibraryStore implementation and a canonical Snapshot provider, then Execute(new PutLibraryTag(null, "Research")). Query via LibrarySearch.Search(snapshot, service.State, new SearchRequest("term")).
