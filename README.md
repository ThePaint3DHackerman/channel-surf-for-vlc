# Channel Surf for VLC

A Windows channel browser that opens streams in a separate VLC player window. Search and filter channels, save favorites, switch sources, and reconnect interrupted streams.

This initial source snapshot is **version 1.1.0.0**, recovered from the existing complete `Channel Surf.zip` distribution. See [UPSTREAM-WORK-STATUS.md](UPSTREAM-WORK-STATUS.md) for the separate unfinished newer work.

## Requirements

- Windows with .NET Framework 4.8 and Windows PowerShell 5.1.
- [VLC](https://www.videolan.org/vlc/) installed separately for playback.
- An internet connection for channel data and streams.

VLC is not bundled in this repository.

## Build and run

Open PowerShell in this folder, with any previous Channel Surf build closed:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
& '.\Channel Surf.exe'
```

The execution-policy setting applies only to that PowerShell process. Review a downloaded script before running it. The build script uses the Windows .NET Framework C# compiler and generates `Channel Surf.exe` and `ChannelSurf.ico`; no NuGet packages are required. Keep `Channel Surf.exe.config` and `ChannelSurf.ico` beside the executable when copying it elsewhere.

The build script and application source are kept together from the complete version 1.1 distribution. No rolling-buffer feature is included in this version.

## Use and local data

See [USER-GUIDE.md](USER-GUIDE.md) for playback controls and keyboard shortcuts. Its original first paragraph describes an already-built copy; build from source first when using this repository.

Channel data comes from the external [iptv-org playlist](https://iptv-org.github.io/iptv/index.m3u) and [API](https://github.com/iptv-org/api). Streams can be unavailable or restricted by region. Favorites, recently played channels, cached library data, and diagnostics are stored under `%LOCALAPPDATA%\ChannelSurf`.

VLC control uses a local authenticated interface on `127.0.0.1` with a new randomly generated password each session. The source contains password-generation code, not a saved account password.

No saved favorites, cache files, playlists, diagnostics, recordings, or stream credentials are included in this repository.

## License

The original application code is licensed under [GNU GPL version 3 only](LICENSE) (`GPL-3.0-only`). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party software and channel data, which have their own terms.
