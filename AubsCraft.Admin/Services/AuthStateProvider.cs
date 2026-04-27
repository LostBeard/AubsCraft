using System.Net.Http.Json;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Auth state for the Blazor client. Tracks logged-in user, role, and
/// the "first-run setup" flag. Drives the login / signup / public flows.
/// </summary>
public class AuthStateProvider
{
    private readonly HttpClient _http;

    public bool IsAuthenticated { get; private set; }
    public bool NeedsSetup { get; private set; }
    public string? Username { get; private set; }
    public string? Role { get; private set; }
    public bool IsChecked { get; private set; }

    public bool IsOwner => Role == "Owner";
    public bool IsAdmin => Role == "Admin" || Role == "Owner";
    public bool IsFriend => Role == "Friend";

    public AuthStateProvider(HttpClient http)
    {
        _http = http;
    }

    public async Task CheckAuthAsync()
    {
        try
        {
            var result = await _http.GetFromJsonAsync<AuthStatus>("/api/auth/status");
            if (result != null)
            {
                IsAuthenticated = result.Authenticated;
                NeedsSetup = result.NeedsSetup;
                Username = result.Username;
                Role = result.Role;
            }
        }
        catch
        {
            IsAuthenticated = false;
            Role = null;
        }
        IsChecked = true;
    }

    public async Task<(bool success, string? error)> LoginAsync(string username, string password)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/auth/login", new { username, password });
            if (response.IsSuccessStatusCode)
            {
                IsAuthenticated = true;
                Username = username;
                await CheckAuthAsync();
                return (true, null);
            }
            return (false, "Invalid username or password");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool success, string? error)> SetupAsync(string username, string password)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/auth/setup", new { username, password });
            if (response.IsSuccessStatusCode)
                return (true, null);
            return (false, await ExtractErrorAsync(response, "Setup failed"));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool success, string? error)> RedeemAsync(string code, string username, string password)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/auth/redeem", new { code, username, password });
            if (response.IsSuccessStatusCode)
            {
                await CheckAuthAsync();
                return (true, null);
            }
            return (false, await ExtractErrorAsync(response, "Sign-up failed"));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task LogoutAsync()
    {
        await _http.PostAsync("/api/auth/logout", null);
        IsAuthenticated = false;
        Username = null;
        Role = null;
    }

    private static async Task<string> ExtractErrorAsync(HttpResponseMessage response, string fallback)
    {
        try
        {
            var err = await response.Content.ReadFromJsonAsync<ErrorBody>();
            if (!string.IsNullOrEmpty(err?.Error)) return err.Error;
        }
        catch { }
        return fallback;
    }

    private record AuthStatus(bool Authenticated, bool NeedsSetup, string? Username, string? Role);
    private record ErrorBody(string? Error);
}
