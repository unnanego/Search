chrome.runtime.onMessage.addListener((m, s, reply) => { (async () => {
  if (m.q === 'set') { await chrome.storage.local.set({ kept: m.v }); reply('set'); }
  else if (m.q === 'get') reply({ kept: (await chrome.storage.local.get('kept')).kept ?? null, version: chrome.runtime.getManifest().version, marker: self.MARKER ?? null });
})(); return true; });
