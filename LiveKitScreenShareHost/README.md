# LiveKit Screen Share Host

This project hosts a local LiveKit server and publishes the primary Windows screen into a room by using the official LiveKit Rust FFI DLL from `..\rust-sdks\livekit-ffi`.

## What it does

- starts `livekit-server.exe --dev` when `LIVEKIT_HOST_SERVER=true`
- captures the primary screen continuously
- pushes frames into LiveKit at up to 60 fps
- uses `LiveKit.Proto.OwnedVideoBuffer` instances returned from `VideoConvertRequest`
- publishes the track as `SOURCE_SCREENSHARE`

## Expected layout

- `..\rust-sdks` should contain the cloned LiveKit Rust SDK repo
- `src\native\livekit_ffi.dll` should contain the built FFI DLL copied from `..\rust-sdks\target\release`
- `src\tools\livekit-server.exe` should contain the LiveKit server binary if you want the app to host the server itself

## Build the FFI DLL

```powershell
cd D:\Development\WindowsApps\LiveKitScreenShareHost
.\build-livekit-ffi.ps1
```

If Rust is not installed yet, install `rustup` first and then rerun the script.

## Get a local LiveKit server

Download a Windows `livekit-server.exe` release binary from the official LiveKit releases page and place it at:

```text
D:\Development\WindowsApps\LiveKitScreenShareHost\src\tools\livekit-server.exe
```

## Run

```powershell
cd D:\Development\WindowsApps\LiveKitScreenShareHost
dotnet run --project .\src\LiveKitScreenShareHost.csproj
```

Useful environment variables:

- `LIVEKIT_URL` default: `ws://127.0.0.1:7880`
- `LIVEKIT_API_KEY` default: `devkey`
- `LIVEKIT_API_SECRET` default: `secret`
- `LIVEKIT_ROOM` default: `screen-room`
- `LIVEKIT_IDENTITY` default: `screen-host-<machine>`
- `LIVEKIT_FPS` default: `60`
- `LIVEKIT_HOST_SERVER` default: `true`
- `LIVEKIT_SERVER_EXE` optional explicit path to `livekit-server.exe`
- `LIVEKIT_FFI_DLL` optional explicit path to `livekit_ffi.dll`
