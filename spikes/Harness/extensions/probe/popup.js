// What the action popup can do when the host shows it in a WebView2 of its own at chrome-extension://<id>/popup.html.
(async () => {
  const out = { href: location.href, webview: typeof window.chrome?.webview };
  try { out.activeTab = await chrome.tabs.query({ active: true, currentWindow: true }); } catch (e) { out.activeTabError = String(e); }
  try { out.ping = await chrome.runtime.sendMessage({ type: 'ping' }); } catch (e) { out.pingError = String(e); }
  try { out.storage = await chrome.storage.local.get(null); } catch (e) { out.storageError = String(e); }
  document.body.dataset.probe = JSON.stringify(out);
})();
