// Remember the last rendered values. Adding an entrance class to a replacement
// DOM node starts its keyframes at zero, even if the previous node was halfway
// through them. Only the render that introduces a change may animate it.

const tokens = new Map();
const groups = new Map();
const lists = new Map();
const lengths = new Map();

/** True on the first sighting and whenever the subject changes. */
export function began(key, token) {
  const isNew = !tokens.has(key) || tokens.get(key) !== token;
  tokens.set(key, token);
  return isNew;
}

/** Like began, but the first render is still. */
export function changed(key, token) {
  const seen = tokens.has(key);
  return began(key, token) && seen;
}

/** Existing cells or health dots whose value changed in this render. */
export function changedIn(name, snapshot) {
  const previous = groups.get(name);
  groups.set(name, snapshot);
  const moved = new Set();
  for (const [key, value] of snapshot) {
    if (previous?.has(key) && previous.get(key) !== value) moved.add(key);
  }
  return moved;
}

/** New rows since the preceding list, excluding the initial paint. */
export function addedIn(name, keys) {
  const previous = lists.get(name);
  lists.set(name, new Set(keys));
  return new Set(previous ? keys.filter((key) => !previous.has(key)) : []);
}

/** Compare the current row count with the preceding render of this editor. */
export function previousEditorLength(name, length) {
  const previous = lengths.get(name) ?? length;
  lengths.set(name, length);
  return previous;
}

/** A stagger that stays snappy however long the list is. */
export function stagger(index, step = 14, cap = 220) {
  return `${Math.min(index * step, cap)}ms`;
}
