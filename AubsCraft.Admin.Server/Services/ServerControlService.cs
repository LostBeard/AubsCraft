using System.Diagnostics;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Controls the Minecraft server process via systemd.
/// Requires sudoers entry for the running user.
/// </summary>
public class ServerControlService
{
    private readonly ILogger<ServerControlService> _logger;
    private readonly string _serviceName;

    public ServerControlService(IConfiguration configuration, ILogger<ServerControlService> logger)
    {
        _logger = logger;
        _serviceName = configuration.GetValue<string>("Minecraft:ServiceName") ?? "minecraft";
    }

    public async Task<(bool success, string output)> RestartAsync()
    {
        _logger.LogInformation("Restarting Minecraft server...");
        return await RunSystemctlAsync("restart");
    }

    public async Task<(bool success, string output)> StopAsync()
    {
        _logger.LogInformation("Stopping Minecraft server...");
        return await RunSystemctlAsync("stop");
    }

    public async Task<(bool success, string output)> StartAsync()
    {
        _logger.LogInformation("Starting Minecraft server...");
        return await RunSystemctlAsync("start");
    }

    public async Task<(bool success, string output)> GetStatusAsync()
    {
        return await RunSystemctlAsync("status");
    }

    private async Task<(bool success, string output)> RunSystemctlAsync(string action)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sudo",
                    Arguments = $"systemctl {action} {_serviceName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var combinedOutput = string.IsNullOrEmpty(output) ? error : output;
            var success = process.ExitCode == 0;

            if (success)
                _logger.LogInformation("systemctl {Action} {Service}: success", action, _serviceName);
            else
                _logger.LogWarning("systemctl {Action} {Service} failed: {Output}", action, _serviceName, combinedOutput);

            return (success, combinedOutput);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run systemctl {Action}", action);
            return (false, $"Error: {ex.Message}");
        }
    }
}
