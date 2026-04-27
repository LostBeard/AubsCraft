using System.Security.Cryptography;
using System.Text.Json;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Manages user accounts (users.json). Supports multiple users with roles
/// (Owner / Admin / Friend). Migrates the legacy single-admin admin.json
/// into users.json on first run, preserving the existing account as Owner.
/// </summary>
public class AuthService
{
    private readonly string _usersPath;
    private readonly string _legacyAdminPath;
    private readonly ILogger<AuthService> _logger;
    private UsersFile? _cached;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AuthService(IConfiguration configuration, ILogger<AuthService> logger)
    {
        _logger = logger;
        _usersPath = configuration.GetValue<string>("Auth:UsersPath") ?? "users.json";
        _legacyAdminPath = configuration.GetValue<string>("Auth:CredentialsPath") ?? "admin.json";
    }

    public bool NeedsSetup
    {
        get
        {
            var users = LoadCached();
            return users.Users.All(u => u.Role != Roles.Owner);
        }
    }

    public async Task CreateOwnerAsync(string username, string password)
    {
        await _ioLock.WaitAsync();
        try
        {
            var users = LoadFromDisk();
            if (users.Users.Any(u => u.Role == Roles.Owner))
                throw new InvalidOperationException("Owner account already exists.");

            var user = HashNewUser(username, password, Roles.Owner, createdVia: null);
            users.Users.Add(user);
            await SaveAsync(users);
            _cached = users;
            _logger.LogInformation("Owner account created for {Username}", username);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<User> CreateFriendAsync(string username, string password, string inviteCode)
    {
        await _ioLock.WaitAsync();
        try
        {
            var users = LoadFromDisk();
            if (users.Users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Username already taken.");

            var user = HashNewUser(username, password, Roles.Friend, inviteCode);
            users.Users.Add(user);
            await SaveAsync(users);
            _cached = users;
            _logger.LogInformation("Friend account created for {Username} via invite {Code}", username, inviteCode);
            return user;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<User?> ValidateAsync(string username, string password)
    {
        var users = LoadCached();
        var user = users.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (user == null) return null;

        var salt = Convert.FromBase64String(user.Salt);
        var expected = Convert.FromBase64String(user.PasswordHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithm, HashSize);

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            return null;

        user.LastLoginAt = DateTime.UtcNow;
        await SaveAsync(users);
        return user;
    }

    public User? GetUser(string username)
    {
        var users = LoadCached();
        return users.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    public List<User> GetAllUsers()
    {
        var users = LoadCached();
        return users.Users.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<bool> DeleteUserAsync(string username)
    {
        await _ioLock.WaitAsync();
        try
        {
            var users = LoadFromDisk();
            var match = users.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;
            if (match.Role == Roles.Owner) throw new InvalidOperationException("Cannot delete the Owner account.");
            users.Users.Remove(match);
            await SaveAsync(users);
            _cached = users;
            _logger.LogInformation("User deleted: {Username}", username);
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<bool> SetUserRoleAsync(string username, string role)
    {
        if (role != Roles.Owner && role != Roles.Admin && role != Roles.Friend)
            throw new ArgumentException($"Unknown role: {role}");

        await _ioLock.WaitAsync();
        try
        {
            var users = LoadFromDisk();
            var match = users.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;
            if (match.Role == Roles.Owner && role != Roles.Owner)
                throw new InvalidOperationException("Cannot demote the Owner account.");
            match.Role = role;
            await SaveAsync(users);
            _cached = users;
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private User HashNewUser(string username, string password, string role, string? createdVia)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithm, HashSize);
        return new User
        {
            Username = username,
            PasswordHash = Convert.ToBase64String(hash),
            Salt = Convert.ToBase64String(salt),
            Role = role,
            CreatedAt = DateTime.UtcNow,
            CreatedViaInviteCode = createdVia,
        };
    }

    private UsersFile LoadCached()
    {
        if (_cached != null) return _cached;
        _cached = LoadFromDisk();
        return _cached;
    }

    private UsersFile LoadFromDisk()
    {
        if (File.Exists(_usersPath))
        {
            try
            {
                var json = File.ReadAllText(_usersPath);
                var file = JsonSerializer.Deserialize<UsersFile>(json);
                if (file != null) return file;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read users.json");
            }
        }

        // Migrate legacy admin.json -> users.json (one-shot)
        if (File.Exists(_legacyAdminPath))
        {
            try
            {
                var json = File.ReadAllText(_legacyAdminPath);
                var legacy = JsonSerializer.Deserialize<LegacyAdminCredentials>(json);
                if (legacy != null && !string.IsNullOrEmpty(legacy.Username))
                {
                    var migrated = new UsersFile
                    {
                        Users = new List<User>
                        {
                            new()
                            {
                                Username = legacy.Username,
                                PasswordHash = legacy.PasswordHash,
                                Salt = legacy.Salt,
                                Role = Roles.Owner,
                                CreatedAt = File.GetCreationTimeUtc(_legacyAdminPath),
                            }
                        }
                    };
                    var migratedJson = JsonSerializer.Serialize(migrated, JsonOpts);
                    File.WriteAllText(_usersPath, migratedJson);
                    var backupPath = _legacyAdminPath + ".migrated";
                    if (!File.Exists(backupPath))
                        File.Move(_legacyAdminPath, backupPath);
                    _logger.LogInformation("Migrated admin.json -> users.json (Owner: {Username}). Legacy file moved to {Backup}.",
                        legacy.Username, backupPath);
                    return migrated;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate admin.json");
            }
        }

        return new UsersFile();
    }

    private async Task SaveAsync(UsersFile users)
    {
        var json = JsonSerializer.Serialize(users, JsonOpts);
        await File.WriteAllTextAsync(_usersPath, json);
    }

    private class LegacyAdminCredentials
    {
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Salt { get; set; } = "";
    }
}
