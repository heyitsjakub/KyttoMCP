# Web layer

Vanilla HTML/CSS/JS, ES modules, no build step (§3.1 of
[`docs/design.md`](../docs/design.md), like every `§` reference below).

## Development loop

```sh
cd web
python -m http.server 8765
```

Then open <http://localhost:8765/dev.html>.

A static server rather than opening the file directly, because browsers treat
every `file://` document as an opaque origin and refuse to load ES modules across
one. The shipped app sidesteps this by serving the page from an origin of its
own — on Windows a WebView2 virtual host set up in
`src/Kytto.App/Web/WebHost.cs`, on macOS a `kytto://` scheme handler — which is
the same reason those exist.

`dev.html` installs the fixtures in `js/dev/fixtures.js` as the IPC layer, so the
whole UI runs with no native code at all. That is this project's portability
proof, standing in for the throwaway Windows shell §3.2 asks for — and unlike a
Windows shell it earns its keep daily as the development loop.

**The rule it enforces:** anything that works in `index.html` but not in
`dev.html` has reached around `ipc.js` to the native side, and that is a bug.

## Tests

```sh
cd web
npm test
```

Runs `js/dev/*.test.js` with Node's built-in test runner; there are no
dependencies to install.

## Structure

| File | Responsibility |
|---|---|
| `js/ipc.js` | The only door to the native side. `invoke()` and `on()`. |
| `js/state.js` | One state object, one `setState()`, one render. |
| `js/main.js` | Bootstrap and delegated event handling. |
| `js/dom.js` | Element builders shared by the screens. |
| `js/anim.js` | Remembers what was on screen last render, so entrance animations run once. |
| `js/postMutation.js` | Secondary refreshes after a native write, without turning a committed change into a reported failure. |
| `js/screens/*.js` | One `render(root)` per screen. Split by screen, not by type. |

## House rules

- Never mutate the DOM from an event handler. Change state, let it render.
- Never read a value back out of the DOM. If it matters, it lives in state.
- Never `innerHTML` with interpolated data. Server names and error strings come
  from files Kytto did not write — use `textContent`.
- One delegated listener per event type on the container, dispatching on
  `data-action`. Not one listener per row.
- No `if (mac)`. If behaviour must differ, native reports a capability in
  `app.info` and the UI branches on that (§3.2).
- An animation confirms something happened; it never carries information. With
  motion off the screen says the same thing, immediately.
- Entrance animations go through `anim.js`. A render rebuilds the whole tree, so
  a keyframe attached unconditionally replays on every unrelated state change —
  and a write is several state changes in a row, which cuts one off milliseconds
  after it starts. `anim.js` answers both: what is actually new, and for how
  long it stays new.
