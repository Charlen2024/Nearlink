# Nearlink

Nearlink is a local-network messaging and file-transfer app for Windows and Android. Devices communicate directly over the LAN; the project does not require a cloud relay.

## Features

- Pair and manage multiple Windows and Android devices.
- Exchange messages and files in real time, with session history and image previews.
- Transfer files between paired Windows computers and between Android and Windows.
- Upload in 4 MiB chunks, query confirmed offsets after connection loss, and resume the active transfer.
- Verify file length and SHA-256 before committing received files; use atomic file moves and idempotent transfer IDs to avoid duplicate history entries after a lost response.
- Preserve incoming-transfer approval for Android-to-Windows file sends.

## Project layout

- `windows/` — C# / WinUI 3 desktop app, LAN host, paired desktop client, reliable transfer module, and durable transfer store.
- `android/` — Kotlin / Material Android client and the matching resumable Android-to-Windows upload adapter.

## Build

### Windows

Requirements: Windows, .NET 8 SDK, and network access to restore the Windows App SDK NuGet package.

```powershell
dotnet publish windows/Nearlink.Native.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Android

Requirements: JDK 17 or 21 and Android SDK Platform 35 / Build Tools 35.

```powershell
cd android
.\gradlew.bat :app:assembleDebug
```

The debug APK is written to `android/app/build/outputs/apk/debug/app-debug.apk`.

## Transfer architecture

`windows/ReliableTransfer.cs` owns upload retries, chunk offsets, file hashing, download verification, and the sender's durable transfer journal. `windows/TransferHost.cs` exposes the authorized LAN endpoints. `windows/TransferStore.cs` persists resumable chunks and commits verified files to session history. `windows/RemoteDesktopSession.cs` and the local host both use the same C# transfer module. The Android desktop-upload flow speaks the same chunk protocol and carries a short-lived, device-bound approval ticket.

Downloads currently retry from the beginning into a temporary file; offset-based resume is implemented for uploads. Android-to-Android direct transfer uses its existing peer socket protocol and has not yet been migrated to the shared chunk protocol.

## Security and privacy

Transfers stay on the local network and are not end-to-end encrypted. HTTP traffic and stored files are not encrypted by this app. Pair only trusted devices and use a trusted network. Android-to-Windows file uploads require approval on the receiving computer.

No license is included yet. Public visibility on GitHub does not itself grant permission to reuse or redistribute the code.
