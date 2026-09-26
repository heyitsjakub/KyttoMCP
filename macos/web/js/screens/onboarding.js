// First run (§8, M6).
//
// The first screen is a calm hand-off into the matrix: it says what Kytto found,
// makes the read-only boundary explicit, and gives the user one clear next step.
// It stays a single screen rather than becoming a tour or carousel — the matrix
// is the product, so the shortest useful onboarding is the one that leads to it.

import { el, button, clientIcon } from '../dom.js';

const CLIENT_STATE = {
  ready: (client) => `${client.serverCount} configured server${client.serverCount === 1 ? '' : 's'}`,
  noConfig: () => 'installed · no configuration yet',
  orphanedConfig: () => 'configuration found · app not installed',
  notInstalled: () => 'not detected',
};

function mark() {
  const node = el('span', 'kytto-mark onboarding-mark');
  node.setAttribute('aria-hidden', 'true');
  for (let index = 0; index < 9; index += 1) {
    node.append(el('span', [0, 4, 8].includes(index) ? 'active' : null));
  }
  return node;
}

function metric(value, label) {
  const item = el('div', 'onboarding-stat');
  item.append(el('strong', null, String(value)), el('span', null, label));
  return item;
}

export function renderOnboarding(state) {
  const wrapper = el('div', 'onboarding');
  const shell = el('div', 'onboarding-shell');

  const hero = el('section', 'onboarding-hero');
  hero.setAttribute('aria-labelledby', 'onboarding-title');

  const brand = el('div', 'onboarding-brand');
  const brandCopy = el('span', 'brand-copy');
  brandCopy.append(el('strong', null, 'Kytto'), el('span', null, 'MCP Control'));
  brand.append(mark(), brandCopy);

  const eyebrow = el('div', 'onboarding-eyebrow');
  eyebrow.append(el('span', 'onboarding-eyebrow-dot'), el('span', null, 'First run'));

  const detected = state.clients.filter((client) => client.state !== 'notInstalled');
  const totalServers = state.servers.length;
  const customSources = state.clients.filter((client) => client.isReadOnly).length;
  const platform = state.app?.platformDisplayName ?? 'this platform';

  const heading = el('h1', null, 'Your MCP workspace is ready');
  heading.id = 'onboarding-title';
  hero.append(
    brand,
    eyebrow,
    heading,
    el(
      'p',
      'onboarding-copy',
      detected.length === 0
        ? `Kytto did not detect a supported client on ${platform}, but you can point it to a configuration in Settings.`
        : `Kytto found your local MCP setup on ${platform}. Review the discovery below, then manage everything from one matrix.`,
    ),
  );

  const stats = el('div', 'onboarding-stats');
  stats.append(metric(totalServers, 'servers found'));
  stats.append(metric(detected.length, 'clients detected'));
  if (customSources > 0) stats.append(metric(customSources, 'read-only sources'));
  hero.append(stats);

  const trust = el('div', 'onboarding-trust');
  trust.append(
    el('strong', null, 'Nothing has been changed'),
    el('span', null, 'Kytto only reads configuration on this screen. Edits create a backup first.'),
  );
  hero.append(trust);

  const panel = el('section', 'onboarding-panel');
  panel.setAttribute('aria-labelledby', 'onboarding-found-title');
  const panelHeader = el('header', 'onboarding-panel-header');
  const panelHeading = el('div');
  const sectionLabel = el('span', 'onboarding-section-label', 'Discovery');
  const foundHeading = el('h2', null, 'What Kytto found');
  foundHeading.id = 'onboarding-found-title';
  panelHeading.append(sectionLabel, foundHeading);
  panelHeader.append(
    panelHeading,
    el('span', 'onboarding-panel-meta', `${state.clients.length} supported clients`),
  );
  panel.append(panelHeader);

  const list = el('ul', 'onboarding-clients');
  for (const client of state.clients) {
    const item = el('li', `onboarding-client${client.state === 'notInstalled' ? ' is-idle' : ''}`);
    const copy = el('div', 'onboarding-client-copy');
    const title = el('div', 'onboarding-client-title');
    title.append(el('strong', null, client.displayName));
    const stateLine = el('div', 'onboarding-client-state');
    stateLine.append(
      el('span', `onboarding-state-dot ${client.state === 'ready' ? 'ready' : 'idle'}`),
      el('span', null, (CLIENT_STATE[client.state] ?? (() => 'status unavailable'))(client)),
    );
    title.append(stateLine);
    copy.append(title, el('span', 'mono muted', client.configPathDisplay));
    item.append(clientIcon(client), copy);
    list.append(item);
  }
  panel.append(list);

  const footer = el('footer', 'onboarding-footer');
  footer.append(
    el('span', 'muted', 'You can revisit configuration details from the sidebar at any time.'),
    button('Continue to matrix', 'finish-onboarding', { className: 'button primary' }),
  );
  panel.append(footer);

  shell.append(hero, panel);
  wrapper.append(shell);
  return wrapper;
}
