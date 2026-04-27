using System.Security.Cryptography;
using System.Text.Json;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Manages invite codes used by Friend accounts to self-register.
/// Codes have max-use counts, optional expiry, and a redemption log
/// so admins can audit who signed up using which code.
/// </summary>
public class InviteCodeService
{
    private readonly string _path;
    private readonly ILogger<InviteCodeService> _logger;
    private InviteCodesFile? _cached;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int GeneratedCodeLength = 6;

    public InviteCodeService(IConfiguration configuration, ILogger<InviteCodeService> logger)
    {
        _logger = logger;
        _path = configuration.GetValue<string>("Auth:InviteCodesPath") ?? "invite-codes.json";
    }

    public async Task<InviteCode> CreateAsync(
        string? requestedCode,
        int maxUses,
        int? expiresInDays,
        string notes,
        string createdBy)
    {
        if (maxUses <= 0) throw new ArgumentException("maxUses must be greater than zero.");
        if (expiresInDays is <= 0) throw new ArgumentException("expiresInDays must be positive.");

        await _ioLock.WaitAsync();
        try
        {
            var file = LoadFromDisk();

            string code = (requestedCode ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code))
                code = GenerateUniqueCode(file);
            else if (file.Codes.Any(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("That code already exists.");

            var entry = new InviteCode
            {
                Code = code,
                MaxUses = maxUses,
                UsesRemaining = maxUses,
                ExpiresAt = expiresInDays.HasValue ? DateTime.UtcNow.AddDays(expiresInDays.Value) : null,
                Notes = notes ?? "",
                CreatedBy = createdBy,
                CreatedAt = DateTime.UtcNow,
            };
            file.Codes.Add(entry);
            await SaveAsync(file);
            _cached = file;
            _logger.LogInformation("Invite code created: {Code} (max {Max} uses, expires {Expires}) by {By}",
                code, maxUses, entry.ExpiresAt?.ToString("O") ?? "never", createdBy);
            return entry;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public List<InviteCode> ListAll()
    {
        return LoadCached().Codes.OrderByDescending(c => c.CreatedAt).ToList();
    }

    public async Task<bool> RevokeAsync(string code)
    {
        await _ioLock.WaitAsync();
        try
        {
            var file = LoadFromDisk();
            var match = file.Codes.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;
            match.Revoked = true;
            await SaveAsync(file);
            _cached = file;
            _logger.LogInformation("Invite code revoked: {Code}", code);
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string code)
    {
        await _ioLock.WaitAsync();
        try
        {
            var file = LoadFromDisk();
            var removed = file.Codes.RemoveAll(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            await SaveAsync(file);
            _cached = file;
            _logger.LogInformation("Invite code deleted: {Code}", code);
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Validates a code (without consuming a use). Returns the matched code if usable, or null.
    /// </summary>
    public InviteCode? Peek(string code)
    {
        var file = LoadCached();
        var match = file.Codes.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        return match?.IsValid(DateTime.UtcNow) == true ? match : null;
    }

    /// <summary>
    /// Atomically consumes one use of the given code and records the redemption.
    /// Throws if the code is invalid; only call after Peek + actual user creation succeeded.
    /// </summary>
    public async Task ConsumeAsync(string code, string username)
    {
        await _ioLock.WaitAsync();
        try
        {
            var file = LoadFromDisk();
            var match = file.Codes.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (match == null || !match.IsValid(DateTime.UtcNow))
                throw new InvalidOperationException("Invite code is no longer valid.");

            match.UsesRemaining = Math.Max(0, match.UsesRemaining - 1);
            match.Redemptions.Add(new InviteRedemption { Username = username, RedeemedAt = DateTime.UtcNow });
            await SaveAsync(file);
            _cached = file;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private InviteCodesFile LoadCached()
    {
        if (_cached != null) return _cached;
        _cached = LoadFromDisk();
        return _cached;
    }

    private InviteCodesFile LoadFromDisk()
    {
        if (!File.Exists(_path)) return new InviteCodesFile();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<InviteCodesFile>(json) ?? new InviteCodesFile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read invite-codes.json");
            return new InviteCodesFile();
        }
    }

    private async Task SaveAsync(InviteCodesFile file)
    {
        var json = JsonSerializer.Serialize(file, JsonOpts);
        await File.WriteAllTextAsync(_path, json);
    }

    private static string GenerateUniqueCode(InviteCodesFile file)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var bytes = RandomNumberGenerator.GetBytes(GeneratedCodeLength);
            var chars = new char[GeneratedCodeLength];
            for (var i = 0; i < GeneratedCodeLength; i++)
                chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            var code = new string(chars);
            if (!file.Codes.Any(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
                return code;
        }
        throw new InvalidOperationException("Failed to generate a unique invite code.");
    }
}
