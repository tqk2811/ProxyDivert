using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ProxyDivert.Core.Vpn;

/// <summary>
/// Where the SoftEther watermark blob lives on this machine, and how to go and get it.
/// </summary>
/// <remarks>
/// A real SoftEther server rejects a session whose opening POST does not carry the genuine
/// watermark, and that blob is GPL data from the SoftEther sources that this application cannot
/// ship: shipping it would be distributing part of a GPL work, while fetching it onto the machine
/// that runs it is not distribution at all. So the blob is something the user acquires, once, and
/// this class is the whole of that story — where it may be, whether what is there is really it, and
/// the download that puts it there.
///
/// The location is fixed rather than configurable, which is what keeps this out of the outbound
/// build path entirely: no setting to thread through the registry, the signature and the build
/// context. A user who wants the blob somewhere else still says so per outbound, with the
/// <c>Watermark =</c> line of a .vpn file, and that line keeps winning over anything found here.
/// </remarks>
public sealed class SoftEtherWatermarkStore : IDisposable
{
    /// <summary>The name the blob is saved under, matching tools/Get-SoftEtherWatermark.ps1.</summary>
    public const string FileName = "softether-watermark.dat";

    /// <summary>The SoftEther source file carrying the blob as a C array.</summary>
    public const string DefaultSourceUrl =
        "https://raw.githubusercontent.com/SoftEtherVPN/SoftEtherVPN/master/src/Cedar/WaterMark.c";

    // The blob is a small GIF (1411 bytes in the sources as of 2026-09). The range is wide on
    // purpose: it is here to catch a cut that took the wrong array or stopped early, not to pin a
    // number that upstream is free to change.
    private const int MinimumLength = 512;
    private const int MaximumLength = 64 * 1024;

    // The same file also holds a second, decoy array. Anchoring on the name matters: taking the
    // wrong one produces a perfectly valid file that the server still answers 403 to, which is a
    // failure with nothing to see.
    private static readonly Regex ArrayPattern = new Regex(
        @"BYTE\s+WaterMark\s*\[\s*\]\s*=\s*\{(?<body>.*?)\}\s*;",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BytePattern = new Regex(@"0x[0-9A-Fa-f]{2}", RegexOptions.Compiled);

    private readonly bool _ownsHttpClient;
    private HttpClient? _httpClient;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<string> _searchPaths;
    private bool _disposed;

    /// <param name="httpClient">
    /// Borrowed when given — the caller keeps owning it — and created (and disposed) here otherwise.
    /// </param>
    /// <param name="searchPaths">
    /// Where to look, most preferred first. Defaults to the executable's own folder and then
    /// %LOCALAPPDATA%\ProxyDivert, so an installation in a read-only folder still has somewhere to
    /// keep it.
    /// </param>
    public SoftEtherWatermarkStore(
        HttpClient? httpClient = null,
        IEnumerable<string>? searchPaths = null,
        ILoggerFactory? loggerFactory = null)
    {
        // Made on first use rather than in the constructor: the build path asks this class where the
        // blob is on every VPN outbound it builds, and none of those questions need a socket.
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient;
        _logger = loggerFactory?.CreateLogger<SoftEtherWatermarkStore>() ?? (ILogger)NullLogger.Instance;

        string[] paths = (searchPaths ?? DefaultSearchPaths())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray();
        if (paths.Length == 0)
            throw new ArgumentException("At least one search path is needed.", nameof(searchPaths));

        _searchPaths = paths;
    }

    /// <summary>The folders searched, most preferred first.</summary>
    public IReadOnlyList<string> SearchPaths => _searchPaths;

    /// <summary>
    /// The blob on this machine, or null when it has not been fetched yet. A file that is there but
    /// is not the blob counts as not being there — a truncated download should read as "fetch it",
    /// not as a working setup that fails much later against a live server.
    /// </summary>
    public string? Find()
    {
        foreach (string directory in _searchPaths)
        {
            string candidate = Path.Combine(directory, FileName);
            byte[] content;
            try
            {
                if (!File.Exists(candidate)) continue;
                content = File.ReadAllBytes(candidate);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (LooksValid(content)) return candidate;

            _logger.LogWarning(
                "Ignoring '{Path}': it is not the SoftEther watermark blob ({Length} bytes).",
                candidate, content.Length);
        }

        return null;
    }

    /// <summary>
    /// Fetches the blob and saves it, returning where it landed. Throws with a message the user can
    /// act on when the source cannot be read or nothing writable is left to save it in.
    /// </summary>
    public async Task<string> DownloadAsync(
        string? sourceUrl = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        string url = string.IsNullOrWhiteSpace(sourceUrl) ? DefaultSourceUrl : sourceUrl!;
        _logger.LogInformation("Fetching the SoftEther watermark from {Url}.", url);

        HttpClient http = _httpClient ??= new HttpClient();

        string source;
        try
        {
            using HttpResponseMessage response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            source = await ReadStringAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Could not download the SoftEther watermark from {url}: {ex.Message}", ex);
        }

        byte[] blob = Extract(source);
        string path = Save(blob);

        _logger.LogInformation("Saved the SoftEther watermark to {Path} ({Length} bytes).", path, blob.Length);
        return path;
    }

    /// <summary>
    /// Cuts the watermark array out of the SoftEther source file. Throws when the array is not there
    /// or what came out is not the blob.
    /// </summary>
    public static byte[] Extract(string source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        Match match = ArrayPattern.Match(source);
        if (!match.Success)
            throw new InvalidOperationException(
                "The SoftEther source does not contain a 'BYTE WaterMark[]' array — upstream may have "
                + "changed how it is declared.");

        MatchCollection literals = BytePattern.Matches(match.Groups["body"].Value);
        var blob = new byte[literals.Count];
        for (int i = 0; i < literals.Count; i++)
            blob[i] = Convert.ToByte(literals[i].Value.Substring(2), 16);

        if (!LooksValid(blob))
            throw new InvalidOperationException(
                $"What was cut out of the SoftEther source is {blob.Length} bytes and does not look like "
                + "the watermark blob — the wrong array was probably taken.");

        return blob;
    }

    /// <summary>
    /// Whether these bytes are the watermark: a small GIF, which is what the blob is despite the
    /// <c>Content-Type: image/jpeg</c> the protocol sends it under.
    /// </summary>
    public static bool LooksValid(byte[]? blob)
    {
        if (blob is null || blob.Length < MinimumLength || blob.Length > MaximumLength) return false;
        if (blob[blob.Length - 1] != 0x3B) return false;
        return blob[0] == (byte)'G' && blob[1] == (byte)'I' && blob[2] == (byte)'F' && blob[3] == (byte)'8';
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient?.Dispose();
    }

    private static IEnumerable<string> DefaultSearchPaths()
    {
        yield return AppContext.BaseDirectory;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "ProxyDivert");
    }

    // Saves into the first search path that will take it: an installation under Program Files is
    // read-only to the user who runs it, and failing there rather than falling back would make the
    // button useless on exactly the machines that install the application properly.
    private string Save(byte[] blob)
    {
        var failures = new List<string>();
        foreach (string directory in _searchPaths)
        {
            string path = Path.Combine(directory, FileName);
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(path, blob);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "Downloaded the SoftEther watermark but could not save it anywhere: "
            + string.Join("; ", failures));
    }

    private static async Task<string> ReadStringAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
#if NET5_0_OR_GREATER
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SoftEtherWatermarkStore));
    }
}
