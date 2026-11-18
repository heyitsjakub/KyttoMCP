// Entrance animations in a UI that rebuilds itself on every state change.
//
// `render` throws the whole tree away and builds a new one (§3.1), which breaks
// animation in both directions. A node carrying an entrance animation replays it
// every time anything at all changes — the sheet would pop on each keystroke,
// rows would flash on every toggle. And one write is several state changes in a
// row (busy, result, notice, backups), so a node that starts animating on the
// first is thrown away by the second, milliseconds in, having shown nothing.
//
// Both come down to the same missing thing: the DOM cannot say what is new here,
// so this module remembers. `hold` is how long a thing stays new — long enough
// for its animation to finish across the renders that follow, short enough that
// the next unrelated change does not inherit it. Nothing here affects what is
// rendered, only how it arrives.

const HOLD = 320;

/** key → { token, at } */
const singles = new Map();
const beginnings = new Map();
/** group name → { snapshot: Map, marks: Map<key, time> } */
const groups = new Map();
/** list name → { snapshot: Set, marks: Map<key, time> } */
const lists = new Map();
/** editor name → { length, marks: Map<index, time> } */
const editors = new Map();
/** editor name → { length, at } */
const contractions = new Map();

/**
 * True when `token` differs from the one last seen under `key`, first sighting
 * included, and for `hold` afterwards. For things that animate as they appear:
 * sheets, banners, bars.
 * @returns {boolean}
 */
export function appeared(key, token, hold = HOLD) {
  const now = performance.now();
  const previous = singles.get(key);

  if (!previous || previous.token !== token) {
    singles.set(key, { token, at: now });
    return true;
  }
  return now - previous.at <= hold;
}

/** True only on the render where a token first appears or changes. */
export function began(key, token) {
  const previous = beginnings.get(key);
  beginnings.set(key, token);
  return previous !== token;
}

/**
 * Like `appeared`, but first sighting is still. Used for motion inside something
 * already on screen: changing panel, form step or transport.
 */
export function changed(key, token, hold = HOLD) {
  const now = performance.now();
  const previous = singles.get(key);

  if (!previous) {
    singles.set(key, { token, at: -Infinity });
    return false;
  }
  if (previous.token !== token) {
    singles.set(key, { token, at: now });
    return true;
  }
  return now - previous.at <= hold;
}

/**
 * Change detection for a set of things rebuilt wholesale each render, such as
 * the matrix cells. Keys seen for the first time are never reported, so a fresh
 * screen paints still rather than animating everything at once, and the previous
 * snapshot is dropped, so servers that go away do not linger in memory.
 * @param {string} name
 * @param {Map<string, string>} snapshot key → the value it currently has
 * @param {number} [hold] how long a key stays reported after it moves
 * @returns {Set<string>} the keys that moved, now or just before
 */
export function changedIn(name, snapshot, hold = HOLD) {
  const now = performance.now();
  const group = groups.get(name) ?? { snapshot: null, marks: new Map() };
  const previous = group.snapshot;
  group.snapshot = snapshot;
  groups.set(name, group);

  if (previous) {
    for (const [key, value] of snapshot) {
      if (previous.has(key) && previous.get(key) !== value) group.marks.set(key, now);
    }
  }

  const moved = new Set();
  for (const [key, at] of group.marks) {
    if (now - at <= hold) moved.add(key);
    else group.marks.delete(key);
  }
  return moved;
}

/**
 * Keys newly added to a rendered list. The first snapshot is still, and keys
 * that merely changed value do not replay their entrance.
 */
export function addedIn(name, keys, hold = HOLD) {
  const now = performance.now();
  const snapshot = new Set(keys);
  const group = lists.get(name) ?? { snapshot: null, marks: new Map() };

  if (group.snapshot) {
    for (const key of snapshot) {
      if (!group.snapshot.has(key)) group.marks.set(key, now);
    }
  }
  group.snapshot = snapshot;
  lists.set(name, group);

  const added = new Set();
  for (const [key, at] of group.marks) {
    if (snapshot.has(key) && now - at <= hold) added.add(key);
    else group.marks.delete(key);
  }
  return added;
}

/**
 * Indices appended to a dynamic editor. Removing a row stays still, while one
 * or several new rows get a short, targeted entrance.
 */
export function appendedIn(name, length, hold = HOLD) {
  const now = performance.now();
  const editor = editors.get(name) ?? { length: null, marks: new Map() };

  if (editor.length !== null && length > editor.length) {
    for (let index = editor.length; index < length; index += 1) {
      editor.marks.set(index, now);
    }
  }
  editor.length = length;
  editors.set(name, editor);

  const appended = new Set();
  for (const [index, at] of editor.marks) {
    if (index < length && now - at <= hold) appended.add(index);
    else editor.marks.delete(index);
  }
  return appended;
}

/** True briefly after a dynamic editor became shorter. */
export function contractedIn(name, length, hold = 220) {
  const now = performance.now();
  const previous = contractions.get(name);
  const at = previous && length < previous.length ? now : previous?.at ?? -Infinity;
  contractions.set(name, { length, at });
  return now - at <= hold;
}

/** A stagger that stays snappy however long the list is. */
export function stagger(index, step = 14, cap = 220) {
  return `${Math.min(index * step, cap)}ms`;
}
