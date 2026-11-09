import { expect, test } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';

test('Ctrl+V accepts a copied image file from the Windows file clipboard', async ({ page }, testInfo) => {
  test.skip(process.platform !== 'win32' || !process.env.WINDOWS_CLIPBOARD_TEST, 'Optional Windows integration: WINDOWS_CLIPBOARD_TEST=1 with --headed');
  const file = testInfo.outputPath('copied-file.png');
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=', 'base64'));
  await page.goto('./');
  await page.evaluate(() => document.addEventListener('paste', event => {
    (window as unknown as { clipboardShape: unknown }).clipboardShape = {
      types: Array.from(event.clipboardData?.types || []),
      files: Array.from(event.clipboardData?.files || []).map(file => ({ name: file.name, type: file.type })),
      items: Array.from(event.clipboardData?.items || []).map(item => ({ kind: item.kind, type: item.type })),
    };
  }, { once: true }));
  execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', 'Set-Clipboard -LiteralPath $env:NEUROSET_CLIPBOARD_FIXTURE'], {
    env: { ...process.env, NEUROSET_CLIPBOARD_FIXTURE: file },
  });
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).press('Control+V');
  const clipboardShape = await page.evaluate(() => (window as unknown as { clipboardShape: unknown }).clipboardShape);
  await expect(page.locator('.composer .attachments img'), JSON.stringify(clipboardShape)).toHaveCount(1);
  await page.evaluate(() => (document.activeElement as HTMLElement).blur());
  await expect(page.locator('body')).toBeFocused();
  await page.keyboard.press('Control+V');
  await expect(page.locator('.composer .attachments img')).toHaveCount(2);
});

test('file clipboard fallbacks accept missing MIME types once and leave other editors alone', async ({ page }) => {
  await page.goto('./');
  const pasteFile = async (target: string, fileListOnly: boolean) => page.evaluate(({ target, fileListOnly }) => {
    const data = new DataTransfer();
    const encoded = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=';
    data.items.add(new File([Uint8Array.from(atob(encoded), char => char.charCodeAt(0))], 'clipboard-file', { type: '' }));
    const event = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
    if (fileListOnly) Object.defineProperty(data, 'items', { value: [] });
    document.querySelector(target)!.dispatchEvent(event);
    return event.defaultPrevented;
  }, { target, fileListOnly });
  expect(await pasteFile('textarea#message-input', false)).toBe(true);
  await expect(page.locator('.composer .attachments img')).toHaveCount(1);
  expect(await pasteFile('body', true)).toBe(true);
  await expect(page.locator('.composer .attachments img')).toHaveCount(2);
  expect(await pasteFile('input[aria-label="Поиск по чатам"]', false)).toBe(false);
  await page.getByRole('button', { name: 'Подключить модель', exact: true }).click();
  expect(await pasteFile('body', false)).toBe(false);
  await page.getByLabel('Адрес API').fill('https://api.example.test/v1');
  expect(await pasteFile('.settings-body input[type="url"]', false)).toBe(false);
  await expect(page.locator('.composer .attachments img')).toHaveCount(2);
});

test('native Ctrl+V pastes an image with the composer focused or the page body focused', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.goto('./');
  await page.evaluate(async () => {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 2;
    canvas.getContext('2d')!.fillRect(0, 0, 2, 2);
    const blob = await new Promise<Blob>(resolve => canvas.toBlob(blob => resolve(blob!), 'image/png'));
    await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })]);
  });
  await page.getByRole('textbox', { name: 'Сообщение', exact: true }).press('Control+V');
  await expect(page.locator('.composer .attachments img')).toHaveCount(1);
  await page.evaluate(() => (document.activeElement as HTMLElement).blur());
  await expect(page.locator('body')).toBeFocused();
  await page.keyboard.press('Control+V');
  await expect(page.locator('.composer .attachments img')).toHaveCount(2);
});
