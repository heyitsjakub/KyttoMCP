// A just-enough DOM for rendering the screens under Node's test runner.
//
// The web layer builds everything with createElement and textContent (§3.1), so
// its output can be inspected without a browser: this is a tree that records tags,
// classes, data attributes, attributes and text, and answers the handful of
// selectors the render code and the tests use. It is not a browser — no layout, no
// events — which is why layout rules are asserted against the stylesheet instead
// (see matrix-detail.test.js) and checked in a real browser through web/dev.html.
//
// Not a test file (the runner only picks up *.test.js), so importing it does not
// add a suite.

export class FakeElement {
  constructor(tagName, namespace = null) {
    this.tagName = String(tagName).toUpperCase();
    this.namespaceURI = namespace;
    this.dataset = {};
    this.children = [];
    this.attributes = new Map();
    this.disabled = false;
    this.title = '';
    this.id = '';
    this.parent = null;
    this.style = { setProperty: (name, value) => { this.style[name] = value; } };
    this._classes = new Set();
    this._text = '';
    this.classList = {
      add: (...names) => names.forEach((name) => this._classes.add(name)),
      remove: (...names) => names.forEach((name) => this._classes.delete(name)),
      contains: (name) => this._classes.has(name),
    };
  }

  get className() {
    return [...this._classes].join(' ');
  }

  set className(value) {
    this._classes = new Set(String(value).split(/\s+/).filter(Boolean));
  }

  get textContent() {
    return this._text + this.children.map((child) => child.textContent).join('');
  }

  set textContent(value) {
    this.children = [];
    this._text = String(value);
  }

  append(...nodes) {
    for (const node of nodes) {
      node.parent = this;
      this.children.push(node);
    }
  }

  replaceChildren(...nodes) {
    this.children = [];
    this.append(...nodes);
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.get(name) ?? null;
  }

  addEventListener() {}

  /** Elements below this one, in document order. */
  descendants() {
    return this.children.flatMap((child) => [child, ...child.descendants()]);
  }

  matches(selector) {
    // Compound selectors only: `tag`, `.class`, `[data-x]`, `[data-x="y"]`, and
    // any run of them (`button.cell-toggle[data-state="enabled"]`).
    const parts = selector.match(/^([a-zA-Z][\w-]*)?((?:\.[\w-]+|#[\w-]+|\[[^\]]+\])*)$/);
    if (!parts) throw new Error(`FakeElement cannot match “${selector}”.`);
    const [, tag, rest] = parts;
    if (tag && this.tagName !== tag.toUpperCase()) return false;
    for (const token of rest.match(/\.[\w-]+|#[\w-]+|\[[^\]]+\]/g) ?? []) {
      if (token.startsWith('.')) {
        if (!this._classes.has(token.slice(1))) return false;
        continue;
      }
      if (token.startsWith('#')) {
        if (this.id !== token.slice(1)) return false;
        continue;
      }
      const [, name, value] = token.match(/^\[([\w-]+)(?:="([^"]*)")?\]$/);
      const key = name.replace(/^data-/, '').replace(/-(\w)/g, (_, letter) => letter.toUpperCase());
      const actual = name.startsWith('data-') ? this.dataset[key] : this.getAttribute(name);
      if (actual === undefined || actual === null) return false;
      if (value !== undefined && String(actual) !== value) return false;
    }
    return true;
  }

  /// `a b > c` is not supported; `a b c` (descendants) and `a, b` are.
  matchesChain(steps) {
    if (!this.matches(steps[steps.length - 1])) return false;
    let index = steps.length - 2;
    for (let ancestor = this.parent; ancestor && index >= 0; ancestor = ancestor.parent) {
      if (ancestor.matches(steps[index])) index -= 1;
    }
    return index < 0;
  }

  querySelectorAll(selector) {
    const alternatives = selector
      .split(',')
      .map((part) => part.trim().match(/(?:\[[^\]]*\]|\S)+/g) ?? [])
      .filter((steps) => steps.length > 0);
    return this.descendants().filter((node) => alternatives.some((steps) => node.matchesChain(steps)));
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }
}

/// Installs `document` for the duration of a test file. Returns the root to render
/// into. Call once, at the top.
export function installFakeDom() {
  globalThis.document = {
    createElement: (tag) => new FakeElement(tag),
    createElementNS: (namespace, tag) => new FakeElement(tag, namespace),
  };
  return new FakeElement('div');
}

/// "tag.classes#text" for every element below `node` — a stable outline for
/// asserting that two renderings say the same thing.
export function outline(node) {
  return node.descendants().map((child) => {
    const own = child.children.length === 0 ? child.textContent : child._text;
    return `${child.tagName.toLowerCase()}.${child.className}|${own}`;
  });
}
