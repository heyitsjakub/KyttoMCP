// Field-level before/after for a changed contract (§7.10).
//
// Native has already bounded what arrives here: at most 40 fields and an omission
// notice, excerpts of at most 800 characters, an escaped JSON Pointer for the path.
// This module still treats every string as untrusted display text — a tool
// description is written by whoever runs the server — so it only ever sets
// `textContent`, and it clips again rather than trusting a bound it did not make.
//
// It never decides whether anything changed. The excerpts are for reading; whether a
// contract differs was decided natively, on the full values.

import { el } from '../dom.js';

const MAX_VALUE_CHARACTERS = 1_200;
const MAX_PATH_CHARACTERS = 300;
const MAX_FIELDS = 41;

/// At most `limit` Unicode scalars, then an ellipsis. Short text is returned as it
/// is without being copied into an array first.
const clip = (text, limit) => {
  const value = String(text);
  if (value.length <= limit) return value;
  return `${Array.from(value.slice(0, limit * 2)).slice(0, limit).join('')}…`;
};

/// One changed field: where, what it was, what it is, and why it deserves a look.
/// A missing value reads "Not present" in words, so absent never looks like empty.
function fieldBlock(field) {
  const block = el('div', 'contract-field');
  block.append(el('code', 'contract-path selectable', clip(field.path ?? '', MAX_PATH_CHARACTERS)));

  const values = el('div', 'contract-values');
  for (const [label, className, text] of [
    ['Before', 'contract-before', field.before],
    ['After', 'contract-after', field.after],
  ]) {
    const side = el('div', className);
    side.append(
      el('span', 'contract-value-label', label),
      el('pre', 'contract-value selectable', text == null ? 'Not present' : clip(text, MAX_VALUE_CHARACTERS)),
    );
    values.append(side);
  }
  block.append(values);

  if (field.explanation) {
    block.append(el('p', 'field-hint contract-explanation', clip(field.explanation, MAX_VALUE_CHARACTERS)));
  }
  return block;
}

/// The details of one change as a list, or null when native sent none — an added or
/// removed tool, or a change recorded before field details existed.
export function contractFieldDetails(change) {
  const fields = Array.isArray(change?.details) ? change.details.slice(0, MAX_FIELDS) : [];
  if (fields.length === 0) return null;
  const list = el('div', 'contract-fields');
  list.setAttribute('role', 'group');
  list.setAttribute('aria-label', `Changed fields of ${clip(change.toolName ?? 'this tool', 120)}`);
  for (const field of fields) list.append(fieldBlock(field));
  return list;
}
