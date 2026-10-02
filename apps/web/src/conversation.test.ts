import { afterEach, describe, expect, it, vi } from 'vitest';
import { requestCompletion } from './api';
import { defaultSettings, type ApiMessage, type ImageArtifact, type ToolStep } from './types';
import { runConversation } from './conversation';
import { executeTool } from './tools';
import { buildImagePrompt, selectImageModel } from './images';
import { publicUrl } from './web';

vi.mock('./tools', async importOriginal => ({ ...await importOriginal<typeof import('./tools')>(), executeTool: vi.fn() }));
afterEach(() => { vi.unstubAllGlobals(); vi.clearAllMocks(); });
const call = (name: string, id = 'call-1') => ({ id, type: 'function', function: { name, arguments: '{"timezone":"UTC"}' } });
const response = (message: unknown) => Response.json({ choices: [{ message }] });
function callbacks() {
  const steps: ToolStep[] = [];
  const protocols: ApiMessage[][] = [];
  const images: ImageArtifact[] = [];
  return { steps, protocols, images, onText: vi.fn(), onStep: (step: ToolStep) => steps.push(step), onProtocol: (messages: ApiMessage[]) => protocols.push(messages), onImages: (items: ImageArtifact[]) => images.push(...items) };
}

describe('tool conversation', () => {
  it('assembles function names and arguments split across SSE chunks', async () => {
    const events = [
      { tool_calls: [{ index: 0, id: 'abc', function: { name: 'current_', arguments: '{"time' } }] },
      { tool_calls: [{ index: 0, function: { name: 'time', arguments: 'zone":"UTC"}' } }] },
    ];
    const body = events.map(delta => `data: ${JSON.stringify({ choices: [{ delta }] })}\n\n`).join('') + 'data: [DONE]\n\n';
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(body, { headers: { 'Content-Type': 'text/event-stream' } })));
    const result = await requestCompletion(defaultSettings, [], new AbortController().signal, () => {});
    expect(result.tool_calls).toEqual([{ ...call('current_time', 'abc') }]);
  });

  it('returns tool output with matching call ID, and preserves complete protocol in subsequent turns', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response({ content: null, tool_calls: [call('current_time')] })).mockResolvedValueOnce(response({ content: 'Сейчас полдень.' }));
    vi.stubGlobal('fetch', fetchMock);
    vi.mocked(executeTool).mockResolvedValue({ text: '{"time":"12:00"}' });
    const events = callbacks();
    await runConversation(defaultSettings, [{ id: 'u', role: 'user', content: 'Который час?' }], new AbortController().signal, events);
    const next = JSON.parse(fetchMock.mock.calls[1][1].body);
    expect(next.messages.slice(-2)).toEqual([
      { role: 'assistant', content: null, tool_calls: [call('current_time')] },
      { role: 'tool', tool_call_id: 'call-1', content: '{"time":"12:00"}' },
    ]);
    expect(events.steps.map(step => step.status)).toEqual(['running', 'done']);
    expect(events.protocols.at(-1)).toHaveLength(3);
  });

  it('refuses unknown filesystem tools without executing them', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(response({ tool_calls: [call('read_file')] })).mockResolvedValueOnce(response({ content: 'Нет доступа к файлам.' })));
    const events = callbacks();
    await runConversation(defaultSettings, [], new AbortController().signal, events);
    expect(executeTool).not.toHaveBeenCalled();
    expect(events.steps.at(-1)?.status).toBe('error');
  });

  it('forces exactly one image attempt and never retries a paid request after an error', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response({ tool_calls: [call('image_generation', 'a'), call('image_generation', 'b')] })).mockResolvedValueOnce(response({ content: 'Ошибка генерации.' }));
    vi.stubGlobal('fetch', fetchMock);
    vi.mocked(executeTool).mockRejectedValue(new Error('Timeout'));
    await runConversation(defaultSettings, [], new AbortController().signal, callbacks(), 'gpt-image-2');
    expect(executeTool).toHaveBeenCalledTimes(1);
    const first = JSON.parse(fetchMock.mock.calls[0][1].body);
    expect(first.tools.map((tool: { function: { name: string } }) => tool.function.name)).toEqual(['image_generation']);
    expect(first.tool_choice.function.name).toBe('image_generation');
    expect(JSON.parse(fetchMock.mock.calls[1][1].body).tools).toBeUndefined();
  });

  it('stops tool loops after three failures and requests a final answer', async () => {
    const fetchMock = vi.fn().mockImplementation(async () => fetchMock.mock.calls.length <= 3 ? response({ tool_calls: [call('current_time')] }) : response({ content: 'Не удалось получить данные.' }));
    vi.stubGlobal('fetch', fetchMock);
    vi.mocked(executeTool).mockRejectedValue(new Error('Service unavailable'));
    await runConversation(defaultSettings, [], new AbortController().signal, callbacks());
    expect(executeTool).toHaveBeenCalledTimes(3);
    expect(JSON.parse(fetchMock.mock.calls[3][1].body).tools).toBeUndefined();
  });

  it('does not store a dangling tool call when cancelled', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response({ tool_calls: [call('current_time')] })));
    const abort = new AbortController();
    vi.mocked(executeTool).mockImplementation(async () => { abort.abort(); throw abort.signal.reason; });
    const events = callbacks();
    await expect(runConversation(defaultSettings, [], abort.signal, events)).rejects.toThrow();
    expect(events.protocols).toHaveLength(0);
  });
});

describe('web and image policies', () => {
  it('selects GPT Image 2 with provider prefixes and rejects vision-only models', () => {
    expect(selectImageModel(['gpt-4o', 'gpt-image-1', 'openai/gpt-image-2'])).toBe('openai/gpt-image-2');
    expect(selectImageModel(['gpt-4.1', 'vision-model'])).toBe('');
  });
  it('builds native Neuroset positive and negative constraints', () => {
    const prompt = buildImagePrompt({ prompt: 'A forest', filters: ['watercolor'], negative_filters: ['text'] });
    expect(prompt).toContain('{watercolor}');
    expect(prompt).toContain('-{text}');
    expect(() => buildImagePrompt({ prompt: '', filters: [], negative_filters: [] })).toThrow();
  });
  it.each(['http://127.0.0.1', 'http://192.168.1.1', 'file:///test', 'https://name:secret@example.com', 'http://localhost', 'http://[::1]'])('rejects non-public reader targets: %s', url => {
    expect(() => publicUrl(url)).toThrow();
  });
});
