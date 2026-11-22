// The Chrome Web Store, made to work for this browser (StoreRelay.swift). The store sees a browser that isn't Chrome:
// a banner asking to switch, and an "Add to Chrome" button that stays grey or does nothing. On its pages the banner
// goes and the store's button is replaced by an "Add to Search" one, the button people already look for. What gets
// installed is read from the tab's own address by the window, never from anything the page says; the page only asks,
// and the usual confirmation still stands between the asking and the installing. Runs in Search's isolated world.
(function () {
  if (__SEARCH_STORE_HOSTS__.indexOf(location.hostname) < 0 || window.__searchStore) return;
  var state = { installed: [], busy: [] };

  function pageID() {
    var m = location.pathname.match(/\/detail\/(?:[^\/]+\/)?([a-p]{32})/);
    return m ? m[1] : null;
  }

  // The store's own button: the disabled one that names Chrome, or, where the store believes the browser can take
  // it (WebView2 says it's Edge), the enabled one beside the extension's name. Never one in the banner or a card.
  function theirs() {
    var buttons = document.querySelectorAll('button');
    var fallback = null;
    for (var i = 0; i < buttons.length; i++) {
      var b = buttons[i];
      if (b.dataset.search || !/chrome/i.test(b.textContent || '')) continue;
      if (b.disabled) return b;
      if (!fallback && !b.closest('[role="dialog"]') && b.closest('main, section') && (b.closest('main, section').querySelector('h1'))) fallback = b;
    }
    return fallback;
  }

  // From the banner's own button up, as far as it goes without taking in the header beside it.
  function bannerOf(button) {
    var box = null, up = button.parentElement;
    while (up && up !== document.body) {
      if (up.querySelector('button[disabled], button[data-search]') || up.querySelector('h1')) break;
      if ((up.innerText || '').length > 160) break;
      box = up;
      up = up.parentElement;
    }
    return box;
  }

  function hideBanner() {
    var cards = document.querySelectorAll('[role="dialog"]');
    for (var c = 0; c < cards.length; c++) {
      if (!cards[c].dataset.search && cards[c].querySelector('img[src*="productlogos/chrome"]')) { cards[c].style.display = 'none'; cards[c].dataset.search = 'promo'; }
    }
    var buttons = document.querySelectorAll('button:not([disabled])');
    for (var i = 0; i < buttons.length; i++) {
      var b = buttons[i];
      if (b.dataset.search || !/chrome/i.test(b.getAttribute('aria-label') || '')) continue;
      var box = bannerOf(b);
      if (box && !box.dataset.search) { box.style.display = 'none'; box.dataset.search = 'banner'; }
    }
  }

  // The words only, so the button keeps the store's own shape and colour.
  function label(button, text) {
    var walker = document.createTreeWalker(button, NodeFilter.SHOW_TEXT);
    var node, last = null;
    while ((node = walker.nextNode())) { if (node.nodeValue.trim()) last = node; }
    if (last) last.nodeValue = text; else button.textContent = text;
  }

  function render(ours) {
    var id = pageID();
    var installed = !!id && state.installed.indexOf(id) >= 0, busy = !!id && state.busy.indexOf(id) >= 0;
    label(ours, installed ? 'Added to Search' : (busy ? 'Adding…' : 'Add to Search'));
    ours.disabled = installed || busy;
  }

  function mend() {
    hideBanner();
    if (!pageID()) return;
    var original = theirs();
    if (original && original.parentNode) {
      var ours = original.cloneNode(true);
      ['disabled', 'jsaction', 'jscontroller', 'jsname', 'jslog', 'aria-describedby'].forEach(function (name) { ours.removeAttribute(name); });
      ours.dataset.search = 'add';
      original.dataset.search = 'theirs';
      original.style.display = 'none';
      original.parentNode.insertBefore(ours, original.nextSibling);
      __searchSend({ kind: 'store', placed: pageID() });
    }
    renderAll();
  }

  function renderAll() {
    var mine = document.querySelectorAll('button[data-search="add"], button[data-search="pressed"]');
    for (var i = 0; i < mine.length; i++) render(mine[i]);
  }

  // The store's own button, live where the store takes this for Chrome: the one last pressed, so it can say how the
  // install it asked for is going once the store's page has asked (store-page.js).
  var pressed = null;
  document.addEventListener('search-store', function (e) {
    var what = null;
    try { what = JSON.parse(e.detail); } catch (x) { return; }
    if (what && what.add) { if (pressed && !pressed.dataset.search) pressed.dataset.search = 'pressed'; __searchSend({ kind: 'store', add: true }); renderAll(); }
    else if (what && what.remove) __searchSend({ kind: 'store', remove: true });
  });

  // Caught on the window, before the store's own handlers, which listen on the document.
  window.addEventListener('click', function (e) {
    var button = e.isTrusted && e.target && e.target.closest ? e.target.closest('button') : null;
    if (button) pressed = button;
    var mine = e.target && e.target.closest && e.target.closest('button[data-search="add"]');
    if (!mine) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (!mine.disabled && e.isTrusted) __searchSend({ kind: 'store', add: true });
  }, true);

  window.__searchStore = { state: function (next) { state = next || state; renderAll(); } };

  // The store is one page that rewrites itself: whatever it redraws, mend again. A timer rather than a frame.
  var queued = false;
  function start() {
    new MutationObserver(function () {
      if (queued) return;
      queued = true;
      setTimeout(function () { queued = false; mend(); }, 60);
    }).observe(document.documentElement, { childList: true, subtree: true });
    mend();
  }
  if (document.documentElement) start(); else document.addEventListener('DOMContentLoaded', start);
})();
