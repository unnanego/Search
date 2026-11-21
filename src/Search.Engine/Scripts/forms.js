// What a page says about its sign-ins that a browser has to know (FormRelay.script in Forms.swift): where the caret
// is and whether that box takes an account, where the box sits, when a sign-in has just been sent, and whether its
// boxes went away. Runs in Search's isolated world, in the main frame only (the window hears nothing from a frame).
//
// What was typed into a sign-in is held here, never said: the window asks for it (take) the moment it hears one went
// out, and nothing a page can read ever carries it. Filling goes through the field's own setter and fires the events
// a keystroke would: assigning to .value behind a framework's back leaves it thinking the box is still empty, which
// is a sign-in button that stays grey. Nothing is ever filled unless the window asks, on the person's pick.
(() => {
  if (globalThis.__searchForms || window.top !== window) return;
  const send = message => { try { globalThis.__searchSend(message); } catch (e) {} };

  // Fields that hold a secret: a password box from the moment it is focused or typed in, and every box Search fills
  // with a password. Search's own readers of a page (an agent's reads, checks and pictures) keep their values back
  // for good, even after a "show password" button turns one into a text box.
  const secrets = new WeakSet();
  const keepSecret = box => { try { if (box) secrets.add(box); } catch (e) {} };
  const passwordBox = el => !!el && el.tagName === 'INPUT' && (el.type || '').toLowerCase() === 'password';
  // The box itself, inside an open shadow root too: the event's target is only its host from out here.
  const boxOf = e => (e.composedPath ? e.composedPath() : [])[0] || e.target;
  const saysCode = el => tokens(el).includes('one-time-code');
  for (const kind of ['focusin', 'input', 'change']) {
    document.addEventListener(kind, e => {
      const box = boxOf(e);
      if (passwordBox(box)) return keepSecret(box);
      // A code typed in by hand: a one-time-code box, or one of a row of one-character boxes.
      if (kind !== 'focusin' && box && box.tagName === 'INPUT') { try { if (saysCode(box) || cells(box)) keepSecret(box); } catch (err) {} }
    }, true);
  }
  document.querySelectorAll('input[type="password"]').forEach(keepSecret);
  // A "show password" that swaps the box for a new one holding the same value: the new one is as secret.
  try {
    new MutationObserver(changes => {
      const gone = [], came = [];
      const inputs = (node, into) => {
        if (!node || node.nodeType !== 1) return;
        if (node.tagName === 'INPUT' || node.tagName === 'TEXTAREA') into.push(node);
        if (node.querySelectorAll) into.push(...node.querySelectorAll('input, textarea'));
      };
      for (const c of changes) { c.removedNodes.forEach(n => inputs(n, gone)); c.addedNodes.forEach(n => inputs(n, came)); }
      const held = gone.filter(b => secrets.has(b) && b.value).map(b => b.value);
      if (!held.length) return;
      for (const b of came) if (held.includes(b.value)) keepSecret(b);
    }).observe(document, { childList: true, subtree: true });
  } catch (err) {}

  // The password box, and the last box before it that could hold a name.
  function pair() {
    let pass = null;
    for (const b of document.querySelectorAll('input[type="password"]')) {
      const r = b.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) { pass = b; break; }
    }
    if (!pass) return null;
    const scope = pass.form || (pass.closest && pass.closest('form')) || document;
    let user = null;
    for (const box of scope.querySelectorAll('input')) {
      if (box === pass) break;
      const kind = (box.type || 'text').toLowerCase();
      if (kind === 'text' || kind === 'email' || kind === 'tel') user = box;
    }
    return { user, pass };
  }

  // Which boxes a saved account is offered in. Strict on purpose, as Safari's and Chrome's are: an account list
  // under a project's name or a message is noise, and a pick there types a login into a page that never asked.
  //
  // A password box, unless it is for a new password. A name box beside a password, in its form or saying it is one.
  // Or, with no password box in sight (Google, Microsoft, Okta, which ask for the name first), a box that says it
  // takes a name, an address or a phone number, alone in its form or nearly, and never one for a search, a code, a
  // title, a message or a checkout. Its page then has to look like a sign-in's (its address, its form's, a button
  // labelled Next or Sign in) for the site's other hosts' accounts to come; otherwise only this host's (wide: false).
  function texty(el) {
    if (!el || (el.tagName || '').toLowerCase() !== 'input' || el.disabled || el.readOnly) return false;
    if (!['text', 'email', 'tel', 'number'].includes((el.type || 'text').toLowerCase())) return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  }
  function tokens(el) { return (el.getAttribute('autocomplete') || '').toLowerCase().split(/\s+/).filter(Boolean); }
  // autocomplete says it is something else: a person's name, an organisation, an address, a card, a code.
  const foreign = /^(name|honorific-prefix|given-name|additional-name|family-name|honorific-suffix|nickname|organization|organization-title|one-time-code|new-password|street-address|postal-code|country|country-name|bday(-.*)?|sex|url|photo|language|impp|transaction-.*|cc-.*|address-.*|shipping|billing)$/;
  const elsewhere = el => tokens(el).some(t => foreign.test(t));
  const vouched = el => tokens(el).some(t => /^(username|email|webauthn)$/.test(t));
  function labels(el) {
    const out = [];
    try { (el.labels || []).forEach(l => out.push(l.textContent || '')); } catch (e) {}
    const by = el.getAttribute('aria-labelledby');
    if (by) for (const id of by.split(/\s+/)) { const l = document.getElementById(id); if (l) out.push(l.textContent || ''); }
    return out.join(' ').slice(0, 200);
  }
  const words = el => [el.name, el.id, el.getAttribute('aria-label'), el.getAttribute('placeholder'), labels(el)].join(' ').toLowerCase();
  function aside(el) {
    if (/^(q|to|cc|bcc|subject)$/.test(((el.name || '') + '').toLowerCase()) || /^(q|to|cc|bcc|subject)$/.test((el.id || '').toLowerCase())) return true;
    if (/(^|[\s_\-\[])title($|[\s_\-\]])|query|search|recherche|suche|buscar|otp|otc|one-time|captcha|code|coupon|promo|voucher|gift|recipient|destinataire|comment|message|description|project|projet/.test(words(el))) return true;
    return !!(el.closest && el.closest('[role="search"], [role="combobox"] input, form[role="search"]'))
      || (el.getAttribute('role') || '') === 'combobox' && !vouched(el);
  }
  // What a box says it takes: a name to sign in with, in a few languages.
  const who = /user|login|log-in|logon|e-?mail|courriel|correo|identifi|account|compte|cuenta|konto|phone|mobile|portable|t[e\u00e9]l[e\u00e9]phone|benutzer|nutzer|anmelde|usuario|utente|utilizador|gebruiker|nom d.utilisateur|fiscal|(^|[\s_\-])spi($|[\s_\-])/;
  const claimsName = el => vouched(el) || (el.type || '').toLowerCase() === 'email' || who.test(words(el));
  // A form, or a page, that is clearly about something else.
  const otherForm = /project|projet|title|titre|descri|comment|message|compose|reply|(?<!e-?mail[\s_\-]?)address|adresse|coupon|promo|newsletter|subscri|abonn|checkout|cart|basket|panier|shipping|billing|payment|paiement|search|contact|feedback|survey|invite|share/;
  const otherPage = /checkout|\/cart|basket|panier|commande|payment|paiement|newsletter|subscribe|abonnement/;
  function about(scope) {
    if (!scope || scope === document || !scope.getAttribute) return '';
    return [scope.id, scope.getAttribute('name'), scope.getAttribute('class'), scope.getAttribute('aria-label'), scope.getAttribute('action')].join(' ').toLowerCase();
  }
  // The page looks like a sign-in: its address or its form's says so, or a button near the box is a sign-in's.
  const signInPath = /log-?in|log-?on|sign-?in|sign_in|signon|auth(?!or)|account|identif|connexion|connect|session|sso|saml|idp/;
  const signInHost = /^(login|signin|auth|accounts?|sso|id|idp|identity|connexion|connect|secure|compte)\./;
  const signInButton = /^(next|continue|continuer|suivant|sign ?in|log ?in|login|log on|se connecter|connexion|me connecter|s.identifier|weiter|anmelden|einloggen|siguiente|continuar|iniciar sesi[o\u00f3]n|entrar|acceder|avanti|accedi|volgende|inloggen|doorgaan|dalej|zaloguj)(\s|$)/;
  function looksLikeSignIn(el, scope) {
    if (signInHost.test(location.hostname) || signInPath.test(location.pathname.toLowerCase())) return true;
    const action = scope && scope.getAttribute ? scope.getAttribute('action') : '';
    if (action) { try { if (signInPath.test(new URL(action, location.href).pathname.toLowerCase())) return true; } catch (e) {} }
    // The nearest buttons: the form's, or those of the few boxes around the field when there is no form (Google's
    // Next is outside it).
    let near = el;
    for (let up = 0; near && up < 5; up++, near = near.parentElement) {
      const buttons = near.querySelectorAll('button, input[type="submit"], [role="button"]');
      for (let i = 0; i < buttons.length && i < 20; i++) {
        const b = buttons[i];
        let said = ((b.tagName || '').toLowerCase() === 'input' ? b.value : b.textContent) || b.getAttribute('aria-label') || '';
        said = said.replace(/[^\p{L}\s']/gu, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
        if (said.length <= 40 && signInButton.test(said)) return true;
      }
      if (near === scope) break;
    }
    return false;
  }
  // The name box of a sign-in that asks for the password on the next page: "wide", "exact" (only this host's
  // accounts), or nothing.
  function firstStep(el) {
    if (!texty(el) || aside(el) || elsewhere(el) || !claimsName(el)) return null;
    const scope = el.form || (el.closest && el.closest('form')) || document;
    if (otherForm.test(about(scope)) || otherPage.test(location.pathname.toLowerCase())) return null;
    let others = 0;
    for (const box of scope.querySelectorAll('input')) if (box !== el && texty(box) && !aside(box)) others++;
    if (others > (vouched(el) ? 1 : 0)) return null;
    if (!vouched(el) && !scope.querySelector('button, input[type="submit"], input[type="image"], [role="button"]')) return null;
    return looksLikeSignIn(el, scope) ? 'wide' : 'exact';
  }
  const nameBox = el => !!firstStep(el);
  const newPassword = el => tokens(el).includes('new-password');
  // What a box the caret is in is offered: null for nothing.
  function judge(el) {
    if (!el || (el.tagName || '').toLowerCase() !== 'input') return null;
    const both = pair();
    if (both) {
      if (el === both.pass) return newPassword(el) ? null : { name: false, wide: true };
      if (el !== both.user || newPassword(both.pass)) return null;
      if (aside(el) || elsewhere(el)) return null;
      const together = !!el.form && el.form === both.pass.form;
      return together || claimsName(el) ? { name: false, wide: true } : null;
    }
    const step = firstStep(el);
    return step ? { name: true, wide: step === 'wide' } : null;
  }
  // The name box of a first step, if this page is one: the box the caret is in, or else the one box on the page that
  // is. A page with more than a handful of boxes to type in is not a first step.
  function named() {
    if (pair()) return null;
    const el = document.activeElement;
    if (nameBox(el)) return el;
    const seen = [];
    for (const box of document.querySelectorAll('input')) {
      if (texty(box) && !aside(box)) seen.push(box);
      if (seen.length > 6) return null;
    }
    return seen.find(nameBox) || null;
  }
  // A row of one-character boxes around a box, the nearest few: a code's, typed digit by digit.
  function cells(el) {
    if (!texty(el) || el.maxLength !== 1) return null;
    let up = el.parentElement;
    for (let i = 0; up && i < 4; i++, up = up.parentElement) {
      const row = [...up.querySelectorAll('input')].filter(texty);
      if (row.length < 4) continue;
      return row.length <= 8 && row.every(b => b.maxLength === 1) ? row : null;
    }
    return null;
  }
  // The boxes that go with one: its form, or, with no form, the widest box around it holding twenty or fewer.
  const formOf = el => el.form || (el.closest && el.closest('form')) || null;
  function boxesUnder(node, most) {
    let n = 0;
    for (const tag of ['input', 'select', 'textarea']) { if (n > most) break; n += node.getElementsByTagName(tag).length; }
    return n;
  }
  function scopeOf(el) {
    const form = formOf(el);
    if (form) return form;
    let best = el.parentElement, up = el.parentElement;
    for (let level = 0; up && level < 8; level++, up = up.parentElement) {
      if (boxesUnder(up, 20) > 20) break;
      best = up;
    }
    return best || document;
  }
  function put(box, value) {
    if (!box) return;
    const tag = (box.tagName || '').toLowerCase();
    const proto = tag === 'textarea' ? HTMLTextAreaElement.prototype : tag === 'select' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value');
    if (setter && setter.set) setter.set.call(box, value); else box.value = value;
    box.dispatchEvent(new Event('input', { bubbles: true }));
    box.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // What is in the boxes when they are sent. Held every time (a click on "show password" holds it too) because the
  // window only judges once the page has moved on, and keeps the last thing it took. A first step says the name it
  // is sending: the page after it asks for the password alone, and the name is what the account is saved under.
  let offered = 0, held = null;
  function offer() {
    const both = pair();
    if (!both) {
      const box = named();
      const name = box ? box.value.trim() : '';
      if (name) { offered = Date.now(); send({ kind: 'forms.name', user: name }); }
      return;
    }
    if (!both.pass.value) return;
    offered = Date.now();
    held = { user: both.user ? both.user.value : '', password: both.pass.value };
    // Said at once over Search's own binding, which only the window hears: asked for afterwards, a page that moves
    // on quickly was already gone.
    send({ kind: 'forms.sent', held });
  }
  document.addEventListener('submit', offer, true);
  // A form sent by the page's own script (form.submit() fires no submit event, and the link or timer that called it
  // is no button) still builds its entry list, and so does a script reading the form to send it itself. Unless
  // something did a moment ago: a page that swaps the password for a hash before sending would have the hash taken.
  document.addEventListener('formdata', () => { if (Date.now() - offered > 2000) offer(); }, true);
  document.addEventListener('keydown', e => {
    if (e.key !== 'Enter') return;
    const both = pair();
    if (both && (document.activeElement === both.pass || document.activeElement === both.user)) offer();
    else if (!both && nameBox(document.activeElement)) offer();
  }, true);
  // Plenty of sign-in buttons aren't in a form and never fire submit.
  document.addEventListener('click', e => {
    const el = e.target;
    if (el && el.closest && el.closest('button, input[type="submit"], [role="button"]')) setTimeout(offer, 0);
  }, true);

  let told = false;
  function tell() {
    if (told || !pair()) return;
    told = true;
    send({ kind: 'forms.found' });
  }
  if (document.readyState === 'complete') tell(); else addEventListener('load', tell);
  // A form the page builds for itself a moment after it loads, or the password step of a sign-in that asks for the
  // name first.
  setTimeout(tell, 700);
  setTimeout(tell, 2200);
  // The boxes going away without a new page (a sign-in done in place) is the other way a sign-in shows it took.
  // Looked at a quarter of a second after the page changes, not at every change: a chat writing out its answer
  // changes the page on every word, and looking through the whole page each time made long ones crawl.
  let settling = null, looking = null;
  function look() {
    looking = null;
    if (!told) { tell(); return; }
    if (pair()) return;
    told = false;
    clearTimeout(settling);
    settling = setTimeout(() => { if (!pair()) send({ kind: 'forms.settled' }); }, 400);
  }
  new MutationObserver(() => { if (!looking) looking = setTimeout(look, 250); }).observe(document, { childList: true, subtree: true }); // the document, not <html>: at document start there's no <html> yet

  // Whether the caret is somewhere on the page that takes typing: the window's Tab key is the page's there.
  function editable(el) {
    if (!el) return false;
    const tag = (el.tagName || '').toLowerCase();
    if (tag === 'textarea' || el.isContentEditable === true || (el.getAttribute && el.getAttribute('role') === 'textbox')) return true;
    // A document that types into a frame of its own: Google Docs keeps the caret there.
    if (tag === 'iframe') { try { return editable(el.contentDocument && el.contentDocument.activeElement); } catch (e) { return false; } }
    if (tag !== 'input') return false;
    return ['text', 'search', 'email', 'url', 'tel', 'password', 'number', 'date', 'datetime-local', 'month', 'week', 'time'].includes((el.type || 'text').toLowerCase());
  }
  // A box the page says takes a passkey: autocomplete="username webauthn". It gets a spot, and no passwords.
  const forPasskeys = el => !!(el && (el.tagName || '').toLowerCase() === 'input' && /(^|\s)webauthn(\s|$)/i.test(el.getAttribute('autocomplete') || ''));

  // What was last said, so a scroll keeps quiet when nothing moved. The box the last list hung from: the caret going
  // straight from it to a box of another form says it left first, so a pick made in one form keeps the list from
  // coming back under the boxes it just filled, not under every box after it on the page.
  let said = null, hungFrom = null;
  // What the box the caret is in was judged to be: judged again when the caret moves, not on every frame of a scroll.
  let judged = { el: null, verdict: null };
  function caret(scrolled) {
    const el = document.activeElement;
    if (scrolled !== true || judged.el !== el) judged = { el, verdict: judge(el) };
    const verdict = judged.verdict;
    const passwords = !!verdict, name = !!(verdict && verdict.name), wide = !verdict || verdict.wide;
    let rect = null;
    if (passwords || forPasskeys(el)) {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) rect = { x: r.left, y: r.top, w: r.width, h: r.height };
    }
    const typing = editable(el), dpr = window.devicePixelRatio || 1;
    const now = typing + (rect ? ` ${passwords} ${name} ${wide} ${rect.x} ${rect.y} ${rect.w} ${rect.h} ${dpr}` : '');
    if (scrolled === true && now === said) return;
    said = now;
    if (rect && hungFrom && hungFrom !== el && scopeOf(hungFrom) !== scopeOf(el)) send({ kind: 'forms.focus', typing, rect: null });
    hungFrom = rect ? el : null;
    send({ kind: 'forms.focus', typing, rect, passwords, name, wide, dpr });
  }
  // The box moves when the page scrolls or the window changes size, and whatever hangs from it has to move too: once
  // a frame at most, and only when something did change.
  let moving = false;
  function moved() {
    if (moving) return;
    moving = true;
    requestAnimationFrame(() => { moving = false; caret(true); });
  }
  addEventListener('scroll', moved, true);
  addEventListener('resize', moved);
  document.addEventListener('focusin', caret, true);
  document.addEventListener('focusout', () => setTimeout(caret, 0), true);
  document.addEventListener('mouseup', () => setTimeout(caret, 0), true);

  globalThis.__searchForms = {
    secrets,
    // A picked account into the sign-in: the name only if its box is still empty, the password always.
    fill(user, password) {
      const both = pair();
      if (!both) return false;
      if (both.user && !both.user.value) put(both.user, user);
      keepSecret(both.pass);
      put(both.pass, password);
      return true;
    },
    // The name alone, into a first step's box: there is no password box yet, and none is filled anywhere.
    name(user) {
      const box = named();
      if (!box) return false;
      put(box, user);
      return true;
    },
    // What the last sign-in sent held, handed over once and let go of here.
    take() { const out = held; held = null; return out; },
    // Whether there is still a sign-in on the page: asked after a password went out, to tell one that took from one
    // refused.
    hasPassword: () => !!pair(),
    // What a box would be offered, for the tests: null, or whether a pick puts in the name alone and whether the
    // site's other hosts' accounts come too.
    verdict: el => judge(el || document.activeElement),
    // A form the window has just seen go out, read now while its boxes are still filled.
    sent() { if (Date.now() - offered > 2000) offer(); }
  };
  caret();
})();
