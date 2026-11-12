using System.Collections.Concurrent;
using System.Text.Json;
using Kytto.App.Ipc;
using Kytto.Core;

namespace Kytto.App;

/// <summary>
/// Re-reading the configs without holding the window still.
/// </summary>
/// <remarks>
/// <para>
/// A watcher event, a click on Refresh: both used to run a whole discovery pass on the UI
/// thread. Claude Code rewrites <c>~/.claude.json</c> after nearly every message, so on a
/// long-used machine that was a pass over megabytes of history every few seconds, and the
/// window stalled for each one.
/// </para>
/// <para>
/// The pass now runs on a worker thread and is <em>applied</em> on the caller's context. Two
/// things keep that safe, because writes stay synchronous and stay the source of truth:
/// the digests a pass sees are collected in a ledger of its own and adopted only when the
/// pass is applied — a pass that read a file before a write must not tell the next write
/// that the file is still as it was — and a pass that overlapped a write is discarded and
/// run again, since what it saw may predate the write.
/// </para>
/// </remarks>
internal sealed partial class AppModel
{
    private const int BackgroundAttempts = 3;

    private readonly Lock _reloadGate = new();
    private int _reloadGeneration;

    /// <summary>
    /// Re-reads every config off the UI thread and applies the result.
    /// </summary>
    /// <returns>
    /// Whether anything the page shows is different afterwards. A watcher event that found
    /// the same servers, clients, diagnostics and restart state returns false, so the page
    /// is not redrawn for it.
    /// </returns>
    internal async Task<bool> ReloadInBackgroundAsync()
    {
        var before = StateFingerprint();
        for (var attempt = 0; attempt < BackgroundAttempts; attempt++)
        {
            var generation = Volatile.Read(ref _reloadGeneration);
            var digests = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            // Built here, on the caller's context: it snapshots the stores and settings.
            var discovery = BuildDiscovery((path, digest) => digests[path] = digest);
            var result = await Task.Run(discovery.Run).ConfigureAwait(true);

            lock (_reloadGate)
            {
                // A write reloaded while the pass was reading: its view may be older than
                // the file now is. Run again rather than apply it.
                if (generation != Volatile.Read(ref _reloadGeneration)) continue;

                foreach (var (path, digest) in digests) _ledger.Record(path, digest);
                ArmWatcher(discovery, pendingChange: _watcher?.HasPendingChange ?? false);
                _current = result;
                RefreshRestartState();
                RefreshGatewayFindings();
            }
            return StateFingerprint() != before;
        }

        // Writes kept landing; take the pass on this thread, which cannot be overtaken.
        Reload();
        return StateFingerprint() != before;
    }

    /// <summary>Everything the page would be told, in the form it would be told it.</summary>
    private string StateFingerprint() => JsonSerializer.Serialize(this.ToStateDto(), Envelope.Json);
}
