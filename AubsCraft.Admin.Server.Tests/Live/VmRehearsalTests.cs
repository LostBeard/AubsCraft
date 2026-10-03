using System.Net.Http.Json;
using AubsCraft.Admin.Server.Hubs;
using AubsCraft.Admin.Server.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// The production cutover, driven exactly as the Proxy page drives it, against a panel running in a real
/// Ubuntu 24.04 laid out like the VM (server-setup/rehearsal/provision-vm-like.sh, then setup-multiserver.sh).
/// Explicit: needs that machine at AUBS_REHEARSAL_URL (e.g. http://localhost:5080, a WSL distro).
/// </summary>
[Explicit("Needs a VM-like Ubuntu with the panel at AUBS_REHEARSAL_URL")]
[NonParallelizable]
public class VmRehearsalTests
{
    private static string BaseUrl => Environment.GetEnvironmentVariable("AUBS_REHEARSAL_URL") ?? "http://localhost:5080";

    [Test]
    public async Task Cutover_ThroughThePanel_OnTheRealOs()
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(BaseUrl) };
        await http.PostAsJsonAsync("/api/auth/setup", new { Username = "owner", Password = "rehearsal" });
        var login = await http.PostAsJsonAsync("/api/auth/login", new { Username = "owner", Password = "rehearsal" });
        Assert.That(login.IsSuccessStatusCode, Is.True, await login.Content.ReadAsStringAsync());
        var cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(BaseUrl), "hubs/server"), o => o.Headers["Cookie"] = cookie)
            .Build();
        var progress = new List<OperationProgressDto>();
        var done = new TaskCompletionSource<OperationProgressDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<OperationProgressDto>("ReceiveOperationProgress", p =>
        {
            lock (progress) progress.Add(p);
            TestContext.Progress.WriteLine(p.Message);
            if (p.Done) done.TrySetResult(p);
        });
        await hub.StartAsync();

        var preview = await hub.InvokeAsync<CutoverPreviewDto>("GetCutoverPreview", "aubscraft");
        Assert.That(preview.Prerequisites.Where(p => !p.Ok).Select(p => p.What), Is.Empty, "the machine is ready");
        var start = await hub.InvokeAsync<HubResult>("StartProxyCutover", "aubscraft", preview.NewGamePort, preview.NewVoicePort);
        Assert.That(start.Success, Is.True, start.Message);

        var result = await done.Task.WaitAsync(TimeSpan.FromMinutes(15));
        Assert.That(result.Failed, Is.False, string.Join("\n", progress.Select(p => p.Message)));

        var status = await hub.InvokeAsync<ProxyStatusDto?>("GetProxyStatus");
        Assert.That(status?.Online, Is.True, "the panel reaches the proxy over Velocircon");
        var servers = await hub.InvokeAsync<List<AubsCraft.Admin.Server.Models.ServerSummaryDto>>("GetServers");
        Assert.That(servers.Single().GamePort, Is.EqualTo(preview.NewGamePort));
        await TestUtil.WaitUntilAsync(async () =>
            (await http.GetFromJsonAsync<AubsCraft.Admin.Server.Models.PublicStatusDto>("/api/public/status"))!.Connected,
            TimeSpan.FromSeconds(60), "the panel sees the server again behind the proxy");
    }
}
