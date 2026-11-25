using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Declutter.Core.Updates;

namespace Declutter.Core.Tests;

public class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "declutter-update-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly byte[] FakeExe = [(byte)'M', (byte)'Z', 1, 2, 3, 4, 5, 6, 7, 8];
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string ReleaseJson(string tag = "v1.0.15", bool prerelease = false, string? digest = null) => $$"""
        {
          "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/evanavery-svg/declutter/releases/tag/{{tag}}",
          "body": "Fixes and improvements.",
          "assets": [
            { "name": "Declutter.exe", "size": 10, "digest": {{(digest == null ? "null" : $"\"sha256:{digest}\"")}},
              "browser_download_url": "https://github.com/evanavery-svg/declutter/releases/download/{{tag}}/Declutter.exe" },
            { "name": "Declutter-ARM64.exe", "size": 10,
              "browser_download_url": "https://github.com/evanavery-svg/declutter/releases/download/{{tag}}/Declutter-ARM64.exe" }
          ]
        }
        """;

    [Fact]
    public void Release_IsParsed()
    {
        var r = Releases.Parse(ReleaseJson(digest: Sha(FakeExe)))!;
        Assert.Equal(new Version(1, 0, 15), r.Version);
        Assert.Equal(2, r.Assets.Count);
        Assert.Equal(Sha(FakeExe), r.Assets[0].Sha256);
        Assert.Null(r.Assets[1].Sha256);
        Assert.Equal("Fixes and improvements.", r.Notes);
    }

    [Fact]
    public void Prereleases_AreIgnored() => Assert.Null(Releases.Parse(ReleaseJson(prerelease: true)));

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("nonsense")]
    [InlineData("""{"tag_name":"latest"}""")]
    public void BadReleases_AreIgnored(string json) => Assert.Null(Releases.Parse(json));

    [Theory]
    [InlineData("v1.0.14", 1, 0, 14)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.0.0", 2, 0, 0)]
    public void Tags_Parse(string tag, int a, int b, int c) => Assert.Equal(new Version(a, b, c), Releases.ParseVersion(tag));

    [Theory]
    [InlineData("1.0.15", "1.0.14.0", true)]
    [InlineData("1.0.14", "1.0.14.0", false)]
    [InlineData("1.0.9", "1.0.14.0", false)]
    [InlineData("1.1.0", "1.0.99.0", true)]
    public void Newer_ComparesMajorMinorBuild(string latest, string current, bool newer) =>
        Assert.Equal(newer, Releases.IsNewer(Version.Parse(latest), Version.Parse(current)));

    [Fact]
    public void Asset_MatchesTheProcessor()
    {
        var r = Releases.Parse(ReleaseJson())!;
        Assert.Equal("Declutter.exe", Releases.PickAsset(r, Architecture.X64)!.Name);
        Assert.Equal("Declutter-ARM64.exe", Releases.PickAsset(r, Architecture.Arm64)!.Name);
    }

    [Theory]
    [InlineData("sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("sha512:abc", null)]
    [InlineData("sha256:tooshort", null)]
    [InlineData(null, null)]
    public void Digest_Parsing(string? digest, string? expected) => Assert.Equal(expected, Releases.ParseSha256(digest));

    // --- Downloading --------------------------------------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static Updater Serving(byte[] body, string? finalUrl = null) => new(new HttpClient(new FakeHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
        RequestMessage = finalUrl == null ? req : new HttpRequestMessage(HttpMethod.Get, finalUrl),
    })));

    private static ReleaseAsset Asset(string? sha, long size = 10, string url = "https://github.com/evanavery-svg/declutter/releases/download/v1.0.15/Declutter.exe") =>
        new("Declutter.exe", url, size, sha);

    [Fact]
    public async Task Download_VerifiedFile_IsKept()
    {
        string dest = Path.Combine(_dir, "Declutter.exe.download");
        var progress = new List<double>();
        await Serving(FakeExe).DownloadAsync(Asset(Sha(FakeExe)), dest, new SyncProgress<double>(progress.Add), default);
        Assert.Equal(FakeExe, await File.ReadAllBytesAsync(dest));
        Assert.Equal(1.0, progress.Last());
    }

    [Fact]
    public async Task Download_WithWrongDigest_IsRejectedAndDeleted()
    {
        string dest = Path.Combine(_dir, "x.download");
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Serving(FakeExe).DownloadAsync(Asset(new string('0', 64)), dest, null, default));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Download_Truncated_IsRejected()
    {
        string dest = Path.Combine(_dir, "x.download");
        await Assert.ThrowsAsync<UpdateException>(() => Serving(FakeExe[..5]).DownloadAsync(Asset(null), dest, null, default));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Download_ThatIsNotAProgram_IsRejected()
    {
        byte[] html = "<html>...."u8.ToArray(); // 10 bytes, like the published size, but not a program
        await Assert.ThrowsAsync<UpdateException>(() => Serving(html).DownloadAsync(Asset(null), Path.Combine(_dir, "x"), null, default));
    }

    [Theory]
    [InlineData("http://github.com/evanavery-svg/declutter/releases/download/v1/Declutter.exe")]
    [InlineData("https://evil.example.com/Declutter.exe")]
    public async Task Download_FromAnywhereButGitHubOverHttps_IsRefused(string url) =>
        await Assert.ThrowsAsync<UpdateException>(() => Serving(FakeExe).DownloadAsync(Asset(null, url: url), Path.Combine(_dir, "x"), null, default));

    [Fact]
    public async Task Download_RedirectedAwayFromGitHub_IsRefused() =>
        await Assert.ThrowsAsync<UpdateException>(() =>
            Serving(FakeExe, finalUrl: "https://evil.example.com/x.exe").DownloadAsync(Asset(null), Path.Combine(_dir, "x"), null, default));

    [Fact]
    public async Task LatestRelease_IsFetchedWithAUserAgent()
    {
        HttpRequestMessage? seen = null;
        var updater = new Updater(new HttpClient(new FakeHandler(req =>
        {
            seen = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()) };
        })));

        var release = await updater.GetLatestAsync(default);

        Assert.Equal(new Version(1, 0, 15), release!.Version);
        Assert.Equal(Releases.LatestReleaseApi, seen!.RequestUri!.ToString());
        Assert.NotEmpty(seen.Headers.UserAgent);
    }

    [Fact]
    public async Task LatestRelease_HttpError_IsNull()
    {
        var updater = new Updater(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))));
        Assert.Null(await updater.GetLatestAsync(default));
    }

    // --- Swapping -----------------------------------------------------------------------------------

    [Fact]
    public void Swap_ReplacesTheProgram_AndKeepsTheOldOneUntilCleanup()
    {
        string exe = Path.Combine(_dir, "Declutter.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(SelfReplace.DownloadPathFor(exe), "new");

        SelfReplace.Swap(exe, SelfReplace.DownloadPathFor(exe));

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(SelfReplace.OldPathFor(exe)));

        SelfReplace.CleanUp(exe);
        Assert.False(File.Exists(SelfReplace.OldPathFor(exe)));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void Swap_WithoutADownload_LeavesTheProgramAlone()
    {
        string exe = Path.Combine(_dir, "Declutter.exe");
        File.WriteAllText(exe, "old");
        Assert.Throws<UpdateException>(() => SelfReplace.Swap(exe, SelfReplace.DownloadPathFor(exe)));
        Assert.Equal("old", File.ReadAllText(exe));
    }
}
