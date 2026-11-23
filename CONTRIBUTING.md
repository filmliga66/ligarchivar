# Contributing

See [Building from source](README.md#building-from-source) for the SDKs and
local setup. Keep pull requests focused and fill in the PR template so reviewers
know what changed, how to verify it, and whether existing installations are affected.

## Before requesting review

- For backend changes, run `dotnet test` from `src/backend`.
- For frontend changes, run `npm run lint`, `npm run build`, and
  `npm run test:browser` from `src/frontend`. See the
  [frontend README](src/frontend/README.md) for browser setup.
- For archive writes, review file ordering, collisions, and any deletions. Use a
  disposable archive copy for manual checks, never the production archive.
- For authentication or deployment changes, document new settings and any upgrade
  steps. Do not include real credentials in tests, logs, or screenshots.

## Merge checks

The `PR Validation` workflow runs the backend, frontend, and Docker smoke jobs.
Required checks are managed in the [GitHub ruleset for `main`](https://github.com/filmliga66/ligarchivar/rules/15029683),
not in the workflow. To require the Docker job for merges, a repository admin
can add `Docker (build + smoke test)` under **Require status checks to pass**.
