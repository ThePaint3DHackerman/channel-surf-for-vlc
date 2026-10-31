# Channel Surf

Double-click **Channel Surf.exe**. VLC is already installed on this PC, and no installer is needed.

- Type in the search box to find a channel, country, language, or category.
- Click a channel once to watch it in its own VLC video window.
- Click **☆** to save a favorite. Favorites and recently played are remembered on this PC.
- Use the country, language, and category menus to narrow the list.
- **Previous / Next** flips through the current search/filter results, including favorites.
- If a channel has multiple links, use **Stream source** to try another. **Retry** reconnects the selected source.
- **Auto-reconnect dropped streams** retries an interrupted channel up to three times with a delay between attempts. The final attempt can try another source for that channel. Pause, Stop, and changing channels cancel recovery; turn the checkbox off to disable it.
- Use the volume slider, Pause, Stop, Show player, and Fullscreen controls beside the channel list.

## Shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl + F | Focus channel search |
| Enter | Play the highlighted channel |
| Alt + Left / Right | Previous / next while in Channel Surf |
| Ctrl + Alt + PageUp / PageDown | Previous / next, including while watching VLC |
| Ctrl + Alt + Space | Pause / resume, including while watching VLC |
| Esc in search | Clear the search |

Keep Channel Surf open while watching. Closing it closes only the VLC player it started. Your other VLC sessions continue independently. If another application has claimed a global shortcut, use the on-screen controls or Alt + Left / Right in Channel Surf.

The library uses the playlist you supplied: [iptv-org's global playlist](https://iptv-org.github.io/iptv/index.m3u). Country, language, and category information comes from the [iptv-org API](https://github.com/iptv-org/api). Stream variants are grouped into one channel. Some public streams can be offline or limited by region; try another source or channel if one doesn't connect.

The channel list is cached for six hours. **Refresh** downloads the latest list. The saved list remains usable if the library service is offline; watching a stream still needs a working connection. Data is stored in `%LOCALAPPDATA%\ChannelSurf` (`state.json` for favorites/recents, plus cached library files). Adult channels are hidden unless enabled in the sidebar.

The playback status shows when a stream drops, when the app is reconnecting, and when retries are exhausted. Source descriptions include the feed label and server hostname so alternate links are easier to distinguish. Playback diagnostics in the same data folder record commands, state changes, and retries without storing complete stream URLs.

## Source and rebuilding

The included C# and XAML files are the source. The app uses Windows .NET Framework 4.8, which is present on this PC. From PowerShell in this folder, run `./Build.ps1` with Channel Surf closed. It uses the Windows C# compiler; no package downloads are required.

VLC remains a separate installed application. The companion controls only its own VLC process through an authenticated interface bound to `127.0.0.1`; the random password lasts for that session. It launches VLC without loading or saving its usual configuration and uses a native VLC video window.

VLC control reference: [VideoLAN HTTP interface documentation](https://github.com/videolan/vlc-3.0/blob/master/share/lua/http/requests/README.txt).
