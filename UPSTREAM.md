# Upstream triage

Baseline `9cc5f07` (2026-11-20) → upstream head `c48f067` (2026-11-22), 73 commits, triaged 2026-11-22.

Tags: **port** (Windows has the counterpart and doesn't yet behave the new way), **covered** (Windows already does), **defer** (belongs to something not built yet), **mac-only** (AppKit/WebKit/macOS, Mac test harness, or nothing Windows has).

Counts: port 16, covered 7, defer 9, mac-only 41.

None of the 73 commits touched `LiveFolders.swift`, `Snap.swift`, `Launcher.swift` or `HotKey.swift`. Phase 6 builds those from the code as it was at the baseline.

## Port

Smallest and safest first. Ported in Phase 6, all but the last, `3df5362`, which came after it (WINDOWS-PLAN.md, After Phase 6): a split is pinned as a split, its pins naming one `pair` in pins.json. Its drag halves are Windows' own: a split pinned whole is carried whole among the rows; a tab dragged onto the pins to pin it isn't built yet. `e5620f4`'s swipe half and `719f3a7`'s Move Left / Move Right across profiles wait, as noted.

| Commit | Subject | Windows file(s): change |
|---|---|---|
| `17a7ddb` | Shield: hide Reddit's promoted posts on reddit.com | `Shield.cs`: add `(["reddit.com"], "shreddit-ad-post, shreddit-comments-page-ad, shreddit-sidebar-ad, shreddit-dynamic-ad-link, comment-tree-ad, .promotedlink")` to `_slotsBySite`. |
| `ec16f2a` | ⌘O keeps a peek as a tab (#1340) | `MainWindow.Keys.cs`: `Key(VirtualKey.O, Ctrl, …)` calls `KeepPeek()` while `_peek` is set. Being in `_keys`, the peek page passes it. Not Ctrl+Enter: every page would lose it, and sites send with it. |
| `4ef2a06` | A download into a folder Search can't write to asks where to save it (#1291) | `Pages.cs` `DownloadStarting` plus `MainWindow.Panels.cs`: probe `Path.GetDirectoryName(e.ResultFilePath)` by creating and deleting a file. If that fails, take a deferral, show a `FileSavePicker` and set `ResultFilePath`, or `Cancel` if you dismiss it. |
| `294d6ed` | YouTube's blocker tests pass, so its wall stays down (#1320) | `Shield.cs`: an `allow` rule (priority 2) for `\|\|static.doubleclick.net/instream/ad_status.js` from YouTube's domains. `youtube-page.js`: answer the requests Shield blocks with an empty 200 in the page, and keep a playable answer's status when the wall is written over it. |
| `cf2d210` | YouTube's first video starts at once on a return visit with its pre-roll taken out | `youtube-page.js`: if `ytInitialPlayerResponse` had ads stripped, a `/watch?v=` page (no `list`, not music.) restarts that video once through `movie_player.loadVideoById(id, t)`. |
| `fad2939` | Hide Elements hides on sites whose CSP limits styles (#1270) | `dress.js`, `curtain.js` (optionally `SiteStyles.cs`): one helper that puts each sheet in a constructed `CSSStyleSheet` adopted by the document, with `<style>` as the fallback. *Subtle:* first check that CSP really blocks a `<style>` from Search's DevTools world. |
| `e5620f4` | Spaces turned on: no nameless card from a stroke with one space; a lone Arc space names it | `ImportTake.Sidebar`, else-branch: an untouched first space (named "Personal", no icon, colour 0) takes the lone Arc space's name, icon, colour and gradient. The swipe half waits for swipe (defer). |
| `6f58803` | The last tab across the top is never cut off (#1295) | `MainWindow.xaml`: `StripDef` is `Auto`, so the strip is measured unbounded and never scrolls. Bound it to the room left and make it scroll, keeping the selected tab in view. Check on screen. |
| `c722e6d` | The window moves from above the tabs across the top (#1321) | `MainWindow.xaml(.cs)`: across the top, only `DragSpace` drags, and a full row leaves it 0 wide. Make `TopBar` the title bar in both modes, mark the strip's tabs and extension slot as Passthrough (`InputNonClientPointerSource`), and keep a drag band above the tabs. |
| `5d309e4` | Keep a sleeping tab's sessionStorage (#1303) | `MainWindow.Idle.cs` `PutToSleep` reads `[origin, items]` into memory on the tab (not private, ≤512 KiB). `Pages.Build` adds a one-shot world script that restores it before the page's scripts (main frame, same origin, empty storage only) and removes it after the first real commit. |
| `719f3a7` | Profiles: any order, moved with Move Left / Move Right across profiles | `Profiles.cs` `Read`: keep Personal where the file has it (and its `home`, see `5b89262`), not forced first. Moving across profiles waits for profiles in one window (defer). |
| `a1953c3` | Try HTTPS first for http:// pages (#1302) | `Prefs.cs`, `MainWindow.Settings.cs` and `Pages.cs`: a "Try HTTPS first" switch, off by default. Upgrade top-level http:// to https:// in `NavigationStarting`, except local hosts; if it fails, load the http:// once. *Subtle:* first check whether WebView2 already upgrades. |
| `5b89262` | Spaces: any space can be deleted, the first included (#1326) | `Spaces.cs` (`Remove`, `HomeIn`, `Arrange`, `OwnsStore`), `Profiles.cs` (keep Personal's `home`, add `Rehome`), `Sync.cs`, `MainWindow.Spaces.cs` (menu, wording, last space → delete profile). *Subtle:* the on-disk meaning of Personal's home changes. |
| `f074c8d` | Import: Arc's spaces replace an untouched first space instead of sitting beside it | `ImportTake.Sidebar` and `MainWindow.Import.cs`: once spaces come in for the window's profile, delete the old home (as `5b89262` does) if it has no pins, no folders and no tab but a blank one. Needs `5b89262`. |
| `9d9eb3b` | ⌘T finds your open tabs as you type, off unless turned on (#1288) | `MainWindow.Field.cs` and a pref: in Ctrl+T's field, rank open tabs (≤3), then pins (≤2), by 3/2/1 relevance and pick the first as you type. Enter switches to it, the text stays what you typed, and ↑ lets go. |
| `3df5362` | Pin a split as a split (#1286) | `Pins.cs` (`PinDef.Pair`, written only when set), `Splits.cs`/`Session.cs`, `MainWindow.Pins.cs`/`Row.cs`/`Split.cs`: pinning a split page pins every page as rows sharing a pair id, and they stay one split. Largest; read the Swift. |

## Covered

| Commit | Subject | Note |
|---|---|---|
| `074c6b5` | Say under each browser found what can come over from it | The import sheet's boxes name each kind with its count; the spaces box shows only where there are spaces. Welcome half: defer. |
| `07b9476` | Security keys work with Offer passkeys off (idea 846) | No passkeys-off switch on Windows; WebView2 and Windows Hello handle security keys. |
| `86e28d6` | Answer a site's request for a client certificate with one you choose (idea 536) | `ClientCertificateRequested` is unhandled, so WebView2 shows its own picker from the Windows store. |
| `6e71d50` | One button per space beside the bookmarks…; a space's colour from the click (#1339) | Windows always has a button per space, and the wash changes as soon as you switch. The Mac's click-on-current-opens-menu is right-click here. |
| `6f8a772` | Games can lock the pointer… (#1305) | Chromium grants Pointer Lock itself. Untested. |
| `20d4e04` | Extension popups: classic scroll bars no longer add 17 points each way (#1311) | `extension-page.js` measures fit-content and scroll size and never adds a gutter. |
| `6658f81` | A bookmarks folder of thousands opens at once (#1306) | The bookmarks panel is a virtualizing `ListView`. |

## Defer

| Commit | Subject | Waits for |
|---|---|---|
| `558d6f3` | Spaces: one stroke, one slide, its glide included (idea 834) | Swipe between spaces |
| `bbba41c` | The rows kept for a swipe between spaces are hidden at rest (idea 847) | Swipe between spaces (keep hidden rows hidden when built) |
| `5afb34c` | Extensions: bookmarks.get answers like Chrome, so Floccus can sync (#1312) | Extension `chrome.bookmarks` backed by our bookmarks (not built) |
| `69c1b23` | Swipe back closes a tab a link opened, off unless turned on (#1337) | Swipe-back gesture handling |
| `292dbbf` | A blank tab can show the Home bookmark folder as a grid (#1316) | A blank-tab page (with Themes' blank tab) |
| `e1dcac2` | Several tabs picked: their menu acts on all of them (#1287) | Multi-selection of tabs |
| `e5483cc` | Welcome: the pictures show what tabs wear (#1275) | Welcome |
| `4295581` | Themes: an optional background for the blank tab (#1318) | Themes / blank-tab backgrounds |
| `1f21a1c` | Float a YouTube Music song as its cover, not the player's buttons | The richer floating player (Windows floats Chromium's video PiP) |

## Mac-only

| Commit | Subject | Note |
|---|---|---|
| `e709891` | Split: Settings stays in its own pane (idea 833) | Settings is a dialog and History a panel on Windows, never pages in a pane |
| `b0935a8` | The column's address, a pin's letter and a group's name keep their field again | Caused by the Mac's hidden copy of the column |
| `f1dc308` | Tests: put pins down and close windows between sidebar tests | Mac tests |
| `2a45297` | FollowFileTests names the look and layout settings read again since | Mac tests |
| `dbb8345` | PinDragTests: wake the pin before carrying it out among the rows | Mac tests |
| `8ae9bb6` | Inline predictions follow Search's own switch (idea 842) | macOS inline predictive text |
| `738db1e` | A probe: what a site keeps survives ⌘Q and a relaunch (idea 838) | Mac probe (WebKit data stores) |
| `8669621` | A probe: a click… counts as using the site for tracking prevention (idea 838) | WebKit ITP probe |
| `32f7482` | Settings: say what Prevent cross-site tracking can cost (idea 838) | WebKit ITP wording; no such setting |
| `675e95e` | Extensions: chrome.i18n functions work called loose (idea 812) | WebKit shim; Chromium's own i18n |
| `1efc952` | Full screen: the top bars move down with the menu bar's reveal (idea 854) | macOS menu bar |
| `81db36f` | fix(passkeys): allow sign-in from Peek and small windows (#1273) | Mac passkey shim; WebView2 native |
| `eb07238` | An extension worker's plain fetch to a granted site carries its cookies (#1331) | WebKit shim; Chromium native |
| `e9f7e6f` | Similarweb's button opens its panel… (#1324) | WebKit frame check |
| `6b33518` | An extension's popup opens from the first button at the column's foot again (#1323) | NSPopover |
| `3782008` | Tests: above a tab across the top the window is movable (#1321) | Mac test |
| `ae6817f` | The extensions button sits centred at the column's foot (#1281) | SF Symbol nudge |
| `17fe62e` | A title bar double-click no longer bounces back to the old size (#1338) | macOS title bar double-click |
| `da56032` | History: copy the pre-1.0.16 history once into each signed-out space (#1325) | One-time migration of Mac data; Windows spaces start with their own history |
| `8ccd05f` | Settings: say what Tabs show chooses (#1276) | No such setting |
| `df7eecb` | Remove 28 declarations nothing refers to (#1280) | Dead Swift |
| `fa3e432` | The site card hangs from its tab after a double-click (#1310) | Mac row layout timing |
| `d92086a` | The bookmarks list is as tall as the rows it shows (#1309) | No bookmark-button dropdown on Windows |
| `e818a00` | Tests: the Arc import test expects an untouched home replaced | Mac test |
| `fd21c5c` | Full screen reveal: the inset is set only when it moves | macOS menu bar |
| `576a7fc` | Bench: marks reads the Bookmarks menu as the menu bar in use would | Mac menu bar |
| `a986976` | A right-click on a tab group's name lands on the group | NSPopover measuring view |
| `6635c58` | Floating Downloads: shadow not clipped… (idea 897) | Mac floating card; Windows uses a panel |
| `660e12f` | Tests: space_roomy films the column in the light look | Mac test |
| `e045344` | Tests: local_paths keeps its files under the home folder | Mac test |
| `a504a1d` | Tests: settings_layout expects the Themes section (#1318) | Mac test |
| `ca659da` | Tests: peek_stays expects a page kept beside to join the split | Mac test |
| `a60eb22` | Escape answers a website pop-up's camera question again | Windows opens pop-ups as tabs |
| `84110b7` | Tests: run-ci-tests isolates however many download cases there are | Mac test |
| `9173652` | Tests: link_menu and split_selected expect splits of up to four pages | Mac test |
| `9577bc7` | Tests: little_switcher turns the switcher's keys off to test them off | Mac test |
| `7a66a39` | Tests: pin_tiers expects Clear to leave an empty tab in a group alone | Mac test |
| `02cebc5` | Tests: the passkeys-off probes follow the kind of build they run | Mac test |
| `1121e21` | ⇧⌘T puts a split's page back with its history | Mac wake-order bug; Windows' Ctrl+Shift+T restores neither history nor split |
| `a6cac52` | ⌘S: the page keeps its own background while the column slides (idea 847) | WebKit painting |
| `c48f067` | Tests: little_window defines editor_ready where the last check needs it | Mac test |

## CHANGELOG lines → commits

Every line `9cc5f07..c48f067` added to `CHANGELOG.md`:

- **Added:** Try HTTPS first `a1953c3` · import sheet/Welcome say what can come `074c6b5` · Swipe back closes `69c1b23` · Home bookmarks on a blank tab `292dbbf` · ⌘T finds open tabs `9d9eb3b` · pin a split `3df5362` · one button per space `6e71d50` · ⌘O keeps a peek `ec16f2a` · Themes `4295581`
- **Changed:** several tabs picked `e1dcac2` · Tabs show wording `8ccd05f` · Welcome pictures `e5483cc` · profiles in any order `719f3a7` · Arc spaces replace an untouched first space `f074c8d`
- **Fixed:** YouTube Music float `1f21a1c` · ⇧⌘T split history `1121e21` · Escape in pop-up camera question `a60eb22` · floating Downloads `6635c58` · ⌘S slide `a6cac52` · pre-1.0.16 history `da56032` · Spaces on, no nameless card `e5620f4` · delete any space `5b89262` · bookmarks folder of thousands `6658f81` · last tab cut off `6f58803` · bookmarks list height `d92086a` · site card after double-click `fa3e432` · popup scroll bars `20d4e04` · pointer lock `6f8a772` · client certificate `86e28d6` · YouTube blocker wall `294d6ed` · popup from first button `6b33518` · sleeping tab's sessionStorage `5d309e4` · Similarweb `e9f7e6f` · Brandsearch worker cookies `eb07238` · passkeys in Peek `81db36f` · Floccus `5afb34c` · full screen bars `1efc952` · Monarch i18n `675e95e` · security key `07b9476` · swipe rows hidden `bbba41c` · Reddit `17a7ddb` · field keeps keys `b0935a8` · Settings in split pane `e709891` · one swipe one slide `558d6f3` · inline predictions `8ae9bb6` · YouTube return visit `cf2d210` · window moves from above tabs `c722e6d` · unwritable downloads folder `4ef2a06` · Hide Elements under CSP `fad2939` · extensions button centred `ae6817f` · title bar double-click `17fe62e` · space colour at once `6e71d50`

The Stylus line next to them was already there at the baseline. Upstream now has two `### Changed` headings in the unreleased section; that's upstream's slip, nothing to port.
