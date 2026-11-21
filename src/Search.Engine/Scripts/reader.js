// Reading mode (Reader.swift): the article, and nothing that was arranged around it, drawn in place of the page in the
// same tab. Not loaded at document start: Reader.cs sends it with each request, and it defines __searchReader once
// per document: guess(look), twin(), show(html, title, from, look), look(look), showing().
//
// The hard part is deciding what the article is. The guess is the old heuristic and it holds up: the piece of the page
// carrying the most prose, punished for how much of it is links, because navigation, related-articles rails and
// comment threads are all made of links, and prose is not. Unless the site has already answered it: a markdown copy of
// the page (see twin), drawn by Markdown.cs, goes in the same reading page instead.
(() => {
  if (globalThis.__searchReader) return;

  const sans = '"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif';
  const dark = 'color-scheme:dark;--r-ground:#1c1c1c;--r-ink:#e0e0e0;--r-quiet:#8a8a8a;' +
    '--r-aside:#a8a8a8;--r-rule:#333;--r-code:#2a2a2a;--r-picture:brightness(.92)';
  const sheet = [
    // Light, or dark as the window is or as chosen in the corner (on <html>, which the reader keeps). The dark ground
    // is the window's own; the text a shade softer than its ink, there being a lot of it.
    ':root{color-scheme:light;--r-ground:#fff;--r-ink:#171717;--r-quiet:#a3a3a3;',
    '--r-aside:#555;--r-rule:#e8e8e8;--r-code:#f5f5f5;--r-picture:none}',
    '@media (prefers-color-scheme:dark){:root:not([data-office-look=light]){' + dark + '}}',
    ':root[data-office-look=dark]{' + dark + '}',
    'html,body{background:var(--r-ground) !important;margin:0 !important;padding:0 !important}',
    '#office-reader{max-width:38em;margin:0 auto;padding:72px 24px 160px;',
    'font:400 18px/1.72 ui-serif,Georgia,"Times New Roman",serif;color:var(--r-ink)}',
    '#office-reader h1{font:600 30px/1.24 ' + sans + ';',
    'margin:0 0 8px;letter-spacing:-0.01em}',
    '#office-reader .office-from{font:400 12px/1 ' + sans + ';color:var(--r-quiet);',
    'margin:0 0 40px;text-transform:uppercase;letter-spacing:.06em}',
    '#office-reader p{margin:0 0 1.35em}',
    '#office-reader img,#office-reader video,#office-reader iframe{max-width:100%;',
    'height:auto;border-radius:6px;margin:1.6em 0;display:block}',
    // A photo, a touch dimmer on dark, so it doesn't glare.
    '#office-reader img{filter:var(--r-picture)}',
    '#office-reader iframe{width:100%;aspect-ratio:16/9;height:auto;border:0}',
    '#office-reader svg{max-width:100%}',
    '#office-reader iframe.office-chart{aspect-ratio:auto}',
    '#office-reader figure{margin:1.8em 0}',
    '#office-reader figcaption{font:400 13px/1.5 ' + sans + ';',
    'color:var(--r-quiet);margin-top:.6em}',
    // A caption and its credit, which the site's own layout kept apart.
    '#office-reader figcaption > * + *{margin-left:.35em}',
    // The byline and a summary, quieter than the text.
    '#office-reader .office-above{font:400 14px/1.55 ' + sans + ';',
    'color:var(--r-aside);margin:0 0 1.6em}',
    '#office-reader .office-above ul{padding-left:1.2em}',
    '#office-reader .office-above h2{margin:.4em 0}',
    '#office-reader a{color:var(--r-ink);text-underline-offset:3px}',
    '#office-reader h2,#office-reader h3{font:600 20px/1.3 ' + sans + ';',
    'margin:2em 0 .6em}',
    '#office-reader h4,#office-reader h5,#office-reader h6{font:600 16px/1.4 ' + sans + ';',
    'margin:1.8em 0 .5em}',
    '#office-reader pre,#office-reader code,#office-reader kbd{font-family:ui-monospace,"Cascadia Mono",Consolas,monospace;font-size:14px}',
    '#office-reader pre{background:var(--r-code);padding:14px;border-radius:8px;overflow:auto}',
    '#office-reader blockquote{margin:1.6em 0;padding-left:1.2em;',
    'border-left:2px solid var(--r-rule);color:var(--r-aside)}',
    // Said outright: a site's own reset (Tailwind's, most often) takes the numbers off every list on the page, this
    // one included.
    '#office-reader ul,#office-reader ol{margin:0 0 1.35em;padding-left:1.4em}',
    '#office-reader ul{list-style:disc outside}',
    '#office-reader ol{list-style:decimal outside}',
    '#office-reader li>ul{list-style-type:circle}',
    '#office-reader :not(pre)>code{background:var(--r-code);padding:.1em .35em;border-radius:4px}',
    '#office-reader li{margin:.3em 0}',
    '#office-reader li>ul,#office-reader li>ol{margin-bottom:0}',
    '#office-reader hr{border:0;border-top:1px solid var(--r-rule);margin:2.4em 0}',
    '#office-reader .office-wide{overflow-x:auto;margin:1.6em 0}',
    '#office-reader table{border-collapse:collapse;font:400 15px/1.5 ' + sans + '}',
    '#office-reader th,#office-reader td{border-bottom:1px solid var(--r-rule);padding:8px 12px 8px 0;',
    'text-align:left;vertical-align:top}',
    '#office-reader th{font-weight:600}',
    '#office-reader .office-part{font:600 16px/1.4 ' + sans + ';margin:1.8em 0 .5em}',
    '#office-reader details{margin:0 0 1.35em}',
    '#office-reader summary{cursor:pointer}',
  ].join('');

  // Light, dark, or as the window is ('system'), on <html>, which the reader keeps: it holds for the article and goes
  // when the page does.
  const look = chosen => {
    if (chosen === 'system') document.documentElement.removeAttribute('data-office-look');
    else document.documentElement.setAttribute('data-office-look', chosen === 'dark' ? 'dark' : 'light');
  };

  // Puts the reading page in place of the page: the title, where it's from, and what to read.
  //
  // The page's own stylesheets go first. Left in, they reach the reading page through every rule written for a bare
  // element (Tailwind's reset took the numbers off every list) and every font the page loaded. Search's own stay: each
  // has an `office-` id (what you hid, your style for the site). Done once, as reading mode opens; a page that adds a
  // sheet back later keeps it, because a watcher that took it away again would fight any extension (Dark Reader,
  // Stylus) that puts its own back, forever.
  const present = (wrap, title, from, chosen) => {
    look(chosen || 'light');
    const theirs = document.querySelectorAll('style, link[rel~="stylesheet" i]');
    for (const one of theirs) if ((one.id || '').indexOf('office-') !== 0) one.remove();
    document.documentElement.removeAttribute('style');
    document.body.removeAttribute('style');
    const style = document.createElement('style');
    style.id = 'office-reader-sheet';
    style.textContent = sheet;
    const top = document.createElement('h1');
    top.textContent = title;
    const line = document.createElement('p');
    line.className = 'office-from';
    line.textContent = from;
    wrap.id = 'office-reader';
    document.body.innerHTML = '';
    (document.head || document.documentElement).appendChild(style);
    wrap.insertBefore(line, wrap.firstChild);
    wrap.insertBefore(top, wrap.firstChild);
    document.body.appendChild(wrap);
    window.scrollTo(0, 0);
  };

  const prose = el => {
    const paragraphs = el.querySelectorAll('p');
    if (paragraphs.length < 2) return 0;
    let letters = 0;
    for (const p of paragraphs) letters += (p.innerText || '').length;
    if (letters < 400) return 0;
    // A rail of related links has plenty of text and nothing to read. What tells such a rail from an article is not
    // how many links it holds but how much of it they are: a rail is almost all link, an article that cites its
    // sources a tenth of itself at most (#757). Both halves of the ratio from textContent, which asks for no layout.
    const whole = (el.textContent || '').length;
    if (!whole) return 0;
    let inside = 0;
    for (const a of el.querySelectorAll('a')) inside += (a.textContent || '').length;
    return letters * (1 - Math.min(inside / whole, 1));
  };

  const best = () => {
    const candidates = document.querySelectorAll('article, main, [role="main"], .post, .entry, .article, .content, #content, div, section');
    let top = null, mark = 0;
    for (const el of candidates) {
      const score = prose(el);
      if (!score) continue;
      if (!top) { top = el; mark = score; continue; }
      // Between a box and one it holds, the smaller is taken unless the larger is clearly better, not merely bigger.
      // The box around an article and its comments scores a little above the article, a dozen short comments being
      // text too; the two sit within a sixth of each other there. A quarter is the margin, and any box further ahead
      // than that is a better one rather than a wider one.
      if (top.contains(el)) {
        if (score * 1.25 > mark) { top = el; mark = score; }
      } else if (el.contains(top)) {
        if (score > mark * 1.25) { top = el; mark = score; }
      } else if (score > mark) {
        top = el; mark = score;
      }
    }
    return top;
  };

  // The guess, for a page with no twin: 'read', or 'none' when there's nothing to read.
  const guess = chosen => {
    // Already an article: going back can bring one as it was left.
    if (document.getElementById('office-reader')) return 'read';

    const article = best();
    if (!article) return 'none';

    // The photo an article opens with isn't always in it: the FT keeps its own in a topper beside the <article>. So
    // the first large picture before the text comes too, looked for no more than four boxes out, and never in a menu,
    // a rail or a footer.
    let lead = null;
    for (let up = article.parentElement, depth = 0; up && up !== document.body && depth < 4 && !lead; up = up.parentElement, depth++) {
      const shown = up.querySelectorAll('figure, picture');
      for (let l = 0; l < shown.length && !lead; l++) {
        const face = shown[l].querySelector('img');
        // Before the text, and not around it.
        if (face && article.compareDocumentPosition(shown[l]) === Node.DOCUMENT_POSITION_PRECEDING &&
            !shown[l].closest('nav, aside, footer, [role="banner"]') &&
            face.getBoundingClientRect().width >= 400) lead = shown[l];
      }
    }

    // A chart's frame is as tall as its own script made it, and that script won't run in the reader: the height is
    // read while the page shows it.
    for (const frame of article.querySelectorAll('iframe')) frame.setAttribute('data-office-height', Math.round(frame.getBoundingClientRect().height));

    // The size a drawing in the text is given, an icon as often as not, goes with the page's styles: left to itself,
    // an <svg> fills the column. The size it is drawn at is written onto it.
    for (const drawing of article.querySelectorAll('svg')) {
      if (drawing.parentElement.closest('svg')) continue;
      const drawn = drawing.getBoundingClientRect();
      drawing.setAttribute('width', Math.round(drawn.width));
      drawing.setAttribute('height', Math.round(drawn.height));
    }

    // Bloomberg keeps an article's byline, its takeaways and the video it opens with between the headline and the
    // text, outside the part with the most prose. Such parts of its head come along, in the page's order: those drawn
    // on the page, and never one from a menu, a rail or a footer. Class names are matched in part and whatever their
    // case, as sites build them (BasicByline_byline__VTyoS); a subheading is an h2.
    const heading = document.querySelector('h1');
    const above = [];
    if (heading) {
      for (const part of document.querySelectorAll('[class*="byline" i],[class*="takeaways" i],[class*="lede" i],h2')) {
        if (part.contains(article) || article.contains(part) || above.some(o => o.contains(part))) continue;
        if (!(heading.compareDocumentPosition(part) & Node.DOCUMENT_POSITION_FOLLOWING)) continue;
        if (!(article.compareDocumentPosition(part) & Node.DOCUMENT_POSITION_PRECEDING)) continue;
        if (part.closest('nav, aside, footer, [role="banner"]') || !part.getClientRects().length) continue;
        above.push(part);
      }
    }

    // Every picture is resolved while the real page is still standing. currentSrc is what the browser actually chose
    // and loaded, after srcset, sizes and <picture> have had their say. Writing it back as a plain src is the only way
    // to be sure the reader shows the same image the page did: copying the markup alone gets you a lazy placeholder,
    // or nothing at all.
    const late = ['data-src', 'data-original', 'data-lazy-src', 'data-lazy', 'data-full-src', 'data-hi-res-src', 'data-image', 'data-echo'];
    let pictures = Array.from(article.querySelectorAll('img'));
    if (lead) pictures = pictures.concat(Array.from(lead.querySelectorAll('img')));
    above.forEach(o => { pictures = pictures.concat(Array.from(o.querySelectorAll('img'))); });
    for (const picture of pictures) {
      picture.setAttribute('loading', 'eager');
      let real = picture.currentSrc || picture.getAttribute('src') || '';
      // A one-pixel placeholder counts as nothing.
      if (!real || real.indexOf('data:image') === 0 || picture.naturalWidth <= 2) {
        for (const name of late) {
          const kept = picture.getAttribute(name);
          if (kept) { real = kept; break; }
        }
      }
      if (real) picture.setAttribute('src', real);
      const lateSet = picture.getAttribute('data-srcset');
      if (lateSet && !picture.getAttribute('srcset')) picture.setAttribute('srcset', lateSet);
    }

    // What the page's own styles hide, seen while they still apply: the reading page goes without them (see present),
    // and a label meant only for screen readers, a menu folded shut or the mobile copy of a table would all come into
    // view. Not a box that holds paragraphs, though: an article's folded-away rest is still the article. The head's
    // parts are looked at the same.
    const inherent = /^(SOURCE|TRACK|PARAM|AREA|TEMPLATE|BR|WBR|SCRIPT|STYLE|NOSCRIPT)$/;
    let all = [];
    [article].concat(above).forEach(root => { all = all.concat(Array.from(root.querySelectorAll('*'))); });
    for (const el of all) {
      if (inherent.test(el.tagName) || el.closest('details:not([open])')) continue;
      const seen = getComputedStyle(el);
      let gone = seen.display === 'none' || seen.visibility === 'hidden';
      if (!gone && (seen.overflow === 'hidden' || seen.clip !== 'auto')) {
        const box = el.getBoundingClientRect();
        // The usual way of hiding text from sight but not from a screen reader: a box a pixel square, its overflow
        // cut off.
        gone = box.width <= 1 && box.height <= 1;
      }
      if (gone && !(seen.display === 'none' && el.querySelector('p'))) el.setAttribute('data-office-hidden', '');
    }

    const title = (heading && heading.innerText.trim()) || document.title;

    const wrap = document.createElement('div');
    wrap.innerHTML = article.innerHTML;
    for (let o = above.length - 1; o >= 0; o--) {
      const copy = above[o].cloneNode(true);
      copy.classList.add('office-above');
      wrap.insertBefore(copy, wrap.firstChild);
    }

    for (const hidden of wrap.querySelectorAll('[data-office-hidden]')) hidden.remove();
    // A style written on an element is the page's styling too: a fixed width, a white font for a dark box that isn't
    // there any more.
    for (const styled of wrap.querySelectorAll('[style]')) styled.removeAttribute('style');
    // The photo comes without the topper's own classes and styles, which the page's stylesheet would still lay out as
    // a topper: off to one side, cropped.
    if (lead) {
      const opening = lead.cloneNode(true);
      for (const part of [opening].concat(Array.from(opening.querySelectorAll('*')))) {
        part.removeAttribute('class');
        part.removeAttribute('style');
      }
      let after = wrap.firstChild;
      while (after && after.classList && after.classList.contains('office-above')) after = after.nextSibling;
      wrap.insertBefore(opening, after);
    }

    // A picture in a button, a click to see it larger, is the picture: Bloomberg puts every photo in one. The button
    // goes and what it holds stays.
    for (const pressed of wrap.querySelectorAll('button')) {
      if (!pressed.querySelector('img, picture, video')) continue;
      while (pressed.firstChild) pressed.parentNode.insertBefore(pressed.firstChild, pressed);
      pressed.remove();
    }

    // What was arranged around the words rather than being part of them. Not header: an article's opening image lives
    // there as often as not. A video.js player's controls, title bar and read-outs go too; the video stays.
    const clutter = wrap.querySelectorAll(
      'script,style,noscript,form,nav,aside,footer,button,input,select,textarea,' +
      '[role="complementary"],[role="navigation"],[role="banner"],[aria-hidden="true"],' +
      '.video-js > [class*="vjs-"]:not(video)');
    for (const thing of clutter) thing.remove();

    // So does a box inviting you to a newsletter, which Bloomberg sets in the middle of the text. Only a short one: a
    // newsletter's own page may keep its whole text in a box named the same.
    for (const invite of wrap.querySelectorAll('[class*="newsletter" i]')) if (invite.querySelectorAll('p').length < 3) invite.remove();

    // Embedded video is part of the article, and so is a chart or a map from one of the chart makers a newsroom puts
    // in it, at the height it had. Any other frame is not: an advertisement, a widget.
    const players = /youtube|youtu\.be|vimeo|dailymotion|loom\.com|streamable|wistia|ted\.com/i;
    const charts = /flourish|flo\.uri\.sh|datawrapper|dwcdn\.net|infogram|tableau|ig\.ft\.com|google\.com\/maps/i;
    for (const frame of wrap.querySelectorAll('iframe')) {
      const where = frame.getAttribute('src') || frame.getAttribute('data-src') || '';
      if (players.test(where)) {
        frame.setAttribute('src', where);
        frame.removeAttribute('height');
        frame.removeAttribute('width');
      } else if (where && charts.test(where)) {
        const tall = +frame.getAttribute('data-office-height');
        frame.setAttribute('src', where);
        frame.setAttribute('style', 'height:' + (tall > 60 ? tall : 420) + 'px');
        frame.className = 'office-chart';
      } else {
        frame.remove();
      }
    }

    // A video's poster, which its player also shows as a picture beside it: once is enough.
    const videos = wrap.querySelectorAll('video'), posters = [];
    for (const video of videos) if (video.poster) posters.push(video.poster);
    for (const still of wrap.querySelectorAll('img')) if (posters.indexOf(still.src) >= 0 && !still.closest('video')) still.remove();

    // A video its player fed from the page itself, through a blob: address, has nothing to play once that player is
    // gone. It plays from the plain stream it names beside that, with controls and never by itself, and fetches
    // nothing of it until play is pressed; with none, it is its poster; with no poster, it goes.
    for (const video of videos) {
      let fed = /^blob:/.test(video.getAttribute('src') || ''), plain = '';
      const named = video.querySelectorAll('source');
      for (const source of named) {
        if (/^blob:/.test(source.getAttribute('src') || '')) fed = true;
        else if (!plain && /^https?:/.test(source.src)) plain = source.src;
      }
      if (!fed) continue;
      if (plain) {
        for (const source of named) source.remove();
        video.setAttribute('preload', 'metadata');
        video.setAttribute('src', plain);
        video.setAttribute('controls', '');
        video.setAttribute('playsinline', '');
        video.removeAttribute('autoplay');
        // Out of the player's box, which the page's stylesheet still sizes for the player.
        const player = video.parentNode.closest('.video-js');
        if (player) player.parentNode.replaceChild(video, player);
      } else if (video.poster) {
        const still = document.createElement('img');
        still.setAttribute('src', video.poster);
        video.parentNode.replaceChild(still, video);
      } else {
        video.remove();
      }
    }

    // A picture with nothing behind it is a broken icon, which is worse than no picture at all.
    for (const kept of wrap.querySelectorAll('img')) {
      const src = kept.getAttribute('src') || '';
      if (!src || src.indexOf('data:image') === 0) kept.remove();
    }

    // A box whose content went (the figure a frame sat in, the one a picture with nothing behind it left) goes too,
    // rather than stay as an empty band.
    const boxes = wrap.querySelectorAll('figure, picture, div, section, p');
    for (let e = boxes.length - 1; e >= 0; e--) {
      if (!boxes[e].textContent.trim() && !boxes[e].querySelector('img, video, audio, iframe, svg, canvas, table')) boxes[e].remove();
    }

    // The article's own heading goes when it only says the title again, which the reader puts on top.
    const own = wrap.querySelector('h1');
    if (own && own.textContent.replace(/\s+/g, ' ').trim().toLowerCase() === title.replace(/\s+/g, ' ').toLowerCase()) own.remove();

    present(wrap, title, location.host.replace(/^www\./, ''), chosen);
    return 'read';
  };

  // The site's own markdown for this page, if it has some: the text and the address it came from, with the page's
  // title and host for when the markdown has no heading; or null. Three places, in order, all the site's own say-so:
  // the page is markdown already (a README opened as it is); the page names its twin, <link rel="alternate"
  // type="text/markdown">; the same address with .md on the end, which llms.txt proposed and docs sites took up.
  //
  // Only a response that says it is markdown counts, so a site answering every address with its home page, or a 404
  // page, is never read as one. Only the page's own origin is asked, and only an answer from it read: reading mode
  // never sends a request beyond the page's own site. The request goes from the page, with its cookies, so a site
  // you're signed in to gives the twin you'd see. Nothing is cached.
  const twin = async () => {
    const markdown = ['text/markdown', 'text/x-markdown'];
    const kind = type => (type || '').toLowerCase().split(';')[0].trim();
    const heading = document.querySelector('h1');
    const about = { title: (heading && heading.innerText.trim()) || document.title, from: location.host.replace(/^www\./, '') };

    const own = kind(document.contentType);
    if (markdown.indexOf(own) >= 0 || (own === 'text/plain' && /\.(md|markdown)$/i.test(location.pathname))) {
      return { url: location.href, text: document.body ? document.body.textContent : '', title: about.title, from: about.from };
    }
    if (!/^https?:$/.test(location.protocol)) return null;

    const home = address => { try { return new URL(address, location.href).origin === location.origin; } catch (e) { return false; } };
    // `named`: the page asked for this one by name, and a site that says so may serve its markdown as plain text.
    const read = async (address, patience, named) => {
      if (!home(address)) return null;
      const stop = new AbortController();
      const timer = setTimeout(() => stop.abort(), patience);
      try {
        const answer = await fetch(address, {
          headers: { Accept: 'text/markdown, text/plain;q=0.5' },
          cache: 'no-store', credentials: 'same-origin', signal: stop.signal
        });
        const type = kind(answer.headers.get('content-type'));
        const fits = markdown.indexOf(type) >= 0 || (named && type === 'text/plain');
        if (!answer.ok || !fits || (answer.url && !home(answer.url))) {
          if (answer.body) answer.body.cancel();
          return null;
        }
        const text = await answer.text();
        if (text.length > 4000000 || /^\s*<(!doctype|html)/i.test(text)) return null;
        return { url: answer.url || address, text, title: about.title, from: about.from };
      } catch (e) {
        return null;
      } finally {
        clearTimeout(timer);
      }
    };

    for (const link of document.querySelectorAll('link[rel~="alternate" i][type]')) {
      if (markdown.indexOf(kind(link.type)) < 0 || !/^https?:/.test(link.href) || !home(link.href)) continue;
      const named = await read(link.href, 5000, true);
      if (named) return named;
      break;
    }

    // Most sites have no twin, so this one is kept short: the reader shouldn't wait on an address that was only ever
    // a guess.
    const guessed = new URL(location.href);
    guessed.hash = '';
    if (/\.(md|markdown)$/i.test(guessed.pathname)) return null;
    guessed.pathname += /\/$/.test(guessed.pathname) ? 'index.html.md' : '.md';
    return await read(guessed.href, 1500, false);
  };

  // The twin, drawn: `html` from Markdown.Page, set in the same page the guess makes.
  const show = (html, title, from, chosen) => {
    const wrap = document.createElement('div');
    wrap.innerHTML = html;
    present(wrap, title, from, chosen);
    return 'read';
  };

  globalThis.__searchReader = { guess, twin, show, look, showing: () => !!document.getElementById('office-reader') };
})();
