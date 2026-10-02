import type { Settings, Source } from './types';

export function publicUrl(value: string): string {
  const url = new URL(value);
  const host = url.hostname.toLowerCase();
  // Requests go only through remote reader services; never fetch model-supplied URLs from the user's network.
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password ||
      !host.includes('.') || host.includes(':') || /^\d+\.\d+\.\d+\.\d+$/.test(host) ||
      /(^|\.)(localhost|local|internal|test|invalid)$/.test(host)) throw new Error('Нужна ссылка на общедоступную веб-страницу.');
  return url.href;
}

async function readBounded(response: Response, limit = 1_000_000) {
  if (!response.ok) throw new Error(`Веб-сервис: HTTP ${response.status}. ${response.status === 401 ? 'Проверьте ключ Tavily.' : response.status === 429 ? 'Лимит запросов исчерпан.' : 'Не удалось получить данные.'}`);
  if (!response.body) throw new Error('Пустой ответ веб-сервиса.');
  const reader = response.body.getReader();
  let size = 0;
  let text = '';
  const decoder = new TextDecoder();
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) return text + decoder.decode();
      size += value.length;
      if (size > limit) throw new Error('Ответ веб-сервиса слишком большой.');
      text += decoder.decode(value, { stream: true });
    }
  } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); }
}

async function tavily(resource: string, key: string, payload: unknown, signal: AbortSignal) {
  const response = await fetch(`https://api.tavily.com/${resource}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${key}` },
    body: JSON.stringify(payload), signal,
  });
  return JSON.parse(await readBounded(response));
}

export async function searchWeb(settings: Settings, query: string, signal: AbortSignal) {
  if (!settings.searchKey.trim()) throw new Error('Для поиска нужен ключ Tavily: Настройки → Инструменты → Ключ поиска. Чтение ссылок доступно без ключа.');
  const data = await tavily('search', settings.searchKey.trim(), { query, max_results: 5, search_depth: 'basic', include_answer: false }, signal);
  if (!Array.isArray(data.results)) throw new Error('Сервис поиска не вернул результатов.');
  const results = data.results.flatMap((item: { url: string; title: string; content: string }) => {
    try { return [{ url: publicUrl(item.url), title: String(item.title).slice(0, 300), content: String(item.content || '').slice(0, 3500) }]; }
    catch { return []; }
  }).slice(0, 5);
  return { text: JSON.stringify({ query, results }), sources: results.map(({ title, url }: Source) => ({ title, url })) as Source[] };
}

export async function fetchPage(settings: Settings, value: string, signal: AbortSignal) {
  const url = publicUrl(value);
  let content: string;
  if (settings.searchKey.trim()) {
    const data = await tavily('extract', settings.searchKey.trim(), { urls: [url], format: 'markdown' }, signal);
    if (typeof data.results?.[0]?.raw_content !== 'string') throw new Error('Сервис не смог прочитать страницу.');
    content = data.results[0].raw_content;
  } else {
    const response = await fetch(`https://r.jina.ai/${url}`, { signal, headers: { Accept: 'text/plain' }, credentials: 'omit', referrerPolicy: 'no-referrer' });
    content = await readBounded(response);
  }
  return { text: JSON.stringify({ url, content: content.slice(0, 18000), truncated: content.length > 18000 }), sources: [{ title: new URL(url).hostname, url }] };
}
