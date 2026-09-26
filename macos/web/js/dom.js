// Building blocks shared by the screens.
//
// Everything goes through createElement and textContent. Server names, commands
// and error strings come out of config files Kytto did not write and out of
// process stderr, so they are never interpolated into markup (§3.1).

import { clientIconURL } from './ipc.js';

export function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

export function button(label, action, { className = 'button', dataset = {} } = {}) {
  const node = el('button', className, label);
  node.dataset.action = action;
  for (const [key, value] of Object.entries(dataset)) node.dataset[key] = value;
  return node;
}

export function field(labelText, control, hint) {
  const wrapper = el('label', 'field');
  wrapper.append(el('span', 'field-label', labelText));
  wrapper.append(control);
  if (hint) wrapper.append(el('span', 'field-hint', hint));
  return wrapper;
}

export function input(value, { action, placeholder, type = 'text', dataset = {} } = {}) {
  const node = el('input', 'text-input');
  node.type = type;
  node.value = value ?? '';
  if (placeholder) node.placeholder = placeholder;
  if (action) node.dataset.action = action;
  for (const [key, value] of Object.entries(dataset)) node.dataset[key] = value;
  return node;
}

/// A modal layer. The backdrop carries its own action so clicking outside closes.
export function sheet(title, body, footer) {
  const backdrop = el('div', 'sheet-backdrop');
  backdrop.dataset.action = 'close-sheet';

  const panel = el('div', 'sheet');
  const titleID = `sheet-title-${nextSheetID++}`;
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-modal', 'true');
  panel.setAttribute('aria-labelledby', titleID);
  // Clicks inside must not reach the backdrop's close handler.
  panel.dataset.stopClose = 'true';

  const header = el('header', 'sheet-header');
  const heading = el('h2', null, title);
  heading.id = titleID;
  header.append(heading);
  header.append(button('Close', 'close-sheet', { className: 'button subtle' }));
  panel.append(header);

  const content = el('div', 'sheet-body');
  content.append(...(Array.isArray(body) ? body : [body]));
  panel.append(content);

  if (footer) {
    const bar = el('footer', 'sheet-footer');
    bar.append(...(Array.isArray(footer) ? footer : [footer]));
    panel.append(bar);
  }

  backdrop.append(panel);
  return backdrop;
}

let nextSheetID = 1;

/// Token counts, at the precision anyone actually reads them (§7.4).
export function formatTokens(count) {
  if (count === null || count === undefined) return '—';
  if (count < 1000) return String(count);
  if (count < 10_000) return `${(count / 1000).toFixed(1)}k`;
  return `${Math.round(count / 1000)}k`;
}

const SVG_NS = 'http://www.w3.org/2000/svg';

/**
 * A line-drawn glyph, built node by node like everything else here.
 *
 * The paths are ours and are the only strings in the app that describe shapes
 * rather than data, which is why they can be listed inline without any of §3.1's
 * escaping worries applying to them.
 *
 * @param {string[]} paths `d` attributes, on a 16×16 grid.
 */
export function glyph(paths) {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 16 16');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.4');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');
  svg.classList.add('glyph');
  for (const definition of paths) {
    const path = document.createElementNS(SVG_NS, 'path');
    path.setAttribute('d', definition);
    svg.append(path);
  }
  return svg;
}

export const GLYPH = {
  // Drawn as one stroke from the short leg up, so it can be dashed and drawn on
  // when a cell lands (see .cell-check in the stylesheet).
  check: ['M3.6 8.4 6.7 11.5 12.6 5'],
  matrix: ['M2.5 3h11v10h-11z', 'M2.5 6.5h11', 'M6.5 6.5v6.5'],
  profiles: ['M3 3.5h10v3H3z', 'M3 9.5h10v3H3z', 'M5 6.5v3'],
  secrets: ['M3.5 7.5h9v6h-9z', 'M5.5 7.5V5.5a2.5 2.5 0 0 1 5 0v2'],
  backups: ['M2.5 8a5.5 5.5 0 1 0 1.7-3.9', 'M2.5 2.5v3h3', 'M8 5.2V8l2 1.3'],
  library: ['M2.5 3.5h11v9h-11z', 'M5 6.5h6', 'M5 9.5h4'],
  skills: ['M8 2.5 9.5 5l2.8.4-2 2 0.5 2.8L8 8.9 5.2 10.2 5.7 7.4l-2-2L6.5 5z'],
  warning: ['M8 2.6 15 14H1z', 'M8 6.4v3.2', 'M8 11.9v.2'],
  refresh: ['M13.5 8a5.5 5.5 0 1 1-1.7-3.9', 'M13.5 2.5v3h-3'],
  activity: ['M2 8h2.2l1.3-3.3L8 11.5l1.8-5 1.1 1.5H14'],
  // Points down when the section is open; the stylesheet rotates it shut.
  disclosure: ['M4.5 6.5 8 10l3.5-3.5'],
  // A gear reads as settings at 16px only if the teeth stay countable, so it is
  // a hub with six spokes rather than a toothed outline that turns to mush.
  settings: [
    'M8 5.75a2.25 2.25 0 1 0 0 4.5 2.25 2.25 0 0 0 0-4.5',
    'M8 1.9v1.7', 'M8 12.4v1.7',
    'M2.72 4.95l1.47.85', 'M11.81 10.2l1.47.85',
    'M2.72 11.05l1.47-.85', 'M11.81 5.8l1.47-.85',
  ],
};

/**
 * A client's real application icon, from the shell.
 *
 * The picture is worth the native code: the matrix has one column per client and
 * their names do not fit in one. Where there is no shell — or no icon to be had —
 * the same 22px square comes back with initials in it, so nothing shifts.
 *
 * @param {{ id: string, displayName: string }} client
 */
export function clientIcon(client) {
  const url = clientIconURL(client.id);
  if (!url) return initialsTile(client.displayName);

  const image = el('img', 'app-icon');
  image.src = url;
  image.alt = '';
  image.draggable = false;
  image.addEventListener(
    'error',
    () => image.replaceWith(initialsTile(client.displayName)),
    { once: true },
  );
  return image;
}

/// Two characters, and they have to tell clients apart: the first letter of each
/// word where there are several, the first two letters where there is one —
/// otherwise Cursor and Codex are both "C".
const initialsTile = (displayName) => {
  const words = displayName.split(/\s+/).filter(Boolean);
  const initials =
    words.length > 1
      ? words.map((word) => word[0]).join('')
      : (words[0] ?? '').slice(0, 2);
  return el('span', 'app-icon fallback', initials.slice(0, 2).toUpperCase());
};

export function definitionList(pairs) {
  const list = el('dl', 'definitions');
  for (const [term, value, className] of pairs) {
    list.append(el('dt', null, term));
    list.append(el('dd', className, value));
  }
  return list;
}
