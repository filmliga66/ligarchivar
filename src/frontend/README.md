# LigArchivar frontend

Use the Node.js version in [`.nvmrc`](.nvmrc). From this directory:

```sh
npm ci
npm run dev
```

The dev server proxies API requests to the local backend. See the
[root README](../../README.md#building-from-source) for backend setup.

## Checks

```sh
npm run lint
npm run build
npx playwright install chromium
npm run test:browser
```

Install Chromium once after installing dependencies (use `npx playwright install
--with-deps chromium` on Linux if browser system libraries are missing). The
tests serve the built frontend locally and intercept HTTP API requests. They do
not need a running backend, real credentials, or an archive. Run `npm run build`
again after changing frontend code.
