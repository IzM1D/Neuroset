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
  webEnabled: boolean;
  pythonEnabled: boolean;
  searchKey: string;
  imageModel: string;
}

export interface ToolCall {
  id: string;
  type: 'function';
  function: { name: string; arguments: string };
}

export type ApiContentPart = { type: 'text'; text: string } | { type: 'image_url'; image_url: { url: string } };

export interface ApiMessage {
  role: 'system' | 'user' | 'assistant' | 'tool';
  content: string | ApiContentPart[] | null;
  tool_calls?: ToolCall[];
  tool_call_id?: string;
}

export interface ToolDefinition {
  type: 'function';
  function: { name: string; description: string; parameters: Record<string, unknown> };
}

export interface Source { title: string; url: string }
export interface ImageArtifact { id: string; title: string; model: string; kind: 'image' | 'plot' }
export interface ImageAttachment { id: string; name: string; mediaType: string; sizeBytes: number }
export interface ToolStep {
  id: string;
  name: string;
  label: string;
  input: string;
  status: 'running' | 'done' | 'error';
  output?: string;
  sources?: Source[];
}

export interface Message {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  error?: string;
  interrupted?: boolean;
  steps?: ToolStep[];
  images?: ImageArtifact[];
  attachments?: ImageAttachment[];
  protocol?: ApiMessage[];
}

export interface Chat {
  id: string;
  title: string;
  updatedAt: number;
  messages: Message[];
  draft: string;
  draftAttachments?: ImageAttachment[];
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
  webEnabled: true,
  pythonEnabled: true,
  searchKey: '',
  imageModel: '',
};

export function createChat(): Chat {
  return { id: crypto.randomUUID(), title: 'Новый чат', updatedAt: Date.now(), messages: [], draft: '' };
}
