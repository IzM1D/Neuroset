import { test, expect, type Page } from '@playwright/test';

async function setup(page: Page) {
  await page.route('https://api.example.test/v1/models', route => route.fulfill({ json: { data: [{ id: 'chat-model' }, { id: 'gpt-image-2' }] } }));
  await page.goto('./');
  await page.getByRole('button', { name: 'Подключить модель' }).click();
  await page.getByLabel('Адрес API').fill('https://api.example.test/v1');
  await page.getByLabel('Модель', { exact: true }).fill('chat-model');
  await page.getByLabel('Модель изображений', { exact: true }).fill('gpt-image-2');
  await page.getByLabel('Ключ поиска Tavily', { exact: true }).fill('tvly-test');
  await page.getByRole('button', { name: 'Сохранить настройки' }).click();
}

test('search and page results return to the model with sources and persist in history', async ({ page }) => {
  await setup(page);
  await page.route('https://api.tavily.com/search', async route => {
    expect(route.request().headers().authorization).toBe('Bearer tvly-test');
    expect(route.request().postDataJSON().query).toBe('Новости науки');
    await route.fulfill({ json: { results: [{ title: 'Источник', url: 'https://example.com/news', content: 'Новая находка.' }] } });
  });
  await page.route('https://api.tavily.com/extract', route => route.fulfill({ json: { results: [{ raw_content: 'Подробности новой находки.' }] } }));
  let calls = 0;
  await page.route('https://api.example.test/v1/chat/completions', async route => {
    const request = route.request().postDataJSON();
    calls++;
    if (calls <= 2) {
      const name = calls === 1 ? 'web_search' : 'fetch_url';
      const args = calls === 1 ? { query: 'Новости науки' } : { url: 'https://example.com/news' };
      await route.fulfill({ json: { choices: [{ message: { tool_calls: [{ id: `call-${calls}`, type: 'function', function: { name, arguments: JSON.stringify(args) } }] } }] } });
    } else {
      expect(request.messages.filter((message: { role: string }) => message.role === 'tool')).toHaveLength(2);
      await route.fulfill({ json: { choices: [{ message: { content: 'Нашёл новость: [источник](https://example.com/news).' } }] } });
    }
  });
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).fill('Найди новости науки');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.locator('.markdown')).toContainText('Нашёл новость');
  await expect(page.locator('.tool-step.done')).toHaveCount(2);
  await page.locator('.tool-step summary').first().click();
  await expect(page.locator('.sources a').first()).toHaveAttribute('href', 'https://example.com/news');
  await expect.poll(() => page.evaluate(() => localStorage.getItem('neuroset.web.v1.chats'))).toContain('protocol');
  await page.reload();
  await expect(page.locator('.tool-step.done')).toHaveCount(2);
  expect(await page.evaluate(() => localStorage.getItem('neuroset.web.v1.settings'))).not.toContain('tvly-test');
});

test('image button forces one native image call, persists PNG and requires re-enabling on retry', async ({ page }) => {
  await setup(page);
  const png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=';
  let imageRequests = 0;
  let chatRequests = 0;
  await page.route('https://api.example.test/v1/images/generations', async route => {
    imageRequests++;
    const body = route.request().postDataJSON();
    expect(body).toMatchObject({ model: 'gpt-image-2', n: 1, output_format: 'png' });
    expect(body.prompt).toContain('{watercolor}');
    expect(body.prompt).toContain('-{text}');
    await route.fulfill({ json: { data: [{ b64_json: png }] } });
  });
  await page.route('https://api.example.test/v1/chat/completions', async route => {
    chatRequests++;
    const body = route.request().postDataJSON();
    if (chatRequests === 1) {
      expect(body.tool_choice.function.name).toBe('image_generation');
      await route.fulfill({ json: { choices: [{ message: { tool_calls: [{ id: 'image-call', type: 'function', function: { name: 'image_generation', arguments: JSON.stringify({ title: 'Лес', prompt: 'A forest', filters: ['watercolor'], negative_filters: ['text'] }) } }] } }] } });
    } else {
      expect(body.tools?.some((tool: { function: { name: string } }) => tool.function.name === 'image_generation')).toBeFalsy();
      await route.fulfill({ json: { choices: [{ message: { content: 'Готово.' } }] } });
    }
  });
  await page.getByRole('button', { name: 'Создать изображение', exact: true }).click();
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).fill('Нарисуй лес');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.locator('.generated-image img')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Повторить ответ' })).toBeEnabled();
  expect(imageRequests).toBe(1);
  await expect(page.getByRole('button', { name: 'Создать изображение', exact: true })).toHaveAttribute('aria-pressed', 'false');
  await expect.poll(() => page.evaluate(() => localStorage.getItem('neuroset.web.v1.chats'))).toContain('gpt-image-2');
  await page.reload();
  await expect(page.locator('.generated-image img')).toBeVisible();
  await expect(page.locator('.generated-image img')).toHaveJSProperty('naturalWidth', 1);
  await page.getByRole('button', { name: 'Повторить ответ' }).click();
  await expect(page.getByRole('button', { name: 'Повторить ответ' })).toBeEnabled();
  expect(imageRequests).toBe(1);
});

test('real Python calculates and renders a plot in an isolated browser runtime', async ({ page }) => {
  test.skip(!process.env.PYTHON_RUNTIME_TEST, 'Optional real CDN runtime test');
  test.setTimeout(180_000);
  await setup(page);
  let calls = 0;
  await page.route('https://api.example.test/v1/chat/completions', async route => {
    calls++;
    if (calls === 1) {
      await route.fulfill({ json: { choices: [{ message: { tool_calls: [{ id: 'python-call', type: 'function', function: { name: 'run_python', arguments: JSON.stringify({ code: 'import numpy as np\nimport matplotlib.pyplot as plt\nprint(int(np.sum(np.arange(10)**2)))\nplt.plot([1,2,3], [1,4,9])' }) } }] } }] } });
    } else {
      const result = route.request().postDataJSON().messages.at(-1);
      expect(JSON.parse(result.content).stdout).toContain('285');
      await route.fulfill({ json: { choices: [{ message: { content: 'Сумма квадратов: 285. График построен.' } }] } });
    }
  });
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).fill('Посчитай и нарисуй график');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.locator('.generated-image img')).toBeVisible({ timeout: 150_000 });
  await expect(page.locator('.markdown')).toContainText('Сумма квадратов: 285');
  await expect(page.locator('iframe')).toHaveCount(0);
});
