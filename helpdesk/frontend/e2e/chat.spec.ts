import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

const reply = {
  conversationId: 'c1',
  actionLane: 'PUBLIC_TECH',
  responseText: 'Try holding the power button for **15 seconds**.',
  metadata: {},
};

async function mockApi(page: Page) {
  await page.route('**/readyz', (route) => route.fulfill({ status: 200, body: 'Healthy' }));
  // No SignalR in these tests: the app falls back to the REST endpoint.
  await page.route('**/hubs/**', (route) => route.abort());
  await page.route('**/api/v1/chat/message', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(reply) }),
  );
  await page.route('**/api/v1/chat/attachments/config', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        maxFileBytes: 10 * 1024 * 1024,
        maxFilesPerMessage: 5,
        maxFilesPerConversation: 20,
        maxTotalBytesPerConversation: 25 * 1024 * 1024,
        allowedExtensions: ['.png', '.pdf', '.txt', '.log', '.csv', '.docx'],
        pasteMinChars: 1500,
        pasteMinLines: 25,
      }),
    }),
  );
}

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  expect(results.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ') + ' ' + (n.any[0]?.message ?? '')).join('; ')})`)).toEqual([]);
}

test.beforeEach(async ({ page }) => {
  await mockApi(page);
});

test('the empty chat is accessible and reports service status', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('status').filter({ hasText: 'Service ready' })).toBeVisible();
  await expectAccessible(page);
});

test('sends a message and renders the reply accessibly', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('textbox').fill('My laptop will not turn on');
  await page.getByRole('button', { name: /send/i }).click();

  await expect(page.getByText('15 seconds')).toBeVisible();
  await expect(page.getByText('My laptop will not turn on')).toBeVisible();
  await expectAccessible(page);
});

test('shows an API error without exposing details', async ({ page }) => {
  await page.route('**/api/v1/chat/message', (route) =>
    route.fulfill({
      status: 500,
      contentType: 'application/problem+json',
      body: JSON.stringify({ title: 'Internal Server Error', status: 500, detail: 'stack trace here' }),
    }),
  );
  await page.goto('/');
  await page.getByRole('textbox').fill('Hello');
  await page.getByRole('button', { name: /send/i }).click();

  await expect(page.getByRole('alert')).toHaveText('Internal Server Error');
  await expect(page.getByText('stack trace here')).toHaveCount(0);
});

test('attaches a file, sends it with the message and shows it accessibly', async ({ page }) => {
  let uploaded: string | undefined;
  await page.route('**/api/v1/chat/attachments', (route) => {
    uploaded = route.request().postData() ?? '';
    return route.fulfill({
      status: 201,
      contentType: 'application/json',
      body: JSON.stringify({
        id: '11111111-1111-1111-1111-111111111111',
        name: 'app.log',
        contentType: 'text/plain',
        size: 12,
        kind: 'text',
        token: 'token',
      }),
    });
  });
  let chatBody: { attachments?: { name: string }[]; messageText?: string } | undefined;
  await page.route('**/api/v1/chat/message', (route) => {
    chatBody = route.request().postDataJSON();
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(reply) });
  });

  await page.goto('/');
  await page.getByLabel('Choose files to attach').setInputFiles({
    name: 'app.log',
    mimeType: 'text/plain',
    buffer: Buffer.from('hello world!'),
  });
  await expect(page.getByRole('button', { name: 'Remove app.log' })).toBeVisible();
  await expectAccessible(page);

  await page.getByRole('textbox').fill('What is wrong?');
  await page.getByRole('button', { name: /send/i }).click();

  await expect(page.getByRole('list', { name: 'Attachments' }).getByText('app.log')).toBeVisible();
  await expect(page.getByText('15 seconds')).toBeVisible();
  expect(uploaded).toContain('app.log');
  expect(chatBody?.messageText).toBe('What is wrong?');
  expect(chatBody?.attachments?.map((a) => a.name)).toEqual(['app.log']);
  await expectAccessible(page);
});

test('a pasted log becomes an attachment that can be turned back into text', async ({ page }) => {
  await page.route('**/api/v1/chat/attachments', (route) =>
    route.fulfill({
      status: 201,
      contentType: 'application/json',
      body: JSON.stringify({
        id: '22222222-2222-2222-2222-222222222222',
        name: 'pasted-log.txt',
        contentType: 'text/plain',
        size: 600,
        kind: 'text',
        token: 'token',
      }),
    }),
  );
  const log = Array.from({ length: 8 }, (_, i) => `2026-10-24 12:00:0${i} ERROR something failed (${i})`).join('\n');

  await page.goto('/');
  await page.getByRole('textbox').evaluate((el, text) => {
    const data = new DataTransfer();
    data.setData('text/plain', text);
    el.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  }, log);

  await expect(page.getByText('Pasted text was added as an attachment.')).toBeVisible();
  await expect(page.getByRole('textbox')).toHaveValue('');
  await expectAccessible(page);

  await page.getByRole('button', { name: 'Paste as text instead' }).click();
  await expect(page.getByRole('textbox')).toHaveValue(log);
  await expect(page.getByRole('list', { name: 'Attachments to send' })).toHaveCount(0);
});
