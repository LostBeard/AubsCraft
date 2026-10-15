using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using AubsCraft.Admin;
using AubsCraft.Admin.Services;
using SpawnDev.GameUI;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.WebWorkers;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.TangoADB;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Initialize BlazorJS runtime (required for SpawnDev.ILGPU and JS interop)
builder.Services.AddSpawnJSRuntime(out var JS);

// Add WebWorkerService - we create dedicated workers ourselves, not via TaskPool
builder.Services.AddWebWorkerService();

// TangoADB - ADB over WebUSB for the /quest installer page
builder.Services.AddTangoADB();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<AuthStateProvider>();
builder.Services.AddScoped<ServerHubClient>();
builder.Services.AddSingleton<GpuContextService>();
builder.Services.AddSingleton<InputService>();
builder.Services.AddSingleton<AubsCraft.Admin.Rendering.MapRenderService>();
builder.Services.AddSingleton<WorldCacheService>();
builder.Services.AddSingleton<RenderWorkerHost>();
// GameUI draws the viewer's HUD on the GPU, in the render worker (the AubsCraft theme is the blocky Minecraft look)
builder.Services.AddGameUI(UITheme.AubsCraft);
builder.Services.AddSingleton<AubsCraft.Admin.Rendering.ViewerHud>();
// RenderWorkerService is NOT registered here - it's created via worker.New() with OffscreenCanvas

if (JS.IsWindow)
{
    builder.RootComponents.Add<App>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");
}

await builder.Build().SpawnJSRunAsync();
