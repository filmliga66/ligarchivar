import { expect, test, type Page, type Route } from '@playwright/test';
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

async function openEvent(page: Page, onRename: (route: Route) => Promise<void>) {
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
    if (['/api/events/rename', '/api/events/rename-by-datetime'].includes(url.pathname) && request.method() === 'POST') {
      return onRename(route);
    }
    return route.abort();
  });

  await page.goto('/');
  await page.getByRole('treeitem', { name: event.name }).click();
}

test('renumbering uses the displayed file order and shows the new names', async ({ page }) => {
  await openEvent(page, (route) => route.fulfill({
    json: {
      ...event,
      files: [
        { ...event.files[1], name: 'f2026-0042' },
        { ...event.files[0], name: 'f2026-0043' },
      ],
    } satisfies EventDetailDto,
  }));
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

for (const [button, endpoint] of [
  ['Umbenennen', '/api/events/rename'],
  ['Nach Datum umbenennen', '/api/events/rename-by-datetime'],
]) {
  test(`${button}: confirms only the listed deletions and cancellation never executes`, async ({ page }) => {
    const requests: { path: string; body: Record<string, unknown> }[] = [];
    await openEvent(page, (route) => {
      const request = route.request();
      const body = request.postDataJSON() ?? {};
      requests.push({ path: new URL(request.url()).pathname, body });
      if (body.confirmationToken) {
        return route.fulfill({ json: { ...event, files: [{ ...event.files[0], name: 'Neu' }] } });
      }
      return route.fulfill({
        status: 202,
        json: { filesToDelete: ['RAW.DNG', 'raw_bearbeitet.dng'], confirmationToken: 'test-confirmation' },
      });
    });

    const rename = page.getByRole('button', { name: button, exact: true });
    const dialog = page.getByRole('dialog');
    await rename.click();
    await expect(dialog.getByRole('listitem')).toHaveText(['RAW.DNG', 'raw_bearbeitet.dng']);
    await expect(dialog).toContainText('endgültig gelöscht');
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();
    await expect(dialog).not.toBeVisible();
    expect(requests).toHaveLength(1);

    await rename.click();
    await dialog.getByRole('button', { name: 'DNG-Dateien endgültig löschen' }).click();
    await expect(dialog).not.toBeVisible();
    await expect(page.getByRole('cell', { name: 'Neu', exact: true })).toBeVisible();
    expect(requests).toHaveLength(3);
    expect(requests.every((request) => request.path === endpoint)).toBe(true);
    expect(requests[0].body).not.toHaveProperty('confirmationToken');
    expect(requests[2].body).toEqual({ ...requests[1].body, confirmationToken: 'test-confirmation' });
  });
}

test('a changed deletion plan requires a second confirmation and shows execution errors', async ({ page }) => {
  const tokens: (string | undefined)[] = [];
  await openEvent(page, (route) => {
    const { confirmationToken } = route.request().postDataJSON();
    tokens.push(confirmationToken);
    if (confirmationToken === 'updated') {
      return route.fulfill({
        status: 500,
        json: { message: "Datei 'new.dng' konnte nicht gelöscht werden. Es kann bereits Teiländerungen geben. Bitte die Dateiliste neu laden." },
      });
    }
    return route.fulfill({
      status: 202,
      json: confirmationToken
        ? { filesToDelete: ['new.dng'], confirmationToken: 'updated' }
        : { filesToDelete: ['original.dng'], confirmationToken: 'original' },
    });
  });

  await page.getByRole('button', { name: 'Umbenennen', exact: true }).click();
  const dialog = page.getByRole('dialog');
  const confirm = dialog.getByRole('button', { name: 'DNG-Dateien endgültig löschen' });
  await expect(dialog.getByRole('listitem')).toHaveText(['original.dng']);
  await confirm.click();
  await expect(dialog.getByRole('listitem')).toHaveText(['new.dng']);
  expect(tokens).toEqual([undefined, 'original']);

  await confirm.click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByRole('alert')).toContainText("Datei 'new.dng' konnte nicht gelöscht werden.");
  await expect(page.getByRole('alert')).toContainText('Teiländerungen');
  await expect(page.getByRole('alert')).toContainText('Dateiliste neu laden');
  expect(tokens).toEqual([undefined, 'original', 'updated']);
});
