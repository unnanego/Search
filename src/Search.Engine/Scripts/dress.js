// A site's two stylesheets on its page (Curtain.swift's Veiling.style and SiteStyles.swift's apply): what you hid,
// as `display: none` rules in office-veil, and your own style for the site in office-style. A function, called by
// Curtain.cs with its arguments as soon as it's read: at document start for the next page (armed for one host, which
// it checks, so a page of another site never wears this one's), or on the page up now (host null).
//
// `hiding` or `style` null leaves that sheet as it is; '' empties it. Text in a style element, never markup: nothing
// in it can close the element and become something else.
((host, hiding, style) => {
  if (window !== window.top) return;
  if (host !== null && location.hostname.toLowerCase().replace(/^www\./, '') !== host) return;
  const veil = css => {
    let sheet = document.getElementById('office-veil');
    if (!sheet) {
      if (!css) return;
      sheet = document.createElement('style');
      sheet.id = 'office-veil';
      (document.head || document.documentElement).appendChild(sheet);
    }
    sheet.textContent = css;
  };
  // Last in the document once there is one, so a rule of yours wins over the site's own of the same weight without
  // needing !important.
  const mine = css => {
    let sheet = document.getElementById('office-style');
    if (!sheet) {
      if (!css) return;
      sheet = document.createElement('style');
      sheet.id = 'office-style';
    }
    sheet.textContent = css;
    const last = () => document.documentElement.appendChild(sheet);
    last();
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', last, { once: true });
  };
  const dress = () => { if (hiding !== null) veil(hiding); if (style !== null) mine(style); };
  // At document start there may be no <html> yet: the sheets go in the moment there is, before anything is drawn.
  if (document.documentElement) dress();
  else new MutationObserver((_, watch) => { if (document.documentElement) { watch.disconnect(); dress(); } }).observe(document, { childList: true });
})
