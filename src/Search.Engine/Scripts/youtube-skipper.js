// An ad that plays all the same goes by muted and at 16×, and the video comes back as it was (YouTubeAds.swift's
// skipper). In Search's isolated world; never seeks, since an ad stitched into the video would make "the end" the end.
'use strict';
var held = new WeakMap();
function look(event) {
  var video = event.target;
  if (!(video instanceof HTMLVideoElement)) return;
  var player = video.closest('.html5-video-player');
  var ad = !!player && player.classList.contains('ad-showing');
  var was = held.get(video);
  try {
    if (ad) {
      if (!was) { was = { muted: video.muted, rate: video.playbackRate }; held.set(video, was); video.muted = true; }
      if (video.playbackRate !== 16) video.playbackRate = 16;
    } else if (was) {
      held.delete(video);
      video.playbackRate = was.rate;
      video.muted = was.muted;
    }
  } catch (e) {}
}
['playing', 'timeupdate', 'durationchange', 'emptied'].forEach(function (type) { document.addEventListener(type, look, true); });
