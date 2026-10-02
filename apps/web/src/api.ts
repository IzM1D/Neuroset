import type { ApiMessage, Message, Settings, ToolCall, ToolDefinition } from './types';

export function endpoint(baseUrl: string, resource = 'chat/completions'): string {
  let url: URL;
  try { url = new URL(baseUrl.trim()); } catch { throw new Error('Введите полный адрес API, например https://api.openai.com/v1.'); }
  if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) {
    throw new Error('Адрес API должен начинаться с https:// или http:// и не содержать логин и пароль.');
  }
  url.pathname = url.pathname.replace(/\/+$/, '').replace(/\/chat\/completions$/, '') + '/' + resource;
  url.hash = '';
  return url.toString();
}

export function headers(apiKey: string): Record<string, string> {
  return { 'Content-Type': 'application/json', ...(apiKey.trim() ? { Authorization: `Bearer ${apiKey.trim()}` } : {}) };
}

function providerError(data: unknown): string | undefined {
  if (!data || typeof data !== 'object' || !('error' in data) || !data.error) return;
  const error = data.error;
  if (typeof error === 'string') return error;
  if (typeof error === 'object' && 'message' in error && typeof error.message === 'string') return error.message;
  return 'API вернул ошибку.';
}

export async function checkResponse(response: Response) {
  if (response.ok) return;
  const text = await response.text();
  let detail = '';
  try { detail = providerError(JSON.parse(text)) || ''; } catch { /* An HTML error page is not useful in chat. */ }
  const hints: Record<number, string> = {
    401: 'Проверьте API-ключ в настройках.', 403: 'Нет доступа к API или выбранной модели.',
    404: 'Проверьте адрес API и название модели.', 429: 'Лимит запросов или баланс исчерпан. Попробуйте позже.',
  };
  throw new Error(`HTTP ${response.status}: ${detail || hints[response.status] || 'Сервер не смог обработать запрос.'}`);
}

export async function discoverModels(settings: Settings, signal: AbortSignal): Promise<string[]> {
  const response = await fetch(endpoint(settings.baseUrl, 'models'), { headers: headers(settings.apiKey), signal });
  await checkResponse(response);
  const data = await response.json();
  const error = providerError(data);
  if (error) throw new Error(error);
  if (!Array.isArray(data.data)) throw new Error('API не вернул список моделей. Введите название вручную.');
  return [...new Set<string>(data.data.map((model: { id?: unknown }) => model?.id).filter((id: unknown) => typeof id === 'string'))].sort();
}

// Decode lines incrementally: network chunks can split both UTF-8 characters and SSE frames.
export async function* sseData(body: ReadableStream<Uint8Array>): AsyncGenerator<string> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let data: string[] = [];
  try {
    while (true) {
      const { value, done } = await reader.read();
      buffer += done ? decoder.decode() : decoder.decode(value, { stream: true });
      let match: RegExpExecArray | null;
      while ((match = /\r\n|\r|\n/.exec(buffer))) {
        if (!done && match[0] === '\r' && match.index === buffer.length - 1) break;
        const line = buffer.slice(0, match.index);
        buffer = buffer.slice(match.index + match[0].length);
        if (!line) {
          if (data.length) yield data.join('\n');
          data = [];
        } else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
      }
      if (done) {
        if (buffer.startsWith('data:')) data.push(buffer.slice(5).replace(/^ /, ''));
        if (data.length) yield data.join('\n');
        break;
      }
    }
  } finally {
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
}

export async function requestCompletion(settings: Settings, messages: ApiMessage[], signal: AbortSignal, onText: (text: string) => void,
  tools: ToolDefinition[] = [], forceTool?: string) {
  const body = {
    model: settings.model.trim(),
    messages,
    ...(tools.length ? { tools, tool_choice: forceTool ? { type: 'function', function: { name: forceTool } } : 'auto' } : {}),
    stream: settings.stream,
    ...(settings.temperature !== '' ? { temperature: Number(settings.temperature) } : {}),
    ...(settings.maxTokens !== '' ? { max_tokens: Number(settings.maxTokens) } : {}),
  };
  const response = await fetch(endpoint(settings.baseUrl), {
    method: 'POST', headers: headers(settings.apiKey), body: JSON.stringify(body), signal,
  });
  await checkResponse(response);
  let receivedText = false;
  let content = '';
  const calls = new Map<number, ToolCall>();
  const acceptCalls = (items: unknown, streaming: boolean) => {
    if (!Array.isArray(items)) return;
    for (const [position, item] of items.entries()) {
      const index = streaming ? item.index : position;
      if (!Number.isInteger(index) || index < 0 || index > 15) throw new Error('API вернул некорректный вызов инструмента.');
      const call = calls.get(index) || { id: '', type: 'function' as const, function: { name: '', arguments: '' } };
      if (typeof item.id === 'string') call.id += item.id;
      if (typeof item.function?.name === 'string') call.function.name += item.function.name;
      if (typeof item.function?.arguments === 'string') call.function.arguments += item.function.arguments;
      if (call.function.arguments.length > 100_000) throw new Error('Слишком большой запрос к инструменту.');
      calls.set(index, call);
    }
  };
  const accept = (text: unknown) => {
    if (typeof text === 'string' && text) { receivedText = true; content += text; onText(text); }
  };
  if (response.headers.get('content-type')?.includes('text/event-stream')) {
    if (!response.body) throw new Error('Браузер не смог прочитать поток ответа.');
    let finished = false;
    for await (const event of sseData(response.body)) {
      if (event.trim() === '[DONE]') { finished = true; break; }
      let data;
      try { data = JSON.parse(event); } catch { throw new Error('API вернул некорректный поток ответа.'); }
      const error = providerError(data);
      if (error) throw new Error(error);
      const choice = data.choices?.[0];
      accept(choice?.delta?.content);
      accept(choice?.delta?.refusal);
      acceptCalls(choice?.delta?.tool_calls, true);
      if (choice?.finish_reason != null) finished = true;
    }
    if (!finished) throw new Error('Поток ответа прервался. Попробуйте повторить запрос.');
  } else {
    const data = await response.json();
    const error = providerError(data);
    if (error) throw new Error(error);
    accept(data.choices?.[0]?.message?.content);
    accept(data.choices?.[0]?.message?.refusal);
    acceptCalls(data.choices?.[0]?.message?.tool_calls, false);
  }
  if (!receivedText && !calls.size) throw new Error('Модель вернула пустой ответ. Проверьте модель и параметры генерации.');
  const toolCalls = [...calls.entries()].sort(([a], [b]) => a - b).map(([, call]) => call);
  if (toolCalls.some(call => !call.id || !call.function.name) || new Set(toolCalls.map(call => call.id)).size !== toolCalls.length) {
    throw new Error('API вернул неполный вызов инструмента.');
  }
  return { role: 'assistant' as const, content: content || null, ...(toolCalls.length ? { tool_calls: toolCalls } : {}) };
}

export async function completeChat(settings: Settings, messages: Message[], signal: AbortSignal, onText: (text: string) => void) {
  return requestCompletion(settings, [
    ...(settings.systemPrompt.trim() ? [{ role: 'system' as const, content: settings.systemPrompt.trim() }] : []),
    ...messages.filter(message => message.content && !message.error).map(({ role, content }) => ({ role, content })),
  ], signal, onText);
}

export function friendlyError(error: unknown): string {
  if (error instanceof TypeError) return 'Не удалось подключиться к API. Проверьте адрес и сеть. Сервер должен разрешать CORS для этого сайта; на GitHub Pages используйте HTTPS API.';
  return error instanceof Error ? error.message : 'Не удалось выполнить запрос.';
}
