namespace SimpleSftp.Core;

// Attempts are reserved before PBKDF2 starts, so parallel connections cannot
// bypass the budget. Successful attempts refund their reservation.
internal sealed class AuthenticationThrottle(TimeProvider? clock = null, int addressLimit = 20, int userLimit = 10, int capacity = 4096)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<string, Bucket> buckets = [];
    private sealed class Bucket(DateTimeOffset expires) { internal DateTimeOffset Expires = expires; internal int Count; }

    internal bool TryAcquire(string address, string username, out Action success)
    {
        success = () => { };
        var addressKey = "ip:" + address;
        var userKey = "user:" + (username.Length <= 64 ? username : "<invalid>");
        lock (sync)
        {
            var now = clock.GetUtcNow();
            // Never evict active buckets: that would let an attacker reset a
            // throttled username by flooding the table with new names.
            if (buckets.Count + 2 > capacity)
                foreach (var key in buckets.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) buckets.Remove(key);
            Bucket? Find(string key) => buckets.TryGetValue(key, out var value) && value.Expires > now ? value : null;
            var ip = Find(addressKey);
            var user = Find(userKey);
            if ((ip?.Count ?? 0) >= addressLimit || (user?.Count ?? 0) >= userLimit) return false;
            var missing = (ip == null ? 1 : 0) + (user == null ? 1 : 0);
            if (buckets.Count + missing > capacity) return false;
            if (ip == null) buckets[addressKey] = ip = new Bucket(now.AddMinutes(1));
            if (user == null) buckets[userKey] = user = new Bucket(now.AddMinutes(1));
            ip.Count++;
            user.Count++;
            var refunded = false;
            success = () =>
            {
                lock (sync)
                {
                    if (refunded) return;
                    refunded = true;
                    ip.Count--;
                    user.Count--;
                }
            };
            return true;
        }
    }
}

internal sealed class PasswordAuthenticator(AuthenticationThrottle? throttle = null, Func<string, string, bool>? verify = null)
{
    private readonly AuthenticationThrottle throttle = throttle ?? new();
    private readonly Func<string, string, bool> verify = verify ?? Passwords.Verify;
    private static readonly Lazy<string> DummyHash = new(() => Passwords.Hash(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));

    internal bool Authenticate(string address, string username, string password, SftpUser? user, out bool throttled)
    {
        throttled = !throttle.TryAcquire(address, username, out var success);
        if (throttled) return false;
        var eligible = user is { Enabled: true };
        // Unknown and disabled accounts do the same PBKDF2 work as active ones.
        var valid = verify(password, eligible ? user!.PasswordHash : DummyHash.Value);
        if (!valid || !eligible) return false;
        success();
        return true;
    }
}
