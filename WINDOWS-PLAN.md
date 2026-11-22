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

*Phase 2 status (2026-11-21): built.* `python test/phase2.py` runs 43 checks against the dev build in its own test world, and they pass. Phase 1's suite still passes, and Core and Engine have 297 unit tests. Built:
- **Shield.**
- **Panels:** history (Ctrl+H), downloads (Ctrl+J), bookmarks (Ctrl+Shift+O), passwords (Ctrl+Shift+L) and what's hidden on this site (Ctrl+Shift+U). Downloads go to our own panel rather than Edge's flyout, and finished ones are remembered.
- **Bookmarks:** Ctrl+D, in the address field's suggestions, and import/export of the HTML bookmarks file.
- **Hiding:** hide an element (Ctrl+Shift+H), kept before first paint, and your own style for a site.
- **Reading mode:** F9, in place.
- **Passwords:** saved once a sign-in has worked, in a DPAPI vault; offered under the box and filled only on your choice, from the isolated world; copied from the panel after Windows Hello.
- **Site card and permissions:** questions over the page, answers kept per site, never for private tabs.
- **Notifications:** posted as Windows toasts, kept for the bench in test runs.
- **Default-browser registration and links from other apps.**
- **Updates and installer:** Velopack.

The Setup.exe is **82 MB**, against a target under 20 MB. The 181 MB published app is mostly the bundled .NET runtime and the 25 MB Windows SDK projection, after referencing only the Windows App SDK parts Search uses, which already cut it from 105 MB. Next, try trimming, or depend on the .NET and Windows App runtimes being installed (Velopack can install them).

Not done yet: zoom, the public suffix list, the bookmarks bar, a real icon and name, signing, and a published release.

*Phase 2 findings (2026-11-21):*
- **New-document scripts run before `<html>` exists.** `forms.js` observed `document.documentElement`, threw at document start and never defined itself; observe `document` instead.
- **Search's own scripts run in an isolated world** (`World.cs`), the stand-in for `WKContentWorld`, set up through the DevTools protocol.
  - `Page.addScriptToEvaluateOnNewDocument` with a `worldName` runs the scripts. It needs `Page.enable`; without it they silently never run.
  - Messages come out through `Runtime.addBinding`, attached to each new document's world context, which the window gets from `Page.createIsolatedWorld`.
  - `Runtime.enable` is never sent: anti-bot checks look for its side effects, and without it a binding can't be given to a world by name.
  - The page shares the DOM but can't see the scripts' globals, listeners or messages. Passwords are filled from here.
  - Page keys now go through this channel too.
- **Hold every WinRT event source you subscribe to.** A `CoreWebView2DevToolsProtocolEventReceiver` (or `core.Find`) that nothing references gets collected, and its subscription with it. That happened at the next garbage collection, typically when another web view was built, and at first looked like a private-tab bug.
- **Private tabs have a WebView2 environment of their own**: a separate browser process and data folder, emptied at launch, with an InPrivate profile per tab.
- **Shield is a built-in extension**, written at launch:
  - declarativeNetRequest rules for the Mac's third-party list and YouTube's ad paths;
  - a content-script stylesheet for ad slots, plus the per-site ones;
  - an `allowAllRequests` rule for each paused site.
  - It's installed fresh in each profile once per launch, since WebView2 can't report an installed extension's version.
  - YouTube's two scripts go in the page world and Search's world, replaced when pauses change.
- **Zoom is still open.** DevTools device-metrics emulation behaves like real zoom (`devicePixelRatio` 1.25, `innerWidth` 1132→906), but `Page.captureScreenshot` then hangs. A dispatched Ctrl+wheel does nothing. Real Ctrl+wheel and pinch work.
- **No public suffix list on Windows.** `Registrable` takes the test as a parameter; without a list, only the exact host counts as the same site, so passwords are offered on fewer pages, never more. Bundling publicsuffix.org's list is a decision to make.
- **Default browser:** Windows never lets an app make itself the default. The installer registers Search for the current user, and Settings opens Windows' own page on its entry. Test runs never touch the registry.
- **Links from other apps:** one instance per world (`AppInstance`). A second launch hands its links over and leaves.
- **Updates and installer:** Velopack. `packaging/pack.ps1` builds Setup.exe and the update packages, signed when `SEARCH_SIGN_PARAMS` is set. The app checks GitHub releases of `Updater.Releases` once a day and applies a download at the next launch.
  - Publishing a release is a separate, deliberate step.
  - The fork needs its own name and icon before any release goes out (README).

**Phase 3 — The Arc layer.** Spaces (colours, gradients, routes, swipe); pins and pin folders; tab groups; Split View (up to 4); peek; the little window; Ctrl+Tab switcher; fold; side panel; sleep and archive; floating video.

*Phase 3 status (2026-11-21): built.* `python test/phase3.py` runs 44 checks in three test worlds of its own, and they pass. Phases 1 and 2 still pass, and Core and Engine have 441 unit tests. Built:
- **Split View:** two to four pages, keys moving between them, a draggable divider, saved in the Mac's shape.
- **Peek:** Shift+click, in Search's isolated world, using real clicks only. Kept, it becomes a tab, loaded as it is.
- **The Ctrl+K switcher.**
- **Floating video:** Chromium's own picture-in-picture, asked for as a user gesture through DevTools.
- **The folded column:** slides out from the window's edge.
- **Small windows for links from other apps:** each has a browser model and engine of its own.
- **Spaces:**
  - their own row, session and, if chosen, sign-ins (a WebView2 profile per space);
  - a colour or gradient washing the window, and Alt+1–9 to go to one;
  - pages kept alive, set aside, while another space is shown;
  - routes sending links from other apps to their space.
- **Pins:** squares at the top of the column with the site's icon or a letter, kept per space.
- **Tab groups:** headings that fold. A dragged tab joins the group it's dropped in.
- **Sleep:** after 30 minutes, on unless turned off. The web view is closed, the address and scroll kept.
- **Self-closing tabs:** off unless turned on, into a 30-day archive with a panel of its own.

What Phase 3 taught us:
- **Only a click moves the keys between split pages.** Taking every focus change as a choice let a programmatic focus arriving late steal the keys back from the page you'd just opened.
- **A tab is loading from the moment it's told to navigate.** Waiting for WebView2's first event left a gap in which the sleep rule took pages that had never loaded.
- **Close a secondary window's WebView2 while the window is closing, not after it has closed.** After, WinUI could fail inside its dispatcher (0xC000027B in CoreMessagingXP).
- **Disk writes are flushed on the way out.** The space you were on lives in a notes file written in the background.

Not done:
- **Side panel:** needs extensions, so it moves to Phase 4.
- **Pin folders, live folders and pins shared across windows:** the models are ported; the window doesn't use them yet.
- **The pins file:** `pins.json` isn't written yet. Pins live in each space's session, which the Mac also reads when `pins.json` is missing.
- **Swipe between spaces, and drag-and-drop of pins.**
- **The Mac's picture-based Ctrl+Tab switcher.** Ctrl+Tab walks the row, as the README says.
- **Removing a space's WebView2 profile from disk.** `Spaces.Erasing` lists the spaces whose profiles are due to go, but nothing deletes them yet.

**Phase 4 — Extensions (smaller after spike 1).** Web Store install with CRX3 verification, the puzzle menu and pinning, popups, site access, unpacked + reload, native messaging.

*Phase 4 status (2026-11-21): built.* `python test/phase4.py` runs 48 checks in its own test world, with extensions written by the test and a Chrome Web Store played by its own server (`test/makecrx.cs` signs the packages), and they pass. Phases 1–3 still pass; Core and Engine have 747 unit tests. Built:
- **Installing:**
  - from the Chrome Web Store, by its page's own button or "Add to Search", or a link or id pasted into the Extensions panel;
  - the package's signature checked against its id (CRX3, RSA or ECDSA), then unpacked;
  - from a folder, copied in, with Reload bringing in its later edits.
  - Every install asks first, saying what the extension will be able to do. An update that wants more asks again; store extensions are checked once a day.
- **The copy Search loads:** `installed.json` in the Mac's shape, and each extension in `Extensions\<id>`. Its manifest is rewritten from the original:
  - a store copy keeps the store's key, so its id is the store's;
  - a folder copy gets a key of Search's own, so its id is stable and known before it loads;
  - its worker is wrapped so Search's shim runs first;
  - its sites are narrowed as you chose.
- **Every profile in step:** a space with sign-ins of its own is a profile of its own, and each profile is given each extension once per launch. Removed ones are taken out of profiles that weren't open at the time. Private tabs have none.
- **The buttons:**
  - the pinned extensions and the puzzle, at the foot of the column, or beside the tabs when they run across the top;
  - a right-click for pin, side panel, options, "Runs on", reload, turn off and remove;
  - the Extensions panel, which Settings also opens.
- **Popups:** a web view of Search's own over the window, hanging from the button and sized to its page (25–800 × 25–600). A click elsewhere, Esc, `window.close()` or another press of the button puts it away.
  - A press is worked out by the extension itself, in a page of its own that Search loads first (the bridge): its popup for this tab, its side panel if it opens on a press, or its `action.onClicked`. WebView2 can't fire onClicked, so Search fires it through the worker shim.
- **The side panel:** an extension's panel beside the page, resizable, its width kept. It follows you from tab to tab.
- **The window model, shimmed:**
  - in Search's popups and panels, "the current window" is the tab in front's, and `tabs.getCurrent()` is nothing, as in Chrome;
  - in the worker, while one of Search's surfaces has the keyboard, the tab in front's window answers instead;
  - Search's surfaces are left out of `windows.getAll` and `tabs.query`;
  - Search's own messages never reach an extension's listeners.
- **Where an extension runs:** every site it asked for, or only the sites you name, each with its subdomains. Its copy's host permissions and content-script matches are narrowed and it's loaded again, so elsewhere its scripts aren't put in the page at all.
- **Native messaging:** WebView2 already reads the hosts registered for Chrome, Edge and Chromium (spike 6). There was nothing to build.
- **The Chrome Web Store in WebView2:** WebView2 says it's Chrome 157 and gives the store its private install API. So the store's own button is live, and would install straight into WebView2, past everything above.
  - In the store's own world, Search replaces that API before the store's scripts run: a real press asks Search instead, and so does the store's "Remove".
  - For a store page that sees another browser, the Mac's relay ("Add to Search" in place of a grey button) is kept.

What Phase 4 taught us (spikes/VERDICTS.md, spikes 6 and 7):
- **Never remove an extension to update it**; add it again. Removing erases what it saved.
- **No file or folder in an extension may start with `_`**; WebView2 says only "access denied".
- **An extension turned off has to be turned on as the profile lists it.**
- **A tab's page is built a moment after its address is set**, now that it waits for its profile's extensions. A press in that moment waits for it.

Not done:
- **activeTab and "On click":** Chrome lends an extension the page in front when you press its button. WebView2 has no way to, so an extension without host permissions can't read the address of the tab it's given. For the same reason "Runs on › On click" isn't offered (the model has it).
- **Badges, and changes the extension makes to its button:** `action.setBadgeText`, `setIcon`, `setTitle` and a per-tab `setPopup` aren't seen. The button shows the manifest's icon, and a press asks the extension for its current popup.
- **More of the extension API surface:** `sidePanel.open()` from the extension, `commands` (an extension's keyboard shortcuts), `tabs.captureVisibleTab`, and `contextMenus` items in Search's own menus.
- **Tab managers still see one window per tab.** Only "the current window" is shimmed; the row's order and groups aren't.
- **Extensions in private tabs**, which Chrome allows per extension.
- **Real third-party extensions are untested** (uBlock Origin Lite, Bitwarden, 1Password, Dark Reader), since that means downloading them, which needs a go-ahead. Nothing was installed from the real store; on its page, the install and remove APIs were only checked to be Search's.

**Phase 5 — Ecosystem.** Import from Chrome, Edge, Brave, Arc, Firefox, and Zen (tabs, bookmarks, history; passwords via CSV); profiles; multiple windows; folder Sync compatible with the Mac; `search-mcp` agent control with its trust prompts.

*Phase 5 status (2026-11-22): built.* `python test/phase5.py` runs 57 checks in test worlds of its own and they pass: two worlds share one Sync folder as two computers would, a Chrome is laid out in the world's own Import folder, and the real `search-mcp` is run as an agent runs it. Phases 1–4 still pass; Core and Engine have 1,055 unit tests. Built:
- **Several windows** (Windows.swift):
  - Ctrl+N opens one in the space of the window in front, with an empty tab;
  - each has its own tabs; settings, history, bookmarks, spaces, pins, extensions and the vault are shared (`Shared.cs`);
  - the pins are the same in every window, through `pins.json` (Pins.swift);
  - the oldest window of a profile keeps that profile's session files; the others are lines in `windows.json`, in the Mac's shape, and all come back after a restart;
  - closing the oldest hands its files to the next; Ctrl+Shift+T brings back a closed window when it went after the last closed tab;
  - Exit (Ctrl+Shift+Q) keeps every window. Closing windows one at a time forgets each, as in Chrome.
- **Profiles** (Profiles.swift): Settings makes, renames and removes one, and opens a window of it.
  - Each has its own spaces, tabs, pins, sign-ins (a WebView2 profile named by its id), history and bookmarks.
  - The files are the Mac's: `profiles.json`, then `history-<id>.json` and the like beside Personal's.
  - A window is one profile's for life, and says whose in its title once there are two. Until then nothing about profiles shows.
- **Sync** (Sync.swift, SyncMerge.swift): on with any folder (OneDrive, Dropbox, a share), in the Mac's file format (`Search Sync/<scope>/<device>.json`), so a PC and a Mac can share one folder.
  - A pass runs a second after launch, every 30 seconds, two seconds after a change, when another computer's file changes, and when a window comes to the front.
  - It carries spaces, pins, bookmarks and the settings the Mac syncs. Settings Windows doesn't have yet are carried through untouched.
- **Import** (Import*.swift): Chrome, Edge, Brave, Arc, Vivaldi, Opera, Chromium and their channels; Firefox, Zen, Floorp, LibreWolf, Waterfox.
  - It brings bookmarks, history, passwords, the open tabs and their groups, Arc's and Zen's spaces and pins, and Web Store extensions.
  - Counts come from the files alone; databases are copied first, as the browser usually has them open.
  - Passwords: a DPAPI key and AES-GCM, as Chrome and Edge keep them; Firefox's key4.db; any browser's CSV export.
  - Chrome 127's app-bound passwords are counted, and the sheet gives the steps to export them to a CSV.
  - SQLite is Windows' own `winsqlite3.dll`, so nothing is added to the installer.
- **Coding agents** (AgentControl, AgentTrust, AgentBanner, PageHands): off unless Settings turns it on.
  - The pipe is `\\.\pipe\<folder>\agent`, for this user only.
  - "<agent> wants to use Search" is answered by a pointer click alone, never a key or UI Automation, and never in its first half second.
  - The agent works in tabs of its own, and sees yours only if you say so. A line at the foot of the column says who is using Search, with Stop.
  - Every one of the Mac's verbs; clicks and keys are real input through DevTools; password, card and one-time-code values never leave the page.
  - "Always allow" is offered only for `claude.exe` signed by Anthropic, checked with WinVerifyTrust and kept DPAPI-sealed.
  - A setting some other program turned on is turned off at launch (a DPAPI mark, the keychain's stand-in).
  - `search-mcp` speaks to the pipe on Windows; the Mac path is unchanged.

What Phase 5 taught us:
- **Two windows must never answer each other's pin changes.** Pin titles differ between windows (each pin's page is its own), so a window that reported pins on every write, after following another's, made two windows trade changes once a second and lose a pin. A window reports only a change made in it.
- **Escape a file's slashes once.** A row inside `windows.json` was escaped by Session.Encode and again by the outer writer, so its addresses didn't parse on the way back.
- **A session's address is the field's, not an agent's:** the field takes `127.0.0.1:port`, but an agent must give a whole `http://` address.

Not done:
- **Profiles in one window** (OneWindow.swift) and **the same tabs in every window** (Twins.swift). Folding (Ctrl+S) is still one setting for every window rather than each window's own.
- **Deleting a removed profile's or space's WebView2 data from disk.** `Spaces.Erasing` lists them, but nothing deletes them yet.
- **Import, untried on real browsers:** only test worlds were read, never this PC's own browsers. Arc, Comet, Naver Whale, Thorium and Opera's newer layout use paths not yet seen on a real install. An Arc profile doesn't yet become a Search profile.
- **Sync, untried against a real Mac's folder.** The order of keys in the outer file follows Foundation's as best known; the items themselves don't depend on it.
- **Agents:**
  - "Always allow" is untried with Claude Code itself starting `search-mcp`. If a launcher such as `cmd.exe` or `py.exe` sits between them, it's never offered.
  - DPAPI keeps out hand edits, not another program of yours.
  - Agent tabs are bench tabs: you can't open one yet, and the working mark isn't drawn on a tab's icon (the Stop line names the tab instead).
  - `bench-mcp` is still Mac-only.
- **Not looked at on screen:** the Settings sections, the import sheet and the agent's question, which were checked only through the bench.
- **The upstream triage** (section 5) wasn't done this phase.

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
