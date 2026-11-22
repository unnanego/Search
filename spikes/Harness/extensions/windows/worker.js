// What the worker sees of windows and focus, and whether native messaging reaches a host.
chrome.runtime.onMessage.addListener((m, s, reply) => {
  (async () => {
    try {
      if (m.q === 'focused') reply({
        last: (await chrome.tabs.query({ active: true, lastFocusedWindow: true })).map(t => ({ id: t.id, windowId: t.windowId, url: t.url })),
        current: (await chrome.tabs.query({ active: true, currentWindow: true })).map(t => ({ id: t.id, windowId: t.windowId, url: t.url })),
        lastWindow: (await chrome.windows.getLastFocused()).id
      });
      else if (m.q === 'native') reply({ ok: await chrome.runtime.sendNativeMessage(m.name, { hi: 1 }) });
      else reply({ unknown: m });
    } catch (e) { reply({ error: String(e?.message ?? e) }); }
  })();
  return true;
});
