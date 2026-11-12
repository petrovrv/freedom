// CO agent browser tabs (browser-agent 0.13.0): the tab chamberlain reads the tab list over CDP by evaluating
// coTabs() in this service worker. A pinned tab is the user's lock: never closed, never sent to another site.
// chrome.debugger.getTargets() only lists targets (no attach, no banner); its id is the CDP target id.
self.coTabsVersion = 1;
self.coTabs = async () => {
  const [tabs, targets] = await Promise.all([chrome.tabs.query({}), chrome.debugger.getTargets()]);
  const ids = new Map(targets.filter(t => t.tabId !== undefined).map(t => [t.tabId, t.id]));
  return tabs.map(t => ({
    targetId: ids.get(t.id) || null, windowId: t.windowId, pinned: !!t.pinned, active: !!t.active,
    discarded: !!t.discarded, frozen: !!t.frozen, lastAccessed: t.lastAccessed || 0,
  }));
};
// Keeps the worker running so the chamberlain finds it; tab events wake it after the browser stopped it.
const keep = () => chrome.runtime.getPlatformInfo(() => {});
setInterval(keep, 20000);
chrome.runtime.onStartup.addListener(keep);
chrome.runtime.onInstalled.addListener(keep);
chrome.tabs.onActivated.addListener(keep);
chrome.tabs.onUpdated.addListener(keep);
