# Sonic Search

A super-fast file search tool for NTFS drives on Windows. Instead of walking folders like normal file search, it reads the NTFS **Master File Table (MFT)** directly, so it can index millions of files in seconds and search them instantly.

## How it works

- **MFT-based indexing** — one pass over the raw NTFS volume via the `NtfsReader` library builds an in-memory index of every file and folder on the drive.
- **USN Journal for incremental updates** — after the initial scan, Sonic Search polls the NTFS USN change journal so new/renamed/deleted files update the index without a full re-scan.
- **Filename-prefix trie** — accelerates the common case of typing the start of a filename, on top of a parallel full-snapshot scan for everything else (substrings, wildcards, qualifiers).
- **Content search** — optional full-text search inside file contents (`content:`), run in parallel across candidate files.

## Features

- Instant search with Contains / Starts With / Exact / Regex / wildcard (`*`, `?`) matching modes
- Filter qualifiers you can combine freely:
  - `ext:` / `type:` — by file extension
  - `folder:` / `file:` — folders or files only
  - `size:` — e.g. `size:>10mb`, `size:<500kb`
  - `date:` / `modified:` — e.g. `date:today`, `date:>7d`
  - `location:` / `path:` / `in:` — e.g. `location:desktop`, `path:downloads`, or any custom path
  - `content:` / `text:` — search inside file contents
- Autocomplete with search history — including recalling filter values you've used before (e.g. typing `loc` suggests `location:desktop` if you searched that before)
- Global hotkey to summon the search window from anywhere
- File preview, icons, and execution-frequency-based result ranking
- Fully open source

See the in-app **Settings → Filters Help** tab for the full filter reference, and **Settings → About** for version info.

## Requirements

- Windows with an NTFS-formatted drive
- .NET Framework 4.8
- **Must be run as Administrator** — reading the raw NTFS volume and the USN journal requires elevation

## Building

Open `SonicSearch.sln` in Visual Studio, or from the command line:

```bash
dotnet build SonicSearch.sln -c Release
```

The solution has two projects:
- `NtfsReader` — the MFT/USN reading library (based on Jeroen Kessels' JkDefrag, LGPL-licensed)
- `SonicSearch` — the WPF application

## Screenshots

_Coming soon — the UI has since been rebuilt from the ground up (WinForms → WPF, dark theme). The old screenshots in `screenshots/` are from a previous version and no longer reflect the current app._
