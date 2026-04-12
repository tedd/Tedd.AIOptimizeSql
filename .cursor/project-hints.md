# Project hints

One-line notes from prior troubleshooting. Read before long retry loops on builds or CLI tools.

- Locked binaries under `Debug` while the app runs from Visual Studio: try `dotnet build -c Release` from CLI, or ask the user to stop debugging so `Debug` output is not locked.
