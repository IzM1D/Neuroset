import { expect, test, type Page } from '@playwright/test';

async function configure(page: Page) {
  await page.route('https://api.example.test/v1/models', route => route.fulfill({ json: { data: [{ id: 'test-model' }, { id: 'gpt-image-2' }] } }));
  await page.goto('./');
  await page.getByRole('button', { name: 'Подключить модель' }).click();
  await page.getByLabel('Адрес API').fill('https://api.example.test/v1');
  await page.getByLabel('API-ключ', { exact: true }).fill('test-key');
  await page.getByLabel('Модель', { exact: true }).fill('test-model');
  await page.getByRole('button', { name: 'Сохранить настройки' }).click();
}

test('streaming, context, persistence, editing, retry, Markdown and copy', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  const requests: { messages: { role: string; content: string }[] }[] = [];
  await page.route('https://api.example.test/v1/chat/completions', async route => {
    requests.push(route.request().postDataJSON());
    expect(route.request().headers().authorization).toBe('Bearer test-key');
    await route.fulfill({ contentType: 'text/event-stream', body: `data: ${JSON.stringify({ choices: [{ delta: { content: 'Привет! **Ответ**\n\n```js\nconst answer = 42;\n```' }, finish_reason: 'stop' }] })}\n\ndata: [DONE]\n\n` });
  });
  await configure(page);
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).fill('Первый вопрос');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.locator('.message-assistant strong')).toHaveText('Ответ');
  await page.getByRole('button', { name: 'Копировать код' }).click();
  expect((await page.evaluate(() => navigator.clipboard.readText())).replace(/\r\n/g, '\n')).toBe('const answer = 42;\n');
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).fill('Второй вопрос');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.locator('.message-assistant')).toHaveCount(2);
  await expect(page.getByRole('button', { name: 'Повторить ответ' })).toBeEnabled();
  expect(requests[1].messages.filter(message => message.role !== 'system').map(message => message.role)).toEqual(['user', 'assistant', 'user']);
  await page.getByRole('button', { name: 'Редактировать сообщение' }).first().click();
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).fill('Изменённый вопрос');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.getByRole('button', { name: 'Повторить ответ' })).toBeEnabled();
  expect(requests[2].messages.filter(message => message.role !== 'system')).toEqual([{ role: 'user', content: 'Изменённый вопрос' }]);
  await expect(page.locator('.message-user')).toHaveCount(1);
  await page.getByRole('button', { name: 'Повторить ответ' }).click();
  await expect(page.getByRole('button', { name: 'Повторить ответ' })).toBeEnabled();
  expect(requests).toHaveLength(4);
  await expect.poll(() => page.evaluate(() => localStorage.getItem('neuroset.web.v1.chats'))).toContain('Изменённый вопрос');
  await page.reload();
  await expect(page.locator('.user-bubble')).toHaveText('Изменённый вопрос');
  expect(await page.evaluate(() => localStorage.getItem('neuroset.web.v1.settings'))).not.toContain('test-key');
  await page.getByRole('button', { name: 'Скачать чат' }).click();
});

test('errors are recoverable, cancellation releases composer, and unsafe HTML is inert', async ({ page }) => {
  let mode = 'error';
  await page.route('https://api.example.test/v1/chat/completions', async route => {
    if (mode === 'error') await route.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ error: { message: 'Неверный ключ' } }) });
    else if (mode === 'slow') await new Promise<void>(resolve => {
      route.request().response().catch(() => {}).finally(() => resolve());
      setTimeout(() => { route.abort().catch(() => {}); resolve(); }, 3000);
    });
    else await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ choices: [{ message: { content: '<script>window.hacked=true</script>\n\n[опасно](javascript:alert(1))\n\nГотово' } }] }) });
  });
  await configure(page);
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).fill('Тест');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.getByRole('alert')).toContainText('Неверный ключ');
  mode = 'slow';
  await page.getByRole('button', { name: 'Повторить ответ' }).click();
  await page.getByRole('button', { name: 'Остановить ответ' }).click();
  await expect(page.getByText('Генерация остановлена')).toBeVisible();
  mode = 'ok';
  await page.getByRole('button', { name: 'Повторить ответ' }).click();
  await expect(page.locator('.markdown')).toContainText('Готово');
  expect(await page.evaluate(() => 'hacked' in window)).toBe(false);
  await expect(page.locator('.markdown a[href^="javascript:"]')).toHaveCount(0);
});

test('mobile navigation, model discovery, themes and deleting chats', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.route('https://api.example.test/v1/models', route => route.fulfill({ json: { data: [{ id: 'test-model' }] } }));
  await configure(page);
  await expect(page.getByRole('heading', { name: 'С чего начнём?' })).toBeVisible();
  await page.getByRole('button', { name: 'Открыть меню' }).click();
  await page.getByRole('button', { name: 'Настройки', exact: true }).click();
  await page.getByRole('button', { name: 'Загрузить', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('Доступно моделей: 2');
  await page.getByLabel('Оформление').selectOption('light');
  await page.getByRole('button', { name: 'Сохранить настройки' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.getByRole('button', { name: 'Удалить: Новый чат' }).click();
  await page.getByRole('button', { name: 'Удалить', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'С чего начнём?' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('production assets load under a GitHub Pages repository path', async ({ page }) => {
  await page.goto('/neuroset/');
  await expect(page.getByRole('heading', { name: 'С чего начнём?' })).toBeVisible();
});

const attachmentPng = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=';
const imageFile = (name: string) => ({ name, mimeType: 'image/png', buffer: Buffer.from(attachmentPng, 'base64') });

async function storedImageCount(page: Page) {
  return page.evaluate(() => new Promise<number>((resolve, reject) => {
    const request = indexedDB.open('neuroset.web.assets', 1);
    request.onsuccess = () => {
      const database = request.result;
      const count = database.transaction('images').objectStore('images').count();
      count.onsuccess = () => { resolve(count.result); database.close(); };
      count.onerror = () => { reject(count.error); database.close(); };
    };
    request.onerror = () => reject(request.error);
  }));
}

test('image attachments persist, reach the API without text, survive retry and editing, and are cleaned up', async ({ page }) => {
  const requests: { messages: { role: string; content: unknown }[] }[] = [];
  await page.route('https://api.example.test/v1/chat/completions', async route => {
    requests.push(route.request().postDataJSON());
    await route.fulfill({ json: { choices: [{ message: { content: 'Вижу изображение.' } }] } });
  });
  await configure(page);
  const picker = page.getByLabel('Загрузить изображения');
  const fileChooser = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Прикрепить изображения', exact: true }).click();
  await (await fileChooser).setFiles([imageFile('photo.png'), imageFile('remove.png')]);
  await expect(page.locator('.composer .attachments img')).toHaveCount(2);
  await page.getByRole('button', { name: 'Убрать remove.png' }).click();
  await expect.poll(() => storedImageCount(page)).toBe(1);
  await expect.poll(() => page.evaluate(() => localStorage.getItem('neuroset.web.v1.chats'))).toContain('photo.png');
  await page.reload();
  await expect(page.locator('.composer img[alt="photo.png"]')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Отправить сообщение' })).toBeEnabled();
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.locator('.message-assistant')).toContainText('Вижу изображение.');
  const imageContent = [{ type: 'image_url', image_url: { url: `data:image/png;base64,${attachmentPng}` } }];
  expect(requests[0].messages.find(message => message.role === 'user')?.content).toEqual(imageContent);
  await expect(page.locator('.message-user img')).toHaveJSProperty('naturalWidth', 1);
  await page.getByRole('button', { name: 'Повторить ответ' }).click();
  await expect(page.getByRole('button', { name: 'Повторить ответ' })).toBeEnabled();
  expect(requests[1].messages.find(message => message.role === 'user')?.content).toEqual(imageContent);
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).fill('Что было на картинке?');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.getByRole('button', { name: 'Повторить ответ' })).toBeEnabled();
  expect(requests[2].messages.find(message => message.role === 'user')?.content).toEqual(imageContent);
  await expect.poll(() => page.evaluate(() => localStorage.getItem('neuroset.web.v1.chats'))).toContain('Что было на картинке?');
  expect(await page.evaluate(() => localStorage.getItem('neuroset.web.v1.chats'))).not.toContain('data:image');
  await page.reload();
  await expect(page.locator('.message-user img')).toHaveJSProperty('naturalWidth', 1);
  await page.getByRole('button', { name: 'Редактировать сообщение' }).first().click();
  await expect(page.locator('.composer img')).toHaveCount(1);
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).fill('Опиши картинку');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.getByRole('button', { name: 'Повторить ответ' })).toBeEnabled();
  expect(requests[3].messages.find(message => message.role === 'user')?.content).toEqual([{ type: 'text', text: 'Опиши картинку' }, ...imageContent]);
  await expect.poll(() => storedImageCount(page)).toBe(1);
  await page.getByRole('button', { name: 'Редактировать сообщение' }).first().click();
  await page.getByRole('button', { name: 'Убрать photo.png' }).click();
  // Removing an attachment from the edit draft must preserve the original until sending.
  await expect.poll(() => storedImageCount(page)).toBe(1);
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.getByRole('button', { name: 'Повторить ответ' })).toBeEnabled();
  expect(requests[4].messages.find(message => message.role === 'user')?.content).toBe('Опиши картинку');
  await expect.poll(() => storedImageCount(page)).toBe(0);
  await picker.setInputFiles(imageFile('draft.png'));
  await expect(page.locator('.composer img')).toHaveCount(1);
  await page.locator('.chat-row').first().hover();
  await page.getByRole('button', { name: /^Удалить: / }).first().click();
  await page.getByRole('button', { name: 'Удалить', exact: true }).click();
  await expect.poll(() => storedImageCount(page)).toBe(0);
});

test('paste and drop attach images, drafts stay in their chat, and mobile controls fit', async ({ page }) => {
  await configure(page);
  await page.evaluate(encoded => {
    const data = new DataTransfer();
    data.items.add(new File([Uint8Array.from(atob(encoded), char => char.charCodeAt(0))], 'pasted.png', { type: 'image/png' }));
    document.querySelector('textarea#message-input')!.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  }, attachmentPng);
  await expect(page.locator('.composer img[alt="pasted.png"]')).toBeVisible();
  await page.evaluate(encoded => {
    const data = new DataTransfer();
    data.items.add(new File([Uint8Array.from(atob(encoded), char => char.charCodeAt(0))], 'dropped.png', { type: 'image/png' }));
    const panel = document.querySelector('.main-panel')!;
    panel.dispatchEvent(new DragEvent('dragenter', { dataTransfer: data, bubbles: true, cancelable: true }));
    panel.dispatchEvent(new DragEvent('drop', { dataTransfer: data, bubbles: true, cancelable: true }));
  }, attachmentPng);
  await expect(page.locator('.composer .attachments img')).toHaveCount(2);
  await expect(page.locator('.image-drop-hint')).toHaveCount(0);
  await page.getByRole('button', { name: 'Новый чат', exact: true }).first().click();
  await expect(page.locator('.composer .attachments img')).toHaveCount(0);
  await expect(page.locator('.chat-select')).toHaveCount(2);
  await page.locator('.chat-select').last().click();
  await expect(page.locator('.composer .attachments img')).toHaveCount(2);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Скрыть боковую панель' }).click();
  await expect(page.getByRole('button', { name: 'Прикрепить изображения', exact: true })).toBeInViewport();
  await expect(page.getByRole('button', { name: 'Отправить сообщение' })).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('attachment validation rejects unsupported, damaged, oversized files and excess images', async ({ page }) => {
  await configure(page);
  const picker = page.getByLabel('Загрузить изображения');
  await picker.setInputFiles({ name: 'unsafe.svg', mimeType: 'image/svg+xml', buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"/>') });
  await expect(page.getByRole('status')).toContainText('поддерживаются PNG, JPEG, WebP и GIF');
  await picker.setInputFiles({ name: 'large.png', mimeType: 'image/png', buffer: Buffer.alloc(10 * 1024 * 1024 + 1) });
  await expect(page.getByRole('status')).toContainText('превышает 10 МБ');
  await picker.setInputFiles({ name: 'broken.png', mimeType: 'image/png', buffer: Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]) });
  await expect(page.getByRole('status')).toContainText('Не удалось прочитать изображение');
  await expect(page.getByRole('button', { name: 'Отправить сообщение' })).toBeDisabled();
  await picker.setInputFiles([1, 2, 3, 4, 5].map(index => imageFile(`${index}.png`)));
  await expect(page.locator('.composer .attachments img')).toHaveCount(4);
  await expect(page.getByRole('status')).toContainText('не больше 4 изображений');
  await expect.poll(() => storedImageCount(page)).toBe(4);
});
