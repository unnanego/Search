// Taking things off a page (Curtain.swift's Veiling.picker): point at a cookie bar, a newsletter overlay, a sidebar of
// related nonsense, and it goes. Loaded on every page but asleep: it costs one closure and a few functions until
// somebody actually asks for it (__searchCurtain.on()). Main frame only, as on the Mac.
//
// What it says, through __searchSend: curtain.chosen { selector, label, note }, curtain.trouble { trouble },
// curtain.undo (Ctrl+Z while pointing), curtain.off (pointing ended, by Esc here or by the window). The window has the
// keyboard on the Mac; here the page does, so Esc and Ctrl+Z are heard here while pointing, and only then.
(() => {
  if (window !== window.top || globalThis.__searchCurtain) return;
  let frame = null, tag = null, target = null, live = false;

  const chrome = () => {
    if (frame) return frame;
    frame = document.createElement('div');
    frame.style.cssText = 'position:fixed;z-index:2147483646;pointer-events:none;' +
      'border:2px solid rgba(23,23,23,.9);background:rgba(23,23,23,.07);' +
      'border-radius:4px;transition:all .07s ease-out;display:none';
    tag = document.createElement('div');
    tag.style.cssText = 'position:absolute;font:500 11px "Segoe UI Variable Text","Segoe UI",system-ui,' +
      'sans-serif;color:#fff;background:#171717;padding:2px 7px;' +
      'border-radius:5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis';
    frame.appendChild(tag);
    document.documentElement.appendChild(frame);
    return frame;
  };

  const place = el => {
    const box = chrome(), r = el.getBoundingClientRect();
    box.style.display = 'block';
    box.style.left = r.left + 'px';
    box.style.top = r.top + 'px';
    box.style.width = r.width + 'px';
    box.style.height = r.height + 'px';
    tag.textContent = name(el);
    // Above the element if there is sky above it, tucked inside its top edge if there isn't: an element flush with
    // the top of the window would otherwise have its name cut off by the window.
    tag.style.top = r.top >= 26 ? '-21px' : '3px';
    // And never off the left or right edge either.
    tag.style.left = Math.max(2, -r.left + 4) + 'px';
    tag.style.maxWidth = Math.max(80, window.innerWidth - Math.max(0, r.left) - 16) + 'px';
  };

  const known = {
    nav: 'Navigation', header: 'Header', footer: 'Footer', aside: 'Sidebar',
    form: 'Form', dialog: 'Dialog', video: 'Video', img: 'Image',
    button: 'Button', iframe: 'Embed', figure: 'Figure', table: 'Table'
  };

  const clip = (text, n) => text.length > n ? text.slice(0, n) + '…' : text;

  // What it is, in the order a person would answer the question: what it calls itself, then what kind of thing it
  // is, then what it says.
  const name = el => {
    const said = el.getAttribute && (el.getAttribute('aria-label') || el.getAttribute('title'));
    if (said && said.trim()) return clip(said.trim(), 40);
    const tagName = el.tagName.toLowerCase();
    if (known[tagName]) return known[tagName];
    const role = el.getAttribute && el.getAttribute('role');
    if (role) return role.charAt(0).toUpperCase() + role.slice(1);
    const text = (el.innerText || '').trim().replace(/\s+/g, ' ');
    return text ? clip(text, 40) : tagName;
  };

  // How big, and which corner. Two sidebars read alike; they are rarely the same shape in the same place.
  const shape = el => {
    const r = el.getBoundingClientRect();
    const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
    const side = cx < window.innerWidth / 3 ? 'left' : (cx > window.innerWidth * 2 / 3 ? 'right' : 'centre');
    const band = cy < window.innerHeight / 3 ? 'top' : (cy > window.innerHeight * 2 / 3 ? 'bottom' : 'middle');
    return Math.round(r.width) + '×' + Math.round(r.height) + ' · ' + band + ' ' + side;
  };

  // A class worth hanging a rule on: a word, not a build artefact.
  const steady = c => /^[a-zA-Z][\w-]{2,29}$/.test(c) && !/\d{3,}/.test(c) && !/^(css|sc|jsx|emotion|svelte|styles?)-/.test(c);

  const unique = sel => { try { return document.querySelectorAll(sel).length === 1; } catch (e) { return false; } };

  // A selector that finds the same element again next time: its id, a testing or naming hook, its steady classes,
  // and as a last resort a path anchored on the nearest thing with a name.
  const selectorFor = el => {
    if (el.id && unique('#' + CSS.escape(el.id))) return '#' + CSS.escape(el.id);

    const hooks = ['data-testid', 'data-test', 'data-qa', 'data-cy', 'aria-label', 'name', 'role'];
    for (const hook of hooks) {
      const v = el.getAttribute && el.getAttribute(hook);
      if (v) {
        const s = el.tagName.toLowerCase() + '[' + hook + '="' + CSS.escape(v) + '"]';
        if (unique(s)) return s;
      }
    }

    const classes = (el.className && typeof el.className === 'string') ? el.className.trim().split(/\s+/).filter(steady) : [];
    if (classes.length) {
      const byClass = el.tagName.toLowerCase() + '.' + classes.map(CSS.escape).join('.');
      if (unique(byClass)) return byClass;
    }

    const parts = [];
    let node = el;
    while (node && node.nodeType === 1 && node !== document.documentElement) {
      if (node.id && unique('#' + CSS.escape(node.id))) {
        parts.unshift('#' + CSS.escape(node.id));
        break;
      }
      const tagName = node.tagName.toLowerCase();
      const parent = node.parentElement;
      if (!parent) { parts.unshift(tagName); break; }
      const kin = Array.prototype.filter.call(parent.children, c => c.tagName === node.tagName);
      parts.unshift(kin.length > 1 ? tagName + ':nth-of-type(' + (kin.indexOf(node) + 1) + ')' : tagName);
      node = parent;
    }
    return parts.join(' > ');
  };

  const onMove = e => {
    if (!live) return;
    const el = document.elementFromPoint(e.clientX, e.clientY);
    if (!el || el === frame || el === document.documentElement || el === document.body) return;
    target = el;
    place(el);
  };

  // Everything a press can be, swallowed. Real pages act on pointerdown or mousedown and are gone before a click ever
  // completes, which looked exactly like nothing happening.
  const swallow = e => {
    if (!live) return;
    e.preventDefault();
    e.stopPropagation();
    e.stopImmediatePropagation();
  };

  const onPress = e => {
    if (!live) return;
    swallow(e);
    // The pointer may have arrived without ever moving: a click on the very first element under it, or a tap.
    const el = target || document.elementFromPoint(e.clientX, e.clientY);
    if (!el || el === frame || el === document.documentElement || el === document.body) return;
    try {
      __searchSend({ kind: 'curtain.chosen', selector: selectorFor(el), label: name(el), note: shape(el) });
    } catch (err) {
      __searchSend({ kind: 'curtain.trouble', trouble: String(err) });
    }
    target = null;
    if (frame) frame.style.display = 'none';
  };

  // Esc ends pointing; Ctrl+Z puts the last thing back. Real presses only, and the page never sees them.
  const onKey = e => {
    if (!live || !e.isTrusted) return;
    const z = e.code === 'KeyZ' || (!e.code && e.keyCode === 90);
    if (e.key === 'Escape' || e.keyCode === 27) { swallow(e); off(); }
    else if (z && (e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey) { swallow(e); __searchSend({ kind: 'curtain.undo' }); }
  };

  const presses = ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click', 'dblclick', 'contextmenu', 'touchstart'];

  const on = () => {
    if (live) return;
    live = true;
    chrome();
    document.documentElement.style.cursor = 'crosshair';
    document.addEventListener('mousemove', onMove, true);
    document.addEventListener('pointermove', onMove, true);
    presses.forEach(kind => document.addEventListener(kind, kind === 'pointerdown' ? onPress : swallow, true));
    window.addEventListener('keydown', onKey, true);
  };

  const off = () => {
    if (!live) return;
    live = false;
    target = null;
    if (frame) frame.style.display = 'none';
    document.documentElement.style.cursor = '';
    document.removeEventListener('mousemove', onMove, true);
    document.removeEventListener('pointermove', onMove, true);
    presses.forEach(kind => document.removeEventListener(kind, kind === 'pointerdown' ? onPress : swallow, true));
    window.removeEventListener('keydown', onKey, true);
    __searchSend({ kind: 'curtain.off' });
  };

  globalThis.__searchCurtain = {
    on, off,
    // Show one hidden thing for as long as the pointer rests on its row. The stylesheet is rebuilt without that one
    // selector rather than fighting it with another rule, so the element comes back with the layout it actually had.
    // Both sheets through __searchSheet (sheet.js, put in front of this by Curtain.cs), as dress.js's are.
    peek(css, sel) {
      __searchSheet('office-veil', css);
      __searchSheet('office-peek', sel + ' { outline: 2px solid rgba(23,23,23,.9) !important; outline-offset: 2px !important; }');
      try {
        const el = document.querySelector(sel);
        if (el) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
      } catch (e) {}
    },
    unpeek(css) {
      __searchSheet('office-veil', css);
      __searchSheet('office-peek', '');
    }
  };
})();
