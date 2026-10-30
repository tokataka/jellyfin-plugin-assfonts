# Jellyfin ASS Fonts

Embeds subset fonts into ASS/SSA subtitles served by Jellyfin using [assfonts](https://github.com/wyzdwdz/assfonts). Uses fonts attached to subtitles and a configured font directory. Generated subtitles are cached.

## Installation

Add this URL in **Dashboard → Plugins → Repositories**:

```text
https://raw.githubusercontent.com/tokataka/jellyfin-plugin-repository/main/manifest.json
```

Install **ASS Fonts** from the catalog and restart Jellyfin.

Set the font directory on the plugin's configuration page.

## Requirements

Jellyfin 12.4.0.0 or later (.NET 10) on one of the following platforms:

- Linux x64/ARM64: glibc 2.35+ and libstdc++ with GLIBCXX_3.4.30.
- Windows x64/ARM64.
- macOS x64/ARM64.

## License

- [GPL-3.0-or-later](LICENSE).
- Bundled [assfonts](https://github.com/wyzdwdz/assfonts): GPL-3.0-or-later. See [third-party notices](THIRD-PARTY-NOTICES.txt).
