// One of Search's stylesheets on the page by name (Curtain.swift's Veiling.sheets), for dress.js and curtain.js, which
// Curtain.cs puts this in front of. __searchSheet(id, css) puts `css` in the sheet named `id`, made the first time; an
// empty sheet never made isn't made. A sheet made by script and adopted by the document, not a <style> element: a site
// whose Content-Security-Policy limits styles (style-src) turns the element away even from Search's world, and
// everything Hide Elements took off, and your own style for the site, stayed off the page. Adopted sheets come after
// every sheet of the document's own, so a rule of yours wins over the site's own of the same weight.
//
// Only if adopting throws is the element used instead; it is returned then (null otherwise), for a caller that wants
// it in a particular place. Text in a style element, never markup: nothing in it can close the element.
globalThis.__searchSheet ??= (() => {
  const made = {};
  return (id, css) => {
    if (!css && !made[id] && !document.getElementById(id)) return null;
    try {
      const sheet = made[id] ??= new CSSStyleSheet();
      sheet.replaceSync(css);
      const adopted = document.adoptedStyleSheets;
      if (adopted.indexOf(sheet) < 0) document.adoptedStyleSheets = [...adopted, sheet];
      return null;
    } catch (e) {}
    let s = document.getElementById(id);
    if (!s) {
      s = document.createElement('style');
      s.id = id;
      (document.head || document.documentElement).appendChild(s);
    }
    s.textContent = css;
    return s;
  };
})();
