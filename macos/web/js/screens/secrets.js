// Secrets (§6).
//
// What this screen can honestly offer: it knows every file a token is sitting
// in, it can change it in all of them at once, and it says when a file is
// readable by others or tracked by Git.
//
// What it does not pretend: the value still lives in the config file. No MCP
// client understands a reference, so replacing the value with one would either
// break every server or make them depend on Kytto staying installed. That is
// stated on screen rather than glossed over.

import { el, button } from '../dom.js';

/**
 * What this platform calls the store Kytto keeps its own copy in.
 *
 * Reported by `app.info`, never written into this file: the same page runs on a
 * Mac and on Windows, where the store is Credential Manager, and a hardcoded
 * "Keychain" here would be a name of an OS crossing the boundary (§3.2). The
 * fallback is the neutral phrase rather than either product.
 */
function storeName(state) {
  return state.app?.secretStoreDisplayName ?? 'the system credential store';
}

export function renderSecrets(state) {
  const panel = el('div', 'table-scroll');

  if (state.secrets.length === 0) {
    const empty = el('div', 'placeholder');
    empty.append(el('p', null, 'No API keys or tokens found in your configurations.'));
    empty.append(
      el('p', 'muted', 'Kytto looks at environment variables whose name or value suggests a credential.'),
    );
    panel.append(empty);
    return panel;
  }

  panel.append(
    el(
      'p',
      'panel-note',
      'These values are stored in your client config files as plain text — that is the only form MCP clients read. Kytto keeps track of where each one is so you can change it everywhere at once.',
    ),
  );

  const list = el('div', 'secret-list');
  for (const secret of state.secrets) {
    list.append(secretRow(state, secret));
  }
  panel.append(list);
  return panel;
}

function secretRow(state, secret) {
  const row = el('section', 'secret');

  const head = el('div', 'secret-head');
  head.append(el('span', 'secret-key mono', secret.key));
  if (secret.isShared) {
    const badge = el('span', 'badge', `${secret.usages.length} places`);
    badge.title = 'The same value is in more than one client. Rotating updates all of them.';
    head.append(badge);
  }
  if (secret.isInSecretStore) {
    const badge = el('span', 'badge', `in ${storeName(state)}`);
    badge.title = "Kytto keeps a copy, so this value can be restored if it is lost.";
    head.append(badge);
  }
  head.append(el('span', 'spacer'));

  const revealed = state.revealedSecrets[secret.id];
  head.append(el('span', 'secret-value mono', revealed ?? secret.maskedValue));
  head.append(
    button(revealed ? 'Hide' : 'Reveal', revealed ? 'hide-secret' : 'reveal-secret', {
      className: 'button subtle',
      dataset: { secretId: secret.id },
    }),
  );
  row.append(head);

  const usages = el('ul', 'plain-list');
  for (const usage of secret.usages) {
    const item = el('li');
    item.append(
      el(
        'span',
        null,
        `${state.clients.find((client) => client.id === usage.clientID)?.displayName ?? usage.clientID} · ${usage.serverName}`,
      ),
    );
    item.append(el('span', 'mono muted', usage.pathDisplay));
    usages.append(item);
  }
  row.append(usages);

  for (const item of secret.exposure) {
    const warning = el('div', 'secret-exposure');
    warning.append(el('span', 'exposure-detail', item.detail));
    if (item.kind === 'readableByOthers') {
      const client = secret.usages.find((usage) => usage.pathDisplay === item.pathDisplay);
      if (client) {
        warning.append(
          button('Restrict to me', 'restrict-permissions', {
            className: 'button subtle',
            dataset: { clientId: client.clientID },
          }),
        );
      }
    }
    row.append(warning);
  }

  const actions = el('div', 'secret-actions');
  actions.append(
    button('Rotate…', 'rotate-secret', {
      className: 'button',
      dataset: { secretId: secret.id },
    }),
  );
  actions.append(
    secret.isInSecretStore
      ? button(`Remove from ${storeName(state)}`, 'forget-secret', {
          className: 'button subtle',
          dataset: { secretId: secret.id },
        })
      : button(`Keep a copy in ${storeName(state)}`, 'adopt-secret', {
          className: 'button subtle',
          dataset: { secretId: secret.id },
        }),
  );
  row.append(actions);

  return row;
}

/// Rotation is a write to several files at once, so it says which ones first.
export function renderRotateSheet(state) {
  const secret = state.secrets.find((entry) => entry.id === state.sheet.secretId);
  if (!secret) return null;

  const { sheet } = { sheet: state.sheet };
  const body = [];

  if (sheet.error) {
    const errors = el('div', 'form-errors');
    errors.append(el('p', null, sheet.error));
    body.push(errors);
  }

  body.push(el('p', null, `Set a new value for ${secret.key}.`));

  const list = el('ul', 'plain-list');
  for (const usage of secret.usages) {
    const item = el('li');
    item.append(el('span', null, usage.serverName));
    item.append(el('span', 'mono muted', usage.pathDisplay));
    list.append(item);
  }
  body.push(list);

  const input = el('input', 'text-input');
  input.type = 'password';
  input.placeholder = 'New value';
  input.dataset.action = 'rotate-value';
  input.value = sheet.value ?? '';
  const field = el('label', 'field');
  field.append(el('span', 'field-label', 'New value'));
  field.append(input);
  body.push(field);

  const keep = el('label', 'checkbox');
  const box = el('input');
  box.type = 'checkbox';
  box.checked = sheet.storeInSecretStore !== false;
  box.dataset.action = 'rotate-secret-store';
  keep.append(box);
  keep.append(el('span', null, `Keep a copy in ${storeName(state)}`));
  body.push(keep);

  body.push(
    el(
      'p',
      'field-hint',
      'Every file listed above is backed up before it is changed, and the clients will need restarting afterwards.',
    ),
  );

  return {
    title: `Rotate ${secret.key}`,
    body,
    footer: [
      button('Cancel', 'close-sheet', { className: 'button subtle' }),
      el('span', 'spacer'),
      button(sheet.busy ? 'Rotating…' : 'Rotate everywhere', sheet.busy ? 'noop' : 'confirm-rotate', {
        className: sheet.busy ? 'button busy' : 'button primary',
        dataset: { secretId: secret.id },
      }),
    ],
  };
}
