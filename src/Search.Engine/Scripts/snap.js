// Capture's layer over the page (Snap.swift's SnapGeometry.script): one light layer, and three ways out of it. Drag
// across an area, click an element (outlined under the pointer, as Hide Elements outlines it), or press Whole Page;
// Escape, or esc on the bar, and nothing is taken. Put in by Snap.cs only when Capture is chosen, in Search's own
// world (World.cs), main frame only. Everything it draws is in a closed shadow root, so the page's styles never reach
// it, and it is out of the page before it says what was chosen.
//
// What it says, through __searchSend, each with the id it was raised with: snap.chosen { id, take: "area" | "element"
// | "page", x, y, w, h (the choice in the layout viewport, CSS pixels), sx, sy (the scroll), dw, dh (the page's full
// size), iw, ih (the viewport's), vs (the pinch zoom), dpr (devicePixelRatio) }, or snap.off { id } when it was put
// away with nothing taken (SnapChoice.From reads the first).
(() => {
  if (window !== window.top || globalThis.__searchSnap) return;
  let host = null, frame = null, tag = null, band = null;
  let live = false, id = 0, start = null, dragging = false, target = null;
  // The accent colour as "r, g, b" (Windows' own blue unless the window says otherwise).
  let accent = '0, 120, 212';
  // A press that moves less than this, either way, is a click; a drag smaller than it is none (SnapMath.Drag).
  const drag = 4;
  const font = '"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif';

  const css = (el, text) => { el.style.cssText = text; return el; };

  const build = () => {
    host = css(document.createElement('div'), 'all:initial;position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;pointer-events:none');
    const root = host.attachShadow({ mode: 'closed' });
    // The outline in the accent colour, ringed in white and then a dark hairline so it reads on a page of any colour,
    // a dark one included, and a fill of the accent a little stronger in dark mode.
    const look = document.createElement('style');
    look.textContent =
      '.frame{position:fixed;pointer-events:none;box-sizing:border-box;border-radius:4px;display:none;' +
      'border:2px solid rgb(' + accent + ');background:rgba(' + accent + ',.12);' +
      'box-shadow:0 0 0 1px rgba(255,255,255,.9),0 0 0 2px rgba(0,0,0,.35)}' +
      '.band{position:fixed;pointer-events:none;box-sizing:border-box;display:none;' +
      'border:2px solid rgb(' + accent + ');background:rgba(' + accent + ',.08);' +
      'box-shadow:0 0 0 1px rgba(255,255,255,.9),0 0 0 2px rgba(0,0,0,.35),0 0 0 100vmax rgba(0,0,0,.22)}' +
      '@media (prefers-color-scheme: dark){' +
      '.frame{background:rgba(' + accent + ',.24)}' +
      '.band{background:rgba(' + accent + ',.14);' +
      'box-shadow:0 0 0 1px rgba(255,255,255,.9),0 0 0 2px rgba(0,0,0,.5),0 0 0 100vmax rgba(0,0,0,.4)}}';
    root.appendChild(look);
    frame = document.createElement('div');
    frame.className = 'frame';
    tag = css(document.createElement('div'),
      'position:absolute;font:500 11px ' + font + ';color:#fff;background:rgb(' + accent + ');padding:2px 7px;border-radius:5px;' +
      'white-space:nowrap;overflow:hidden;text-overflow:ellipsis;box-shadow:0 0 0 1px rgba(255,255,255,.6)');
    frame.appendChild(tag);
    // The area being dragged, and everything else a shade darker.
    band = document.createElement('div');
    band.className = 'band';
    const bar = css(document.createElement('div'),
      'position:fixed;top:12px;left:50%;transform:translateX(-50%);pointer-events:auto;' +
      'display:flex;align-items:center;gap:10px;padding:5px 5px 5px 12px;border-radius:10px;' +
      'background:rgba(23,23,23,.92);color:rgba(255,255,255,.75);cursor:default;' +
      'font:500 12px ' + font + ';white-space:nowrap;' +
      'box-shadow:0 0 0 1px rgba(255,255,255,.16),0 6px 18px rgba(0,0,0,.18)');
    const hint = document.createElement('span');
    hint.textContent = 'Drag an area or click an element';
    const whole = css(document.createElement('div'), 'padding:4px 10px;border-radius:7px;background:#fff;color:#171717;cursor:pointer');
    whole.textContent = 'Whole Page';
    whole.setAttribute('role', 'button');
    whole.addEventListener('click', e => { e.preventDefault(); e.stopPropagation(); finish(choice('page')); });
    const esc = css(document.createElement('div'), 'padding:4px 8px 4px 2px;color:rgba(255,255,255,.5);cursor:pointer');
    esc.textContent = 'esc';
    esc.setAttribute('role', 'button');
    esc.addEventListener('click', e => { e.stopPropagation(); off(); });
    bar.append(hint, whole, esc);
    root.append(band, frame, bar);
    document.documentElement.appendChild(host);
  };

  const known = {
    nav: 'Navigation', header: 'Header', footer: 'Footer', aside: 'Sidebar', main: 'Main',
    article: 'Article', section: 'Section', form: 'Form', dialog: 'Dialog', video: 'Video',
    img: 'Image', picture: 'Image', svg: 'Graphic', canvas: 'Canvas', button: 'Button',
    iframe: 'Embed', figure: 'Figure', table: 'Table', pre: 'Code'
  };

  // What it calls itself, or what kind of thing it is, and its size.
  const name = el => {
    const said = el.getAttribute && (el.getAttribute('aria-label') || el.getAttribute('title'));
    let what = said && said.trim() ? said.trim() : (known[el.tagName.toLowerCase()] || el.tagName.toLowerCase());
    if (what.length > 40) what = what.slice(0, 40) + '…';
    const r = el.getBoundingClientRect();
    return what + ' · ' + Math.round(r.width) + '×' + Math.round(r.height);
  };

  const place = el => {
    const r = el.getBoundingClientRect();
    frame.style.display = 'block';
    frame.style.left = r.left + 'px';
    frame.style.top = r.top + 'px';
    frame.style.width = r.width + 'px';
    frame.style.height = r.height + 'px';
    tag.textContent = name(el);
    // Above the element if there is room above it, tucked inside its top edge if there isn't, and never off an edge.
    tag.style.top = r.top >= 26 ? '-21px' : '3px';
    tag.style.left = Math.max(2, -r.left + 4) + 'px';
    tag.style.maxWidth = Math.max(80, window.innerWidth - Math.max(0, r.left) - 16) + 'px';
  };

  const under = (x, y) => {
    const el = document.elementFromPoint(x, y);
    return !el || el === host || el === document.documentElement ? null : el;
  };

  const choice = (take, r) => {
    const doc = document.documentElement, body = document.body, vv = window.visualViewport;
    return {
      take,
      x: r ? r.left : 0, y: r ? r.top : 0, w: r ? r.width : 0, h: r ? r.height : 0,
      sx: window.scrollX, sy: window.scrollY,
      dw: Math.max(doc.scrollWidth, body ? body.scrollWidth : 0, doc.clientWidth),
      dh: Math.max(doc.scrollHeight, body ? body.scrollHeight : 0, doc.clientHeight),
      iw: doc.clientWidth || window.innerWidth, ih: doc.clientHeight || window.innerHeight,
      vs: vv ? vv.scale : 1, dpr: window.devicePixelRatio || 1
    };
  };

  const ours = e => e.target === host;

  const eat = e => { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); };

  const swallow = e => { if (live && !ours(e)) eat(e); };

  const onDown = e => {
    if (!live || ours(e)) return;
    eat(e);
    if (e.button !== 0) return;
    start = { x: e.clientX, y: e.clientY };
    dragging = false;
  };

  const onMove = e => {
    if (!live) return;
    if (start) {
      const dx = e.clientX - start.x, dy = e.clientY - start.y;
      if (!dragging && dx * dx + dy * dy < drag * drag) return;
      dragging = true;
      frame.style.display = 'none';
      band.style.display = 'block';
      band.style.left = Math.min(start.x, e.clientX) + 'px';
      band.style.top = Math.min(start.y, e.clientY) + 'px';
      band.style.width = Math.abs(dx) + 'px';
      band.style.height = Math.abs(dy) + 'px';
      return;
    }
    if (ours(e)) { frame.style.display = 'none'; target = null; return; }
    const el = under(e.clientX, e.clientY);
    if (!el) return;
    target = el;
    place(el);
  };

  // What a press brings after its pointerup (the click, a mouseup) still the page's once the layer is down, eaten for
  // a moment: a link or a button clicked to be captured would otherwise be followed or pressed as it was taken.
  const after = ['click', 'dblclick', 'mouseup', 'auxclick', 'contextmenu'];

  const trail = () => {
    after.forEach(kind => window.addEventListener(kind, eat, true));
    setTimeout(() => after.forEach(kind => window.removeEventListener(kind, eat, true)), 500);
  };

  const onUp = e => {
    if (!live || !start) return;
    swallow(e);
    const from = start;
    start = null;
    if (dragging) {
      dragging = false;
      const r = { left: Math.min(from.x, e.clientX), top: Math.min(from.y, e.clientY), width: Math.abs(e.clientX - from.x), height: Math.abs(e.clientY - from.y) };
      if (r.width < drag || r.height < drag) { band.style.display = 'none'; return; }
      trail();
      finish(choice('area', r));
      return;
    }
    const el = target || under(e.clientX, e.clientY);
    if (!el) return;
    trail();
    finish(choice('element', el.getBoundingClientRect()));
  };

  // Chromium took the press for itself (a link or an image dragged, text selected) and sends no pointerup. A press
  // that wasn't yet an area is the click it was meant as.
  const onCancel = () => {
    if (!live || !start) return;
    const from = start;
    start = null;
    if (dragging) { dragging = false; band.style.display = 'none'; return; }
    const el = target || under(from.x, from.y);
    if (!el) return;
    trail();
    finish(choice('element', el.getBoundingClientRect()));
  };

  // Real presses only: a page can't put the layer away by dispatching one.
  const onKey = e => {
    if (!live || !e.isTrusted || (e.key !== 'Escape' && e.keyCode !== 27)) return;
    eat(e);
    off();
  };

  // dragstart and selectstart too: a link or an image pressed is otherwise picked up to be dragged, and text selected.
  const presses = ['mousedown', 'mouseup', 'click', 'dblclick', 'contextmenu', 'touchstart', 'auxclick', 'dragstart', 'selectstart'];

  const listen = add => {
    const how = add ? 'addEventListener' : 'removeEventListener';
    window[how]('pointerdown', onDown, true);
    window[how]('pointermove', onMove, true);
    window[how]('pointerup', onUp, true);
    window[how]('pointercancel', onCancel, true);
    window[how]('keydown', onKey, true);
    presses.forEach(kind => window[how](kind, swallow, true));
  };

  const teardown = () => {
    live = false;
    start = null;
    dragging = false;
    target = null;
    listen(false);
    document.documentElement.style.cursor = '';
    if (host && host.parentNode) host.parentNode.removeChild(host);
    host = null;
  };

  // Out of the page first, then said: what's taken next never has the layer in it.
  const finish = chosen => {
    if (!live) return;
    const was = id;
    teardown();
    __searchSend(chosen ? Object.assign({ kind: 'snap.chosen', id: was }, chosen) : { kind: 'snap.off', id: was });
  };

  const off = () => finish(null);

  globalThis.__searchSnap = {
    // Raised for `raised` (Snap.cs's count, said back with the choice); one up already goes away with nothing.
    on(raised, colour) {
      if (live) off();
      if (colour) accent = colour;
      id = raised;
      live = true;
      build();
      document.documentElement.style.cursor = 'crosshair';
      listen(true);
      return true;
    },
    // Put away; given an id, only if that layer is the one up (a late call never takes down the next one).
    off(raised) { if (raised === undefined || raised === id) off(); },
    // For the bench: the three choices without a pointer.
    area(x, y, w, h) { finish(choice('area', { left: x, top: y, width: w, height: h })); },
    element(x, y) { const el = under(x, y); if (el) finish(choice('element', el.getBoundingClientRect())); else off(); },
    page() { finish(choice('page')); },
    live: () => live
  };
})();
