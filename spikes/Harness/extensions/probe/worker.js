// Asks each Chrome API whether it exists in WebView2 and whether it answers, and what the extension sees of the
// tab that asked. Each answer is cut short; a call that never answers is a 3 s timeout, not a hang.
const events = [];
chrome.tabs?.onCreated?.addListener(t => events.push({ ev: 'tabs.onCreated', id: t.id, url: t.pendingUrl ?? t.url }));
chrome.tabs?.onUpdated?.addListener((id, ch) => { if (ch.status) events.push({ ev: 'tabs.onUpdated', id, status: ch.status }); });
chrome.tabs?.onActivated?.addListener(i => events.push({ ev: 'tabs.onActivated', ...i }));
chrome.runtime.onInstalled.addListener(d => events.push({ ev: 'runtime.onInstalled', reason: d.reason }));

const short = v => { try { const s = JSON.stringify(v); return s && s.length > 300 ? s.slice(0, 300) + '…' : s; } catch { return String(v); } };
const lookup = path => path.split('.').reduce((o, k) => o?.[k], chrome);

async function probe(results, name, path, fn) {
  if (lookup(path) === undefined) { results[name] = { exists: false }; return; }
  try {
    const v = await Promise.race([Promise.resolve().then(fn), new Promise((_, no) => setTimeout(() => no(new Error('timeout 3s')), 3000))]);
    results[name] = { exists: true, ok: true, value: short(v) };
  } catch (e) { results[name] = { exists: true, ok: false, error: String(e?.message ?? e) }; }
}

const cb = (call) => new Promise((ok, no) => call(v => chrome.runtime.lastError ? no(new Error(chrome.runtime.lastError.message)) : ok(v ?? 'ok')));

async function run(msg, sender) {
  const r = {}, created = msg.origin + '/created.html', tabId = sender.tab?.id;
  await probe(r, 'runtime.getPlatformInfo', 'runtime.getPlatformInfo', () => chrome.runtime.getPlatformInfo());
  await probe(r, 'tabs.query all', 'tabs.query', () => chrome.tabs.query({}));
  await probe(r, 'tabs.query active', 'tabs.query', () => chrome.tabs.query({ active: true, currentWindow: true }));
  await probe(r, 'tabs.get sender', 'tabs.get', () => chrome.tabs.get(tabId));
  await probe(r, 'tabs.sendMessage sender', 'tabs.sendMessage', () => chrome.tabs.sendMessage(tabId, { type: 'hello' }));
  await probe(r, 'tabs.captureVisibleTab', 'tabs.captureVisibleTab', async () => (await chrome.tabs.captureVisibleTab()).length + ' chars');
  await probe(r, 'tabs.update muted', 'tabs.update', () => chrome.tabs.update(tabId, { muted: true }));
  await probe(r, 'windows.getAll', 'windows.getAll', () => chrome.windows.getAll());
  await probe(r, 'windows.getCurrent', 'windows.getCurrent', () => chrome.windows.getCurrent());
  await probe(r, 'tabGroups.query', 'tabGroups.query', () => chrome.tabGroups.query({}));
  await probe(r, 'action.setBadgeText', 'action.setBadgeText', () => chrome.action.setBadgeText({ text: '1' }));
  await probe(r, 'action.getPopup', 'action.getPopup', () => chrome.action.getPopup({}));
  await probe(r, 'action.openPopup', 'action.openPopup', () => chrome.action.openPopup());
  await probe(r, 'sidePanel.setOptions', 'sidePanel.setOptions', () => chrome.sidePanel.setOptions({ path: 'popup.html' }));
  await probe(r, 'storage.local', 'storage.local', async () => { await chrome.storage.local.set({ a: 1 }); return chrome.storage.local.get('a'); });
  await probe(r, 'storage.sync', 'storage.sync', async () => { await chrome.storage.sync.set({ b: 2 }); return chrome.storage.sync.get('b'); });
  await probe(r, 'storage.session', 'storage.session', async () => { await chrome.storage.session.set({ c: 3 }); return chrome.storage.session.get('c'); });
  await probe(r, 'dnr.updateDynamicRules', 'declarativeNetRequest.updateDynamicRules', async () => {
    await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: [7], addRules: [{ id: 7, action: { type: 'block' }, condition: { urlFilter: '||blocked.test^' } }] });
    return chrome.declarativeNetRequest.getDynamicRules();
  });
  await probe(r, 'scripting.executeScript', 'scripting.executeScript', () => chrome.scripting.executeScript({ target: { tabId }, func: () => location.href }));
  await probe(r, 'scripting.insertCSS', 'scripting.insertCSS', () => chrome.scripting.insertCSS({ target: { tabId }, css: 'h1{color:red}' }));
  await probe(r, 'cookies.getAll', 'cookies.getAll', () => chrome.cookies.getAll({ domain: 'probe.test' }));
  await probe(r, 'alarms.create', 'alarms.create', async () => { await chrome.alarms.create('a', { delayInMinutes: 1 }); return chrome.alarms.getAll(); });
  await probe(r, 'contextMenus.create', 'contextMenus.create', () => cb(done => chrome.contextMenus.create({ id: 'x', title: 'x' }, done)));
  await probe(r, 'notifications.create', 'notifications.create', () => chrome.notifications.create({ type: 'basic', iconUrl: 'popup.html', title: 't', message: 'm' }));
  await probe(r, 'offscreen.createDocument', 'offscreen.createDocument', () => chrome.offscreen.createDocument({ url: 'popup.html', reasons: ['DOM_PARSER'], justification: 'probe' }));
  await probe(r, 'identity.getRedirectURL', 'identity.getRedirectURL', () => chrome.identity.getRedirectURL());
  await probe(r, 'bookmarks.getTree', 'bookmarks.getTree', () => chrome.bookmarks.getTree());
  await probe(r, 'history.search', 'history.search', () => chrome.history.search({ text: '' }));
  await probe(r, 'downloads.search', 'downloads.search', () => chrome.downloads.search({}));
  await probe(r, 'sessions.getRecentlyClosed', 'sessions.getRecentlyClosed', () => chrome.sessions.getRecentlyClosed());
  await probe(r, 'topSites.get', 'topSites.get', () => chrome.topSites.get());
  await probe(r, 'webNavigation.getAllFrames', 'webNavigation.getAllFrames', () => chrome.webNavigation.getAllFrames({ tabId }));
  await probe(r, 'webRequest.onBeforeRequest', 'webRequest.onBeforeRequest', () => 'present');
  await probe(r, 'privacy.network', 'privacy.network', () => 'present');
  await probe(r, 'proxy.settings', 'proxy.settings', () => 'present');
  await probe(r, 'idle.queryState', 'idle.queryState', () => chrome.idle.queryState(60));
  await probe(r, 'fontSettings.getFontList', 'fontSettings.getFontList', async () => (await chrome.fontSettings.getFontList()).length + ' fonts');
  await probe(r, 'tts.getVoices', 'tts.getVoices', async () => (await chrome.tts.getVoices()).length + ' voices');
  await probe(r, 'management.getSelf', 'management.getSelf', () => chrome.management.getSelf());
  await probe(r, 'permissions.getAll', 'permissions.getAll', () => chrome.permissions.getAll());
  await probe(r, 'commands.getAll', 'commands.getAll', () => chrome.commands.getAll());
  await probe(r, 'i18n.getUILanguage', 'i18n.getUILanguage', () => chrome.i18n.getUILanguage());
  await probe(r, 'userScripts', 'userScripts', () => 'present');
  await probe(r, 'runtime.connectNative', 'runtime.connectNative', () => 'present');
  await probe(r, 'tabs.create', 'tabs.create', () => chrome.tabs.create({ url: created }));
  await probe(r, 'windows.create', 'windows.create', () => chrome.windows.create({ url: created }));
  await new Promise(ok => setTimeout(ok, 1500));
  await probe(r, 'after create: tabs', 'tabs.query', async () => (await chrome.tabs.query({})).map(t => ({ id: t.id, windowId: t.windowId, url: t.url })));
  await probe(r, 'after create: windows', 'windows.getAll', async () => (await chrome.windows.getAll({ populate: true })).map(w => ({ id: w.id, type: w.type, tabs: w.tabs?.length })));
  return {
    sender: { tab: sender.tab ? { id: sender.tab.id, url: sender.tab.url, windowId: sender.tab.windowId, active: sender.tab.active } : null, frameId: sender.frameId, url: sender.url },
    results: r, events, userAgent: navigator.userAgent, chromeNamespaces: Object.keys(chrome).sort()
  };
}

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (msg.type === 'ping') { reply({ pong: true, senderTab: sender.tab?.id ?? null, senderUrl: sender.url }); return; }
  if (msg.type !== 'report') return;
  run(msg, sender).then(reply, e => reply({ error: String(e) }));
  return true;
});
