# Writing backend tests

Run from the Threadbook directory with installed .NET 10 SDK: `dotnet run --project writing-tests/Threadbook.WritingTests.csproj -c Release`. NuGet.Config clears all sources; no packages required. Console harness exits nonzero on first failed assertion. Actual recovery IO uses a unique temporary directory removed after completion. Original synthetic strings only, no personal data. Existing application tests and UI are owned by the other worker.

Consumption: this independent console project references the actual core/storage projects and exercises their public APIs. No packed artifact is produced by this bounded task; packed consumption and UI acceptance remain integration gates.
