Threadbook portable beta.2 (Windows x64)

Install Microsoft .NET 10 Desktop Runtime x64 manually from
https://dotnet.microsoft.com/en-us/download/dotnet/10.0
No runtime installer is bundled or launched.

Extract the complete ZIP to a writable local folder. Open Threadbook.exe.
Keep portable.flag beside the application: notes are stored in adjacent Data,
regardless of the current working directory. --data-dir takes precedence.
An unwritable or linked portable Data location fails; it does not fall back.
Do not put this package over an existing notebook or installed copy.
Close Threadbook before copying the entire portable folder, including Data.
Back up Data separately before upgrading. The ZIP contains no notebooks.
Removing portable.flag selects the normal default library on the next launch.
Test/demo modes always require an explicit --data-dir synthetic fixture.
See LICENSE for the MIT license. No automatic updates or network services.
