// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from 'react';
import { dbAutoSwitch } from './dbapi';
import { categorize } from './errors';
import { chatPermissions, type ChatPermissions, type ContextFolder, type Project } from './types';
import { saveProjectMeta } from './workbench';

interface DeviceInfo {
  name: string;
  paired: boolean;
  remote?: boolean; // 0.42.0: home PC device from owner.json (no CO Desktop in this browser)
}

// Switches for what agents may do in this chat (Bridge 1.1). Each switch applies from the next message.
// 0.71.0: global auto-switch state, read once per page load (the owner changes it in the Host tab)
let autoSwitchGlobal: Promise<boolean> | null = null;
export function autoSwitchGlobalOn(): Promise<boolean> {
  autoSwitchGlobal ??= dbAutoSwitch().then((r) => r.enabled, () => true);
  return autoSwitchGlobal;
}
export function resetAutoSwitchGlobal(): void {
  autoSwitchGlobal = null;
}

export function PermissionsBar({
  value, onChange, project, onProjectChanged, device, useDevice, onUseDevice, supported, shared, autoSwitch, onAutoSwitch, part = 'all',
}: {
  part?: 'all' | 'chat' | 'folders'; // 0.86.0: 'chat' - Inspector Chat tab (no folder chips), 'folders' - Files tab
  shared?: boolean; // 0.45.0: shared chat -> corporate features off
  autoSwitch?: boolean; // 0.71.0: chat.autoSwitch !== false
  onAutoSwitch?: (on: boolean) => void;
  value: ChatPermissions | undefined;
  onChange: (p: ChatPermissions) => void;
  project: Project;
  onProjectChanged?: (p: Project) => void;
  device: DeviceInfo | null;
  useDevice: boolean;
  onUseDevice: (v: boolean) => void;
  supported: boolean;
}) {
  const p = chatPermissions(value);
  const [err, setErr] = useState<string | null>(null);
  const folders = project.contextFolders || [];
  const [globalAuto, setGlobalAuto] = useState(true);
  useEffect(() => {
    let alive = true;
    void autoSwitchGlobalOn().then((on) => { if (alive) setGlobalAuto(on); });
    return () => { alive = false; };
  }, []);

  async function setFolderAccess(f: ContextFolder, access: ContextFolder['access']) {
    setErr(null);
    try {
      const next = await saveProjectMeta(project, {
        contextFolders: folders.map((x) => (x.id === f.id ? { ...x, access } : x)),
      });
      onProjectChanged?.(next);
    } catch (e) {
      setErr(categorize(e).message);
    }
  }

  const chip = (on: boolean, label: string, title: string, click: () => void, disabled = false) => (
    <button type="button" className={`perm-chip ${on ? 'on' : 'off'}`} title={title} onClick={click} disabled={disabled} aria-pressed={on}>
      {label}
    </button>
  );

  return (
    <div className="perm-bar" aria-label={part === 'folders' ? 'Context folders of this project' : 'Permissions for this chat'}>
      {part === 'folders' ? null : supported ? (
        <>
          {chip(p.files === 'write', p.files === 'write' ? '✎ Files: write' : '👁 Files: read-only',
            'Files in the chat folder, the project workspace and read-write context folders. Click to switch.',
            () => onChange({ ...p, files: p.files === 'write' ? 'read' : 'write' }))}
          {chip(p.shell, '⌨ Commands', p.shell ? 'The agent may run shell commands. Click to forbid.' : 'Shell commands are off. Click to allow.',
            () => onChange({ ...p, shell: !p.shell }))}
          {chip(p.web, '🌐 Web', p.web ? 'Web search and page fetch are allowed. Click to forbid.' : 'Web access is off. Click to allow.',
            () => onChange({ ...p, web: !p.web }))}
        </>
      ) : (
        <span className="muted tiny" title="Bridge 1.1 or later applies these switches">Permissions: Bridge default</span>
      )}
      {device && part !== 'folders' &&
        chip(useDevice && device.paired, `💻 ${device.name}`,
          !device.paired ? 'This computer is not connected to Bridge yet.'
            : device.remote
              ? (useDevice ? `Agents use the folders and tools of ${device.name} (home computer, through Bridge). Click to turn off.`
                : `${device.name}'s tools are off for new messages. Click to turn on.`)
            : useDevice ? 'Agents can use the folders and tools connected in CO Desktop; writes are confirmed there. Click to turn off.'
            : 'This computer\'s tools are off for new messages. Click to turn on.',
          () => onUseDevice(!useDevice), !device.paired)}
      {/* 0.45.0: a shared chat shows one chip instead of the folder chips */}
      {part === 'chat' ? null : shared ? (
        <span className="perm-chip off" title="Shared chat: devices and context folders are off for everyone in it.">👥 Shared</span>
      ) : (
        folders.map((f) =>
          chip(f.access === 'write', `📁 ${f.label}: ${f.access === 'write' ? 'rw' : 'ro'}`,
            'Context folder on the home computer or Google Drive. Click to switch between read-only and read-write.',
            () => void setFolderAccess(f, f.access === 'write' ? 'read' : 'write')))
      )}
      {onAutoSwitch && part !== 'folders' && chip(globalAuto && autoSwitch !== false, '⇄ Auto-switch',
        !globalAuto ? 'Automatic switch to the other agent is off for all chats (Host tab → Agents).'
          : autoSwitch !== false
            ? 'When Claude hits its usage limit, is signed out or the model is not available before doing anything, the message goes once to Codex (and the other way round), with a note in the chat. Click to turn off for this chat.'
            : 'Automatic switch to the other agent is off for this chat. Click to turn on.',
        () => onAutoSwitch(autoSwitch === false), !globalAuto)}
      {err && <span className="error-text tiny">{err}</span>}
    </div>
  );
}
