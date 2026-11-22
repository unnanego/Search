// Search's shim for an extension's own pages (ExtensionPopup.swift's "a popup belongs to the browser's window"),
// added at document start in the page's world: the page shares this world with the extension's scripts, so they
// see the corrected APIs. Ordinary pages leave at once. Everywhere, Search's messages are kept from the extension's
// own listeners. In a popup or a side panel, which Chrome gives no tab and no window of its own, "the current window"
// is the browser's — the window the tab in front is in — and the worker is told so, and which windows are Search's.
(() => {
  if (location.protocol !== 'chrome-extension:' || window.__searchPage) return;
  window.__searchPage = true;
  const c = window.chrome;
  if (!c || !c.runtime || !c.runtime.onMessage) return;
  const mode = __SEARCH_MODE__;
  const ours = m => !!m && typeof m === 'object' && typeof m.__search === 'string';
  const event = c.runtime.onMessage, add = event.addListener.bind(event), remove = event.removeListener.bind(event), has = event.hasListener.bind(event);
  const wrapped = new WeakMap();
  event.addListener = (fn, ...rest) => { const w = function (...args) { if (ours(args[0])) return; return fn.apply(this, args); }; wrapped.set(fn, w); return add(w, ...rest); };
  event.removeListener = fn => remove(wrapped.get(fn) || fn);
  event.hasListener = fn => has(wrapped.get(fn) || fn);
  if (mode === 'tab' || !c.tabs || !c.windows) return;

  const front = { windowId: __SEARCH_FRONT__, tabId: null };
  const surfaces = new Set(__SEARCH_SURFACES__);
  const real = {
    query: c.tabs.query.bind(c.tabs), getCurrent: c.tabs.getCurrent.bind(c.tabs), get: c.windows.get.bind(c.windows),
    getAll: c.windows.getAll.bind(c.windows)
  };
  const CURRENT = c.windows.WINDOW_ID_CURRENT ?? -2;
  const both = fn => function (...args) {
    const done = typeof args[args.length - 1] === 'function' ? args.pop() : null;
    const result = fn.apply(this, args);
    if (!done) return result;
    result.then(v => done(v), () => done(undefined));
  };
  const tell = () => { try { const p = c.runtime.sendMessage({ __search: 'front', front, surfaces: [...surfaces] }); if (p && p.catch) p.catch(() => {}); } catch (e) { } };
  const settle = async () => {
    const own = await real.getCurrent().catch(() => null);
    if (own) surfaces.add(own.windowId);
    front.tabId = null;
    if (typeof front.windowId === 'number') { const [tab] = await real.query({ windowId: front.windowId }).catch(() => []); front.tabId = tab ? tab.id : null; }
    tell();
    return front;
  };
  let ready = settle();
  window.__searchReady = () => ready;
  // The side panel stays open as you go from tab to tab: Search says each time which window is in front now.
  window.__searchFront = windowId => { front.windowId = windowId; ready = settle(); return ready; };

  c.tabs.query = both(async (info = {}) => {
    const q = Object.assign({}, info);
    let want = null;
    if (q.windowId === CURRENT) { delete q.windowId; q.currentWindow = true; }
    if ('currentWindow' in q || 'lastFocusedWindow' in q) { want = q.currentWindow ?? q.lastFocusedWindow; delete q.currentWindow; delete q.lastFocusedWindow; }
    if (want === true && typeof front.windowId === 'number') q.windowId = front.windowId; else if (want === true) q.currentWindow = true;
    const tabs = await real.query(q);
    return tabs.filter(t => !surfaces.has(t.windowId) && !(want === false && t.windowId === front.windowId));
  });
  // A popup or a panel isn't in a tab.
  c.tabs.getCurrent = both(async () => undefined);
  c.windows.getCurrent = both(async options => real.get(front.windowId, options));
  c.windows.getLastFocused = both(async options => real.get(front.windowId, options));
  c.windows.getAll = both(async options => (await real.getAll(options)).filter(w => !surfaces.has(w.id)));
  c.windows.get = both(async (id, options) => real.get(id === CURRENT ? front.windowId : id, options));

  if (mode !== 'popup') return;
  // A popup goes on Esc, as Chrome's do; and says how big its page is, so it can be sized to it.
  addEventListener('keydown', e => { if (e.key === 'Escape' && !e.defaultPrevented) window.close(); });
  const post = () => {
    const h = document.documentElement, b = document.body;
    if (!b || !window.chrome.webview) return;
    const old = h.style.width;
    h.style.width = 'fit-content';
    const width = Math.ceil(Math.max(h.getBoundingClientRect().width, b.scrollWidth));
    h.style.width = old;
    const height = Math.ceil(Math.max(h.scrollHeight, b.scrollHeight, h.getBoundingClientRect().height));
    window.chrome.webview.postMessage({ size: [width, height] });
  };
  let queued = false;
  const later = () => { if (queued) return; queued = true; setTimeout(() => { queued = false; post(); }, 30); };
  addEventListener('DOMContentLoaded', () => { new ResizeObserver(later).observe(document.documentElement); if (document.body) new ResizeObserver(later).observe(document.body); new MutationObserver(later).observe(document.documentElement, { childList: true, subtree: true, attributes: true }); later(); });
  addEventListener('load', later);
})();
