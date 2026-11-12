// SPDX-License-Identifier: AGPL-3.0-or-later
// Parsers for the official data exports of claude.ai and ChatGPT.
// Both formats are undocumented and change over time, so every field is read defensively.

export type ImportSource = 'claude' | 'chatgpt';

export interface ImportedAttachment {
  name: string;
  text?: string;
}

export interface ImportedMessage {
  role: 'user' | 'assistant';
  text: string;
  createdUtc?: string;
  attachments?: ImportedAttachment[];
}

export interface ImportedConversation {
  source: ImportSource;
  id: string;
  title: string;
  createdUtc: string;
  updatedUtc: string;
  projectKey?: string;
  model?: string;
  messages: ImportedMessage[];
}

export interface ImportedProject {
  source: ImportSource;
  key: string;
  name: string;
  description?: string;
  instructions?: string;
  docs: { name: string; content: string }[];
  named: boolean; // false when the export has no project name (ChatGPT)
}

export interface ImportBundle {
  source: ImportSource;
  conversations: ImportedConversation[];
  projects: ImportedProject[];
  warnings: string[];
}

type J = Record<string, any>;

const iso = (v: unknown): string => {
  if (v == null || v === '') return '';
  if (typeof v === 'number') return new Date(v < 1e12 ? v * 1000 : v).toISOString();
  const d = new Date(String(v));
  return isNaN(d.getTime()) ? '' : d.toISOString();
};

// ---------- claude.ai ----------

function claudeText(m: J): string {
  if (Array.isArray(m.content) && m.content.length) {
    const parts = m.content
      .filter((c: J) => c && c.type === 'text' && typeof c.text === 'string')
      .map((c: J) => c.text);
    if (parts.length) return parts.join('\n\n');
  }
  return typeof m.text === 'string' ? m.text : '';
}

export function parseClaude(conversations: J[], projects: J[] | null): ImportBundle {
  const warnings: string[] = [];
  const convs: ImportedConversation[] = [];
  for (const c of conversations) {
    if (!c || typeof c !== 'object') continue;
    const msgs: ImportedMessage[] = [];
    for (const m of (c.chat_messages as J[]) || []) {
      const role = m.sender === 'human' ? 'user' : m.sender === 'assistant' ? 'assistant' : null;
      if (!role) continue;
      const attachments: ImportedAttachment[] = [];
      for (const a of (m.attachments as J[]) || []) {
        if (a?.file_name) attachments.push({ name: String(a.file_name), text: typeof a.extracted_content === 'string' ? a.extracted_content : undefined });
      }
      for (const f of (m.files as J[]) || []) {
        if (f?.file_name && !attachments.some((x) => x.name === f.file_name)) attachments.push({ name: String(f.file_name) });
      }
      const text = claudeText(m);
      if (!text.trim() && !attachments.length) continue;
      msgs.push({ role, text, createdUtc: iso(m.created_at), attachments: attachments.length ? attachments : undefined });
    }
    const firstUser = msgs.find((m) => m.role === 'user')?.text || '';
    convs.push({
      source: 'claude',
      id: String(c.uuid || c.id),
      title: (c.name && String(c.name).trim()) || firstUser.replace(/\s+/g, ' ').slice(0, 60) || 'Untitled',
      createdUtc: iso(c.created_at),
      updatedUtc: iso(c.updated_at) || iso(c.created_at),
      projectKey: c.project_uuid || c.project?.uuid || undefined,
      model: c.model || undefined,
      messages: msgs,
    });
  }
  const projs: ImportedProject[] = (projects || [])
    .filter((p) => p && (p.uuid || p.id))
    .map((p) => ({
      source: 'claude' as const,
      key: String(p.uuid || p.id),
      name: String(p.name || 'Untitled project'),
      description: p.description || undefined,
      instructions: p.prompt_template || p.instructions || undefined,
      docs: ((p.docs as J[]) || [])
        .filter((d) => d && typeof d.content === 'string')
        .map((d) => ({ name: String(d.filename || d.name || 'document.txt'), content: d.content })),
      named: true,
    }));
  if (projs.length && !convs.some((c) => c.projectKey)) {
    warnings.push('The export lists projects but does not link conversations to them; assign chats to projects manually.');
  }
  return { source: 'claude', conversations: convs, projects: projs, warnings };
}

// ---------- ChatGPT ----------

function gptText(msg: J): string {
  const content = msg.content || {};
  const type = content.content_type;
  if (type === 'text' || type === 'multimodal_text') {
    return ((content.parts as unknown[]) || [])
      .map((p) => (typeof p === 'string' ? p : p && typeof p === 'object' && (p as J).content_type === 'image_asset_pointer' ? '[image]' : ''))
      .filter(Boolean)
      .join('\n');
  }
  if (type === 'code' && typeof content.text === 'string') return '```\n' + content.text + '\n```';
  return '';
}

export function parseChatGpt(conversations: J[]): ImportBundle {
  const convs: ImportedConversation[] = [];
  const projects = new Map<string, ImportedProject>();
  for (const c of conversations) {
    if (!c || typeof c !== 'object' || !c.mapping) continue;
    const mapping = c.mapping as Record<string, J>;
    // Follow the active branch from current_node back to the root.
    let nodeId: string | undefined = c.current_node;
    if (!nodeId || !mapping[nodeId]) {
      nodeId = Object.keys(mapping).find((k) => !(mapping[k].children || []).length);
    }
    const chain: J[] = [];
    const seen = new Set<string>();
    while (nodeId && mapping[nodeId] && !seen.has(nodeId)) {
      seen.add(nodeId);
      chain.push(mapping[nodeId]);
      nodeId = mapping[nodeId].parent;
    }
    chain.reverse();
    const msgs: ImportedMessage[] = [];
    for (const n of chain) {
      const m = n.message;
      if (!m) continue;
      const role = m.author?.role;
      if (role !== 'user' && role !== 'assistant') continue;
      if (m.metadata?.is_visually_hidden_from_conversation) continue;
      if (role === 'assistant' && m.recipient && m.recipient !== 'all') continue; // tool calls
      const text = gptText(m);
      const attachments = ((m.metadata?.attachments as J[]) || []).filter((a) => a?.name).map((a) => ({ name: String(a.name) }));
      if (!text.trim() && !attachments.length) continue;
      const last = msgs[msgs.length - 1];
      if (last && last.role === role && role === 'assistant') {
        last.text += '\n\n' + text; // multi-part assistant turns
        continue;
      }
      msgs.push({ role, text, createdUtc: iso(m.create_time), attachments: attachments.length ? attachments : undefined });
    }
    const gizmo: string | undefined = c.gizmo_id || undefined;
    const isProject = !!gizmo && (gizmo.startsWith('g-p-') || c.gizmo_type === 'snorlax');
    if (isProject && !projects.has(gizmo!)) {
      projects.set(gizmo!, { source: 'chatgpt', key: gizmo!, name: `ChatGPT project ${gizmo!.slice(4, 12)}`, docs: [], named: false });
    }
    const firstUser = msgs.find((m) => m.role === 'user')?.text || '';
    convs.push({
      source: 'chatgpt',
      id: String(c.conversation_id || c.id),
      title: (c.title && String(c.title).trim()) || firstUser.replace(/\s+/g, ' ').slice(0, 60) || 'Untitled',
      createdUtc: iso(c.create_time),
      updatedUtc: iso(c.update_time) || iso(c.create_time),
      projectKey: isProject ? gizmo : undefined,
      model: c.default_model_slug || undefined,
      messages: msgs,
    });
  }
  const warnings: string[] = [];
  if (projects.size) warnings.push('ChatGPT exports do not contain project names or project files; rename imported projects before import.');
  return { source: 'chatgpt', conversations: convs, projects: [...projects.values()], warnings };
}

// ---------- detection ----------

export function detect(files: Record<string, unknown>): ImportBundle {
  const names = Object.keys(files);
  const convFiles = names.filter((n) => /(^|\/)conversations(-\d+)?\.json$/i.test(n)).sort();
  if (!convFiles.length) throw new Error('No conversations.json found in the export.');
  const all: J[] = [];
  for (const n of convFiles) {
    const v = files[n];
    if (Array.isArray(v)) all.push(...v);
    else if (v && typeof v === 'object' && Array.isArray((v as J).conversations)) all.push(...(v as J).conversations);
  }
  const sample = all.find(Boolean) || {};
  if ('chat_messages' in sample) {
    const pn = names.find((n) => /(^|\/)projects\.json$/i.test(n));
    return parseClaude(all, pn && Array.isArray(files[pn]) ? (files[pn] as J[]) : null);
  }
  if ('mapping' in sample) return parseChatGpt(all);
  throw new Error('Unknown export format: expected claude.ai (chat_messages) or ChatGPT (mapping).');
}
