// How a coding agent reads a page and touches one element of a read (PageScripts in PageHands.swift), so the bench and
// an agent speak of a page in the same words. Runs in Search's isolated world (World.cs): the page's DOM, none of its
// globals, so the elements kept between calls are out of the page's reach. One file, one function: PageHands.cs calls
// it with what to do and its arguments, always as JSON, never as script, and every answer is a JSON string.
//
//   read    the page's visible controls and headings, with the element list kept under `key` for touch
//   touch   one element of the last read: click (where to click it), press, scroll, focus, set, submit, verify
//   check   whether wait_for's condition holds: text, role and name (never a selector from an agent)
//   secrets where a picture must be blacked out; hidden and shown make those invisible for the picture, and put back
//   scroll  the page by so many CSS pixels, or to its top or bottom
(function (verb, a) {
  var ROLES = {button:1, link:1, checkbox:1, radio:1, switch:1, tab:1, menuitem:1, menuitemcheckbox:1,
    menuitemradio:1, option:1, combobox:1, textbox:1, searchbox:1, slider:1, spinbutton:1, listbox:1,
    heading:1, dialog:1, alertdialog:1, alert:1, treeitem:1, gridcell:1, iframe:1};
  var SEL = 'a[href],area[href],button,input:not([type=hidden]),select,textarea,summary,[contenteditable],' +
    '[role],[tabindex],h1,h2,h3,h4,h5,h6,iframe';
  // Fields once seen as secret stay secret: a "show password" button turning one into a text field doesn't hand its
  // value over. The set is shared with forms.js, which adds every box Search fills or sees as a password.
  var SECRETS = globalThis.__searchSecrets || (globalThis.__searchSecrets = new WeakSet());
  function squash(s) { return String(s == null ? '' : s).replace(/\s+/g, ' ').trim().slice(0, 160); }
  function visible(el) {
    var r = el.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) return false;
    if (el.checkVisibility && !el.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) return false;
    return !el.closest('[aria-hidden=true],[inert]');
  }
  function role(el) {
    var r = (el.getAttribute('role') || '').trim().split(/\s+/)[0];
    if (r && r !== 'none' && r !== 'presentation') return r;
    var t = el.tagName.toLowerCase();
    if (t === 'a' || t === 'area') return el.hasAttribute('href') ? 'link' : 'generic';
    if (t === 'button' || t === 'summary') return 'button';
    if (t === 'select') return el.multiple || el.size > 1 ? 'listbox' : 'combobox';
    if (t === 'textarea') return 'textbox';
    if (/^h[1-6]$/.test(t)) return 'heading';
    if (t === 'iframe') return 'iframe';
    if (t === 'input') {
      return {checkbox: 'checkbox', radio: 'radio', range: 'slider', number: 'spinbutton', search: 'searchbox',
        submit: 'button', button: 'button', reset: 'button', image: 'button', file: 'button',
        color: 'button'}[(el.type || 'text').toLowerCase()] || 'textbox';
    }
    if (el.isContentEditable) return 'textbox';
    return 'generic';
  }
  function name(el) {
    var by = el.getAttribute('aria-labelledby'), t = el.tagName.toLowerCase(), s;
    if (by) {
      s = squash(by.split(/\s+/).map(function (id) {
        var n = document.getElementById(id); return n ? n.innerText || n.textContent : '';
      }).join(' '));
      if (s) return s;
    }
    if ((s = squash(el.getAttribute('aria-label')))) return s;
    if (/^(input|textarea|select)$/.test(t) && el.labels && el.labels.length &&
        (s = squash(Array.prototype.map.call(el.labels, function (l) { return l.innerText; }).join(' ')))) return s;
    if (t === 'input' && /^(submit|button|reset)$/i.test(el.type)) return squash(el.value || el.type);
    if (t === 'input' && /^image$/i.test(el.type)) return squash(el.alt || el.value);
    if (t === 'area') return squash(el.alt);
    if (t === 'iframe') return squash(el.title || el.src);
    if (!/^(input|textarea|select)$/.test(t)) {
      if ((s = squash(el.innerText))) return s;
      var img = el.querySelector('img[alt]');
      if (img && (s = squash(img.alt))) return s;
    }
    return squash(el.getAttribute('placeholder') || el.getAttribute('title'));
  }
  // A password, a card's number or code, a one-time code: by its type, its autocomplete, or what its name and label
  // say it is. Wrong the safe way: a "passport" field is kept back too.
  function secret(el) {
    if (el.tagName !== 'INPUT' && el.tagName !== 'TEXTAREA') return false;
    if (SECRETS.has(el)) return true;
    var ac = (el.getAttribute('autocomplete') || '').toLowerCase();
    var said = [el.name, el.id, el.getAttribute('aria-label'), el.getAttribute('placeholder'),
      el.labels && el.labels.length ? el.labels[0].innerText : ''].join(' ');
    var yes = (el.type || '').toLowerCase() === 'password' ||
      /(^|\s)(cc-|one-time-code|current-password|new-password)/.test(ac) ||
      /pass|pwd|otp|one.?time|2fa|totp|verification.?code|security.?code|cvc|cvv|csc|card.?(number|num|no)|cc.?num|\bpin\b/i.test(said);
    if (yes) SECRETS.add(el);
    return yes;
  }
  function editable(el) {
    if (el.isContentEditable) return true;
    if (el.tagName === 'TEXTAREA') return true;
    return el.tagName === 'INPUT' &&
      !/^(checkbox|radio|submit|button|reset|image|file|range|color|hidden)$/i.test(el.type || 'text');
  }
  function wanted(el) {
    var r = role(el);
    if (ROLES[r]) return r;
    var ti = el.getAttribute('tabindex');
    return ti !== null && +ti >= 0 ? r : null;
  }
  function candidates() {
    // The document's, then each open shadow root's: a closed one is the page's own business.
    var out = [], roots = [document];
    while (roots.length) {
      var root = roots.shift();
      Array.prototype.push.apply(out, root.querySelectorAll(SEL));
      var all = root.querySelectorAll('*');
      for (var i = 0; i < all.length; i++) if (all[i].shadowRoot) roots.push(all[i].shadowRoot);
    }
    return out;
  }
  function described(el) {
    var s = el.tagName.toLowerCase();
    if (el.id) s += '#' + el.id;
    if (typeof el.className === 'string' && el.className.trim()) s += '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.');
    return s;
  }

  // What a picture of the page must not show: every secret field (by what it says it is, or because Search filled it
  // or saw it as a password), and everything whose inside isn't read here and so can't be vouched for: every frame,
  // whoever's it is, every embed and object, and an element that draws something but shows no inside to read, as one
  // with a closed shadow root does. A custom element that does so is always counted; a standard one that can carry a
  // shadow root (div, span, section…) only up to a field's or a widget's size, or every empty background would go too.
  function masked() {
    var out = [], roots = [document];
    var hosts = /^(ARTICLE|ASIDE|BLOCKQUOTE|DIV|FOOTER|H[1-6]|HEADER|MAIN|NAV|P|SECTION|SPAN)$/;
    while (roots.length) {
      var root = roots.shift(), all = root.querySelectorAll('*');
      for (var i = 0; i < all.length; i++) {
        var el = all[i], t = el.tagName;
        if (el.shadowRoot) roots.push(el.shadowRoot);
        if (/^(IFRAME|FRAME|EMBED|OBJECT|PORTAL|FENCEDFRAME)$/.test(t)) { out.push(el); continue; }
        if ((t === 'INPUT' || t === 'TEXTAREA') && secret(el)) { out.push(el); continue; }
        if (el.shadowRoot || el.children.length || (el.textContent || '').trim()) continue;
        var r = el.getBoundingClientRect();
        if (!r.width || !r.height) continue;
        if (t.indexOf('-') > 0 || (hosts.test(t) && r.width <= 800 && r.height <= 300)) out.push(el);
      }
    }
    return out;
  }
  function boxes(els) {
    var out = [];
    els.forEach(function (el) {
      var r = el.getBoundingClientRect();
      if (r.width && r.height) out.push([r.left - 2, r.top - 2, r.width + 4, r.height + 4]);
    });
    return out;
  }

  function read(key, snap, most, longest, withText) {
    var found = candidates(), els = [], nodes = [], cut = false, vw = innerWidth, vh = innerHeight;
    for (var i = 0; i < found.length; i++) {
      var el = found[i], r0 = wanted(el);
      if (!r0 || !visible(el)) continue;
      if (nodes.length >= most) { cut = true; break; }
      var r = el.getBoundingClientRect(), n = { e: els.length, role: r0, name: name(el) };
      if (r0 === 'heading') n.level = +(el.getAttribute('aria-level') || el.tagName.slice(1)) || 2;
      if (/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) && !/^(submit|button|reset|image|checkbox|radio)$/i.test(el.type || '')) {
        if (secret(el)) { n.redacted = true; n.filled = !!el.value; } else n.value = String(el.value).slice(0, 500);
      } else if (el.isContentEditable) n.value = String(el.innerText).slice(0, 500);
      if (/^(checkbox|radio)$/i.test(el.type || '') && el.tagName === 'INPUT') n.checked = el.checked;
      ['checked', 'expanded', 'selected', 'pressed'].forEach(function (k) {
        var v = el.getAttribute('aria-' + k); if (v === 'true' || v === 'false') n[k] = v === 'true';
      });
      if (el.disabled || el.getAttribute('aria-disabled') === 'true') n.disabled = true;
      if (el.readOnly) n.readonly = true;
      if (el.tagName === 'A' && el.href) n.href = String(el.href).slice(0, 300);
      if (el.getRootNode() !== document) n.shadow = true;
      n.box = [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)];
      n.inView = r.bottom > 0 && r.right > 0 && r.top < vh && r.left < vw;
      els.push(el); nodes.push(n);
    }
    globalThis[key] = { snap: snap, els: els };
    var out = { url: location.href, title: document.title, nodes: nodes, nodesTruncated: cut,
      viewport: { width: vw, height: vh, scrollY: Math.round(scrollY),
        scrollHeight: document.documentElement ? document.documentElement.scrollHeight : 0 } };
    if (withText) {
      var text = document.body ? document.body.innerText : '';
      out.textTruncated = text.length > longest;
      out.text = text.slice(0, longest);
    }
    return JSON.stringify(out);
  }

  function touch(key, snap, i, how, text, replace, strict) {
    function no(code, message) { return JSON.stringify({ error: code, message: message }); }
    var kept = globalThis[key];
    if (!kept || kept.snap !== snap) return no('stale_element', 'The page has changed since that read_page');
    var el = kept.els[i];
    if (!el || !el.isConnected) return no('detached', 'That element is no longer in the page');
    // Choosing a file is a person's to do: never from here.
    if (strict && el.tagName === 'INPUT' && /^file$/i.test(el.type || '')) return no('file_input', 'Choosing files is left to you');
    if (how === 'click') {
      if (el.disabled) return no('disabled', 'That element is disabled');
      el.scrollIntoView({ block: 'center', inline: 'center' });
      var r = el.getBoundingClientRect();
      if (!r.width || !r.height || !visible(el)) return no('not_visible', 'That element is hidden');
      var x = r.left + r.width / 2, y = r.top + r.height / 2;
      if (x < 0 || y < 0 || x >= innerWidth || y >= innerHeight) return no('out_of_view', 'That element cannot be scrolled into view');
      var hit = document.elementFromPoint(x, y);
      while (hit && hit.shadowRoot) {
        var deeper = hit.shadowRoot.elementFromPoint(x, y);
        if (!deeper || deeper === hit) break;
        hit = deeper;
      }
      var label = hit && hit.closest ? hit.closest('label') : null;
      if (!hit || !(hit === el || el.contains(hit) || (label && label.control === el)))
        return no('obscured', 'Covered by ' + (hit ? described(hit) : 'nothing at its middle'));
      if (strict && label && label.control && label.control.tagName === 'INPUT' && /^file$/i.test(label.control.type || ''))
        return no('file_input', 'Choosing files is left to you');
      // A selector naming exactly this element, for the bench's real click; none inside a shadow root, which a
      // selector can't reach.
      var q = null;
      if (el.getRootNode() === document) {
        var parts = [];
        for (var n = el; n && n !== document.documentElement; n = n.parentElement) {
          var k = 1;
          for (var p = n.previousElementSibling; p; p = p.previousElementSibling) if (p.tagName === n.tagName) k++;
          parts.unshift(n.tagName.toLowerCase() + ':nth-of-type(' + k + ')');
        }
        parts.unshift('html');
        q = parts.join(' > ');
        if (document.querySelector(q) !== el) q = null;
      }
      return JSON.stringify({ ok: true, selector: q, at: [Math.round(x), Math.round(y)] });
    }
    if (how === 'press') { el.click(); return JSON.stringify({ ok: true }); }
    if (how === 'scroll') {
      el.scrollIntoView({ block: 'center', inline: 'nearest' });
      return JSON.stringify({ ok: true, scrollY: Math.round(scrollY) });
    }
    if (!editable(el)) return no('not_editable', 'That element is not a text field');
    if (el.disabled || el.readOnly) return no('not_editable', 'That field is disabled or read-only');
    var before = el.isContentEditable ? el.textContent : el.value;
    var want = replace ? text : before + text;
    if (how === 'focus') {
      el.scrollIntoView({ block: 'center', inline: 'nearest' });
      el.focus();
      if (replace) {
        if (el.isContentEditable) getSelection().selectAllChildren(el);
        else if (el.select) el.select();
      } else if (!el.isContentEditable && el.setSelectionRange) {
        try { el.setSelectionRange(el.value.length, el.value.length); } catch (e) {}
      }
      // What the field should hold once typed into, kept here when it is secret: what it held before is not for
      // whoever asked.
      if (secret(el)) { kept.want = want; return JSON.stringify({ ok: true, want: null }); }
      return JSON.stringify({ ok: true, want: want });
    }
    if (how === 'set') {
      el.focus();
      // Through the field's own setter, with the events a keystroke fires, so frameworks notice.
      if (el.isContentEditable) {
        el.textContent = want;
        el.dispatchEvent(new InputEvent('input', { bubbles: true, data: text, inputType: 'insertText' }));
      } else {
        var proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        var setter = Object.getOwnPropertyDescriptor(proto, 'value');
        if (setter && setter.set) setter.set.call(el, want); else el.value = want;
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
      }
    }
    if (how === 'submit') {
      var form = el.form || el.closest('form');
      if (!form) return no('no_form', 'That field is in no form');
      if (form.requestSubmit) form.requestSubmit(); else form.submit();
      return JSON.stringify({ ok: true });
    }
    var got = el.isContentEditable ? el.textContent : el.value;
    var expected = how === 'verify' ? (text === null ? kept.want : text) : want;
    var out = { ok: true, verified: got === expected };
    if (!out.verified && !secret(el)) out.holds = String(got).slice(0, 500);
    return JSON.stringify(out);
  }

  function check(c) {
    var ok = true;
    try {
      if (c.text != null) ok = ok && !!document.body && document.body.innerText.indexOf(c.text) !== -1;
      if (c.url != null) ok = ok && location.href.indexOf(c.url) !== -1;
      if (c.selector != null) { var e = document.querySelector(c.selector); ok = ok && !!e && visible(e); }
      if (c.role != null || c.name != null) {
        var want = c.name == null ? null : String(c.name).toLowerCase();
        ok = ok && candidates().some(function (el) {
          var r = wanted(el);
          return r && (c.role == null || r === c.role) &&
            (want == null || name(el).toLowerCase().indexOf(want) !== -1) && visible(el);
        });
      }
    } catch (e) {
      return JSON.stringify({ error: 'bad_condition', message: String(e && e.message || e) });
    }
    return JSON.stringify({ met: c.gone ? !ok : ok, url: location.href, title: document.title });
  }

  // What a picture must not show made invisible for the time it is taken (opacity, which nothing inside can undo),
  // and where it was. Kept under `key` for shown, which puts each back as it was.
  function hidden(key) {
    var els = masked(), kept = [];
    els.forEach(function (el) {
      kept.push([el, el.style.getPropertyValue('opacity'), el.style.getPropertyPriority('opacity')]);
      el.style.setProperty('opacity', '0', 'important');
    });
    globalThis[key] = kept;
    return JSON.stringify({ boxes: boxes(els), width: innerWidth, height: innerHeight });
  }
  function shown(key) {
    var kept = globalThis[key] || [];
    kept.forEach(function (k) {
      if (k[1]) k[0].style.setProperty('opacity', k[1], k[2]); else k[0].style.removeProperty('opacity');
    });
    globalThis[key] = null;
    return JSON.stringify({ shown: kept.length });
  }

  function scroll(dx, dy, to) {
    if (to === 'top') scrollTo(scrollX, 0);
    else if (to === 'bottom') scrollTo(scrollX, document.documentElement.scrollHeight);
    else scrollBy(dx, dy);
    return JSON.stringify({ scrollX: Math.round(scrollX), scrollY: Math.round(scrollY), height: innerHeight,
      scrollHeight: document.documentElement ? document.documentElement.scrollHeight : 0 });
  }

  switch (verb) {
    case 'read': return read(a.key, a.snap, a.most, a.longest, a.text);
    case 'touch': return touch(a.key, a.snap, a.index, a.how, a.text === undefined ? null : a.text, a.replace, !!a.strict);
    case 'check': return check(a.condition || {});
    case 'secrets': return JSON.stringify({ boxes: boxes(masked()), width: innerWidth, height: innerHeight });
    case 'hidden': return hidden(a.key);
    case 'shown': return shown(a.key);
    case 'scroll': return scroll(+a.dx || 0, +a.dy || 0, a.to == null ? null : a.to);
  }
  return JSON.stringify({ error: 'no_script', message: 'No such page script: ' + verb });
})
