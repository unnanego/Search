// YouTube's ad schedule taken out of the player's own data before the player reads it (YouTubeAds.swift's page
// script). In the page's world, before any of its scripts; the wrapper Shield adds runs it only on YouTube's hosts
// and not where Shield is paused. It reaches only JSON.parse, Response.prototype.json, fetch, XMLHttpRequest's open and
// send, navigator.sendBeacon, two window properties and the player's own loadVideoById, all of which the page can
// already rewrite or call itself. It sends nothing anywhere (the requests it answers are ones Shield would have
// blocked) and runs nothing it's given.
//
// YouTube also tests, on every watch page, whether a blocker is there: did static.doubleclick.net's ad_status.js run,
// did the ad id request and the ad reports go through. For an account YouTube enforces on, a failed test writes its
// wall ("Ad blockers violate YouTube's Terms of Service") over a player answer that said OK. So the tests pass where it
// costs nothing: Shield lets ad_status.js through on YouTube's pages, the ad id and ad reports are answered here with
// an empty 200 before they leave, and an answer that carries the wall dormant keeps its status and its video when the
// wall is written over them (the wall's fields are signed: taking it out or changing it gives "Something went wrong").
//
// On a return visit YouTube may hold a watch page whose pre-roll was taken out at 0:00 for 5 to 30 seconds. The same
// video started again through the player's own loadVideoById plays at once, so when the first page's data had an ad
// schedule taken out, on a /watch page (no embed, playlist or YouTube Music), that video is started again, once, at
// the page's own `t`. A page whose data had no ads is left alone, and so are later videos opened inside the page.
'use strict';
// Taken now, before the page runs: what the page puts in their place later is never what this calls.
var apply = Reflect.apply, owns = Object.prototype.hasOwnProperty, define = Object.defineProperty, isArray = Array.isArray;
var ads = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];
var marks = ['responseContext', 'playabilityStatus', 'streamingData', 'videoDetails'];
function plain(o) { return o !== null && typeof o === 'object' && !isArray(o); }
function youtubes(o) { for (var i = 0; i < marks.length; i++) if (apply(owns, o, [marks[i]])) return true; return false; }
// Whether it took anything out.
function strip(o) {
  if (!plain(o) || !youtubes(o)) return false;
  var cut = false;
  for (var i = 0; i < ads.length; i++) if (apply(owns, o, [ads[i]])) { delete o[ads[i]]; cut = true; }
  return cut;
}
// The wall, written over an answer that said OK: the answer keeps its status and its video. Any other status is taken
// as written.
function walled(v) { return plain(v) && plain(v.errorScreen) && apply(owns, v.errorScreen, ['enforcementMessageViewModel']); }
function hold(o) {
  var aux = o.auxiliaryUi, shown = plain(aux) && plain(aux.messageRenderers) && aux.messageRenderers;
  if (!shown || !apply(owns, shown, ['bkaEnforcementMessageViewModel']) || !apply(owns, o, ['playabilityStatus'])) return;
  var status = o.playabilityStatus, details = o.videoDetails, refused = false;
  define(o, 'playabilityStatus', { get: function () { return status; }, set: function (v) { if (walled(v)) refused = true; else status = v; }, enumerable: true, configurable: true });
  define(o, 'videoDetails', { get: function () { return details; }, set: function (v) { if (v !== undefined || !refused) details = v; }, enumerable: true, configurable: true });
}
function one(o) {
  if (!plain(o)) return;
  strip(o);
  if (youtubes(o)) hold(o);
  if (apply(owns, o, ['playerResponse']) && plain(o.playerResponse)) { strip(o.playerResponse); if (youtubes(o.playerResponse)) hold(o.playerResponse); }
}
// Whatever happens in here, the page gets back what it asked for.
function prune(value) {
  try { if (isArray(value)) { for (var i = 0; i < value.length && i < 8; i++) one(value[i]); } else { one(value); } } catch (e) {}
  return value;
}
try {
  var parse = JSON.parse;
  if (typeof parse === 'function') define(JSON, 'parse', { value: ({ parse(text, reviver) { return prune(apply(parse, JSON, arguments)); } }).parse, writable: true, enumerable: false, configurable: true });
} catch (e) {}
try {
  var json = typeof Response === 'function' && Response.prototype.json;
  var then = Promise.prototype.then;
  if (typeof json === 'function') define(Response.prototype, 'json', { value: ({ json() { return apply(then, apply(json, this, []), [prune]); } }).json, writable: true, enumerable: true, configurable: true });
} catch (e) {}
// The ad id and the ad reports Shield blocks, answered here with an empty 200 instead of failing: YouTube counts a
// failure as a blocker. Nothing is sent; the video's own reports go out as before.
var tested = /^https?:\/\/(?:googleads\.g\.doubleclick\.net\/pagead\/id|(?:[^\/]+\.)?youtube\.com(?:\/api\/stats\/ads|\/pagead\/|\/ptracking|\/api\/stats\/qoe\?.*adformat=))/;
function answered(u) { try { return tested.test(String(new URL(u, location.href))); } catch (e) { return false; } }
try {
  var fetch = window.fetch, Answer = Response;
  if (typeof fetch === 'function') window.fetch = ({ fetch(input, init) {
    var u = typeof input === 'string' || input instanceof URL ? input : input && input.url;
    if (u && answered(u)) return Promise.resolve(new Answer('', { status: 200, statusText: 'OK' }));
    return apply(fetch, this, arguments);
  } }).fetch;
} catch (e) {}
try {
  var X = XMLHttpRequest.prototype, open = X.open, send = X.send, asked = new WeakMap();
  X.open = ({ open(method, u) {
    if (answered(u)) asked.set(this, String(new URL(u, location.href))); else asked.delete(this);
    return apply(open, this, arguments);
  } }).open;
  X.send = ({ send() {
    var u = asked.get(this);
    if (!u) return apply(send, this, arguments);
    var request = this;
    define(request, 'readyState', { value: 4 });
    define(request, 'status', { value: 200 });
    define(request, 'statusText', { value: 'OK' });
    define(request, 'responseText', { value: '' });
    define(request, 'response', { value: '' });
    define(request, 'responseURL', { value: u });
    setTimeout(function () {
      request.dispatchEvent(new Event('readystatechange'));
      request.dispatchEvent(new ProgressEvent('load'));
      request.dispatchEvent(new ProgressEvent('loadend'));
    }, 0);
  } }).send;
} catch (e) {}
try {
  var beacon = navigator.sendBeacon;
  if (typeof beacon === 'function') navigator.sendBeacon = ({ sendBeacon(u) { return answered(u) ? true : apply(beacon, this, arguments); } }).sendBeacon;
} catch (e) {}
// The first page's data is a script's `var ytInitial… = {…}`: a setter already there is what that assignment goes through.
var cutFirst = false;
['ytInitialPlayerResponse', 'ytInitialData'].forEach(function (name) {
  try {
    if (apply(owns, window, [name])) return;
    var held;
    define(window, name, {
      get: function () { return held; },
      set: function (value) {
        if (name === 'ytInitialPlayerResponse') try { if (strip(value)) cutFirst = true; } catch (e) {}
        held = prune(value);
      },
      enumerable: true, configurable: true
    });
  } catch (e) {}
});
// The first video, held at 0:00 for its missing pre-roll, started again by the player itself: once, and only that video.
function seconds(t) {
  if (!t) return 0;
  if (/^\d+$/.test(t)) return +t;
  var m = /^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$/.exec(t);
  return m ? (+(m[1] || 0)) * 3600 + (+(m[2] || 0)) * 60 + (+(m[3] || 0)) : 0;
}
try {
  document.addEventListener('DOMContentLoaded', function () {
    try {
      if (!cutFirst || location.pathname !== '/watch' || location.hostname.indexOf('music.') === 0) return;
      var query = new URLSearchParams(location.search), id = query.get('v');
      if (!id || query.get('list')) return;
      var at = seconds(query.get('t') || query.get('start')), href = location.href, until = Date.now() + 10000;
      (function look() {
        if (location.href !== href || Date.now() > until) return;
        var player = document.getElementById('movie_player');
        if (player && typeof player.loadVideoById === 'function') {
          // Already playing: nothing held.
          var video = player.querySelector('video');
          if (video && !video.paused && video.currentTime > 0) return;
          try { player.loadVideoById(id, at); } catch (e) {}
          return;
        }
        setTimeout(look, 100);
      })();
    } catch (e) {}
  }, { once: true });
} catch (e) {}
