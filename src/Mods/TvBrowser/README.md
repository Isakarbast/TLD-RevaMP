# TV Browser

A web browser on the game's TV, used with the keyboard and mouse. In a session everyone sees the same page, and TLD RevaMP's
replays show what was on the TV. An example of the multiplayer mod API's shared records and events, and of replay
channels (API 4).

## Using it

Switch the TV on (its own button), look at it from within 4 m and press **U**. The keyboard and mouse now go to the
page until **Esc** (the view stays still meanwhile):

| Keys | What they do |
| --- | --- |
| typing, Backspace, Delete, Tab, Enter, the arrows, Home, End | as in any browser, into the page |
| the mouse | moves a pointer on the page; click, double-click, drag, right-click. Everyone sees the pointer |
| the mouse wheel | scrolls what the pointer is over (with Shift: sideways) |
| Page Up / Page Down, Space | scroll |
| **F2**, then the letters on a label | link hints: click a link or button, or go into a field, without a mouse |
| Ctrl+L or F6 | the address bar: an address, or words to search for (DuckDuckGo's HTML search by default) |
| Alt+← / Alt+→, the mouse's side buttons | back / forward |
| Ctrl and + / - / 0 | zoom in, out, back to 100 % (for everyone at the TV) |
| Ctrl+C | copy the selected text to your clipboard |
| F5 or Ctrl+R | reload |
| Ctrl+H | the TV's start page |
| Ctrl+V | paste |

The first time, the TV asks before downloading the browser, checked against a pinned SHA-256 and kept in
`BepInEx/config/TvBrowser/`: the Chromium Embedded Framework (CEF), from Spotify's CEF builds. The browser is
`tvbrowser-host` (`Host/`), a small program of this mod's on CEF that the mod carries in its DLL. Its pages are drawn
off-screen by a normal Chromium, not a headless one, so sites see an ordinary browser.

- **Under Proton**: CEF's Linux build (about 320 MB to download, 330 MB unpacked: only what the browser needs, and its
  1.2 GB of debugging data left out). It runs outside Wine.
- **On Windows**: CEF's Windows build (about 170 MB to download, 360 MB unpacked), sandboxed like Chrome: CEF's
  `bootstrap.exe`, as `tvbrowser-host.exe`, sets up Chromium's sandbox and then loads `tvbrowser-host.dll`.

The start page has links to a few sites to begin with.

A page's sound comes from its TV, as a sound in the game: quieter farther away (gone beyond 20 m), and with the game's
own volume. The browser plays nothing itself; `tvbrowser-host` gets each page's sound from CEF and passes it to the
mod (`Sound.cs`). Like the picture, each machine plays its own page's sound.

Settings (`BepInEx/config/tldrevamp.tvbrowser.cfg`): the key, the pointer's speed, how close you must be, how far away a TV keeps its page
open (25 m), the picture rate (15 a second at most) and quality, the search address, and the sound's volume and how
far away a TV is heard.

### YouTube

In Chromium's headless shell (every version of this mod before CEF) YouTube's videos stop after about 40 seconds with
"Something went wrong": YouTube stops sending the video to a headless Chromium, and to any Chromium under Wine,
whatever the mod does. That is why the browser is CEF, and why Proton runs CEF's Linux build, outside Wine: a normal
Chromium, which played past that point (checked 2026-11-05 outside the game). YouTube may also ask a browser that isn't signed in to
"confirm you're not a bot" when its network has sent it many automated requests; signing in on the TV is up to the
player (the profile is thrown away when the game closes). The mod doesn't try to hide what the browser is.

## How it is synced

Nothing is streamed. Each machine runs its own browser, and the TV's page is the same on every machine because the
same things go into it:

- **The shared state** (`TvState` in `Core.cs`, one record per TV, keyed by the TV's world id like the head unit's):
  the address, a navigation counter, which player has the keyboard (the driver), the scroll position and the zoom
  (pages lay out alike only at the same zoom).
- **Keys and the mouse** go to everyone as `keys` events (batched every 0.1 s; of a run of pointer moves only the
  last). Every machine puts the same keys and clicks into the same page at the same fixed size (1024x576), so
  typing, Tab, Enter, clicks and the F2 hints land on the same things. Hints run in the page and take their keys
  from it, so they need no extra sync. The pointer is a place on the TV's picture, so it lands on the same thing at
  any zoom; each machine draws it into its own picture, so everyone sees where the driver points.
- **The driver's browser decides**: when its page goes somewhere (a link, a form, a redirect) it shares the address,
  and others follow; it shares its scroll position up to 4 times a second, and others move when they are more than
  24 px away. One player has the keyboard at a time; another can take it.

Each machine checks addresses itself: only `http` and `https`, no user names in them, and no local or private hosts
(localhost, 127.x, 10.x, 192.168.x and the like). Its browser also refuses those hosts itself, for links and
redirects inside pages too. Downloads, pop-ups (opened in the same TV instead) and JavaScript dialogs are turned off.

Only addresses, keys and scroll positions pass between players. A page's sign-ins stay on each machine: the profile
is a temporary folder, emptied when the game closes.

## Replays

`ReplayTv.cs` records each TV near the player as a replay channel: a half-size, low-quality picture of the page
whenever it changed (at most every 2 s), and "off" when it goes off. Playback puts the picture at the playhead on the
TV's copy. Replays are local files, but they do hold those pictures of whatever the pages showed.

## Files

| File | What it does |
| --- | --- |
| `Plugin.cs` | Settings, API registration, the per-frame runner, a `TvScreen` on every display |
| `TvScreen.cs` | On every display: its shared state, its page, showing the picture, following the driver |
| `Keyboard.cs` | Taking the keyboard, keys into commands, the address bar, sending keys to others, the on-screen help |
| `Core.cs` | The shared state and its edits, addresses and their checks, keys, commands (pure, unit tested) |
| `Browser.cs` | Downloading, starting and stopping the browser; its pages; pop-ups |
| `Bzip2.cs`, `Tar.cs` | Unpacking CEF's `.tar.bz2` (unit tested) |
| `Host/` | `tvbrowser-host` (C++ on CEF) and how it is built (`build.sh` for Linux, `build-windows.sh` with mingw-w64 for Windows); `linux-x64/` and `windows-x64/` are the builds the DLL carries |
| `Cdp.cs`, `Json.cs` | The DevTools protocol over a WebSocket of its own (no libraries) |
| `Tab.cs`, `PageInput.cs` | One TV's page: picture, addresses, keys and the wheel as DevTools input |
| `Pages.cs` | The F2 link-hints script and the start page |
| `ReplayTv.cs` | The replay channel |
| `Sound.cs` | A page's sound, played from its TV |
| `Probe.cs` | Checks for `tools/tvbrowser.py` and `tools/tvsound.py` through the debug bridge |

## Tests

```sh
dotnet run --project tools/unit/TvBrowserCoreTest.csproj                            # state, edits, addresses, keys
TVB_CHROME=/path/to/chrome-headless-shell dotnet run --project tools/unit/TvBrowserPageTest.csproj   # in a real browser
tools/tvbrowser.py                                                                  # two players in the game
tools/tvsound.py [--youtube]                                                        # the sound, in one game
```

Under Proton the game runs the Linux build. An empty file `BepInEx/config/TvBrowser/windows-build` makes it take the
Windows build instead (without the sandbox, which Wine lacks), to test that build on Linux. YouTube still stops there:
it is a Chromium under Wine.

## Licence

GPL-3.0, like TLD RevaMP. CEF is BSD-licensed and is downloaded, not shipped.
