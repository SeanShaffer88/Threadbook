# Command palette focused verification

Native BCL/WPF only. Run `dotnet run --project command-tests/Threadbook.CommandTests.csproj -c Release -- <absolute-new-fixture-directory>` with the repository's offline SDK environment. Requires an interactive Windows WPF desktop; writes synthetic fixtures only to the supplied new directory.

Covers ranking/cap at 10k entries, real routed keyboard events (synthetic KeyboardDevice modifier state for Ctrl+Shift+P), live canonical note/notebook jumps, cancelled leave, same-note draft, stale IDs, read-only search, shared menu execution, pending operation eligibility, UI/provider lock divergence and lock/unlock palette lifecycle. `main.png` and `palette.png` are WPF-rendered synthetic client-area captures, not OS window screenshots. Entrypoint/dispatcher failures return nonzero with stderr.
