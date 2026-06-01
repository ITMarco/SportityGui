# SportityGui

A Windows desktop app for browsing [Sportity](https://webapp.sportity.com) rallying channels and events. Scrapes the Sportity website and presents channels, events, folders, files and messages in a clean, modern interface.

## Features

- Paste any Sportity **channel** or **event** URL — auto-detected
- Browse the full folder/file/message tree
- **Unread indicators** — new items are highlighted in bold with a blue dot; folders bubble up the badge when they contain unread children
- **Single-click a file** → downloads it and shows metadata (first seen, download date, local path)
- **Double-click a file** → opens it with your default application
- **Auto-download** toggle — downloads all files automatically when loading an event
- **Bulk download** via right-click on any folder
- **Search/filter** the tree by name
- **Recent URLs** dropdown — previously used URLs are remembered
- **F5** to refresh the current URL
- Status bar with real-time download progress and cancel button

## Tech stack

| | |
|---|---|
| Framework | .NET 10, WPF |
| Theme | ModernWpf (Windows 11 Fluent Design) |
| HTML scraping | AngleSharp |
| MVVM | CommunityToolkit.Mvvm |
| Icons | MahApps.Metro.IconPacks.Material |

## Data files

These files live next to the `.exe` and are **not** committed to git:

| File | Purpose |
|---|---|
| `state.json` | Read/unread tracking, URL history, download records |
| `preferences.json` | User preferences (download folder, theme, auto-refresh) |
| `secrets.json` | Placeholder for future credentials |

Delete `state.json` to reset read/unread state without losing your preferences.

## Building

```
dotnet build src/SportityGui/SportityGui.csproj
```

## Publishing a standalone .exe

```
dotnet publish src/SportityGui/SportityGui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The `.exe` will be in `src/SportityGui/bin/Release/net10.0-windows/win-x64/publish/`.

## Milestones

- [x] **Milestone 1** — Project scaffold, URL input, theme, state service, HTTP connectivity
- [x] **Milestone 2** — Scraper + tree view: channel event list, folder/file/text hierarchy, icons, expand/collapse
- [x] **Milestone 3** — Read/unread state + first-seen tracking; unread badges bubble up through folders
- [x] **Milestone 4** — File download + metadata panel; double-click opens with default app; downloads go to `ChannelCode/EventName/` subfolders; recent URL history with per-entry delete button
- [x] **Milestone 5** — Text/message rendering; search/filter box; channel URL scraping fixed; file extensions always correct
- [ ] Milestone 6 — Preferences dialog + auto-refresh
