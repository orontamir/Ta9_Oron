using System.Security.Cryptography;
using System.Text;

namespace Ta9.ServiceDefaults.Security;

/// <summary>
/// The keys this service accepts. Keys are kept only as SHA-256 hashes and compared in constant time, against
/// every entry, so timing reveals neither a key nor which caller it belongs to. Missing keys never match.
/// </summary>
public sealed class ApiKeyRegistry
{
    public const int MinimumKeyLength = 32;

    private readonly (CallerIdentity Identity, byte[] Hash)[] _entries;

    public ApiKeyRegistry(ApiKeyOptions options, IEnumerable<Caller> accepted)
    {
        Enabled = options.Enabled;
        Accepted = accepted.Distinct().Select(CallerIdentity.For).ToList();
        Missing = Accepted.Where(c => string.IsNullOrWhiteSpace(c.KeyFrom(options))).ToList();
        TooShort = Accepted.Where(c => c.KeyFrom(options) is { Length: > 0 and < MinimumKeyLength }).ToList();
        _entries = Accepted
            .Where(c => !string.IsNullOrWhiteSpace(c.KeyFrom(options)))
            .Select(c => (c, Hash(c.KeyFrom(options)!)))
            .ToArray();
    }

    public bool Enabled { get; }

    public IReadOnlyList<CallerIdentity> Accepted { get; }

    /// <summary>Accepted callers without a configured key: their requests are always rejected.</summary>
    public IReadOnlyList<CallerIdentity> Missing { get; }

    public IReadOnlyList<CallerIdentity> TooShort { get; }

    public CallerIdentity? Find(string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return null;
        }

        var hash = Hash(presentedKey);
        CallerIdentity? match = null;
        foreach (var (identity, expected) in _entries)
        {
            // No early exit: every entry is compared.
            if (CryptographicOperations.FixedTimeEquals(hash, expected))
            {
                match ??= identity;
            }
        }

        return match;
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
