// Runs in the probe page's isolated world, asks the worker for its report, and leaves it in the DOM for the host to read.
chrome.runtime.onMessage.addListener((m, s, reply) => { if (m.type === 'hello') reply({ fromContent: true, href: location.href }); });
(async () => {
  try {
    const pong = await chrome.runtime.sendMessage({ type: 'ping' });
    const report = await chrome.runtime.sendMessage({ type: 'report', origin: location.origin });
    document.documentElement.dataset.probe = JSON.stringify({ pong, report });
  } catch (e) { document.documentElement.dataset.probe = JSON.stringify({ error: String(e) }); }
})();
