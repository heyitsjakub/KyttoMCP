// Privacy-safe metadata from the local stdio gateway. No arguments, results or
// environment values cross this screen's IPC contract.

import { el, button } from '../dom.js';

export function renderActivity(state) {
  const panel = el('div', 'activity-panel');
  const head = el('header', 'panel-head');
  const copy = el('div');
  copy.append(el('h2', null, 'Live Activity'));
  copy.append(el('p', 'muted', 'Sessions, tool calls, latency and failures observed through Kytto Gateway. Payload capture is off.'));
  head.append(copy, el('span', 'spacer'));
  head.append(button('Refresh', 'refresh-activity'));
  head.append(button('Copy support report', 'copy-activity-report', { className: 'button subtle' }));
  panel.append(head);

  const activity = state.activity;
  if (!activity) {
    panel.append(el('div', 'placeholder', 'Loading gateway activity…'));
    return panel;
  }

  const stats = el('div', 'activity-stats');
  stats.append(
    stat(activity.totalSessions, 'sessions'),
    stat(activity.completedCalls, 'completed calls'),
    stat(activity.failedCalls, 'failed calls', activity.failedCalls > 0 ? 'bad' : null),
    stat(activity.averageDurationMilliseconds == null ? '—' : `${activity.averageDurationMilliseconds} ms`, 'average latency'),
  );
  panel.append(stats);

  if (activity.events.length === 0) {
    panel.append(el('div', 'profile-empty', 'No gateway activity has been recorded yet. Enable Gateway mode for a server, restart its client and use one of its tools.'));
    return panel;
  }

  const list = el('div', 'activity-list');
  for (const event of activity.events) list.append(eventRow(state, event));
  panel.append(list);
  return panel;
}

function stat(value, label, className = null) {
  const item = el('div', `activity-stat${className ? ` ${className}` : ''}`);
  item.append(el('strong', null, String(value)), el('span', 'muted', label));
  return item;
}

function eventRow(state, event) {
  const row = el('article', `activity-event${event.succeeded === false ? ' failed' : ''}`);
  const server = state.servers.find((item) => item.id === event.serverID)?.name ?? event.serverID;
  const client = state.clients.find((item) => item.id === event.clientID)?.displayName ?? event.clientID;
  const title = event.toolName ?? event.kind.replaceAll('.', ' ');
  row.append(el('time', 'muted', new Date(event.timestamp * 1000).toLocaleTimeString()));
  const copy = el('div');
  copy.append(el('strong', 'mono', title));
  const details = [client, server];
  if (event.durationMilliseconds != null) details.push(`${event.durationMilliseconds} ms`);
  if (event.errorCode != null) details.push(`error ${event.errorCode}`);
  if (event.exitCode != null) details.push(`exit ${event.exitCode}`);
  copy.append(el('p', 'muted', details.join(' · ')));
  row.append(copy);
  return row;
}

export function activityReport(state) {
  const activity = state.activity ?? { events: [], totalSessions: 0, completedCalls: 0, failedCalls: 0 };
  const lines = [
    'Kytto MCP gateway activity report',
    `Generated: ${new Date().toISOString()}`,
    'Payloads, arguments, results and environment values are not captured.',
    '',
    `Sessions: ${activity.totalSessions}`,
    `Completed calls: ${activity.completedCalls}`,
    `Failed calls: ${activity.failedCalls}`,
    `Average latency: ${activity.averageDurationMilliseconds ?? 'unknown'} ms`,
    '',
  ];
  for (const event of activity.events.slice(0, 100)) {
    lines.push([
      new Date(event.timestamp * 1000).toISOString(),
      event.kind,
      `client=${event.clientID}`,
      `server=${event.serverID}`,
      event.toolName ? `tool=${event.toolName}` : null,
      event.durationMilliseconds == null ? null : `duration=${event.durationMilliseconds}ms`,
      event.succeeded == null ? null : `success=${event.succeeded}`,
      event.errorCode == null ? null : `error=${event.errorCode}`,
      event.exitCode == null ? null : `exit=${event.exitCode}`,
    ].filter(Boolean).join(' '));
  }
  return lines.join('\n');
}
