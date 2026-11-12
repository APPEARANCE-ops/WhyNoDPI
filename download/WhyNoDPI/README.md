# WhyNoDPI

**WhyNoDPI** is a Windows WPF application for controlling the Zapret engine from one window.

**Application author: Xcuv** · UI version 1.1

## Features

- Starts `bin\winws.exe` directly with arguments from the selected General profile.
- Lets you select the default General profile or an ALT variant.
- Stops only the `winws.exe` process started by WhyNoDPI.
- Checks WinDivert files and detects an already-running `zapret` service.
- Writes launch diagnostics to `%LOCALAPPDATA%\WhyNoDPI\logs`.
- Can leave the engine running when the window closes.
- Watches engine state continuously: if `winws.exe` exits, the UI reports it.
- Measures YouTube, Discord and Telegram reachability with latency.

## User interface

- Sidebar navigation: Home, Strategies, Telegram · VPN, Journal, Settings.
- Hero status card: engine state, active profile and uptime.
- Automatic strategy detection with progress, per-profile score and a cancel button.
- Journal with severity levels, search and filters.
- Status bar with engine state, profile and last service check.
- Keyboard shortcuts: `F5` checks services, `Ctrl+1…5` switches sections.
- Dark theme for every control (inputs, lists, scrollbars, switches), dark Windows 11
  title bar, correct scaling on HiDPI displays.
- Errors and confirmations use in-app dialogs instead of bright system message boxes.
- Unhandled exceptions are written to `%LOCALAPPDATA%\WhyNoDPI\logs\crash-*.log`, and a
  second instance refuses to start with an explanation.

## No BAT execution at runtime

When a Flowseal folder is selected for the first time, WhyNoDPI reads the General profile files once as plain text and imports their arguments into a JSON catalog under `%LOCALAPPDATA%\WhyNoDPI\profiles`. The original profile files are no longer needed after import. Each start launches `bin\winws.exe` directly with the cached arguments — no `cmd.exe`, no BAT process, and no temporary scripts. The app also reads Flowseal’s `utils\game_filter.enabled` format (`mode=all/tcp/udp`, `tcp=...`, `udp=...`) to preserve the configured game-filter port ranges.

## Requirements

- Windows 10/11 x64.
- .NET 10 SDK to build from source.
- Official Flowseal package: https://github.com/Flowseal/zapret-discord-youtube/releases/latest

Extract Flowseal separately, preferably to a short path without spaces or non-ASCII characters, such as `C:\zapret-flowseal`. Select that folder in WhyNoDPI. It must contain `bin\winws.exe`, `lists`, and the original General profile files for the first import.

After import, the profile files can be deleted or left unused, but keep `bin` and `lists`. The `bin` folder contains the engine, DLLs, and WinDivert driver; the UI EXE cannot replace the network driver and required lists.

WinDivert requires administrator rights. If the Windows `zapret` service or another `winws.exe` instance is already running, WhyNoDPI will not start a second engine. Stop the external instance using the program that manages it.

## Publish a single application EXE

Open `WhyNoDPI.slnx` or `WhyNoDPI.csproj` in Visual Studio with the .NET 10 SDK, or run `Build-EXE.ps1` in PowerShell. To publish manually, run:

```powershell
dotnet restore .\WhyNoDPI.csproj -r win-x64 -p:NuGetAudit=false
dotnet publish .\WhyNoDPI.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

The app UI is published as `publish\WhyNoDPI.exe`. The external Flowseal package is still required for `winws.exe`, the WinDivert DLL/driver, and list files. WhyNoDPI itself is a self-contained single EXE.

## Credits

Thanks to **Flowseal** for [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) and its profiles.

WhyNoDPI is an independent application and is not an official Flowseal product.

© 2026 Xcuv

## Telegram: MTProto and VPN

- A Telegram page links to [@mtp4tg](https://t.me/mtp4tg).
- Import `t.me/proxy?...` or `tg://proxy?...` links and hand them to Telegram Desktop for confirmation.
- Proxy secrets are not stored in app settings or written to logs.
- The VPN button opens Windows VPN settings; the app does not enable an unknown VPN service or pretend an MTProxy is a VPN.

Public proxy endpoints and availability can change. Only connect to servers you trust.
