# SportityGui

A Windows desktop app for browsing [Sportity](https://webapp.sportity.com) rallying channels and events. Scrapes the Sportity website and presents channels, events, folders, files and messages in a clean, modern interface with full offline download support.

## Features

### Channels & events
- Paste any Sportity **channel** or **event** URL — auto-detected and added to the sidebar
- Type just a **channel code** (e.g. `ELE`) — the app tries the full channel URL automatically; falls back to an event URL if the channel isn't found
- **Multiple channels** loaded simultaneously in the left panel, each collapsible
- **Single-event channels** (pages that serve files directly at the channel URL) are detected and loaded automatically
- Newest channel always appears at the top; channels are **drag-and-drop reorderable**
- All loaded channels and their order **restore on next launch** (re-scraped in the background with spinners)

### Left panel
- Bold channel name header with **· N events** count, collapse ▼/▶ chevron, ⟳ refresh, and ✕ remove buttons
- Highlighted selection shows the currently open event
- **Remove channel** dialog: keep or also delete all downloaded files from that channel
- Loading spinner per channel header while scraping

### Center tree
- Hierarchical folder/file/message tree with **NEW** corner-ribbon badge on unread items
- Badges bubble up through parent folders; disappear immediately when all children are read
- **Single-click** a file → show metadata only (name, first seen, download time, local path)
- **Double-click** a file → download if needed, then open with the default application
- **Auto-download** toggle — downloads all files automatically when loading an event
- Bulk download via right-click → *Download all in folder*
- Right-click → *Copy URL* or *Open containing folder*
- Search/filter box above the tree

### Downloads
- Files saved to `DownloadFolder / ChannelCode / EventName /`; no channel subfolder when channel code is unknown
- Event folders use the display name (not the internal UUID)
- **Download** button in the details panel for single files; switches to **Open** once downloaded

### Auto-refresh
- Per-channel/event refresh interval — `[− N min +]` in the toolbar shows and edits the active channel's interval
- Countdown displayed in the status bar; fires in the background without blocking the UI
- Global default interval set in Preferences applies to newly added channels

### System tray
- **Start minimised** with `--minimized` command-line flag
- **Minimize to tray** preference — hides from taskbar, shows in system tray with correct app icon
- Double-click or context menu to restore; balloon notifications for new files and events

### Preferences
- Light / Dark / Follow system theme (VSCode-inspired dark palette)
- Download folder picker (prompted on first run if not set)
- Auto-download toggle
- Auto-refresh interval (global default)
- Minimize-to-tray option

### Notifications
- Windows balloon notification when new files appear in a refreshed event
- Notification when new events appear in a refreshed channel

## Tech stack

| | |
|---|---|
| Framework | .NET 10, WPF |
| Theme | ModernWpf 0.9.6 (Windows 11 Fluent Design) |
| HTML scraping | AngleSharp |
| MVVM | CommunityToolkit.Mvvm 8.x |
| Icons | MahApps.Metro.IconPacks.Material |
| Drag-and-drop | gong-wpf-dragdrop 4.0 |
| System tray | System.Windows.Forms (UseWindowsForms) |

## Data files

These files live next to the `.exe` and are **not** committed to git:

| File | Purpose |
|---|---|
| `state.json` | Read/unread tracking, URL history, download records, saved channels |
| `preferences.json` | Theme, download folder, auto-refresh interval, auto-download, minimize-to-tray |

Delete `state.json` to reset all read/unread state and channel list without losing preferences.

## Building

```
dotnet build src/SportityGui/SportityGui.csproj
```

## Publishing a standalone .exe

```
dotnet publish src/SportityGui/SportityGui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The `.exe` will be in `src/SportityGui/bin/Release/net10.0-windows/win-x64/publish/`.

## Command-line flags

| Flag | Effect |
|---|---|
| `--minimized` | Start directly to system tray without showing the window |

## Milestones

- [x] **Milestone 1** — Project scaffold, URL input, theme, state service, HTTP connectivity
- [x] **Milestone 2** — Scraper + tree view: channel event list, folder/file/text hierarchy, icons, expand/collapse
- [x] **Milestone 3** — Read/unread state + first-seen tracking; unread badges bubble up through folders
- [x] **Milestone 4** — File download + metadata panel; double-click opens; downloads organised by channel/event; recent URL history
- [x] **Milestone 5** — Text/message rendering; search/filter; channel scraping fixes; correct file extensions
- [x] **Milestone 6** — Preferences dialog: theme, download folder, auto-refresh interval, auto-download toggle
- [x] **Milestone 7** — System tray: `--minimized` flag, minimize-to-tray preference, balloon notifications, NEW corner ribbon badge, per-folder badge bubbling
- [x] **Milestone 8** — Multi-channel view: multiple channels in left panel, collapse/expand, drag-and-drop reorder, per-channel refresh timers, remove with optional file cleanup, startup restoration, single-click = details only / double-click = download + open
- [x] **Milestone 9** — Smart URL shorthand: single-word input tried as channel code then event; auto-prefix of full Sportity URL
