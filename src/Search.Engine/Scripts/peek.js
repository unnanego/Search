// A peek at a link (Peek.swift): Shift+click on a link opens its page over the one you're reading instead of going
// there. In Search's isolated world, at the capture phase so it comes before the page's own handlers; real clicks only.
(() => {
  addEventListener('click', e => {
    if (!e.isTrusted || e.button !== 0 || !e.shiftKey || e.ctrlKey || e.altKey || e.metaKey) return;
    const link = e.target instanceof Element ? e.target.closest('a[href]') : null;
    if (!link) return;
    const url = new URL(link.getAttribute('href'), location.href);
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return;
    e.preventDefault();
    e.stopImmediatePropagation();
    __searchSend({ kind: 'peek', url: url.href });
  }, true);
})();
