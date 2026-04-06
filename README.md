# livekit-dotnet

Windows desktop screen sharing prototype built with .NET 8, WinUI 3, LiveKit FFI, and Direct3D 11.

This repo contains two apps:

- `LiveKitScreenShareHost`
  Starts a LiveKit publisher, captures a selected display, and can optionally launch a bundled local LiveKit server.
- `LiveKitScreenViewer`
  Connects as a viewer/subscriber and renders the shared screen in a WinUI 3 window using D3D11.

## Repo Layout

- `LiveKitScreenShareHost/`
- `LiveKitScreenViewer/`

The repo intentionally includes these runtime dependencies so a fresh clone does not need extra manual file drops:

- `LiveKitScreenShareHost/src/native/livekit_ffi.dll`
- `LiveKitScreenShareHost/src/tools/livekit-server.exe`

Local build artifacts are excluded from git:

- `.vs/`
- `bin/`
- `obj/`

## Prerequisites

Use a Windows machine with:

- Windows 10 version `19041` or newer
- .NET 8 SDK
- internet access for NuGet restore

For development builds, Visual Studio 2022 with the Windows application tooling installed is the easiest path, but CLI builds with `dotnet build` also work once the required Windows SDK tooling is available.

## Build

Build the host:

```powershell
dotnet build .\LiveKitScreenShareHost\LiveKitScreenShareHost.sln -c Debug -p:Platform=x64
```

Build the viewer:

```powershell
dotnet build .\LiveKitScreenViewer\LiveKitScreenViewer.sln -c Debug -p:Platform=x64
```

## Run

Start the host first:

```powershell
.\LiveKitScreenShareHost\src\bin\x64\Debug\net8.0-windows10.0.19041.0\LiveKitScreenShareHost.exe
```

Then start the viewer:

```powershell
.\LiveKitScreenViewer\src\bin\x64\Debug\net8.0-windows10.0.19041.0\LiveKitScreenViewer.exe
```

Default local settings assume:

- LiveKit URL: `ws://127.0.0.1:7880`
- room: `screen-room`
- API key: `devkey`
- API secret: `secret`

The host can launch the bundled local LiveKit server, and the viewer can connect to it using those defaults.

## Configuration

The host accepts command-line args and environment variables through `AppOptions`. Common settings include:

- room name
- participant identity/name
- viewer port
- capture fps
- LiveKit URL
- API key / secret
- explicit paths for `livekit-server.exe` or `livekit_ffi.dll`

The viewer reads equivalent environment variables for room, identity, URL, API key/secret, and optional FFI DLL override.

## Notes

- The viewer uses Direct3D 11 for rendering and can fall back to WARP on lower-end machines.
- The current pipeline prioritizes correctness and debuggability; further optimization work is planned for smoother sustained `1080p60` playback.
- The host project contains generated LiveKit binding files under `src/Generated/`, and the viewer links those generated definitions from the host project.
