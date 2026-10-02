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
