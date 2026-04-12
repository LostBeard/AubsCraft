using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using AubsCraft.Admin;
using AubsCraft.Admin.Services;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.WebWorkers;
using SpawnDev.BlazorJS.JSObjects;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Initialize BlazorJS runtime (required for SpawnDev.ILGPU and JS interop)
builder.Services.AddBlazorJSRuntime(out var JS);

// Add WebWorkerService - we create dedicated workers ourselves, not via TaskPool
builder.Services.AddWebWorkerService();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<AuthStateProvider>();
builder.Services.AddScoped<ServerHubClient>();
builder.Services.AddSingleton<VoxelEngineService>();
builder.Services.AddSingleton<InputService>();
builder.Services.AddSingleton<AubsCraft.Admin.Rendering.MapRenderService>();
builder.Services.AddSingleton<WorldCacheService>();
builder.Services.AddSingleton<ChunkStreamService>();
// RenderWorkerService is NOT registered - it's created via worker.New() with OffscreenCanvas

if (JS.IsWindow)
{
    builder.RootComponents.Add<App>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");
}

await builder.Build().BlazorJSRunAsync();
