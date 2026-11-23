import { expect, test } from '@playwright/test';
import type { TreeNodeDto } from '../../src/types/archive';

const tree: TreeNodeDto[] = [
  { name: 'Digitalfoto', path: 'Digitalfoto', nodeType: 'asset', isValid: true, children: null },
];

test('a rejected login can be corrected to open the archive', async ({ page }) => {
  let authenticated = false;
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/auth/status' && request.method() === 'GET') {
      return route.fulfill({ json: { authenticated } });
    }
    if (path === '/api/auth/login' && request.method() === 'POST') {
      const { username, password } = request.postDataJSON();
      authenticated = username === 'browser-test' && password === 'fixture-password';
      return route.fulfill({ status: authenticated ? 200 : 401 });
    }
    if (path === '/api/archive/tree' && request.method() === 'GET' && authenticated) {
      return route.fulfill({ json: tree });
    }
    return route.abort();
  });

  await page.goto('/');
  await page.getByLabel('Benutzername').fill('browser-test');
  await page.getByLabel('Passwort', { exact: true }).fill('wrong-password');
  const login = page.getByRole('button', { name: 'Anmelden', exact: true });
  await login.click();
  await expect(page.getByText('Ungültiger Benutzername oder Passwort.')).toBeVisible();

  await page.getByLabel('Passwort', { exact: true }).fill('fixture-password');
  await login.click();
  await expect(page.getByRole('treeitem', { name: 'Digitalfoto' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Abmelden' })).toBeVisible();
});
