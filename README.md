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
- `site/` — Responsive product website and a Go HTTP service with health/readiness endpoints, embedded static assets, and a systemd deployment unit.

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

### Website and Go service

Requirements: Go 1.23 or later.

```powershell
cd site
go run .
```

The site is available at `http://127.0.0.1:8080`. The service exposes `/healthz`, `/readyz`, and `/api/v1/site`. See [`site/DEPLOY.md`](site/DEPLOY.md) for Linux deployment and the current single-instance availability boundary.

## Client updates

Windows and Android check `https://charlen.top/api/v1/releases/{platform}` when the app starts/resumes and every 15 minutes while active. The manifest includes a version, notes, SHA-256, and a server-side download URL. Windows downloads and verifies the ZIP, then a small PowerShell helper replaces `Nearlink.exe` after the app exits and restarts it. Android downloads and verifies the APK, then hands it to Android's package installer; Android still requires the user to approve installation.

Pushing a tag like `v1.2.0` runs [the GitHub Actions release workflow](.github/workflows/release.yml): it builds both clients, creates a GitHub Release, and installs the APK, Windows ZIP, and manifests into `/var/lib/nearlink-site/releases` on the website server. Configure these repository Actions secrets first:

- `NEARLINK_ANDROID_KEYSTORE_BASE64`, `NEARLINK_ANDROID_KEYSTORE_PASSWORD`, `NEARLINK_ANDROID_KEY_ALIAS`, `NEARLINK_ANDROID_KEY_PASSWORD`.
- `NEARLINK_SERVER_HOST`, `NEARLINK_SERVER_USER`, `NEARLINK_SERVER_SSH_KEY`, `NEARLINK_SERVER_KNOWN_HOSTS`.

Android update compatibility depends on keeping the same signing key. The APK currently in `release/` is signed with this development machine's debug keystore; the workflow must use that same keystore for the first update or Android will reject it as a different signer. Back it up securely before using it for releases. Replacing it with a new key later requires a separate migration (typically uninstall and reinstall) for sideloaded users.

For this GitHub-hosted project, GitHub Actions is the recommended CI/release runner. Jenkins would add a server to maintain and is unnecessary for builds, signing, and SSH deployment at this scale. Use Jenkins only if you later need private-network build agents or centralized pipelines shared across multiple repositories.

## Transfer architecture

`windows/ReliableTransfer.cs` owns upload retries, chunk offsets, file hashing, download verification, and the sender's durable transfer journal. `windows/TransferHost.cs` exposes the authorized LAN endpoints. `windows/TransferStore.cs` persists resumable chunks and commits verified files to session history. `windows/RemoteDesktopSession.cs` and the local host both use the same C# transfer module. The Android desktop-upload flow speaks the same chunk protocol and carries a short-lived, device-bound approval ticket.

Downloads currently retry from the beginning into a temporary file; offset-based resume is implemented for uploads. Android-to-Android direct transfer uses its existing peer socket protocol and has not yet been migrated to the shared chunk protocol.

## Security and privacy

Transfers stay on the local network and are not end-to-end encrypted. HTTP traffic and stored files are not encrypted by this app. Pair only trusted devices and use a trusted network. Android-to-Windows file uploads require approval on the receiving computer.

No license is included yet. Public visibility on GitHub does not itself grant permission to reuse or redistribute the code.
