function joined(labels) {
  if (labels.length <= 1) return labels[0] ?? '';
  if (labels.length === 2) return `${labels[0]} and ${labels[1]}`;
  return `${labels.slice(0, -1).join(', ')}, and ${labels.at(-1)}`;
}

/**
 * Runs secondary list refreshes without turning a committed native mutation into
 * a reported failure. Each entry is { label, run }; run may update shared state.
 */
export async function settlePostMutationRefresh(notice, refreshes) {
  const results = await Promise.allSettled(
    refreshes.map((refresh) => Promise.resolve().then(refresh.run)),
  );
  const failed = refreshes
    .filter((_, index) => results[index].status === 'rejected')
    .map((refresh) => refresh.label);
  if (failed.length === 0) return notice;

  const warning = `The change succeeded, but Kytto could not refresh ${joined(failed)}. Reload Kytto before making another change.`;
  const message = notice?.message?.trim()
    ? `${notice.message.trim()} ${warning}`
    : warning;
  return {
    ...(notice ?? {}),
    kind: 'warning',
    message,
  };
}
