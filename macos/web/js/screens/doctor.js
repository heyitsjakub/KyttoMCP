// Actionable diagnostics derived natively from configuration and measured
// health data. The web layer displays findings and requests explicit actions;
// it never guesses paths, commands or edits.

import { el, button, sheet } from '../dom.js';

export function renderDoctor(state) {
  const panel = el('div', 'doctor-panel');
  const head = el('header', 'panel-head');
  const copy = el('div');
  copy.append(el('h2', null, 'MCP Doctor'));
  copy.append(el('p', 'muted', 'Find startup, environment and portability problems before opening every client log.'));
  head.append(copy, el('span', 'spacer'), button('Check all servers', 'check-all'));
  panel.append(head);

  const reports = state.doctorReports ?? [];
  if (reports.length === 0) {
    panel.append(el('div', 'placeholder', 'No actionable server findings.'));
    return panel;
  }

  const list = el('div', 'doctor-list');
  for (const report of reports) {
    const card = el('section', 'doctor-card');
    const title = el('div', 'doctor-card-head');
    title.append(el('h3', null, report.serverName));
    title.append(button('Open server', 'open-server', {
      className: 'button subtle',
      dataset: { serverId: report.serverID },
    }));
    card.append(title);

    for (const finding of report.findings) {
      const item = el('div', `doctor-finding ${finding.severity}`);
      item.append(el('strong', null, finding.title));
      item.append(el('p', null, finding.detail));
      item.append(el('p', 'muted', finding.remediation));
      if (finding.action === 'runHealthCheck') {
        item.append(button('Run health check', 'check-server', {
          dataset: { serverId: report.serverID },
        }));
      } else if (finding.action === 'pinResolvedCommand') {
        item.append(button('Preview safe fix…', 'doctor-preview-fix', {
          className: 'button primary',
          dataset: { serverId: report.serverID },
        }));
      }
      card.append(item);
    }
    list.append(card);
  }
  panel.append(list);
  return panel;
}

export function renderDoctorFix(state) {
  const preview = state.sheet.preview;
  const body = [
    el('p', null, `Pin the executable for “${preview.serverName}” to the path verified by its successful health check?`),
  ];
  const comparison = el('div', 'doctor-fix-comparison');
  comparison.append(commandBlock('Configured now', preview.currentCommand));
  comparison.append(commandBlock('After', preview.replacementCommand));
  body.push(comparison);
  body.push(el('p', 'muted', `Affected client definitions: ${preview.clientIDs.join(', ')}. Kytto will preview this here, back up every affected config, preserve unrelated content and write atomically.`));
  if (state.sheet.error) body.push(el('p', 'failure-message', state.sheet.error));

  return sheet('Safe repair preview', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(state.sheet.busy ? 'Applying…' : 'Apply repair', state.sheet.busy ? 'noop' : 'doctor-apply-fix', {
      className: state.sheet.busy ? 'button primary busy' : 'button primary',
      dataset: { serverId: preview.serverID },
    }),
  ]);
}

function commandBlock(label, command) {
  const block = el('div', 'doctor-command');
  block.append(el('span', 'field-label', label));
  block.append(el('code', 'mono selectable', command));
  return block;
}
