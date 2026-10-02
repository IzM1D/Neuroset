import { afterEach, describe, expect, it, vi } from 'vitest';
import { completeChat, discoverModels, endpoint, sseData } from './api';
import { defaultSettings } from './types';

afterEach(() => vi.unstubAllGlobals());

function stream(text: string, chunkSize = 1) {
  const bytes = new TextEncoder().encode(text);
  return new ReadableStream<Uint8Array>({ start(controller) {
    for (let i = 0; i < bytes.length; i += chunkSize) controller.enqueue(bytes.slice(i, i + chunkSize));
    controller.close();
  } });
}

describe('OpenAI-compatible API', () => {
  it('normalizes base and full endpoint URLs without losing the provider prefix', () => {
    expect(endpoint('https://example.com/proxy/v1/')).toBe('https://example.com/proxy/v1/chat/completions');
    expect(endpoint('https://example.com/v1/chat/completions/')).toBe('https://example.com/v1/chat/completions');
    expect(endpoint('https://example.com/v1/chat/completions', 'models')).toBe('https://example.com/v1/models');
    expect(() => endpoint('file:///etc/passwd')).toThrow();
    expect(() => endpoint('https://name:password@example.com')).toThrow();
  });

  it('handles split UTF-8, CRLF, comments, multiline data and unterminated final frames', async () => {
    const values = [];
    for await (const value of sseData(stream(': heartbeat\r\ndata: Привет\r\ndata: мир\r\n\r\ndata: [DONE]'))) values.push(value);
    expect(values).toEqual(['Привет\nмир', '[DONE]']);
  });

  it('sends context and auth, strips UI metadata, and combines streamed tokens', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(stream('data: {"choices":[{"delta":{"content":"Привет"}}]}\n\ndata: {"choices":[{"delta":{"content":"!"},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n'), { headers: { 'Content-Type': 'text/event-stream' } }));
    vi.stubGlobal('fetch', fetchMock);
    let result = '';
    await completeChat({ ...defaultSettings, model: 'test-model', apiKey: 'test-key', systemPrompt: 'Be helpful' }, [
      { id: '1', role: 'user', content: 'Hi' },
      { id: '2', role: 'assistant', content: '', error: 'old error' },
    ], new AbortController().signal, text => result += text);
    expect(result).toBe('Привет!');
    const [url, request] = fetchMock.mock.calls[0];
    expect(url).toBe('https://api.openai.com/v1/chat/completions');
    expect(request.headers.Authorization).toBe('Bearer test-key');
    expect(JSON.parse(request.body)).toEqual({ model: 'test-model', stream: true, messages: [{ role: 'system', content: 'Be helpful' }, { role: 'user', content: 'Hi' }] });
  });

  it('accepts a JSON response even when streaming was requested', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ choices: [{ message: { content: 'Normal response' } }] })));
    const text = vi.fn();
    await completeChat(defaultSettings, [], new AbortController().signal, text);
    expect(text).toHaveBeenCalledWith('Normal response');
  });

  it.each([
    [Response.json({ error: { message: 'Invalid key' } }, { status: 401 }), 'Invalid key'],
    [Response.json({ error: { message: 'Quota exceeded' } }), 'Quota exceeded'],
    [new Response(stream('data: {"error":{"message":"Stream failure"}}\n\n'), { headers: { 'Content-Type': 'text/event-stream' } }), 'Stream failure'],
    [new Response(stream('data: {"choices":[{"delta":{"content":"Partial"}}]}\n\n'), { headers: { 'Content-Type': 'text/event-stream' } }), 'прервался'],
    [Response.json({ choices: [{ message: { content: '' } }] }), 'пустой ответ'],
  ])('surfaces provider errors and incomplete responses', async (response, message) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response));
    await expect(completeChat(defaultSettings, [], new AbortController().signal, () => {})).rejects.toThrow(message);
  });

  it('discovers unique model IDs and supports keyless providers', async () => {
    const fetchMock = vi.fn().mockResolvedValue(Response.json({ data: [{ id: 'b' }, { id: 'a' }, { id: 'a' }, {}] }));
    vi.stubGlobal('fetch', fetchMock);
    expect(await discoverModels(defaultSettings, new AbortController().signal)).toEqual(['a', 'b']);
    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBeUndefined();
  });
});
