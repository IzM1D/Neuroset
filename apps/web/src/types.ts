export interface Settings {
  baseUrl: string;
  apiKey: string;
  rememberKey: boolean;
  model: string;
  systemPrompt: string;
  temperature: string;
  maxTokens: string;
  stream: boolean;
  theme: 'dark' | 'light';
}

export interface Message {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  error?: string;
  interrupted?: boolean;
}

export interface Chat {
  id: string;
  title: string;
  updatedAt: number;
  messages: Message[];
  draft: string;
}

export const defaultSettings: Settings = {
  baseUrl: 'https://api.openai.com/v1',
  apiKey: '',
  rememberKey: false,
  model: '',
  systemPrompt: '',
  temperature: '',
  maxTokens: '',
  stream: true,
  theme: 'dark',
};

export function createChat(): Chat {
  return { id: crypto.randomUUID(), title: 'Новый чат', updatedAt: Date.now(), messages: [], draft: '' };
}
