using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TqkLibrary.Proxy.SshNet.Interfaces;
using TqkLibrary.Proxy.SshNet.Models;

namespace ProxyDivert.Core.Ssh;

/// <summary>
/// The SSH servers this application has met, and the key each one showed the first time: trust on
/// first use, the way <c>StrictHostKeyChecking=accept-new</c> works in OpenSSH.
/// </summary>
/// <remarks>
/// <para>
/// A server seen for the first time is written down and trusted. After that only the key written
/// down is accepted, and a different one refuses the session with a reason that names the file and
/// the line to delete — the two things a user needs when the server really was reinstalled, and the
/// thing that stops them when it was not.
/// </para>
/// <para>
/// The file is OpenSSH's own known_hosts format (<c>host keytype base64</c>, and
/// <c>[host]:port</c> for a port other than 22), so it can be read and edited with the tools people
/// already have. It is this application's file, not <c>~/.ssh/known_hosts</c>: nothing here writes
/// to a file that belongs to OpenSSH.
/// </para>
/// <para>
/// The location is fixed rather than a setting, for the same reason the SoftEther watermark's is:
/// a setting would have to be threaded through the registry, the signature and the build context.
/// It is also deliberately not a field on the outbound — recording a key would then change the
/// outbound's signature and drop the very session that recorded it.
/// </para>
/// </remarks>
public sealed class SshKnownHostsStore : ISshHostKeyVerifier
{
    public const string FileName = "known_hosts";

    private const int DefaultPort = 22;

    // Two sessions to two servers can finish their handshakes at the same moment, and both may have
    // a line to add.
    private readonly object _lock = new object();
    private readonly ILogger _logger;

    /// <param name="path">
    /// The file to use. Defaults to %LOCALAPPDATA%\ProxyDivert\known_hosts: always writable by the
    /// user who runs the application, which the folder next to an installed executable is not.
    /// </param>
    public SshKnownHostsStore(string? path = null, ILoggerFactory? loggerFactory = null)
    {
        FilePath = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? DefaultPath() : path);
        _logger = loggerFactory?.CreateLogger<SshKnownHostsStore>() ?? (ILogger)NullLogger.Instance;
    }

    public string FilePath { get; }

    public SshHostKeyVerdict Verify(SshHostKey hostKey)
    {
        if (hostKey is null) throw new ArgumentNullException(nameof(hostKey));

        string entry = HostEntry(hostKey.Host, hostKey.Port);
        lock (_lock)
        {
            List<KnownKey> known = Read(entry);
            if (known.Count == 0)
            {
                Append(entry, hostKey);
                _logger.LogInformation(
                    "first connection to {Host}: trusting its {Algorithm} key SHA256:{Fingerprint} from now on ({File})",
                    entry, hostKey.Algorithm, hostKey.FingerprintSha256, FilePath);
                return SshHostKeyVerdict.Trust();
            }

            foreach (KnownKey key in known)
            {
                if (string.Equals(key.FingerprintSha256, hostKey.FingerprintSha256, StringComparison.Ordinal))
                    return SshHostKeyVerdict.Trust();
            }

            KnownKey recorded = known[0];
            return SshHostKeyVerdict.Reject(
                $"it is not the key recorded for {entry} ({recorded.Algorithm} SHA256:{recorded.FingerprintSha256}, "
                + $"line {recorded.Line} of {FilePath}). If the server was reinstalled, delete that line and "
                + "connect again; otherwise something between here and the server is answering in its place.");
        }
    }

    /// <summary>
    /// How a server is written in the file: the bare host on port 22, <c>[host]:port</c> otherwise —
    /// OpenSSH's own spelling, so a line copied from either file means the same in the other.
    /// </summary>
    public static string HostEntry(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host required", nameof(host));
        string name = host.Trim().Trim('[', ']').ToLowerInvariant();
        return port == DefaultPort ? name : $"[{name}]:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    private List<KnownKey> Read(string entry)
    {
        var found = new List<KnownKey>();
        if (!File.Exists(FilePath)) return found;

        string[] lines = File.ReadAllLines(FilePath);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            // Comments, and the markers (@cert-authority, @revoked) and hashed host names this file
            // never writes: a line that cannot be matched is skipped rather than guessed at.
            if (line.Length == 0 || line[0] == '#' || line[0] == '@' || line[0] == '|') continue;

            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !Names(fields[0], entry)) continue;

            string? fingerprint = Fingerprint(fields[2]);
            if (fingerprint is null) continue;

            found.Add(new KnownKey(fields[1], fingerprint, i + 1));
        }
        return found;
    }

    private static bool Names(string hosts, string entry)
    {
        foreach (string host in hosts.Split(','))
        {
            if (string.Equals(host, entry, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // The fingerprint of a recorded key is worked out from the key itself rather than stored next
    // to it, so a line pasted from OpenSSH's file is compared exactly like one written here.
    private static string? Fingerprint(string base64)
    {
        byte[] blob;
        try { blob = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }

        using var sha = SHA256.Create();
        return SshHostKey.NormalizeFingerprint(Convert.ToBase64String(sha.ComputeHash(blob)));
    }

    private void Append(string entry, SshHostKey hostKey)
    {
        string? folder = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // A file edited by hand may have lost its last newline, and appending straight after it
        // would glue the new entry onto somebody's line.
        string prefix = EndsWithNewLine() ? string.Empty : Environment.NewLine;
        File.AppendAllText(
            FilePath,
            $"{prefix}{entry} {hostKey.Algorithm} {Convert.ToBase64String(hostKey.GetKeyBlob())}{Environment.NewLine}");
    }

    private bool EndsWithNewLine()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length == 0) return true;

        using FileStream stream = info.OpenRead();
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    private static string DefaultPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ProxyDivert", FileName);
    }

    private sealed class KnownKey
    {
        public KnownKey(string algorithm, string fingerprintSha256, int line)
        {
            Algorithm = algorithm;
            FingerprintSha256 = fingerprintSha256;
            Line = line;
        }

        public string Algorithm { get; }

        public string FingerprintSha256 { get; }

        public int Line { get; }
    }
}
