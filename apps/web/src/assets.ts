import type { ImageArtifact } from './types';

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('neuroset.web.assets', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('images');
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(new Error('Не удалось открыть хранилище изображений.'));
  });
}

export async function saveImage(blob: Blob, title: string, model: string, kind: ImageArtifact['kind'] = 'image'): Promise<ImageArtifact> {
  const artifact = { id: crypto.randomUUID(), title, model, kind };
  await storeImage(blob, artifact.id);
  return artifact;
}

export async function storeImage(blob: Blob, id: string): Promise<void> {
  const database = await openDatabase();
  try {
    await new Promise<void>((resolve, reject) => {
      const transaction = database.transaction('images', 'readwrite');
      transaction.objectStore('images').put(blob, id);
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(new Error('Не удалось сохранить изображение: хранилище браузера заполнено или недоступно.'));
      transaction.onabort = transaction.onerror;
    });
  } finally { database.close(); }
}

export async function loadImage(id: string): Promise<Blob | undefined> {
  const database = await openDatabase();
  try {
    return await new Promise<Blob | undefined>((resolve, reject) => {
      const request = database.transaction('images').objectStore('images').get(id);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  } finally { database.close(); }
}

export async function removeImages(ids: string[]) {
  if (!ids.length) return;
  const database = await openDatabase();
  try {
    await new Promise<void>((resolve, reject) => {
      const transaction = database.transaction('images', 'readwrite');
      for (const id of ids) transaction.objectStore('images').delete(id);
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
    });
  } finally { database.close(); }
}

export function pngBlob(encoded: string): Blob {
  if (!encoded || encoded.length > 45_000_000) throw new Error('Изображение пустое или превышает 32 МБ.');
  const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
  if (bytes.length > 32 * 1024 * 1024 || ![137, 80, 78, 71, 13, 10, 26, 10].every((byte, index) => bytes[index] === byte)) {
    throw new Error('API не вернул изображение PNG.');
  }
  return new Blob([bytes], { type: 'image/png' });
}
