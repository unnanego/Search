// The Chrome Web Store's own install, taken by Search (StoreRelay.swift's other half, Windows only). WebView2 says it
// is Chrome and gives the store its private install API, so the store's button is live and would put an extension
// straight into the browser, past Search's list, its copy and its shim. In the store's own world, before its scripts,
// that API is replaced: a press of the store's button (a real one, the page must have the user's gesture) asks Search
// instead, and the isolated world passes the asking on. As with Search's own button, what gets installed is read from
// the tab's address by the window; the page only asks. Removing, from the store's page, goes the same way.
(() => {
  if (__SEARCH_STORE_HOSTS__.indexOf(location.hostname) < 0 || window.__searchStorePage) return;
  window.__searchStorePage = true;
  const c = window.chrome || (window.chrome = {});
  const ask = what => document.dispatchEvent(new CustomEvent('search-store', { detail: JSON.stringify(what) }));
  const gesture = () => !navigator.userActivation || navigator.userActivation.isActive;
  const later = (value, done) => { if (typeof done === 'function') setTimeout(() => done(value), 0); return Promise.resolve(value); };
  const store = c.webstorePrivate || (c.webstorePrivate = {});
  const install = function (details) {
    const done = arguments[arguments.length - 1];
    if (gesture()) ask({ add: details && details.id });
    // The store waits for an answer Search gives on its own page instead ("Adding…", "Added to Search").
    if (typeof done === 'function') return;
    return new Promise(() => {});
  };
  store.beginInstallWithManifest3 = install;
  store.install = install;
  store.completeInstall = (id, done) => later(undefined, done);
  const management = c.management;
  if (management) {
    management.uninstall = function (id) {
      const done = arguments[arguments.length - 1];
      if (gesture()) ask({ remove: id });
      return later(undefined, done);
    };
  }
})();
