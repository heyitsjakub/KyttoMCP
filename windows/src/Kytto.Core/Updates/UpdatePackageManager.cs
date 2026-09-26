using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Kytto.Core.Settings;

namespace Kytto.Core.Updates;

public sealed record UpdateProgress(
    string Phase,
    double? Progress,
    long BytesReceived,
    long? TotalBytes);

public sealed record UpdatePackage(
    string Token,
    string Path,
    string Sha256,
    long ByteCount,
    string Version);

public sealed class UpdatePackageException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Explicit, hash-verified Windows update download and install pipeline.</summary>
/// <remarks>
/// The checked manifest supplies the expected hash; no package URL or version is
/// accepted from the web layer. Partial bytes stay in Kytto's private app-data
/// staging directory and are removed on cancellation or failed verification.
/// A successful stage gets an opaque token. Keeping stages by token also means a
/// failed later download cannot destroy the last verified package (§8).
/// </remarks>
public sealed class UpdatePackageManager : IDisposable
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);
    private static readonly Uri DownloadUri = new(UpdateChecker.WindowsDownloadUrl, UriKind.Absolute);
    private readonly KyttoPaths _paths;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, UpdatePackage> _readyPackages = new(StringComparer.Ordinal);
    private CancellationTokenSource? _downloadCancellation;
    private string? _lastToken;
    private bool _disposed;

    public UpdatePackageManager(KyttoPaths paths, HttpClient? httpClient = null)
    {
        _paths = paths;
        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = RequestTimeout,
            };
            _ownsHttp = true;
        }
    }

    public UpdatePackage? ReadyPackage
    {
        get
        {
            lock (_lock)
            {
                return _lastToken is not null && _readyPackages.TryGetValue(_lastToken, out var package)
                    ? package
                    : null;
            }
        }
    }

    public async Task<UpdatePackage> DownloadAsync(
        VerifiedUpdate release,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsSha256(release.Sha256) || !VersionShape(release.Version))
        {
            throw new UpdatePackageException("The verified update metadata is incomplete.");
        }

        CancellationTokenSource cancellation;
        lock (_lock)
        {
            _downloadCancellation?.Cancel();
            _downloadCancellation?.Dispose();
            _downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation = _downloadCancellation;
        }

        var staging = Path.GetFullPath(_paths.UpdateStagingDirectory);
        var root = Path.GetFullPath(_paths.Root);
        if (!IsWithin(staging, root))
        {
            throw new UpdatePackageException("The update staging directory is not private app data.");
        }
        KyttoStorage.EnsurePrivate(root, staging);
        var token = $"stage.{Guid.NewGuid():N}";
        var part = Path.Combine(staging, token + ".part");
        var final = Path.Combine(staging, token + ".exe");
        try
        {
            progress?.Report(new UpdateProgress("downloading", 0, 0, null));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.RequestMessage?.RequestUri is not { } finalUri ||
                !string.Equals(finalUri.OriginalString, DownloadUri.OriginalString, StringComparison.Ordinal))
            {
                throw new UpdatePackageException("The Kytto update download could not be verified.");
            }

            var total = response.Content.Headers.ContentLength;
            if (total is > MaxPackageBytes)
            {
                throw new UpdatePackageException("The Kytto update package is too large.");
            }

            long received = 0;
            string digest;
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > MaxPackageBytes)
                    {
                        throw new UpdatePackageException("The Kytto update package is too large.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    progress?.Report(new UpdateProgress(
                        "downloading",
                        total is > 0 ? Math.Clamp((double)received / total.Value, 0, 1) : null,
                        received,
                        total));
                }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            progress?.Report(new UpdateProgress("verifying", null, received, total));
            if (!string.Equals(digest, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdatePackageException("The downloaded update hash did not match the verified manifest.");
            }

            // The temporary stream must be closed before this move: Windows keeps
            // the FileShare.None handle authoritative until disposal.
            File.Move(part, final);
            var package = new UpdatePackage(token, final, digest, received, release.Version);
            lock (_lock)
            {
                _readyPackages[token] = package;
                _lastToken = token;
            }
            progress?.Report(new UpdateProgress("staged", 1, received, total));
            return package;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new UpdateProgress("cancelled", null, 0, null));
            throw;
        }
        catch (UpdatePackageException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new UpdatePackageException("Kytto could not download the update package.", error);
        }
        finally
        {
            DeleteQuietly(part);
            lock (_lock)
            {
                if (ReferenceEquals(_downloadCancellation, cancellation))
                {
                    _downloadCancellation.Dispose();
                    _downloadCancellation = null;
                }
            }
        }
    }

    public void Cancel()
    {
        lock (_lock) _downloadCancellation?.Cancel();
    }

    /// <summary>Installs the stage named by the opaque token after revalidation.</summary>
    public Task InstallAsync(
        string token,
        string currentVersion,
        IUpdateInstaller? installer = null,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new UpdatePackageException("Choose a staged update before installing it.");
        }
        UpdatePackage package;
        lock (_lock)
        {
            package = _readyPackages.GetValueOrDefault(token)
                ?? throw new UpdatePackageException("That staged update has expired.");
        }
        return InstallCoreAsync(package, currentVersion, installer, progress, cancellationToken);
    }

    /// <summary>Compatibility overload for native callers that use the latest stage.</summary>
    public Task InstallAsync(
        string currentVersion,
        IUpdateInstaller? installer = null,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var package = ReadyPackage
            ?? throw new UpdatePackageException("Download an update before installing it.");
        return InstallCoreAsync(package, currentVersion, installer, progress, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _downloadCancellation?.Cancel();
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
        }
        if (_ownsHttp) _http.Dispose();
    }

    private async Task InstallCoreAsync(
        UpdatePackage package,
        string currentVersion,
        IUpdateInstaller? installer,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!VersionShape(package.Version) || !VersionShape(currentVersion))
        {
            throw new UpdatePackageException("The staged update version could not be verified.");
        }
        if (UpdateChecker.CompareVersions(package.Version, currentVersion) <= 0)
        {
            throw new UpdatePackageException("The staged update is not newer than the installed version.");
        }
        var staging = Path.GetFullPath(_paths.UpdateStagingDirectory);
        if (!Path.IsPathFullyQualified(package.Path) ||
            !IsWithin(Path.GetFullPath(package.Path), staging) ||
            !package.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(package.Path))
        {
            throw new UpdatePackageException("The staged update package is not available.");
        }

        FileInfo info;
        try
        {
            info = new FileInfo(package.Path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                info.Length != package.ByteCount)
            {
                throw new UpdatePackageException("The staged update package permissions are not safe.");
            }
            using var readable = new FileStream(
                package.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException error)
        {
            throw new UpdatePackageException("The staged update package is missing.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UpdatePackageException("The staged update package cannot be read.", error);
        }

        progress?.Report(new UpdateProgress("verifying", null, package.ByteCount, package.ByteCount));
        var digest = await HashFileAsync(package.Path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(digest, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Invalidate(package.Token);
            throw new UpdatePackageException("The staged update package changed and cannot be installed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new UpdateProgress("installing", null, package.ByteCount, package.ByteCount));
        await (installer ?? new WindowsUpdateInstaller()).InstallAsync(package, cancellationToken)
            .ConfigureAwait(false);
        // The signed installer owns replacement, relaunch and rollback. This event
        // tells the UI that Kytto has handed over. There is deliberately no
        // `installed` event here: only the next process can verify that the new
        // binary actually replaced this one (§8).
        progress?.Report(new UpdateProgress("relaunching", null, package.ByteCount, package.ByteCount));
    }

    private void Invalidate(string token)
    {
        lock (_lock)
        {
            if (!_readyPackages.Remove(token, out var package)) return;
            if (_lastToken == token) _lastToken = _readyPackages.Keys.LastOrDefault();
            DeleteQuietly(package.Path);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (IOException error)
        {
            throw new UpdatePackageException("The staged update package could not be read.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UpdatePackageException("The staged update package could not be read.", error);
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool VersionShape(string value)
    {
        var parts = value.Split('.', StringSplitOptions.None);
        return parts.Length is >= 1 and <= 4 && parts.All(part =>
            part.Length > 0 && part.All(character => character is >= '0' and <= '9'));
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UpdatePackageManager));
    }
}

public interface IUpdateInstaller
{
    /// <summary>
    /// Installs the package and returns only after its own replacement/relaunch
    /// verification has completed. A caller must not infer "installed" from the
    /// task being handed off to a child process (§8).
    /// </summary>
    Task InstallAsync(UpdatePackage package, CancellationToken cancellationToken);
}

/// <summary>Runs and verifies the signed first-party installer without bypassing Windows policy.</summary>
public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    public async Task InstallAsync(UpdatePackage package, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(package.Path) ||
            !package.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(package.Path) || !IsAuthenticodeTrusted(package.Path))
        {
            throw new UpdatePackageException("The update installer is not a trusted signed Windows package.");
        }

        // Do not pass shell commands or a bypass flag. The package is started only
        // after the native hash and Authenticode checks and because the user chose
        // Install. The signed installer owns replacement and rollback if relaunch
        // or installation fails.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = package.Path,
            UseShellExecute = true,
            Verb = "runas",
        }) ?? throw new UpdatePackageException("Windows could not start the update installer.");
        // Cancellation deliberately is not passed after this point: cancelling a
        // download must never abandon an installer halfway through replacement.
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            throw new UpdatePackageException("Windows could not observe the update installer.", error);
        }

        if (process.ExitCode != 0)
        {
            throw new UpdatePackageException("The signed update installer did not complete successfully.");
        }
        if (!InstalledVersionMatches(package.Version))
        {
            throw new UpdatePackageException("The installed Kytto version could not be verified.");
        }
    }

    private static bool InstalledVersionMatches(string expectedVersion)
    {
        var installedPath = Path.Combine(AppContext.BaseDirectory, "Kytto.exe");
        if (!File.Exists(installedPath)) installedPath = Environment.ProcessPath ?? "";
        if (!Path.IsPathFullyQualified(installedPath) || !File.Exists(installedPath)) return false;

        var fileVersion = FileVersionInfo.GetVersionInfo(installedPath).FileVersion;
        if (string.IsNullOrWhiteSpace(fileVersion)) return false;
        var plus = fileVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) fileVersion = fileVersion[..plus];
        try
        {
            return UpdateChecker.CompareVersions(fileVersion, expectedVersion) == 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsAuthenticodeTrusted(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var action = WintrustActionGenericVerifyV2;
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
        };
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        var data = new WinTrustData
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
            UIChoice = WintrustUiNone,
            RevocationChecks = WintrustRevokeNone,
            UnionChoice = WintrustChoiceFile,
            FileInfo = fileInfoPointer,
            StateAction = WintrustStateActionIgnore,
            ProviderFlags = WintrustSaferFlag,
        };
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    private static readonly Guid WintrustActionGenericVerifyV2 = new(
        "00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WintrustUiNone = 2;
    private const uint WintrustRevokeNone = 0;
    private const uint WintrustChoiceFile = 1;
    private const uint WintrustStateActionIgnore = 0;
    private const uint WintrustSaferFlag = 0x00000100;

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        ref Guid actionIdentifier,
        ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public string? URLReference;
        public uint ProviderFlags;
        public uint UIContext;
    }
}
