import { createChat, defaultSettings, type Chat, type Settings } from './types';

const prefix = 'neuroset.web.v1.';

function validAttachments(value: unknown): boolean {
  return value === undefined || (Array.isArray(value) && value.length <= 4 && value.every(image =>
    image && typeof image.id === 'string' && typeof image.name === 'string' &&
    ['image/png', 'image/jpeg', 'image/webp', 'image/gif'].includes(image.mediaType) &&
    Number.isFinite(image.sizeBytes) && image.sizeBytes > 0 && image.sizeBytes <= 10 * 1024 * 1024));
}

export function loadSettings(): Settings {
  try {
    const saved = JSON.parse(localStorage.getItem(prefix + 'settings') || '{}');
    const settings = { ...defaultSettings };
    for (const key of Object.keys(settings) as (keyof Settings)[]) {
      if (typeof saved[key] === typeof settings[key]) Object.assign(settings, { [key]: saved[key] });
    }
    settings.theme = settings.theme === 'light' ? 'light' : 'dark';
    if (!settings.rememberKey) {
      settings.apiKey = sessionStorage.getItem(prefix + 'key') || '';
      settings.searchKey = sessionStorage.getItem(prefix + 'searchKey') || '';
    }
    return settings;
  } catch { return { ...defaultSettings }; }
}

export function saveSettings(settings: Settings) {
  localStorage.setItem(prefix + 'settings', JSON.stringify({ ...settings, apiKey: settings.rememberKey ? settings.apiKey : '', searchKey: settings.rememberKey ? settings.searchKey : '' }));
  if (settings.rememberKey) sessionStorage.removeItem(prefix + 'key');
  else sessionStorage.setItem(prefix + 'key', settings.apiKey);
  if (settings.rememberKey) sessionStorage.removeItem(prefix + 'searchKey');
  else sessionStorage.setItem(prefix + 'searchKey', settings.searchKey);
}

export function loadChats(): Chat[] {
  try {
    const saved = JSON.parse(localStorage.getItem(prefix + 'chats') || '[]');
    if (!Array.isArray(saved)) return [createChat()];
    const chats = saved.filter((chat): chat is Chat =>
      chat && typeof chat.id === 'string' && typeof chat.title === 'string' &&
      typeof chat.updatedAt === 'number' && typeof chat.draft === 'string' && validAttachments(chat.draftAttachments) && Array.isArray(chat.messages) &&
      chat.messages.every((message: unknown) => {
        if (!message || typeof message !== 'object') return false;
        const m = message as Record<string, unknown>;
        return typeof m.id === 'string' && ['user', 'assistant'].includes(String(m.role)) && typeof m.content === 'string' &&
          (m.error === undefined || typeof m.error === 'string') && (m.interrupted === undefined || typeof m.interrupted === 'boolean') && validAttachments(m.attachments);
      }));
    return chats.length ? chats : [createChat()];
  } catch { return [createChat()]; }
}

export function saveChats(chats: Chat[]) {
  localStorage.setItem(prefix + 'chats', JSON.stringify(chats));
}
