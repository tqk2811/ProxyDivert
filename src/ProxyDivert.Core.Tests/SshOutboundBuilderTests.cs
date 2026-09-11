using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ProxyDivert.Core.DependencyInjection;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Ssh;
using Xunit;

namespace ProxyDivert.Core.Tests;

// What the SSH builder makes of an outbound. Building is inert — the session is only dialled when a
// connection or the supervisor asks — so none of this needs a server.
public sealed class SshOutboundBuilderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pd-ssh-builder-" + Guid.NewGuid().ToString("N"));

    public SshOutboundBuilderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private SshOutboundBuilder Builder()
        => new SshOutboundBuilder(new SshKnownHostsStore(Path.Combine(_folder, SshKnownHostsStore.FileName)));

    private static Outbound Ssh(string url, string? user = null, string? password = "secret", string? key = null)
        => new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "jump",
            Kind = OutboundKind.Ssh,
            Url = url,
            Username = user,
            Password = password,
            PrivateKeyPath = key,
        };

    private static OutboundBuildContext Context(Outbound outbound)
        => new OutboundBuildContext { Signature = OutboundSignature.Of(outbound) };

    [Fact]
    public async Task AnSshOutbound_IsASessionTheSupervisorCanHoldOpen()
    {
        Outbound outbound = Ssh("ssh://me@ssh.example.com:2222");
        SshOutboundBuilder builder = Builder();

        Assert.True(builder.BuildsManagedSource);
        IOutboundInstance instance = builder.Build(outbound, Context(outbound));
        try
        {
            Assert.Same(instance.Source, instance.Tunnel);
            Assert.Equal("me@ssh.example.com:2222", instance.Tunnel!.Endpoint);
            Assert.False(instance.SupportsUdp);
        }
        finally
        {
            await instance.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheUserBox_WinsOverTheUserInTheAddress()
    {
        Outbound outbound = Ssh("ssh://me@ssh.example.com", user: "admin");

        IOutboundInstance instance = Builder().Build(outbound, Context(outbound));
        try
        {
            Assert.Equal("admin@ssh.example.com:22", instance.Tunnel!.Endpoint);
        }
        finally
        {
            await instance.DisposeAsync();
        }
    }

    [Fact]
    public void NoUserAnywhere_IsSaidAtBuildRatherThanAtTheServer()
    {
        Outbound outbound = Ssh("ssh.example.com");

        var error = Assert.Throws<InvalidOperationException>(() => Builder().Build(outbound, Context(outbound)));
        Assert.Contains("user", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingToLogInWith_IsSaidAtBuild()
    {
        Outbound outbound = Ssh("me@ssh.example.com", password: null);

        Assert.Throws<InvalidOperationException>(() => Builder().Build(outbound, Context(outbound)));
    }

    [Fact]
    public void AKeyFileThatIsNotThere_NamesThePath()
    {
        string missing = Path.Combine(_folder, "id_ed25519");
        Outbound outbound = Ssh("me@ssh.example.com", password: null, key: missing);

        var error = Assert.Throws<FileNotFoundException>(() => Builder().Build(outbound, Context(outbound)));
        Assert.Equal(missing, error.FileName);
    }

    // A key with no passphrase and no password is a complete login on its own.
    [Fact]
    public async Task AKeyAlone_IsEnoughToBuild()
    {
        string key = Path.Combine(_folder, "id_ed25519");
        File.WriteAllText(key, "not parsed until the session is dialled");
        Outbound outbound = Ssh("me@ssh.example.com", password: null, key: key);

        IOutboundInstance instance = Builder().Build(outbound, Context(outbound));
        await instance.DisposeAsync();
    }

    // Replacing the key under the same name is a different login, so the running session has to
    // be seen as out of date.
    [Fact]
    public void ReplacingTheKeyFile_ChangesTheSignature()
    {
        string key = Path.Combine(_folder, "id_ed25519");
        File.WriteAllText(key, "one");
        Outbound outbound = Ssh("me@ssh.example.com", key: key);
        string before = OutboundSignature.Of(outbound);

        File.WriteAllText(key, "a longer second key");

        Assert.NotEqual(before, OutboundSignature.Of(outbound));
    }

    // The builder the container hands out is the one holding the container's store, and it is the
    // only one claiming the kind — a second would make the factory refuse to be built at all.
    [Fact]
    public void TheContainer_HasExactlyOneSshBuilder()
    {
        using ServiceProvider provider = new ServiceCollection().AddProxyDivert().BuildServiceProvider();

        IOutboundSourceBuilder[] builders = provider.GetServices<IOutboundSourceBuilder>()
            .Where(b => b.Kind == OutboundKind.Ssh)
            .ToArray();

        Assert.Single(builders);
        Assert.NotNull(provider.GetRequiredService<OutboundSourceFactory>());
    }
}
