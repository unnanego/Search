// YouTube's ad schedule taken out of the player's own data before the player reads it (YouTubeAds.swift's page
// script). In the page's world, before any of its scripts; the wrapper Shield adds runs it only on YouTube's hosts
// and not where Shield is paused. It reaches only JSON.parse, Response.prototype.json and two window properties, all
// of which the page can already rewrite; it sends nothing anywhere and runs nothing it's given.
'use strict';
// Taken now, before the page runs: what the page puts in their place later is never what this calls.
var apply = Reflect.apply, owns = Object.prototype.hasOwnProperty, define = Object.defineProperty, isArray = Array.isArray;
var ads = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];
var marks = ['responseContext', 'playabilityStatus', 'streamingData', 'videoDetails'];
function plain(o) { return o !== null && typeof o === 'object' && !isArray(o); }
function youtubes(o) { for (var i = 0; i < marks.length; i++) if (apply(owns, o, [marks[i]])) return true; return false; }
function strip(o) { if (!plain(o) || !youtubes(o)) return; for (var i = 0; i < ads.length; i++) if (apply(owns, o, [ads[i]])) delete o[ads[i]]; }
function one(o) { if (!plain(o)) return; strip(o); if (apply(owns, o, ['playerResponse'])) strip(o.playerResponse); }
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
// The first page's data is a script's `var ytInitial… = {…}`: a setter already there is what that assignment goes through.
['ytInitialPlayerResponse', 'ytInitialData'].forEach(function (name) {
  try {
    if (apply(owns, window, [name])) return;
    var held;
    define(window, name, { get: function () { return held; }, set: function (value) { held = prune(value); }, enumerable: true, configurable: true });
  } catch (e) {}
});
