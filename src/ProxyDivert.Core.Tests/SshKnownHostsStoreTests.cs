using System;
using System.IO;
using System.Security.Cryptography;
using ProxyDivert.Core.Ssh;
using TqkLibrary.Proxy.SshNet.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Trust on first use: the first key a server shows is written down, and from then on it is the only
// one accepted. The file is OpenSSH's known_hosts format so that a person can read and fix it.
public sealed class SshKnownHostsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pd-known-hosts-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, SshKnownHostsStore.FileName);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    // What SSH.NET hands over: the key blob, and its SHA-256 in Base64 with no padding.
    private static SshHostKey Key(string host = "ssh.example.com", int port = 22, byte seed = 1)
    {
        byte[] blob = new byte[51];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(seed + i);
        using var sha = SHA256.Create();
        string fingerprint = Convert.ToBase64String(sha.ComputeHash(blob)).TrimEnd('=');
        return new SshHostKey(host, port, "ssh-ed25519", fingerprint, blob);
    }

    [Fact]
    public void AServerSeenForTheFirstTime_IsTrustedAndWrittenDown()
    {
        var store = new SshKnownHostsStore(FilePath);
        SshHostKey key = Key();

        Assert.True(store.Verify(key).IsTrusted);

        string line = Assert.Single(File.ReadAllLines(FilePath));
        Assert.Equal($"ssh.example.com ssh-ed25519 {Convert.ToBase64String(key.GetKeyBlob())}", line);
    }

    [Fact]
    public void TheSameKeyNextTime_IsTrustedWithoutAddingAnything()
    {
        var store = new SshKnownHostsStore(FilePath);
        store.Verify(Key());

        Assert.True(new SshKnownHostsStore(FilePath).Verify(Key()).IsTrusted);
        Assert.Single(File.ReadAllLines(FilePath));
    }

    // The whole point of the store. The reason has to say where the recorded key is, because the
    // honest case — a reinstalled server — is fixed by deleting exactly that line.
    [Fact]
    public void ADifferentKeyForAKnownServer_IsRefusedWithTheFileAndLineToFix()
    {
        var store = new SshKnownHostsStore(FilePath);
        store.Verify(Key(seed: 1));

        SshHostKeyVerdict verdict = store.Verify(Key(seed: 2));

        Assert.False(verdict.IsTrusted);
        Assert.Contains(FilePath, verdict.Reason);
        Assert.Contains("line 1", verdict.Reason);
        // Refusing must not quietly record the new key as well.
        Assert.Single(File.ReadAllLines(FilePath));
    }

    [Fact]
    public void ThePortIsPartOfWhoTheServerIs()
    {
        var store = new SshKnownHostsStore(FilePath);
        store.Verify(Key(port: 22, seed: 1));

        Assert.True(store.Verify(Key(port: 2222, seed: 2)).IsTrusted);
        Assert.Contains("[ssh.example.com]:2222 ssh-ed25519 ", File.ReadAllText(FilePath));
    }

    // A line copied out of ~/.ssh/known_hosts — several names for one key, other hosts around it,
    // comments, a hashed entry — must be read the same way as one this store wrote.
    [Fact]
    public void ALinePastedFromOpenSsh_IsHonoured()
    {
        SshHostKey key = Key(seed: 7);
        Directory.CreateDirectory(_folder);
        File.WriteAllLines(FilePath, new[]
        {
            "# copied from my laptop",
            "|1|c2FsdA==|aGFzaA== ssh-ed25519 AAAA",
            $"other.example.com ssh-ed25519 {Convert.ToBase64String(Key(seed: 9).GetKeyBlob())}",
            $"ssh.example.com,10.0.0.5 ssh-ed25519 {Convert.ToBase64String(key.GetKeyBlob())} me@laptop",
        });

        Assert.True(new SshKnownHostsStore(FilePath).Verify(key).IsTrusted);
        Assert.False(new SshKnownHostsStore(FilePath).Verify(Key(seed: 8)).IsTrusted);
    }

    [Fact]
    public void AFileThatLostItsLastNewline_DoesNotGetTheNewEntryGluedOn()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, $"other.example.com ssh-ed25519 {Convert.ToBase64String(Key(seed: 9).GetKeyBlob())}");

        new SshKnownHostsStore(FilePath).Verify(Key());

        Assert.Equal(2, File.ReadAllLines(FilePath).Length);
    }

    [Theory]
    [InlineData("SSH.Example.com", 22, "ssh.example.com")]
    [InlineData("ssh.example.com", 2222, "[ssh.example.com]:2222")]
    [InlineData("[2001:db8::1]", 22, "2001:db8::1")]
    [InlineData("2001:db8::1", 2200, "[2001:db8::1]:2200")]
    public void AServerIsWrittenTheWayOpenSshWritesIt(string host, int port, string expected)
    {
        Assert.Equal(expected, SshKnownHostsStore.HostEntry(host, port));
    }
}
