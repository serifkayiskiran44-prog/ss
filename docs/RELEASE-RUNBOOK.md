# Release runbook

1. `dotnet build TrMarketplaceHubDesktop.csproj -c Release`
2. `dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Current`
3. `powershell -File installer/Smoke-Test.ps1 -PackageDirectory Windows-Current`
4. Confirm the output contains the EXE, `.runtimeconfig.json`, and `.deps.json`.
5. Do not run live marketplace writes during release acceptance; unverified connectors remain `LIVE_API_BLOCKED`.
