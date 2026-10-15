using System.Net;
using AubsCraft.Admin.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The skin proxy: only Mojang's texture server, only a texture path, only a small PNG; fetched once; failures told apart.</summary>
public class SkinProxyTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<Uri> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static (SkinProxyService Service, Stub Stub) Make(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var stub = new Stub(respond ?? (_ => Ok(Png)));
        return (new SkinProxyService(new HttpClient(stub), NullLogger<SkinProxyService>.Instance), stub);
    }

    [Test]
    public async Task ASkin_IsFetchedOverHttps_AndReturned()
    {
        var (service, stub) = Make();
        var (result, png) = await service.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None);
        Assert.That(result, Is.EqualTo(SkinResult.Ok));
        Assert.That(png, Is.EqualTo(Png));
        Assert.That(stub.Requests.Single().ToString(), Is.EqualTo($"https://textures.minecraft.net/texture/{Hash}"), "an http address from the plugin is fetched over https");
    }

    [Test]
    public async Task ASkin_IsFetchedOnce_HoweverManyAsk()
    {
        var (service, stub) = Make();
        for (int i = 0; i < 5; i++) await service.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None);
        await service.GetAsync($"https://textures.minecraft.net/texture/{Hash.ToUpperInvariant()}", CancellationToken.None);
        Assert.That(stub.Requests.Count, Is.EqualTo(1));
        Assert.That(service.Fetches, Is.EqualTo(1));
    }

    [TestCase("http://evil.example/texture/0123456789abcdef0123456789abcdef")]
    [TestCase("http://textures.minecraft.net.evil.example/texture/0123456789abcdef0123456789abcdef")]
    [TestCase("http://evil.example/textures.minecraft.net/texture/0123456789abcdef0123456789abcdef")]
    [TestCase("http://textures.minecraft.net@evil.example/texture/0123456789abcdef0123456789abcdef")]
    [TestCase("http://user:pass@textures.minecraft.net/texture/0123456789abcdef0123456789abcdef")]
    [TestCase("http://textures.minecraft.net:8080/texture/0123456789abcdef0123456789abcdef")]
    [TestCase("ftp://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef")]
    [TestCase("file:///etc/passwd")]
    [TestCase("http://textures.minecraft.net/")]
    [TestCase("http://textures.minecraft.net/texture/")]
    [TestCase("http://textures.minecraft.net/texture/../secret")]
    [TestCase("http://textures.minecraft.net/texture/xyz-not-hex-0123456789")]
    [TestCase("http://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef?x=1")]
    [TestCase("http://textures.minecraft.net/texture/0123456789abcdef0123456789abcdef#frag")]
    [TestCase("http://127.0.0.1/texture/0123456789abcdef0123456789abcdef")]
    [TestCase("http://localhost/texture/0123456789abcdef0123456789abcdef")]
    [TestCase("")]
    [TestCase(null)]
    [TestCase("not a url")]
    public async Task AnythingButAMojangTextureUrl_IsRefused_WithoutAnyRequest(string? url)
    {
        var (service, stub) = Make();
        var (result, png) = await service.GetAsync(url, CancellationToken.None);
        Assert.That(result, Is.EqualTo(SkinResult.Refused));
        Assert.That(png, Is.Null);
        Assert.That(stub.Requests, Is.Empty, "a refused address is never requested");
    }

    [Test]
    public async Task ATooLongUrl_IsRefused()
    {
        var (service, stub) = Make();
        var (result, _) = await service.GetAsync("http://textures.minecraft.net/texture/" + new string('a', 400), CancellationToken.None);
        Assert.That(result, Is.EqualTo(SkinResult.Refused));
        Assert.That(stub.Requests, Is.Empty);
    }

    [Test]
    public async Task ABodyThatIsNotAPng_IsRefused_AndNotCached()
    {
        var (service, stub) = Make(_ => Ok("<html>not a skin</html>"u8.ToArray()));
        Assert.That((await service.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None)).Result, Is.EqualTo(SkinResult.Refused));
        Assert.That((await service.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None)).Result, Is.EqualTo(SkinResult.Refused));
        Assert.That(stub.Requests.Count, Is.EqualTo(2), "a bad answer is not remembered as the skin");
    }

    [Test]
    public async Task ABodyThatIsTooBig_IsRefused_WhetherOrNotItAnnouncesItsSize()
    {
        var big = new byte[SkinProxyService.MaxBytes + 10];
        Png.CopyTo(big, 0);
        var (announced, _) = Make(_ => Ok(big));
        Assert.That((await announced.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None)).Result, Is.EqualTo(SkinResult.Refused));

        // Chunked: no Content-Length (a stream that cannot seek has none to announce), so the cap has to hold while reading
        var (chunked, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekable(big)) });
        Assert.That((await chunked.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None)).Result, Is.EqualTo(SkinResult.Refused));
    }

    [Test]
    public async Task AnAnnouncedHugeBody_IsRefusedWithoutReadingIt()
    {
        var (service, _) = Make(_ =>
        {
            var content = new StreamContent(new ThrowsWhenRead());
            content.Headers.ContentLength = SkinProxyService.MaxBytes * 100L;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        Assert.That((await service.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None)).Result, Is.EqualTo(SkinResult.Refused));
    }

    private sealed class NonSeekable(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowsWhenRead : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("the body was read");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Test]
    public async Task UpstreamFailures_AreToldApart()
    {
        var (missing, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.That((await missing.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None)).Result, Is.EqualTo(SkinResult.NotFound));
        var (broken, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.That((await broken.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None)).Result, Is.EqualTo(SkinResult.Unavailable));
        var (down, _) = Make(_ => throw new HttpRequestException("no route"));
        Assert.That((await down.GetAsync($"http://textures.minecraft.net/texture/{Hash}", CancellationToken.None)).Result, Is.EqualTo(SkinResult.Unavailable));
    }

    [Test]
    public async Task TheCache_IsBounded()
    {
        var (service, stub) = Make();
        for (int i = 0; i < SkinProxyService.MaxCached + 40; i++)
            await service.GetAsync($"http://textures.minecraft.net/texture/{i:x32}", CancellationToken.None);
        int before = stub.Requests.Count;
        // The newest are still remembered, the oldest were let go
        await service.GetAsync($"http://textures.minecraft.net/texture/{SkinProxyService.MaxCached + 39:x32}", CancellationToken.None);
        Assert.That(stub.Requests.Count, Is.EqualTo(before), "the newest skin is cached");
        await service.GetAsync($"http://textures.minecraft.net/texture/{0:x32}", CancellationToken.None);
        Assert.That(stub.Requests.Count, Is.EqualTo(before + 1), "the oldest was evicted");
    }
}
