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

