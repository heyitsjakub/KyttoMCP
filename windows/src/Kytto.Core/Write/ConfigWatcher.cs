namespace Kytto.Core;

/// <summary>A file or directory tree whose changes can affect discovery.</summary>
public sealed record ConfigWatchTarget(string Path, bool IncludeSubdirectories)
{
    public static ConfigWatchTarget File(string path) => new(path, false);

    public static ConfigWatchTarget Tree(string path) => new(path, true);
}

/// <summary>
/// Notices when a watched config or installed-package tree changes (§6.4).
/// </summary>
/// <remarks>
/// <para>
/// File targets are watched through their parent directory, because a config is
/// replaced by a rename — every atomic write, including Kytto's own, is a new file
/// moved over the old one. The watcher filter still names that one file, so activity
/// elsewhere in a broad parent such as the user profile cannot refresh the app.
/// </para>
/// <para>
/// Coalesced, because one save produces several notifications and a client app
/// rewriting its own config produces a burst of them. The callback is for
/// re-reading state, and re-reading twice for one change is waste the user pays
/// for in a stutter.
/// </para>
/// </remarks>
public sealed class ConfigWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _lock = new();
    private readonly Action _onChange;
    private readonly TimeSpan _quietPeriod;
    private Timer? _timer;
    private bool _disposed;

    public ConfigWatcher(
        IEnumerable<ConfigWatchTarget> targets,
        Action onChange,
        TimeSpan? quietPeriod = null)
    {
        _onChange = onChange;
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(300);

        foreach (var target in targets)
        {
            var fullPath = Path.GetFullPath(target.Path);
            var directory = target.IncludeSubdirectories
                ? fullPath
                : Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directory)) continue;

            try
            {
                var watcher = new FileSystemWatcher(
                    directory,
                    target.IncludeSubdirectories ? "*" : Path.GetFileName(fullPath))
                {
                    // Size and last-write cover an edit in place; file name covers
                    // the rename an atomic write ends with.
                    NotifyFilter = NotifyFilters.LastWrite
                        | NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.Size,
                    IncludeSubdirectories = target.IncludeSubdirectories,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += OnFileSystemEvent;
                watcher.Created += OnFileSystemEvent;
                watcher.Deleted += OnFileSystemEvent;
                watcher.Renamed += OnFileSystemEvent;
                // A watcher that overflows its buffer stops reporting; re-reading
                // everything is exactly the right response to "something happened
                // and I lost track of what".
                watcher.Error += (_, _) => Schedule();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception error) when (
                error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A directory that cannot be watched is not a reason to fail the
                // others, or the app. The user can still refresh by hand.
            }
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs args) => Schedule();

    private void Schedule()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = new Timer(_ => _onChange(), null, _quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
