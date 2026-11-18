// Remote Doctor's authorization surface (§7.3): Kytto's own sign-in for a
// Streamable HTTP server.
//
// What this module is not: it holds no endpoint, code, client ID or token, and it has
// none to hold. The only thing that ever travels is an opaque server id, in a
// `data-` attribute, to `health.signInRemote` and `health.forgetRemoteAuthorization`.
// Everything about the provider — which one, which scopes — is shown by the native
// shell in its own dialog, from validated metadata.
//
// The sign-in button exists only where native reports the `health.oauth` capability
// (§3.2), and a state is always said in words: colour never carries it alone.

import { el, button } from '../dom.js';

/// Whether this server can be signed in to from here. The capability, never the
/// platform, decides.
export function supportsRemoteSignIn(state, server) {
  return server?.transport === 'http' && Boolean(state?.app?.capabilities?.includes('health.oauth'));
}

/// What "not checked yet" means for each remote transport.
export function remoteNotCheckedText(server) {
  return server.transport === 'http'
    ? 'Not checked yet. Check now connects to this endpoint and reads its MCP tool list. It does not call tools.'
    : 'Legacy SSE is not supported by Remote Doctor. Use a Streamable HTTP endpoint.';
}

/// Sign in / Forget, with the one sentence that says what they are for.
/// `busy` is the same flag the Check button reads, so a sign-in, a check and a
/// check-all cannot overlap.
export function remoteAuthControls(state, server) {
  if (!supportsRemoteSignIn(state, server)) return null;
  const busy = state.checking.has(server.id) || Boolean(state.healthProgress);
  const signingIn = state.checking.has(server.id);

  const block = el('div', 'remote-auth');
  if (server.health?.status === 'needsAuthorization') {
    // Said as text and kept apart from the button, so it reads the same without colour.
    const status = el('p', 'remote-auth-state');
    status.append(
      el('strong', null, 'Sign-in required. '),
      el('span', null, 'This endpoint asked for authorization, or Kytto’s saved sign-in no longer works.'),
    );
    block.append(status);
  }

  const actions = el('div', 'remote-auth-actions');
  actions.append(
    button(signingIn ? 'Waiting…' : 'Sign in…', busy ? 'noop' : 'sign-in-remote', {
      className: signingIn ? 'button busy' : 'button',
      dataset: { serverId: server.id },
    }),
    button('Forget Kytto sign-in', busy ? 'noop' : 'forget-remote-authorization', {
      className: 'button subtle',
      dataset: { serverId: server.id },
    }),
  );
  block.append(actions);
  block.append(el(
    'p',
    'field-hint',
    'Signing in opens your browser and keeps the result in the system credential store, to authorize Kytto’s own checks. ' +
      'Your clients keep their own sign-ins, and no client configuration is changed.',
  ));
  return block;
}

/// The Doctor's "authorization required" finding gets the same way forward.
export function doctorAuthAction(state, serverId, finding) {
  if (finding.code !== 'needs-authorization') return null;
  const server = state.servers.find((entry) => entry.id === serverId);
  if (!supportsRemoteSignIn(state, server)) return null;
  const busy = state.checking.has(serverId) || Boolean(state.healthProgress);
  return button(busy ? 'Waiting…' : 'Sign in…', busy ? 'noop' : 'sign-in-remote', {
    className: busy ? 'button busy' : 'button primary',
    dataset: { serverId },
  });
}
