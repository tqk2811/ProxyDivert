using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProxyDivert.Core.Vpn;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The watermark is the one file the application cannot ship and cannot generate, and every way of
// getting it wrong looks the same from the outside: a real server answers 403 and says nothing
// about why. So the cheap checks — right array, right shape, right folder — are pinned here rather
// than discovered against someone's live VPN.
public sealed class SoftEtherWatermarkStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "pd-watermark-" + Guid.NewGuid().ToString("N"));

    public SoftEtherWatermarkStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    // A stand-in for the real blob: a GIF header, filler, and the trailer the check looks for.
    private static byte[] FakeBlob(int length = 1411)
    {
        var blob = new byte[length];
        blob[0] = (byte)'G'; blob[1] = (byte)'I'; blob[2] = (byte)'F'; blob[3] = (byte)'8';
        for (int i = 4; i < length - 1; i++) blob[i] = (byte)(i % 251);
        blob[length - 1] = 0x3B;
        return blob;
    }

    private static string CSource(string arrayName, byte[] blob)
    {
        var text = new StringBuilder();
        text.AppendLine($"BYTE {arrayName}[] =").AppendLine("{");
        text.AppendLine(string.Join(", ", blob.Select(b => "0x" + b.ToString("x2"))));
        text.AppendLine("};");
        return text.ToString();
    }

    [Fact]
    public void TheWatermarkArray_IsCutOutOfTheSource()
    {
        byte[] blob = FakeBlob();

        byte[] cut = SoftEtherWatermarkStore.Extract(CSource("WaterMark", blob));

        Assert.Equal(blob, cut);
    }

    // The real file declares a second array right after the watermark. Taking it produces a file
    // that is perfectly well-formed and that a server still refuses, so the wrong one must not be
    // reachable by accident.
    [Fact]
    public void TheDecoyArrayNextToIt_IsNotTaken()
    {
        byte[] watermark = FakeBlob();
        byte[] decoy = FakeBlob(900);
        string source = CSource("WaterMark", watermark) + CSource("Saitama", decoy);

        byte[] cut = SoftEtherWatermarkStore.Extract(source);

        Assert.Equal(watermark, cut);
    }

    [Fact]
    public void ASourceWithoutTheArray_SaysSo()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SoftEtherWatermarkStore.Extract("// nothing to see here"));

        Assert.Contains("WaterMark", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotTheBlob_IsRejectedRatherThanSaved()
    {
        // Right shape, wrong content: a short array that is not a GIF at all.
        string source = "BYTE WaterMark[] = { 0x00, 0x01, 0x02 };";

        Assert.Throws<InvalidOperationException>(() => SoftEtherWatermarkStore.Extract(source));
    }

    [Theory]
    [InlineData(0)]     // empty
    [InlineData(16)]    // too short to be the blob
    public void ABlobOfTheWrongSize_DoesNotLookValid(int length)
        => Assert.False(SoftEtherWatermarkStore.LooksValid(new byte[length]));

    [Fact]
    public void ABlobMissingItsTrailer_DoesNotLookValid()
    {
        byte[] truncated = FakeBlob();
        truncated[truncated.Length - 1] = 0x00;

        Assert.False(SoftEtherWatermarkStore.LooksValid(truncated));
    }

    [Fact]
    public void NothingFetchedYet_IsFoundAsNothing()
    {
        using var store = new SoftEtherWatermarkStore(searchPaths: new[] { _folder });

        Assert.Null(store.Find());
    }

    [Fact]
    public void TheBlobOnDisk_IsFound()
    {
        string path = Path.Combine(_folder, SoftEtherWatermarkStore.FileName);
        File.WriteAllBytes(path, FakeBlob());

        using var store = new SoftEtherWatermarkStore(searchPaths: new[] { _folder });

        Assert.Equal(path, store.Find());
    }

    // A download that was cut off leaves a file behind. Treating it as present would turn a fixable
    // "press the button" into a 403 against a live server much later.
    [Fact]
    public void ATruncatedFile_CountsAsNotThere()
    {
        File.WriteAllBytes(Path.Combine(_folder, SoftEtherWatermarkStore.FileName), new byte[] { 0x47, 0x49 });

        using var store = new SoftEtherWatermarkStore(searchPaths: new[] { _folder });

        Assert.Null(store.Find());
    }

    [Fact]
    public void TheSecondSearchPath_IsUsedWhenTheFirstHasNothing()
    {
        string second = Path.Combine(_folder, "second");
        Directory.CreateDirectory(second);
        string path = Path.Combine(second, SoftEtherWatermarkStore.FileName);
        File.WriteAllBytes(path, FakeBlob());

        using var store = new SoftEtherWatermarkStore(searchPaths: new[] { Path.Combine(_folder, "first"), second });

        Assert.Equal(path, store.Find());
    }

    [Fact]
    public async Task ADownload_SavesTheBlobWhereItCanBeFound()
    {
        byte[] blob = FakeBlob();
        using var http = new HttpClient(new StubHandler(CSource("WaterMark", blob)));
        using var store = new SoftEtherWatermarkStore(http, new[] { _folder });

        string path = await store.DownloadAsync();

        Assert.Equal(Path.Combine(_folder, SoftEtherWatermarkStore.FileName), path);
        Assert.Equal(blob, File.ReadAllBytes(path));
        Assert.Equal(path, store.Find());
    }

    // Installed under Program Files, the folder beside the exe is read-only to the user running it.
    [Fact]
    public async Task AFolderThatCannotBeWritten_FallsThroughToTheNextOne()
    {
        byte[] blob = FakeBlob();
        // A file where the directory should be: creating or writing inside it always fails.
        string blocked = Path.Combine(_folder, "blocked");
        File.WriteAllText(blocked, "not a directory");
        string writable = Path.Combine(_folder, "writable");

        using var http = new HttpClient(new StubHandler(CSource("WaterMark", blob)));
        using var store = new SoftEtherWatermarkStore(http, new[] { blocked, writable });

        string path = await store.DownloadAsync();

        Assert.Equal(Path.Combine(writable, SoftEtherWatermarkStore.FileName), path);
    }

    [Fact]
    public async Task AServerThatRefuses_SaysWhereItWasAsking()
    {
        using var http = new HttpClient(new StubHandler(string.Empty, HttpStatusCode.NotFound));
        using var store = new SoftEtherWatermarkStore(http, new[] { _folder });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.DownloadAsync("https://example.invalid/WaterMark.c"));

        Assert.Contains("example.invalid", error.Message, StringComparison.Ordinal);
        Assert.Null(store.Find());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/plain"),
            });
    }
}
