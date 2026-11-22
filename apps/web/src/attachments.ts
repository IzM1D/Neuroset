import { loadImage, storeImage } from './assets';
import type { Chat, FileAttachment } from './types';

export function isPreviewImage(mediaType: string): boolean {
  return ['image/png', 'image/jpeg', 'image/webp', 'image/gif'].includes(mediaType);
}

export function chatImageIds(chat: Chat): string[] {
  return [...(chat.draftAttachments || []).map(image => image.id), ...chat.messages.flatMap(message =>
    [...(message.images || []), ...(message.attachments || [])].map(image => image.id))];
}

export async function importAttachment(file: File): Promise<FileAttachment> {
  const bytes = new Uint8Array(await file.slice(0, 12).arrayBuffer());
  const starts = (...signature: number[]) => signature.every((byte, index) => bytes[index] === byte);
  const imageType = starts(137, 80, 78, 71, 13, 10, 26, 10) ? 'image/png'
    : starts(255, 216, 255) ? 'image/jpeg'
    : starts(71, 73, 70, 56) && [55, 57].includes(bytes[4]) && bytes[5] === 97 ? 'image/gif'
    : starts(82, 73, 70, 70) && bytes[8] === 87 && bytes[9] === 69 && bytes[10] === 66 && bytes[11] === 80 ? 'image/webp' : '';
  const extensionTypes: Record<string, string> = { txt: 'text/plain', pdf: 'application/pdf', zip: 'application/zip' };
  const extension = /\.([a-z0-9]+)$/i.exec(file.name)?.[1].toLowerCase();
  const inferredType = extension && Object.hasOwn(extensionTypes, extension) ? extensionTypes[extension] : '';
  const mediaType = imageType || (!isPreviewImage(file.type) && file.type) || inferredType || 'application/octet-stream';
  const blob = file.slice(0, file.size, mediaType);
  const attachment = { id: crypto.randomUUID(), name: file.name || 'Файл', mediaType, sizeBytes: file.size };
  await storeImage(blob, attachment.id);
  return attachment;
}

export async function attachmentDataUrl(attachment: FileAttachment): Promise<string> {
  const blob = await loadImage(attachment.id);
  if (!blob) throw new Error(`Файл «${attachment.name}» отсутствует в хранилище браузера. Прикрепите его заново через редактирование сообщения.`);
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(new Error(`Не удалось прочитать файл «${attachment.name}».`));
    reader.readAsDataURL(blob);
  });
}
