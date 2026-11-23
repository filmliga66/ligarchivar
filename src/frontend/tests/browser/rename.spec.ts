import { expect, test } from '@playwright/test';
import type { EventDetailDto, TreeNodeDto } from '../../src/types/archive';

const event: EventDetailDto = {
  name: 'Testveranstaltung',
  path: 'Digitalfoto/2026/F/Testveranstaltung',
  filePrefix: 'f2026',
  isValid: true,
  isInPictures: true,
  files: [
    { name: 'f2026-0001', extensions: ['jpg'], properties: [], isValid: true, isOrphaned: false, lastWriteTimeUtc: '2026-06-02T10:00:00Z' },
    { name: 'f2026-0002', extensions: ['jpg'], properties: [], isValid: true, isOrphaned: false, lastWriteTimeUtc: '2026-06-01T10:00:00Z' },
  ],
};

const tree: TreeNodeDto[] = [
  { name: event.name, path: event.path, nodeType: 'event', isValid: true, children: null },
];

test('renumbering uses the displayed file order and shows the new names', async ({ page }) => {
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname === '/api/auth/status' && request.method() === 'GET') {
      return route.fulfill({ json: { authenticated: true } });
    }
    if (url.pathname === '/api/archive/tree' && request.method() === 'GET') {
      return route.fulfill({ json: tree });
    }
    if (url.pathname === '/api/events' && request.method() === 'GET' && url.searchParams.get('path') === event.path) {
      return route.fulfill({ json: event });
    }
    if (url.pathname === '/api/events/rename' && request.method() === 'POST') {
      return route.fulfill({
        json: {
          ...event,
          files: [
            { ...event.files[1], name: 'f2026-0042' },
            { ...event.files[0], name: 'f2026-0043' },
          ],
        } satisfies EventDetailDto,
      });
    }
    return route.abort();
  });

  await page.goto('/');
  await page.getByRole('treeitem', { name: event.name }).click();
  const fileNames = page.getByRole('cell', { name: /^f2026-\d{4}$/ });
  await expect(fileNames).toHaveText(['f2026-0001', 'f2026-0002']);
  await page.getByRole('button', { name: 'Datum', exact: true }).click();
  await expect(fileNames).toHaveText(['f2026-0002', 'f2026-0001']);
  await page.getByRole('spinbutton', { name: 'Startnummer' }).fill('42');

  const renameRequest = page.waitForRequest((request) => new URL(request.url()).pathname === '/api/events/rename');
  await page.getByRole('button', { name: 'Umbenennen', exact: true }).click();
  const request = await renameRequest;
  expect(request.method()).toBe('POST');
  expect(new URL(request.url()).searchParams.get('path')).toBe(event.path);
  expect(request.postDataJSON()).toEqual({ startNumber: 42, fileOrder: ['f2026-0002', 'f2026-0001'] });

  await expect(fileNames).toHaveText(['f2026-0042', 'f2026-0043']);
});
