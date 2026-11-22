// Search's shim for an extension's worker (ExtensionShims.swift's Windows half), loaded before the extension's own
// worker by the wrapper Search writes beside it. WebView2 runs extensions natively; what it gets wrong is windows.
// Every web view is a one-tab window to an extension, Search's popups and side panels included, and the worker's
// "current window" is whichever web view last had the keyboard: with a popup open, that's the popup, so an extension
// asking its worker for the tab in front got its own popup back. Search's own pages of the extension (the popup, the
// side panel, the bridge a button press goes through) say which window the tab in front is in and which windows are
// Search's own surfaces; the queries below answer with that window, and leave the surfaces out.
(() => {
  if (globalThis.__searchShim) return;
  globalThis.__searchShim = true;
  const c = globalThis.chrome;
  if (!c || !c.runtime || !c.runtime.onMessage) return;
  const state = { front: null, surfaces: new Set() };
  const ours = m => !!m && typeof m === 'object' && typeof m.__search === 'string';
  const clickListeners = [];

  // A promise-returning API also called with a trailing callback, as Chrome's are.
  const both = fn => function (...args) {
    const done = typeof args[args.length - 1] === 'function' ? args.pop() : null;
    const result = fn.apply(this, args);
    if (!done) return result;
    result.then(v => done(v), () => done(undefined));
  };

  // Heard before any listener the extension adds, and kept from them: Search's messages aren't the extension's.
  c.runtime.onMessage.addListener((m, sender, reply) => {
    if (!ours(m)) return;
    if (m.front && typeof m.front.windowId === 'number') state.front = m.front;
    if (Array.isArray(m.surfaces)) for (const id of m.surfaces) state.surfaces.add(id);
    if (m.__search === 'click') { clicked(m.tabId).then(() => reply(true), () => reply(false)); return true; }
  });
  const hide = (event, skip) => {
    if (!event || !event.addListener) return;
    const add = event.addListener.bind(event), remove = event.removeListener.bind(event), has = event.hasListener.bind(event);
    const wrapped = new WeakMap();
    event.addListener = (fn, ...rest) => {
      const w = function (...args) { if (skip(...args)) return; return fn.apply(this, args); };
      wrapped.set(fn, w);
      return add(w, ...rest);
    };
    event.removeListener = fn => remove(wrapped.get(fn) || fn);
    event.hasListener = fn => has(wrapped.get(fn) || fn);
  };
  hide(c.runtime.onMessage, m => ours(m));

  // The button pressed with no popup to show: chrome.action.onClicked, which WebView2 has no way to fire.
  const action = c.action || c.browserAction;
  if (action && action.onClicked) {
    const add = action.onClicked.addListener.bind(action.onClicked), remove = action.onClicked.removeListener.bind(action.onClicked);
    action.onClicked.addListener = fn => { clickListeners.push(fn); return add(fn); };
    action.onClicked.removeListener = fn => { const at = clickListeners.indexOf(fn); if (at >= 0) clickListeners.splice(at, 1); return remove(fn); };
  }
  async function clicked(tabId) {
    const tab = typeof tabId === 'number' ? await c.tabs.get(tabId).catch(() => undefined) : undefined;
    for (const fn of clickListeners.slice()) { try { fn(tab, { button: 0, modifiers: [] }); } catch (e) { console.error(e); } }
  }

  if (!c.tabs || !c.windows) return;
  const real = {
    query: c.tabs.query.bind(c.tabs), get: c.windows.get.bind(c.windows), getAll: c.windows.getAll.bind(c.windows),
    getLastFocused: c.windows.getLastFocused.bind(c.windows)
  };
  const CURRENT = c.windows.WINDOW_ID_CURRENT ?? -2;

  // The browser's window as you see it: the web view that last had the keyboard, unless that's one of Search's
  // surfaces (then the tab in front when it opened).
  async function frontWindow() {
    const last = await real.getLastFocused().catch(() => null);
    if (last && !state.surfaces.has(last.id)) return last.id;
    return state.front ? state.front.windowId : last ? last.id : undefined;
  }

  c.tabs.query = both(async (info = {}) => {
    const q = Object.assign({}, info);
    let want = null;
    if (q.windowId === CURRENT) { delete q.windowId; q.currentWindow = true; }
    if ('currentWindow' in q || 'lastFocusedWindow' in q) {
      want = q.currentWindow ?? q.lastFocusedWindow;
      delete q.currentWindow; delete q.lastFocusedWindow;
    }
    const id = want === null ? undefined : await frontWindow();
    if (want === true && id !== undefined) q.windowId = id;
    const tabs = await real.query(q);
    return tabs.filter(t => !state.surfaces.has(t.windowId) && !(want === false && t.windowId === id));
  });
  c.windows.getCurrent = both(async (options) => real.get(await frontWindow(), options));
  c.windows.getLastFocused = both(async (options) => real.get(await frontWindow(), options));
  c.windows.getAll = both(async (options) => (await real.getAll(options)).filter(w => !state.surfaces.has(w.id)));
  c.windows.get = both(async (id, options) => real.get(id === CURRENT ? await frontWindow() : id, options));
})();
