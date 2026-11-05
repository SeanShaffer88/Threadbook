# Archive WPF fixture
Run with an explicit NEW absolute fixture directory under the workspace `work` directory:
`dotnet run --project archive-ui-tests/Threadbook.ArchiveUiTests.csproj -c Release -- <new-absolute-fixture>`
Uses the real Desktop, Core and Storage assemblies, generated plaintext/encrypted vaults, dispatcher exception capture and noninteractive confirmation seams. Produces History/Trash/main-window screenshots. Never use personal/default data. Entry exceptions return 2; success returns 0. No package dependencies or Program switches.
