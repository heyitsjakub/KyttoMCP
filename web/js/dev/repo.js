// Where the tests find files that live outside `web/`.
//
// `web/` is shared by the macOS and Windows apps, and each platform's own
// documentation and native glue sits beside it in `macos/` or `windows/`. A
// checkout may hold only one of them (sparse checkout), so a platform that is not
// on disk is skipped rather than failing — the contract is checked against every
// platform that is there.

import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const repoRoot = path.resolve(here, '..', '..', '..');

async function readIfPresent(...parts) {
  try {
    return await readFile(path.join(repoRoot, ...parts), 'utf8');
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
}

/** `docs/ipc.md` of every platform present in this checkout. */
export async function ipcDocs() {
  const docs = [];
  for (const platform of ['macos', 'windows']) {
    const text = await readIfPresent(platform, 'docs', 'ipc.md');
    if (text !== null) docs.push({ platform, text });
  }
  return docs;
}

/** The WebView2 half of the transport, or null without the Windows tree. */
export const windowsBridge = () => readIfPresent('windows', 'src', 'Kytto.App', 'Web', 'bridge.js');
