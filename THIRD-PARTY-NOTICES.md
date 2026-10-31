# External software and data

This repository contains the application's C# and XAML source and its build resources. No VLC binary, downloaded playlist, channel database, stream, or recording is included.

- **VLC:** installed separately; controlled through its local HTTP interface and native player window. See [VideoLAN](https://www.videolan.org/vlc/) and [VLC's HTTP control reference](https://github.com/videolan/vlc-3.0/blob/master/share/lua/http/requests/README.txt) for that external project.
- **.NET Framework:** supplied by Windows/Microsoft; this source repository does not include the framework or its compiler.
- **iptv-org:** the application downloads channel listings from the [playlist project](https://github.com/iptv-org/iptv) and metadata from the [API project](https://github.com/iptv-org/api). Their terms and the relevant broadcasters' terms apply separately. The application source license, once chosen, will not grant rights to third-party streams or metadata.

The original application source is licensed under GPL-3.0-only; see `LICENSE`. That selection does not change third-party terms.
