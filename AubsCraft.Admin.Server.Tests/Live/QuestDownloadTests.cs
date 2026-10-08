using System.Net;
using System.Net.Http.Json;
using AubsCraft.Admin.Server.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// The /quest no-Developer-Mode path: the headset browser downloads QuestCraft straight from /api/quest/asset and
/// Horizon OS offers to install it (what SideQuest's sdq.st/go does). The real app on real Kestrel, the real
/// QuestCraft APK from GitHub (cached in quest-cache), fetched anonymously the way the headset browser fetches it.
/// </summary>
[NonParallelizable]
public class QuestDownloadTests
{
    private sealed class AppFactory(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
        }
    }

    [Test]
    public async Task QuestCraft_DownloadsAsANamedApk_Anonymously()
    {
        var dir = TestUtil.NewTempDir();
        await using var factory = new AppFactory(new()
        {
            ["Servers:RegistryPath"] = Path.Combine(dir, "servers.json"),
            ["ActivityLog:FilePath"] = Path.Combine(dir, "activity-log.json"),
            ["Auth:UsersPath"] = Path.Combine(dir, "users.json"),
            ["Auth:CredentialsPath"] = Path.Combine(dir, "admin.json"),
            ["Auth:InviteCodesPath"] = Path.Combine(dir, "invite-codes.json"),
            ["Auth:WhitelistAuditPath"] = Path.Combine(dir, "whitelist-audit.json"),
            ["Moderation:BansPath"] = Path.Combine(dir, "bans.json"),
        });
        factory.UseKestrel(0);
        factory.StartServer();
        var address = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };

        var manifest = await http.GetFromJsonAsync<System.Text.Json.JsonElement>("api/quest/manifest");
        var qc = manifest.GetProperty("questCraft");
        var id = qc.GetProperty("id").GetString()!;
        var filename = qc.GetProperty("filename").GetString()!;
        Assert.That(filename, Does.EndWith(".apk"));

        using var resp = await http.GetAsync($"api/quest/asset/{id}", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), "no login: a friend's headset has no AubsCraft account");
        Assert.That(resp.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/vnd.android.package-archive"),
            "the type Android's installer opens");
        Assert.That(resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"'),
            Is.EqualTo(filename), "the headset browser saves it as an .apk, which Horizon OS offers to install");
        Assert.That(resp.Content.Headers.ContentLength, Is.GreaterThan(10_000_000), "the whole APK, not an error page");

        // An APK is a zip: the body really is the package, not the SPA's index.html fallback.
        await using var body = await resp.Content.ReadAsStreamAsync();
        var magic = new byte[2];
        await body.ReadExactlyAsync(magic);
        Assert.That(magic, Is.EqualTo("PK"u8.ToArray()));
    }
}
