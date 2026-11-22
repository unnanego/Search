// A site's two stylesheets on its page (Curtain.swift's Veiling.style and SiteStyles.swift's apply): what you hid,
// as `display: none` rules in office-veil, and your own style for the site in office-style. A function, called by
// Curtain.cs with its arguments as soon as it's read: at document start for the next page (armed for one host, which
// it checks, so a page of another site never wears this one's), or on the page up now (host null).
//
// `hiding` or `style` null leaves that sheet as it is; '' empties it. Each goes in through __searchSheet (sheet.js,
// which Curtain.cs puts in front of this): an adopted sheet, so a site's Content-Security-Policy can't turn it away.
((host, hiding, style) => {
  if (window !== window.top) return;
  if (host !== null && location.hostname.toLowerCase().replace(/^www\./, '') !== host) return;
  const veil = css => __searchSheet('office-veil', css);
  // After the site's own sheets, so a rule of yours wins over one of the same weight without needing !important. An
  // adopted sheet always is; the <style> element it falls back to is kept last in the document.
  const mine = css => {
    const sheet = __searchSheet('office-style', css);
    if (!sheet) return;
    const last = () => document.documentElement.appendChild(sheet);
    last();
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', last, { once: true });
  };
  const dress = () => { if (hiding !== null) veil(hiding); if (style !== null) mine(style); };
  // At document start there may be no <html> yet: the sheets go in the moment there is, before anything is drawn.
  if (document.documentElement) dress();
  else new MutationObserver((_, watch) => { if (document.documentElement) { watch.disconnect(); dress(); } }).observe(document, { childList: true });
})
