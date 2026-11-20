# Search for Windows — rewrite plan

Fork: `unnanego/Search` (upstream `driceroland/Search`, spec baseline commit `9cc5f07`, 2026-11-20).

## 1. What we're porting

The Mac app is ~107k lines of Swift across ~190 files, one file per concern (`Sources/Search/`). Its stack:

| Mac | Role |
|---|---|
| SwiftUI + a little AppKit | All chrome: tab column/strip, omnibox, panels, settings |
| WKWebView / WKWebsiteDataStore | Pages, cookies, profiles, private tabs |
| WKContentRuleList | Ad/tracker blocker (`Shield.swift`) running inside the network stack |
| WKWebExtension + 6.8k lines of shims | Chrome extensions on WebKit (`Extensions*.swift`, `ExtensionShims.swift`) |
| Keychain, AuthenticationServices | Passwords, passkeys |
| JSON files in `~/Library/Application Support/Search/` | History, bookmarks, session, spaces, pins, hidden elements |
| Unix socket + `./bench`, `bench-mcp`, `search-mcp` | Test driving and coding-agent control; ~200 Python tests in `Tests/` |
| `search-ai-engine.cpp` (llama.cpp, sandboxed) | Local AI summaries |

The ethos matters as much as the features: use the engine already in the OS, keep it small and fast, no telemetry, no account, quiet by default, and keep most features off until someone turns them on.

## 2. Core decisions

### Engine: WebView2 (Evergreen)
This plays the role WebKit plays on the Mac: Windows 11 already ships it, Microsoft keeps it updated, and the app doesn't have to bundle Chromium. CEF would give more control, but it costs over 150 MB, we'd ship every Chromium security update ourselves, and its extension support is being wound down. WebView2 also gives us several Mac subsystems for nothing:

- **Extensions are Chromium's own** (`AreBrowserExtensionsEnabled`, `Profile.AddBrowserExtensionAsync`). Most of the 6.8k lines of `ExtensionShims.swift` don't need porting at all. What's left is the browser-side UI: the action button, popups, side panel, and Web Store install.
- **Passkeys and WebAuthn** go through Windows Hello natively, so `Passkeys.swift` mostly disappears.
- Profiles (`ProfileName`), InPrivate, tab suspension (`TrySuspendAsync`, `MemoryUsageTargetLevel`), Find (`CoreWebView2Find`), DevTools, notifications (`NotificationReceived`), downloads (`DownloadStarting`), and permission requests all come with the engine.

Privacy work WebView2 forces on us: turn off SmartScreen URL reputation (`IsReputationCheckingRequired = false`) and Edge's built-in autofill/password autosave unless we use them on purpose. Also document that the runtime's own diagnostic data follows the Windows diagnostics setting. The README's "nothing leaves your machine" claims have to be re-checked against WebView2.

### UI: C# / .NET 11 + WinUI 3 (Windows App SDK)
- WinUI 3's WebView2 is composition-hosted, so the omnibox, peek panels, the site card, and popovers can draw over the page. WPF and WinForms have airspace problems there.
- We get Mica/Acrylic for the "glass" window and space washes, a custom title bar (`AppWindow.TitleBar.ExtendsContentIntoTitleBar`), and snap layouts.
- MVVM via CommunityToolkit.Mvvm. `Browser.swift`'s observable state maps to `ObservableObject`s.
- Ship **framework-dependent** on the Windows App Runtime, with ReadyToRun or trimming, to keep the download in the "small" spirit. If startup misses the target (§7), measure NativeAOT.

### Repo layout
The Windows solution lives at the **repo root**, beside the Swift app. The Swift files stay where upstream has them (`Sources/`, `Tests/`, `Engine/`, `Installer/`, `CHANGELOG.md`, `ROADMAP.md`), so `git fetch upstream` merges cleanly and the Mac code stays the living spec. The Windows folders use names that can't collide with upstream's on a case-insensitive file system: no `tests/` beside `Tests/`, no `installer/` beside `Installer/`.

```
Search.sln
Directory.Build.props       # shared .NET settings, nullable, warnings-as-errors
src/Search.Core/            # pure model, no UI, no WebView2: tabs, spaces, pins, session, history, bookmarks, sync merge, omnibox ranking, shield list compile
src/Search.Engine/          # WebView2 wrapper: environments, profiles, Tab ↔ CoreWebView2, scripts, extensions host
src/Search.App/             # WinUI 3: window, column, strip, panels, settings
src/Search.Bench/           # control socket (AF_UNIX, available since Win10 1803) — same protocol as Bench.swift
src/Search.AiEngine/        # llama.cpp host, later
test/Search.Core.Tests/     # xUnit ("test", not "tests": "Tests/" is upstream's)
packaging/                  # Velopack (or MSIX) + signing ("Installer/" is upstream's)
spikes/                     # Phase 0 throwaway code
```

`.gitignore` gets the .NET entries (`bin/`, `obj/`, `*.user`, `.vs/`) added below upstream's, as an addition so that merges stay clean.

`Search.Core` can be ported straight from the Swift model types (`Browser`, `Spaces`, `Pins`, `PinFolders`, `Session`, `History`, `Bookmarks`, `SyncMerge`, `Omnibox`, `Fuzzy`, `Address`, `Registrable`) and unit-tested without a window.

### On-disk formats stay compatible
`%LOCALAPPDATA%\Search\` gets the same JSON shapes as the Mac (`Store.swift`). That way **folder Sync (`Sync.swift`/`SyncMerge.swift`) works between a Mac and a PC**, and a user can carry their data across. The test-world isolation rule from `Store.swift` (`SEARCH_PROBE`, never touch the real profile from a dev build) carries over word for word.

### Naming
The README asks forks to rename before distributing. Pick a working name and icon before any public build.

## 3. Mapping, subsystem by subsystem

| Mac file(s) | Windows approach | Effort |
|---|---|---|
| `Tab.swift`, lazy web views | One `CoreWebView2Controller` per tab, created on first show; restored tabs cost nothing until they're clicked | M |
| `TabBar`, `Side`, `Fold`, `SideIcons`, `TabDrag`, `TabLift` | WinUI custom controls; drag via `CanDrag`/composition | L |
| `Omnibox`, `Address`, `Fuzzy`, `Keyword`, `SiteSearch`, `AddressCommands` | Port logic to Core; UI as a popup over the page | M |
| `Shield.swift`, `YouTubeAds.swift` | **Decided (spike 2):** a bundled, hidden MV3 `declarativeNetRequest` extension. It blocks inside Chromium's network stack and was faster than `WebResourceRequested`. Per-site off is a dynamic `allowAllRequests` rule | M |
| `Curtain`, `SiteStyles`, `Hidden` | `AddScriptToExecuteOnDocumentCreatedAsync` injecting a stylesheet at document start | S |
| `Reader`, `Markdown`, `Dusk*` | The JS and HTML are mostly portable; dark mode gets `PreferredColorScheme` plus Dusk's own script | S–M |
| `Spaces`, `SpaceGradient`, `SpaceRoutes`, `SpaceSwipe` | Core logic ports; swipe → precision-touchpad `ManipulationDelta` / `PointerWheelChanged` horizontal | M |
| `Pins`, `PinFolders`, `LiveFolders`, `Archive`, `Closing`, `Sleep` | Core logic ports. **Sleep = `Close()` the WebView2** (spike 3: Suspend frees ~nothing, closing frees ~28 MB a tab, waking takes ~60 ms); Suspend only for briefly hidden tabs | M |
| `Split`, `SplitView`, `PaneStage` | A grid of WebView2 hosts with draggable dividers | M |
| `Peek`, `Little`, `LinkChooser*` | Overlay panel / small secondary `AppWindow` | M |
| `Float`, `NativePiP` | **Decided (spike 4):** Chromium's native video PiP (always on top). Document PiP is unavailable, so a richer floating player is deferred | S |
| `Extensions*`, `Crx`, `StoreRelay`, `ExtensionsUI`, `ExtensionPopup` | `AddBrowserExtensionAsync` on unpacked dirs; keep the CRX3 download + signature check (`Crx.swift`); popup = a WebView2 in a flyout at `chrome-extension://id/popup.html` (works, spike 1). Nearly all APIs are native; `tabs.create` arrives as `NewWindowRequested`. To fill: a small install-time `tabs`/`windows` shim (every WebView2 looks like a one-tab popup window), `captureVisibleTab`, `commands`, and our bookmarks/history behind those APIs | M |
| `ExtensionNative` | Chrome's native messaging: registry `HKCU\Software\Google\Chrome\NativeMessagingHosts`; check whether WebView2 supports it natively | S–M |
| `Vault`, `Passwords`, `Accounts`, `Forms` | Credentials in a DPAPI-encrypted store or Windows Credential Manager; our own fill UI under the field, as on the Mac (Edge autofill off) | M |
| `Passkeys` | Native through WebView2 + Windows Hello | S |
| `ApplePasswords*` | Drop. Optionally use iCloud for Windows' Passwords later; out of scope | — |
| `History`, `Recall`, `Bookmarks*`, `BookmarksBar` | Core port + panels | M |
| `Fetching`, `Loot`, `DownloadsFloat` | `DownloadStarting` with our own UI | M |
| `Import*` | Chromium/Gecko formats are the same; Windows paths. Arc (Windows) `StorableSidebar.json` under `%LOCALAPPDATA%\Packages\TheBrowserCompany.Arc*`. **Chrome passwords**: since Chrome 127, app-bound encryption blocks DPAPI decryption, so ask for a CSV export instead (as `Safari direct` already falls back to) | M |
| `Links`, `Launcher`, `HotKey` | Register as a default-browser candidate (`RegisteredApplications`, `Capabilities\URLAssociations`), with the user choosing in Settings; global hotkey via `RegisterHotKey` | S–M |
| `Settings`, `SettingsIndex`, `Prefs`, `Shortcuts`, `ShortcutsPage` | Port the index table; ⌘ maps to Ctrl, ⌥ to Alt; check every shortcut against Windows conventions (Ctrl+L, Ctrl+Tab, F5, Alt+←) | M |
| `Notifications`, `MediaKeys`, `Player` | Windows toast via `AppNotificationManager`; SMTC for media keys | S |
| `Profiles`, `OneWindow`, `Windows`, `Twins` | WebView2 profiles; multiple `AppWindow`s | M |
| `Proxy` | `AdditionalBrowserArguments --proxy-server` (each proxy setting needs its own environment, so restart on change) | S |
| `Inspector` | `OpenDevToolsWindow()` | S |
| `Snap` (Capture) | `CapturePreviewAsync` + full-page capture through the DevTools protocol (`Page.captureScreenshot`) | S |
| `Translate` | No on-device translation in WebView2. Defer, or route through the local AI engine | L/defer |
| `AI*`, `search-ai-engine.cpp` | llama.cpp builds on Windows; run it as an **AppContainer/low-integrity** process (the App Sandbox stand-in), with the model passed as an inherited handle | M, later |
| `Updater` | Velopack (delta updates, no admin) + Authenticode (Azure Trusted Signing). Same "stage it, swap on next launch, never restart by itself" behaviour | M |
| `Bench`, `PageHands`, `AgentControl`, `AgentTrust`, `AgentBanner` | AF_UNIX socket in the data folder, **same wire protocol**, so `./bench`, `bench-mcp`, `search-mcp` and the Python tests port with path changes. Real mouse events through `SendInput` or CDP `Input.dispatchMouseEvent` | M |
| `ICloudSync`, `Handoff`, `Scripting` (AppleScript), `Substitutions`, `Sharing`, `Lights`, `LiquidTab`, `Glass`, `FrameRate`, `Trim`, `KeyLayout` | Drop or replace: Share → `DataTransferManager`; Glass → Mica; AppleScript → maybe UI Automation names later | — |
| `Welcome`, `WhatsNew` | Port last, rewriting the copy for Windows | S |

## 4. Phases

Each phase ends with a build someone can use every day for what it covers.

**Phase 0 — Spikes.** Done for 1–5 on 2026-11-21; results in [spikes/VERDICTS.md](spikes/VERDICTS.md). Still open: real third-party extensions, the Web Store install, and a real-size blocklist. Throwaway code in `spikes/`, a written verdict each:
1. Extensions: install uBlock Origin Lite, Bitwarden, 1Password, Dark Reader unpacked. Do the action popups, `chrome.tabs.query`, `tabs.create`, `sidePanel`, and `identity` work? Can we intercept the Web Store's "Add" (`StoreRelay` analog)?
2. Shield: a built-in DNR extension vs `WebResourceRequested`. Measure page load on a news-site corpus.
3. Startup and memory: cold start to the first painted page, and 50 restored lazy tabs. Targets in §7.
4. PiP: Chromium video PiP and document PiP inside WebView2.
5. Overlays: WinUI content over WebView2 (omnibox, peek), and dragging the window by the empty strip.

**Phase 1 — A browser.** Window with a custom title bar; tab column and strip (`⇧⌘S` → Ctrl+Shift+S); omnibox with history completion; lazy tabs plus session restore; back/forward/reload; keyboard map; Find; private tab; light/dark; minimal Settings; `Store` isolation and test world; Bench socket with `open/wait/text/shot/click/probe/close`, so the Python harness runs from day one.

*Phase 1 status (2026-11-21): built.* `python test/phase1.py` runs 40 checks against the dev build in its own test world and passes; with `--keys` it also sends real key presses from a focused page. Core has 129 unit tests. What it taught us:
- **The bench is a named pipe**, `\\.\pipe\<store folder name>\bench`, not an AF_UNIX socket file. On this Windows build a socket file left behind by a closed socket could be neither deleted nor bound again for a long while, which a fixed path can't survive. The wire protocol is unchanged. `test/winbench.py` is the Windows client, and porting `./bench` itself means swapping its transport. Test runs also have the Mac's test-only verbs `press`, `type` and `select`.
- **WinUI hands a focused page's keys to nobody.** A WebView2's keyboard lives in its own process, and WinUI's control exposes no accelerator hook. Each page therefore gets a listener at document start that passes the window's keys, and only those, to the window: real presses only (`isTrusted`), carrying a per-launch secret held in a closure. Pages where scripts don't run, such as the built-in PDF viewer and error pages, don't pass keys yet.
- **WebView2's own browser shortcuts are off** (`AreBrowserAcceleratorKeysEnabled = false`): left on, Ctrl+S opened Save As and those keys never reached the window. Print (Ctrl+P) is handled by the window. **Zoom is missing**: WinUI's WebView2 doesn't expose the zoom factor. Getting it means hosting the controller ourselves or using a DevTools workaround; decide in Phase 2.
- **Shortcuts that clash on Windows:**
  - Duplicate is Ctrl+Shift+K, as in Chrome and Edge, keeping Ctrl+D for bookmarks.
  - Reload from origin is Ctrl+Shift+R or Ctrl+F5.
  - Reading mode will need a key other than the Mac's ⇧⌘R (F9, as in Edge).
- **Restored tabs start asleep**: no web view until shown, as the plan wanted, and a new empty tab gets none until it has an address. When the tab in front is private, the session marks the nearest kept tab as in front instead.

**Phase 2 — Daily driver.** Shield; Curtain (hide elements) and site styles; reading mode; bookmarks, history, and downloads panels; passwords (save after a successful sign-in, offer under the field) and passkeys; site card with permissions; notifications; default-browser registration and links from other apps; Velopack updater and a signed installer. **First outside build here.**

**Phase 3 — The Arc layer.** Spaces (colours, gradients, routes, swipe); pins and pin folders; tab groups; Split View (up to 4); peek; the little window; Ctrl+Tab switcher; fold; side panel; sleep and archive; floating video.

**Phase 4 — Extensions (smaller after spike 1).** Web Store install with CRX3 verification, the puzzle menu and pinning, popups, site access, unpacked + reload, native messaging.

**Phase 5 — Ecosystem.** Import from Chrome, Edge, Brave, Arc, Firefox, and Zen (tabs, bookmarks, history; passwords via CSV); profiles; multiple windows; folder Sync compatible with the Mac; `search-mcp` agent control with its trust prompts.

**Phase 6 — Later.** Local AI engine, translation, live folders, capture, launcher over any app, and whatever upstream adds meanwhile (triaged from `CHANGELOG.md` each phase).

The heaviest phase is now 3 (the most UI). Spike 1 shrank phase 4 to UI, the `tabs`/`windows` shim, and testing real extensions.

## 5. Keeping up with upstream
Upstream ships many commits a day. At the start of each phase:

- `git fetch upstream` and diff `CHANGELOG.md` since the last baseline.
- Tag each new item: port, defer, or Mac-only, in `UPSTREAM.md`.
- Port behaviour, not code. The Swift files' header comments are the spec; read them before implementing each subsystem.

## 6. Biggest risks
1. **WebView2's extension surface.** Spike 1: the APIs answer natively, but every WebView2 is its own one-tab window to extensions. A small `tabs`/`windows` shim is needed. Real third-party extensions (password managers above all) still need testing.
2. ~~Ad blocking cost~~: resolved by spike 2 (DNR). Still to check: real EasyList-sized rulesets against DNR's rule caps.
3. ~~Many WebView2 controllers~~: measured by spike 3. ~28 MB per live tab, so Sleep must close tabs; lazy restore makes idle tabs free.
4. **Privacy promises.** SmartScreen, Edge autofill and runtime diagnostics are all things the Mac never had to switch off.
5. **Chrome password import** is blocked by app-bound encryption, so expect a worse first-run import than on the Mac.

## 7. Targets (from the Mac's promises)
- Cold start to a usable window: < 300 ms; first restored page painted: < 1 s.
- 50 restored tabs at launch: no more memory than 2 open tabs.
- Installer: < 20 MB (the WebView2 runtime already ships with Windows).
- No network request at launch except the once-a-day update check.

## 8. Next concrete steps
1. Scaffold the solution at the root (`Search.sln`, `src/`, `test/`, the .NET `.gitignore` lines, CI on `windows-latest` in `.github/workflows/windows.yml`).
2. Run spikes 1–3 first; they decide Phase 4's scope and the Shield design.
3. Port `Search.Core` model types with xUnit tests while the spikes run.
