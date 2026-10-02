import { checkResponse, endpoint, headers } from './api';
import { pngBlob, saveImage } from './assets';
import type { Settings } from './types';

export const isImageModel = (model: string) => /^(?:[a-z0-9_.-]+\/)?(?:gpt-image-\d+(?:\.\d+)?(?:-[a-z0-9.]+)*|chatgpt-image-latest)$/i.test(model);

export function selectImageModel(models: string[]) {
  return models.filter(isImageModel).sort((a, b) => {
    const rank = (model: string) => /(?:^|\/)gpt-image-2$/i.test(model) ? 2 : /(?:^|\/)gpt-image-2-/i.test(model) ? 1 : 0;
    return rank(b) - rank(a) || b.localeCompare(a);
  })[0] || '';
}

export function buildImagePrompt(args: Record<string, unknown>) {
  if (typeof args.prompt !== 'string' || !args.prompt.trim() || args.prompt.length > 24000) throw new Error('Некорректный промпт изображения.');
  const filters = (value: unknown) => {
    if (!Array.isArray(value) || value.length > 24 || value.some(item => typeof item !== 'string' || item.length > 240)) throw new Error('Некорректные визуальные ограничения.');
    return value as string[];
  };
  const prompt = `${args.prompt.trim()}\n\nVisual constraints: {trait} means include; -{trait} means exclude any occurrence. Apply all inclusion constraints together.\n${filters(args.filters).map(item => `{${item.trim()}}`).join('\n')}\nExclude these visual traits from the result:\n${filters(args.negative_filters).map(item => `-{${item.trim()}}`).join('\n')}`;
  if (prompt.length > 32000) throw new Error('Промпт изображения превышает 32000 символов.');
  return prompt;
}

export async function generateImage(settings: Settings, model: string, args: Record<string, unknown>, signal: AbortSignal) {
  if (!isImageModel(model)) throw new Error('Выберите GPT Image модель в настройках.');
  if (typeof args.title !== 'string' || !args.title.trim() || args.title.length > 160) throw new Error('Некорректное название изображения.');
  const size = args.size ?? 'auto';
  const quality = args.quality ?? 'auto';
  if (!['auto', '1024x1024', '1536x1024', '1024x1536'].includes(String(size)) || !['auto', 'low', 'medium', 'high'].includes(String(quality))) throw new Error('Некорректный размер или качество изображения.');
  const response = await fetch(endpoint(settings.baseUrl, 'images/generations'), {
    method: 'POST', headers: headers(settings.apiKey), signal,
    body: JSON.stringify({ model, prompt: buildImagePrompt(args), n: 1, size, quality, output_format: 'png' }),
  });
  await checkResponse(response);
  const data = await response.json();
  if (data.error) throw new Error(data.error.message || 'Ошибка генерации изображения.');
  if (typeof data.data?.[0]?.b64_json !== 'string') throw new Error('API не вернул изображение в формате b64_json.');
  return saveImage(pngBlob(data.data[0].b64_json), args.title.trim(), model);
}
