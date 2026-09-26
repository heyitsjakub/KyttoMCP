// Add and edit a server (§7.2).
//
// Two paths: the manual form, which is the power path and the one that matters,
// and the bundled catalog, which exists only so the first server is not a blank
// page. The catalog is a starting point that drops the user into the same form.

import { el, button, input, field, sheet } from '../dom.js';
import { appendedIn, contractedIn, stagger } from '../anim.js';

export function renderServerForm(state) {
  const form = state.sheet;
  const isEdit = form.mode === 'edit';

  const body = [];

  if (!isEdit && form.step === 'pick') {
    return sheet('Add server', catalogPicker(state), [
      button('Cancel', 'close-sheet', { className: 'button subtle' }),
      el('span', 'spacer'),
      button('Start from scratch', 'form-manual'),
    ]);
  }

  if (form.errors.length > 0) {
    const errors = el('div', 'form-errors');
    for (const message of form.errors) errors.append(el('p', null, message));
    body.push(errors);
  }

  if (form.note) body.push(el('p', 'field-hint', form.note));

  body.push(
    field(
      'Name',
      input(form.draft.name, {
        action: 'draft-name',
        placeholder: 'github',
      }),
      'Used as the key in the config file. No spaces.',
    ),
  );

  body.push(field('Transport', transportPicker(form.draft.transport)));

  if (form.draft.transport === 'stdio') {
    body.push(
      field(
        'Command',
        input(form.draft.command, { action: 'draft-command', placeholder: 'npx' }),
        'The executable to run. Give a full path if it is not on the system PATH.',
      ),
    );
    body.push(argumentsEditor(form.draft.args));
  } else {
    body.push(
      field(
        'URL',
        input(form.draft.url, { action: 'draft-url', placeholder: 'https://example.com/mcp' }),
      ),
    );
  }

  body.push(environmentEditor(form.draft.env, form.envHints ?? {}));

  if (!isEdit) body.push(clientPicker(state, form.targets));
  else body.push(el('p', 'field-hint', 'Changes apply to every writable client this server is configured in. Read-only custom copies are never changed.'));

  return sheet(isEdit ? `Edit ${form.original ?? 'server'}` : 'Add server', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(isEdit ? 'Save' : 'Add server', form.busy ? 'noop' : 'submit-form', {
      className: form.busy ? 'button busy' : 'button primary',
    }),
  ]);
}

// MARK: - Catalog

function catalogPicker(state) {
  const wrapper = el('div', 'catalog');
  wrapper.append(
    el(
      'p',
      'field-hint',
      'A short list of servers with their commands filled in. Everything else goes through the manual form.',
    ),
  );

  const list = el('ul', 'catalog-list');
  for (const entry of state.catalog) {
    const item = el('li');
    const pick = el('button', 'catalog-entry');
    pick.dataset.action = 'catalog-pick';
    pick.dataset.catalogId = entry.id;

    const head = el('div', 'catalog-head');
    head.append(el('span', 'catalog-name', entry.displayName));
    head.append(el('span', 'badge', entry.transport));
    pick.append(head);

    pick.append(el('div', 'catalog-description', entry.description));
    pick.append(
      el(
        'div',
        'catalog-command mono',
        entry.transport === 'stdio' ? [entry.command, ...entry.args].join(' ') : entry.url,
      ),
    );
    if (entry.requires) pick.append(el('div', 'field-hint', `Needs ${entry.requires}`));

    item.append(pick);
    list.append(item);
  }

  if (state.catalog.length === 0) {
    wrapper.append(el('p', 'muted', 'The catalog is empty.'));
  } else {
    wrapper.append(list);
  }
  return wrapper;
}

// MARK: - Controls

function transportPicker(current) {
  const group = el('div', 'segmented');
  group.setAttribute('aria-label', 'Transport');
  for (const value of ['stdio', 'http', 'sse']) {
    const option = el('button', `segment${value === current ? ' selected' : ''}`, value);
    option.dataset.action = 'draft-transport';
    option.dataset.value = value;
    option.setAttribute('aria-pressed', String(value === current));
    group.append(option);
  }
  return group;
}

function argumentsEditor(args) {
  const section = el('section', 'detail-section');
  section.append(el('h3', null, 'Arguments'));

  const list = el('div', 'row-editor');
  const appended = appendedIn('draft-arguments', args.length, 240);
  if (contractedIn('draft-arguments', args.length)) list.classList.add('is-compacting');
  args.forEach((argument, index) => {
    const row = el('div', 'editor-row');
    if (appended.has(index)) {
      row.classList.add('is-entering');
      row.style.animationDelay = stagger(index, 12, 60);
    }
    row.append(input(argument, { action: 'draft-arg', dataset: { index: String(index) } }));
    row.append(
      button('Remove', 'draft-arg-remove', {
        className: 'button subtle',
        dataset: { index: String(index) },
      }),
    );
    list.append(row);
  });
  section.append(list);
  section.append(button('Add argument', 'draft-arg-add', { className: 'button subtle' }));
  return section;
}

function environmentEditor(env, hints) {
  const section = el('section', 'detail-section');
  section.append(el('h3', null, 'Environment'));

  const list = el('div', 'row-editor');
  const appended = appendedIn('draft-environment', env.length, 240);
  if (contractedIn('draft-environment', env.length)) list.classList.add('is-compacting');
  env.forEach((entry, index) => {
    const row = el('div', 'editor-row');
    if (appended.has(index)) {
      row.classList.add('is-entering');
      row.style.animationDelay = stagger(index, 12, 60);
    }
    row.append(
      input(entry.key, {
        action: 'draft-env-key',
        placeholder: 'API_KEY',
        dataset: { index: String(index) },
      }),
    );
    // An existing value is never sent to this screen, so the field starts empty
    // and an empty field means "leave it alone" rather than "clear it" (§6).
    row.append(
      input(entry.value ?? '', {
        action: 'draft-env-value',
        type: 'password',
        placeholder: entry.hasValue ? 'unchanged' : 'value',
        dataset: { index: String(index) },
      }),
    );
    row.append(
      button('Remove', 'draft-env-remove', {
        className: 'button subtle',
        dataset: { index: String(index) },
      }),
    );
    list.append(row);
    if (hints[entry.key]) list.append(el('p', 'field-hint', hints[entry.key]));
  });
  section.append(list);
  section.append(button('Add variable', 'draft-env-add', { className: 'button subtle' }));
  section.append(
    el(
      'p',
      'field-hint',
      'Values are written into the client config file as plain text — that is the only form MCP clients read. The Secrets screen tracks where each one ended up.',
    ),
  );
  return section;
}

function clientPicker(state, targets) {
  const section = el('section', 'detail-section');
  section.append(el('h3', null, 'Add to'));

  const list = el('div', 'client-picker');
  for (const client of state.clients) {
    if (client.isReadOnly || client.state === 'notInstalled') continue;
    const label = el('label', 'checkbox');
    const box = el('input');
    box.type = 'checkbox';
    box.checked = targets.includes(client.id);
    box.dataset.action = 'draft-target';
    box.dataset.clientId = client.id;
    label.append(box);
    label.append(el('span', null, client.displayName));
    if (client.state === 'noConfig') {
      label.append(el('span', 'field-hint', 'config will be created'));
    }
    list.append(label);
  }
  section.append(list);
  return section;
}
