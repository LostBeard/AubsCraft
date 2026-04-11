using System.Net.Http.Json;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Simple auth state tracker. Checks /api/auth/status to determine
/// if the user is logged in, needs setup, etc.
/// </summary>
public class AuthStateProvider
{
    private readonly HttpClient _http;

    public bool IsAuthenticated { get; private set; }
    public bool NeedsSetup { get; private set; }
    public string? Username { get; private set; }
    public bool IsChecked { get; private set; }

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
            }
        }
        catch
        {
            IsAuthenticated = false;
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
            {
                return (true, null);
            }
            return (false, "Setup failed");
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
    }

    private record AuthStatus(bool Authenticated, bool NeedsSetup, string? Username);
}
