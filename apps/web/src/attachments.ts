import { loadImage, storeImage } from './assets';
import type { Chat, ImageAttachment } from './types';

export const imageAccept = 'image/png,image/jpeg,image/webp,image/gif';
export const maxAttachments = 4;
const maxFileBytes = 10 * 1024 * 1024;
const maxTotalBytes = 20 * 1024 * 1024;

export function chatImageIds(chat: Chat): string[] {
  return [...(chat.draftAttachments || []).map(image => image.id), ...chat.messages.flatMap(message =>
    [...(message.images || []), ...(message.attachments || [])].map(image => image.id))];
}

export async function importAttachment(file: File, existing: ImageAttachment[]): Promise<ImageAttachment> {
  if (existing.length >= maxAttachments) throw new Error(`Можно прикрепить не больше ${maxAttachments} изображений к сообщению.`);
  if (!file.size) throw new Error(`Файл «${file.name}» пустой.`);
  if (file.size > maxFileBytes) throw new Error(`Файл «${file.name}» превышает 10 МБ.`);
  if (existing.reduce((total, image) => total + image.sizeBytes, file.size) > maxTotalBytes) {
    throw new Error('Общий размер изображений в сообщении не должен превышать 20 МБ.');
  }
  const bytes = new Uint8Array(await file.slice(0, 12).arrayBuffer());
  const starts = (...signature: number[]) => signature.every((byte, index) => bytes[index] === byte);
  const mediaType = starts(137, 80, 78, 71, 13, 10, 26, 10) ? 'image/png'
    : starts(255, 216, 255) ? 'image/jpeg'
    : starts(71, 73, 70, 56) && [55, 57].includes(bytes[4]) && bytes[5] === 97 ? 'image/gif'
    : starts(82, 73, 70, 70) && bytes[8] === 87 && bytes[9] === 69 && bytes[10] === 66 && bytes[11] === 80 ? 'image/webp' : '';
  if (!mediaType) throw new Error(`Файл «${file.name}»: поддерживаются PNG, JPEG, WebP и GIF.`);
  const blob = file.slice(0, file.size, mediaType);
  const url = URL.createObjectURL(blob);
  try {
    const image = new Image();
    image.src = url;
    await image.decode();
  } catch { throw new Error(`Не удалось прочитать изображение «${file.name}».`); }
  finally { URL.revokeObjectURL(url); }
  const attachment = { id: crypto.randomUUID(), name: file.name || 'Изображение', mediaType, sizeBytes: file.size };
  await storeImage(blob, attachment.id);
  return attachment;
}

export async function attachmentDataUrl(attachment: ImageAttachment): Promise<string> {
  const blob = await loadImage(attachment.id);
  if (!blob) throw new Error(`Изображение «${attachment.name}» отсутствует в хранилище браузера. Прикрепите его заново через редактирование сообщения.`);
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(new Error(`Не удалось прочитать изображение «${attachment.name}».`));
    reader.readAsDataURL(blob);
  });
}
