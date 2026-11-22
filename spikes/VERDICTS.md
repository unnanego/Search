# Phase 0 spike verdicts

Measured 2026-11-21 on Windows 11 x64 (dev build 26300) with WebView2 Runtime 157.0.4341.60, Windows App SDK 2.7.0, .NET 11.0.100, and a **Debug** build of the harness. Reproduce with `python spikes/run.py [startup tabs shield extensions pip overlay]`; raw JSON lands in `spikes/results/` (git-ignored: it contains local paths).

The harness (`spikes/Harness`) is one WinUI 3 app with one scenario per run. It serves pages from a local TCP server, and every `*.test` host is mapped to it with `--host-resolver-rules`, so pages make real network requests to real hostnames. Each run gets a fresh WebView2 data folder under `%TEMP%\search-spike`.

## Summary

| Spike | Verdict | Plan impact |
|---|---|---|
| 1 Extensions | **Better than feared.** Nearly every Chrome API answers natively. The one structural gap: each WebView2 is its own one-tab "popup" window to extensions | Phase 4 is mostly UI work plus a small `tabs`/`windows` shim |
| 2 Shield | **Bundled DNR extension wins**, faster than the in-app filter | Shield = a built-in MV3 extension |
| 3 Startup / memory | **Startup on target.** Live tabs cost ~28 MB each, and Suspend frees ~nothing; closing frees it all, and a closed tab rebuilds in ~60 ms | Sleep closes the WebView2 rather than suspending it |
| 4 PiP | **Video PiP native**, document PiP missing | `Float` uses Chromium's PiP; no custom window for video |
| 5 Overlays | **Works.** WinUI draws over WebView2, and caption/passthrough regions work | Omnibox, peek and site card can overlay the page |
| 6 Windows and focus (Phase 4) | **DevTools names each tab's window**, the same id extensions see; a worker's "current window" is whichever web view last had the keyboard; **native messaging works natively** | A page shim in Search's popups and panels, a worker shim for the focus case; nothing to build for native messaging |
| 7 Reinstalling (Phase 4) | **Adding again from the same folder reloads and keeps saved data; removing erases it** | Updates and site changes are an add again, never remove + add |

## 1. Extensions (`extensions`, `extensions-handled`)

The probe extension (`spikes/Harness/extensions/probe`) installed unpacked with 27 permissions; none were rejected.

**What works natively:** `runtime` messaging both ways (worker ↔ content script ↔ popup), `tabs.query/get/update/sendMessage/create`, `tabs.onCreated/onUpdated/onActivated`, `windows.getAll/getCurrent/create`, `tabGroups.query`, `action.setBadgeText/getPopup`, `sidePanel.setOptions`, `storage.local/sync/session`, `declarativeNetRequest` (static and dynamic), `scripting.executeScript/insertCSS`, `cookies`, `alarms`, `contextMenus`, `offscreen`, `identity.getRedirectURL`, `bookmarks`, `history`, `downloads`, `sessions`, `topSites`, `webNavigation`, `webRequest`, `privacy`, `proxy`, `idle`, `fontSettings`, `tts`, `management`, `permissions`, `i18n`, and `runtime.connectNative` (exists).

**Popup:** navigating our own WebView2 to `chrome-extension://<id>/popup.html` works. Inside it, `tabs.query`, `runtime.sendMessage` and `storage` all answer. So the puzzle menu's popups are a flyout holding a WebView2, as planned.

**`tabs.create` / `windows.create`** arrive in the host as `NewWindowRequested` (`IsUserInitiated: true`). Handled with `e.NewWindow = ourTab.CoreWebView2`, the page lands in our own tab.

**The gap: windows.** To an extension, every WebView2 is a separate window of `type: "popup"` holding exactly one tab (index 0). There is no "normal" window. As a result:
- `action.openPopup` fails ("Could not find an active browser window"). We show popups ourselves anyway.
- Tab managers, tab-group extensions and anything that reasons about "the tabs in this window" will see N one-tab windows.
- `windows.create` makes more one-tab windows rather than a window of ours.

Fix (Phase 4): a small install-time shim on the worker, as `ExtensionShims.swift` does on the Mac. It wraps `tabs.query/get/move/group`, `windows.*` and the tab events so they report our model (one window, our tab order, our groups). The worker reaches the host through `runtime.connectNative` to a native-messaging host the app registers for itself, or through a loopback port. The other ~95% of the Mac shim file isn't needed.

**Smaller findings:**
- `bookmarks`, `history` and `topSites` are WebView2's own internal stores, not ours. `topSites` even returns Edge's default "Office" entry. Either shim them over our model or accept that they're empty and separate. Decide in Phase 4.
- `tabs.captureVisibleTab` fails ("image readback failed") with composition-hosted WebView2. Shim it with `CapturePreviewAsync` if screenshot extensions matter.
- `commands` (extension keyboard shortcuts) and `userScripts` are absent. `commands` needs our own Settings › Shortcuts UI; `userScripts` is off in Chromium unless developer mode is on.
- `notifications.create` failed only because the probe passed a bad icon. Retest with a real icon.
- **Popups an app doesn't handle outlive it:** an unhandled `NewWindowRequested` opens a WebView2-owned window that keeps the browser process and the data folder alive after the app exits. Search must always handle new windows.
- Not tested yet: real third-party extensions (uBlock Origin Lite, Bitwarden, 1Password, Dark Reader), installing from the Chrome Web Store, and native messaging to a real host. Those need downloading third-party code, so they're a follow-up with explicit sign-off.

## 2. Shield (`shield-none`, `shield-filter`, `shield-dnr`)

The page loads 300 images from `app.test` and 300 from `ads.test` (blocked). The blocklist is 50,000 synthetic domains plus `ads.test`. Six loads per mode; median of loads 2–6.

| Mode | Median load | Loaded / blocked | Requests that reached the server |
|---|---|---|---|
| none | 309 ms | 600 / 0 | all |
| filter (`WebResourceRequested` on `*`, HashSet lookup in C#) | 244 ms | 300 / 300 | app.test only |
| **dnr** (bundled MV3 extension, one rule with 50k `requestDomains`) | **177 ms** | 300 / 300 | app.test only |

- DNR blocks inside Chromium's network stack, with no hop into our process. That makes it the true counterpart of the Mac's `WKContentRuleList`. The 50k-domain ruleset installed in 35 ms.
- The filter route paid ~65 ms over DNR on 600 requests. Every request (3,607 across six loads) crossed into the app process.
- Decision: **Shield is a built-in DNR extension.** The extension is hidden from the puzzle menu, and turning it off per site is a dynamic `allowAllRequests` rule. `WebResourceRequested` is kept only for things DNR can't express.
- Still to check: compile time and memory for a real ~100k-rule EasyList/EasyPrivacy conversion. DNR caps static rules per extension (and regex rules), so check how a real list fits under those caps.

## 3. Startup and memory (`startup-*`, `tabs-10`, `tabs-50`)

Milliseconds from process start, one tab, Debug build:

| | launched | window activated | environment | CoreWebView2 ready | page painted |
|---|---|---|---|---|---|
| cold (fresh profile) | 165 | 240 | 258 | 433 | 578 |
| warm (same profile) ×3 | 125–129 | 177–188 | 186–197 | 325–344 | 483–504 |

- The window is up in under 200 ms and the first page is painted in ~0.5 s warm. That meets the plan's targets (< 300 ms window, < 1 s page) even in Debug. ReadyToRun/Release should only improve it.
- Memory with one tab: the app process is ~92 MB private (WinUI, self-contained, Debug). WebView2 uses ~142 MB private across five processes: browser, GPU, two utility, one renderer.

Per-tab cost, with each tab on its own site (so each gets its own renderer, as real tabs do), on a 200-paragraph page:

| WebView2 private memory | 10 tabs | 50 tabs |
|---|---|---|
| one tab | 144 MB | 143 MB |
| all live | 396 MB | 1,532 MB (~28 MB/tab) |
| all but one hidden (Collapsed) | 396 MB | 1,532 MB |
| hidden ones `TrySuspendAsync` (all succeeded) | 393 MB | 1,504 MB |
| + `MemoryUsageTargetLevel.Low` | 390 MB | 1,490 MB |
| **hidden ones closed** | **179 MB** | **275 MB** |

- Creating and painting a tab takes ~100 ms each in sequence. Closing 49 took 36 ms. **Waking a closed tab (new WebView2 + navigate + paint) takes ~60 ms.**
- Suspend stops a tab's CPU use but returns almost no memory, so it can't stand in for the Mac's Sleep. Decision: **Sleep = `Close()` the WebView2** and keep its address, title, scroll position and a snapshot. Suspend is only for tabs hidden briefly (Split View panes, the tab just left).
- Lazy restored tabs cost nothing (no WebView2 until shown), so "50 restored tabs ≈ 1 tab" holds by design.

## 4. Picture in picture (`pip`)

- `document.pictureInPictureEnabled` is true. `video.requestPictureInPicture()` with a user gesture works and opens Chromium's own always-on-top window (`Chrome_WidgetWin_1`, titled in the UI language). That covers `Float.swift`'s main job without our own window.
- `documentPictureInPicture` is **not defined**, so floating arbitrary page content (the Mac's floating player with its own controls) needs our own `AppWindow`. That window can't hold the live `<video>` element, since a page can't be moved between WebView2s. Decision: video PiP is native; anything richer is deferred.

## 5. Overlays and the title bar (`overlay`)

- A WinUI `Border` over a WebView2 read back exactly `#FF0000` on screen, and the page beside it `#0000FF`. Composition hosting has no airspace problem, so the omnibox, peek, site card and popovers can draw over the page.
- `InputNonClientPointerSource.SetRegionRects` with `Caption` and `Passthrough` works with `ExtendsContentIntoTitleBar`. The empty part of the tab strip can drag the window while tabs and buttons in it stay clickable. Real dragging by hand hasn't been checked yet.

## 6. Windows and focus (`windows`, Phase 4)

Two tabs, and a web view of our own showing the extension's popup page, each a WebView2 in the same profile.

- **`Browser.getWindowForTarget`** (DevTools, no target id) on a tab's web view gives its window id, and it's the id the extension's `windows.getAll` reports for that tab. Each web view is its own window, so a window id names one tab.
- **In our popup**, `tabs.query({active: true, currentWindow: true})` returns the popup itself, and `tabs.getCurrent()` its own one-tab window. A script added with `AddScriptToExecuteOnDocumentCreatedAsync` runs in the extension page's own world before its scripts, so the popup can be told which window the tab in front is in.
- **In the worker**, `currentWindow`, `lastFocusedWindow` and `windows.getLastFocused` follow whichever web view last had the keyboard: tab A, tab B, or our popup once it has focus. With the popup focused, a worker asked "which tab?" answered with the popup. Hence a worker shim, with Search's pages of the extension saying which window is in front.
- **Native messaging works with nothing from us.** `runtime.sendNativeMessage` found and ran a host registered under `HKCU\Software\Google\Chrome\NativeMessagingHosts`, under `Microsoft\Edge` and under `Chromium`, each alone, with Chrome's arguments (`chrome-extension://<id>/ --parent-window=0`). Search reads what Chrome reads, as on the Mac. (The probe's registry keys were removed after each try.)

## 7. Reinstalling (`keeper`, Phase 4)

- `AddBrowserExtensionAsync` again on an installed extension's folder **loads its changed files** (new version, new worker code) **and keeps `storage.local`**.
- Turning it off and on restarts its worker with the new code but keeps the old manifest. Added again while off, it stays off yet still says `IsEnabled: true`, so it has to be switched on as the profile lists it.
- **`RemoveAsync` erases its saved data.** An update must never be remove + add.
- Every profile lists two built-in Microsoft extensions (Clipboard, the PDF viewer). Search never removes what it didn't install.
- Found building Phase 4, not by a spike: **Chromium refuses to load an extension with any file or folder whose name starts with `_`** (reserved for itself), and WebView2 says only "access denied". Search's files in a copy live in `search-browser/`.

## Changes to WINDOWS-PLAN.md

- Shield row: DNR extension, decided.
- Sleep row: close, not suspend, decided.
- Float row: native video PiP; document PiP unavailable.
- Extensions row and risk 1: native API coverage confirmed; the remaining work is the `tabs`/`windows` shim, `captureVisibleTab`, `commands`, and the bookmarks/history/topSites stores.
- Risks 2 and 3 are resolved by the above.
