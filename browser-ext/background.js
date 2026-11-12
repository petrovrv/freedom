// SPDX-License-Identifier: AGPL-3.0-or-later
// AI Workbench extension 0.1.0: the toolbar button (and Alt+Shift+A) opens the side panel. All agent work runs in
// panel.js, which lives as long as the panel is open.
const openOnClick = () => chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => {});
chrome.runtime.onInstalled.addListener(openOnClick);
chrome.runtime.onStartup.addListener(openOnClick);
openOnClick();
