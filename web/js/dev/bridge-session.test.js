import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';
import { windowsBridge } from './repo.js';

const here = path.dirname(fileURLToPath(import.meta.url));
// The Windows half of the transport lives with the shell, not in web/, so this
// reads it from the source tree (it is not part of what the page itself loads).
const bridgeSource = await windowsBridge();
// Without the Windows tree in this checkout there is no bridge to run.
const windowsTest = bridgeSource === null ? test.skip : test;

/** Runs bridge.js the way WebView2 does for one document, and returns that document. */
async function newDocument(tag) {
  const source = bridgeSource;
  const posted = [];
  const handlers = {};
  const window = {
    chrome: {
      webview: {
        postMessage: (message) => posted.push(message),
        addEventListener: (type, handler) => { handlers[type] = handler; },
      },
    },
    addEventListener: () => {},
  };
  vm.runInNewContext(source, { window, crypto: { randomUUID: () => tag } });
  return {
    posted,
    request: (command) => window.webkit.messageHandlers.kytto.postMessage({ command, payload: null }),
    deliver: (data) => handlers.message({ data }),
  };
}

const settled = (promise) => {
  const state = { done: false, value: undefined };
  promise.then((value) => { state.done = true; state.value = value; });
  return state;
};

windowsTest('every request carries the document\'s own session tag', async () => {
  const page = await newDocument('tag-a');

  void page.request('state.get');
  void page.request('settings.get');

  assert.deepEqual(page.posted.map((message) => [message.id, message.session, message.command]), [
    [1, 'tag-a', 'state.get'],
    [2, 'tag-a', 'settings.get'],
  ]);
});

windowsTest('a reply that echoes the tag resolves its request', async () => {
  const page = await newDocument('tag-a');
  const pending = settled(page.request('state.get'));

  page.deliver({ id: 1, session: 'tag-a', reply: '{"ok":true}' });
  await Promise.resolve();

  assert.equal(pending.done, true);
  assert.equal(pending.value, '{"ok":true}');
});

windowsTest('a reply from another document is dropped, even with a matching id', async () => {
  // Request ids restart at 1 in every document. A slow command finishing after a
  // reload or a rebuilt web view must not answer the new page's request #1.
  const oldPage = await newDocument('tag-old');
  const newPage = await newDocument('tag-new');
  void oldPage.request('servers.setEnabled');
  const pending = settled(newPage.request('state.get'));

  newPage.deliver({ id: 1, session: 'tag-old', reply: '{"ok":true,"data":"someone else\'s answer"}' });
  await Promise.resolve();
  assert.equal(pending.done, false);

  // And the right reply still gets through afterwards.
  newPage.deliver({ id: 1, session: 'tag-new', reply: '{"ok":true}' });
  await Promise.resolve();
  assert.equal(pending.done, true);
  assert.equal(pending.value, '{"ok":true}');
});

windowsTest('a reply with no tag is dropped', async () => {
  const page = await newDocument('tag-a');
  const pending = settled(page.request('state.get'));

  page.deliver({ id: 1, reply: '{"ok":true}' });
  page.deliver({ id: 1, session: null, reply: '{"ok":true}' });
  await Promise.resolve();

  assert.equal(pending.done, false);
});

windowsTest('events pushed by the shell, which carry no request id, are left alone', async () => {
  const page = await newDocument('tag-a');
  const pending = settled(page.request('state.get'));

  page.deliver({ event: 'configs.changed' });
  page.deliver(null);
  await Promise.resolve();

  assert.equal(pending.done, false);
});
