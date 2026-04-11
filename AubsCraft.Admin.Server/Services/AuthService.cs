using System.Security.Cryptography;
using System.Text.Json;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Manages admin credentials stored in admin.json.
/// Single admin account - like a router login.
/// Delete admin.json to reset credentials.
/// </summary>
public class AuthService
{
    private readonly string _credentialsPath;
    private readonly ILogger<AuthService> _logger;
    private AdminCredentials? _cached;

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    public AuthService(IConfiguration configuration, ILogger<AuthService> logger)
    {
        _logger = logger;
        _credentialsPath = configuration.GetValue<string>("Auth:CredentialsPath") ?? "admin.json";
    }

    public bool NeedsSetup => !File.Exists(_credentialsPath);

    public async Task CreateAdminAsync(string username, string password)
    {
        if (!NeedsSetup)
            throw new InvalidOperationException("Admin account already exists. Delete admin.json to reset.");

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithm, HashSize);

        var credentials = new AdminCredentials
        {
            Username = username,
            PasswordHash = Convert.ToBase64String(hash),
            Salt = Convert.ToBase64String(salt),
        };

        var json = JsonSerializer.Serialize(credentials, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_credentialsPath, json);
        _cached = credentials;

        _logger.LogInformation("Admin account created for {Username}", username);
    }

    public async Task<bool> ValidateAsync(string username, string password)
    {
        var credentials = await LoadCredentialsAsync();
        if (credentials == null) return false;

        if (!string.Equals(credentials.Username, username, StringComparison.OrdinalIgnoreCase))
            return false;

        var salt = Convert.FromBase64String(credentials.Salt);
        var expectedHash = Convert.FromBase64String(credentials.PasswordHash);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithm, HashSize);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public async Task<string?> GetUsernameAsync()
    {
        var credentials = await LoadCredentialsAsync();
        return credentials?.Username;
    }

    private async Task<AdminCredentials?> LoadCredentialsAsync()
    {
        if (_cached != null) return _cached;
        if (!File.Exists(_credentialsPath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(_credentialsPath);
            _cached = JsonSerializer.Deserialize<AdminCredentials>(json);
            return _cached;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read admin.json");
            return null;
        }
    }

    private class AdminCredentials
    {
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Salt { get; set; } = "";
    }
}
