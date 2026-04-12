using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using AubsCraft.Admin;
using AubsCraft.Admin.Services;
using SpawnDev.BlazorJS;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Initialize BlazorJS runtime (required for SpawnDev.ILGPU and JS interop)
builder.Services.AddBlazorJSRuntime(out var JS);

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<AuthStateProvider>();
builder.Services.AddScoped<ServerHubClient>();
builder.Services.AddSingleton<VoxelEngineService>();
builder.Services.AddSingleton<InputService>();
builder.Services.AddSingleton<AubsCraft.Admin.Rendering.MapRenderService>();

if (JS.IsWindow)
{
    builder.RootComponents.Add<App>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");
}

await builder.Build().BlazorJSRunAsync();
