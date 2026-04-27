using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Sends notification emails for interesting AubsCraft events
/// (sign-ups, code redemptions, whitelist adds, server lifecycle, first
/// player join of the day). All sends are best-effort and async; failures
/// are logged but never propagate to the caller.
/// </summary>
public class EmailNotificationService : IDisposable
{
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailNotificationService> _logger;
    private readonly ActivityLogService _activityLog;
    private readonly ConcurrentDictionary<string, DateTime> _playerJoinNotified = new();
    // Tracks service start time so we don't email about historical events
    // replayed when ActivityLogService reloads its log on restart.
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    public EmailNotificationService(
        IConfiguration config,
        ILogger<EmailNotificationService> logger,
        ActivityLogService activityLog)
    {
        _settings = config.GetSection("Email").Get<EmailSettings>() ?? new EmailSettings();
        _logger = logger;
        _activityLog = activityLog;

        if (_settings.Enabled && _settings.NotifyOnPlayerJoin)
        {
            _activityLog.EventAdded += OnActivityEvent;
            _logger.LogInformation("EmailNotificationService: subscribed to player-join events");
        }
        else if (!_settings.Enabled)
        {
            _logger.LogInformation("EmailNotificationService: disabled (set Email:Enabled=true to enable)");
        }
    }

    public bool IsEnabled => _settings.Enabled;

    public Task NotifySignupAsync(string username, string inviteCode)
    {
        if (!_settings.Enabled || !_settings.NotifyOnSignup) return Task.CompletedTask;
        return SendAsync(
            subject: $"[AubsCraft] New signup: {username}",
            body: $"A new web account was created.\n\nUsername: {username}\nInvite code: {inviteCode}\nTime: {DateTime.UtcNow:u}");
    }

    public Task NotifyInviteCreatedAsync(string code, int maxUses, string createdBy)
    {
        if (!_settings.Enabled || !_settings.NotifyOnInviteCreated) return Task.CompletedTask;
        return SendAsync(
            subject: $"[AubsCraft] Invite code created: {code}",
            body: $"A new invite code was created.\n\nCode: {code}\nMax uses: {maxUses}\nCreated by: {createdBy}\nTime: {DateTime.UtcNow:u}");
    }

    public Task NotifyWhitelistAddAsync(string mcUsername, string platform, string addedByWebUser)
    {
        if (!_settings.Enabled || !_settings.NotifyOnWhitelistAdd) return Task.CompletedTask;
        return SendAsync(
            subject: $"[AubsCraft] {mcUsername} added to whitelist by {addedByWebUser}",
            body: $"A Minecraft account was added to the server whitelist via the web admin.\n\nMC username: {mcUsername}\nPlatform: {platform}\nAdded by web user: {addedByWebUser}\nTime: {DateTime.UtcNow:u}");
    }

    public Task NotifyServerLifecycleAsync(string action, string requestedBy)
    {
        if (!_settings.Enabled || !_settings.NotifyOnServerLifecycle) return Task.CompletedTask;
        return SendAsync(
            subject: $"[AubsCraft] Minecraft server {action}",
            body: $"The Minecraft server was {action}.\n\nRequested by: {requestedBy}\nTime: {DateTime.UtcNow:u}");
    }

    private void OnActivityEvent(ActivityEventDto evt)
    {
        if (evt.Type != ActivityEventType.PlayerJoin) return;
        if (string.IsNullOrEmpty(evt.PlayerName)) return;
        // Skip historical events replayed on startup.
        if (evt.Timestamp.ToUniversalTime() < _startedAtUtc) return;

        // Throttle: one notification per player per UTC day.
        var key = $"{DateTime.UtcNow:yyyy-MM-dd}-{evt.PlayerName.ToLowerInvariant()}";
        if (!_playerJoinNotified.TryAdd(key, DateTime.UtcNow))
            return;

        _ = SendAsync(
            subject: $"[AubsCraft] {evt.PlayerName} joined the server",
            body: $"{evt.PlayerName} just joined the Minecraft server (first time today).\n\nTime: {evt.Timestamp:u}\nDetails: {evt.Details}");
    }

    private async Task SendAsync(string subject, string body)
    {
        if (string.IsNullOrEmpty(_settings.SmtpHost)
            || string.IsNullOrEmpty(_settings.From)
            || string.IsNullOrEmpty(_settings.To))
        {
            _logger.LogWarning("Email send skipped (missing config): {Subject}", subject);
            return;
        }

        try
        {
            #pragma warning disable SYSLIB0014 // SmtpClient is marked obsolete; sufficient for low-volume notifications
            using var smtp = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                Credentials = new NetworkCredential(_settings.Username, _settings.Password),
                EnableSsl = true,
                Timeout = 15_000,
            };
            #pragma warning restore SYSLIB0014

            using var msg = new MailMessage(
                from: new MailAddress(_settings.From, _settings.FromName),
                to: new MailAddress(_settings.To))
            {
                Subject = subject,
                Body = body,
                IsBodyHtml = false,
            };

            await smtp.SendMailAsync(msg);
            _logger.LogInformation("Email sent: {Subject}", subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email send failed: {Subject}", subject);
        }
    }

    public void Dispose()
    {
        if (_settings.Enabled && _settings.NotifyOnPlayerJoin)
            _activityLog.EventAdded -= OnActivityEvent;
    }
}

public class EmailSettings
{
    public bool Enabled { get; set; } = false;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string FromName { get; set; } = "AubsCraft";
    public string To { get; set; } = "";

    public bool NotifyOnSignup { get; set; } = true;
    public bool NotifyOnInviteCreated { get; set; } = false;
    public bool NotifyOnWhitelistAdd { get; set; } = true;
    public bool NotifyOnServerLifecycle { get; set; } = true;
    public bool NotifyOnPlayerJoin { get; set; } = true;
}
