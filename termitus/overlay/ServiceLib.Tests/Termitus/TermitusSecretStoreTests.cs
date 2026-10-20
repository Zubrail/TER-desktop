using System.Runtime.CompilerServices;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>In-memory stand-in so unit tests never touch the real Keychain / DPAPI.</summary>
public sealed class FakeSecretBackend : ITermitusSecretBackend
{
    public const string RefPrefix = "ter-keychain:";
    public string Prefix => RefPrefix;
    public readonly Dictionary<string, string> Items = new();

    public string? Protect(string plain, string? currentRef)
    {
        var r = currentRef ?? RefPrefix + Guid.NewGuid().ToString("N");
        Items[r] = plain;
        return r;
    }

    public string? Unprotect(string reference) => Items.TryGetValue(reference, out var v) ? v : null;

    public void Remove(string reference) => Items.Remove(reference);
}

internal static class TermitusTestSecretSetup
{
    [ModuleInitializer]
    internal static void Init() => TermitusSecretStore.Backend = new FakeSecretBackend();
}

[NotInParallel("termitus-secret-store")]
public class TermitusSecretStoreTests
{
    [Test]
    public async Task SubItemUrl_LivesInTheStore_NotInTheColumn()
    {
        var fake = new FakeSecretBackend();
        TermitusSecretStore.Backend = fake;
        try
        {
            var sub = new SubItem { Id = "s1", Url = "https://termitus.online/sub/SECRET-TOKEN" };
            await Assert.That(sub.UrlStored).StartsWith("ter-keychain:");
            await Assert.That(sub.UrlStored).DoesNotContain("SECRET-TOKEN");
            await Assert.That(sub.Url).IsEqualTo("https://termitus.online/sub/SECRET-TOKEN");
            // changing it updates the same entry (no orphans)
            var r = sub.UrlStored;
            sub.Url = "https://termitus.online/sub/NEW-TOKEN";
            await Assert.That(sub.UrlStored).IsEqualTo(r);
            await Assert.That(fake.Items.Count).IsEqualTo(1);
            // JSON round trip (DeepCopy): the link is not serialised, the reference is
            var json = JsonUtils.Serialize(sub);
            await Assert.That(json).DoesNotContain("NEW-TOKEN");
            var copy = JsonUtils.Deserialize<SubItem>(json)!;
            await Assert.That(copy.Url).IsEqualTo("https://termitus.online/sub/NEW-TOKEN");
            // a missing secret reads as empty (the user signs in again), it never throws or leaks the reference as a URL
            fake.Items.Clear();
            TermitusSecretStore.Backend = fake; // clears the cache
            await Assert.That(sub.Url).IsEqualTo(string.Empty);
        }
        finally
        {
            TermitusSecretStore.Backend = new FakeSecretBackend();
        }
    }

    [Test]
    public async Task NoBackend_KeepsThePlainValue()
    {
        TermitusSecretStore.Backend = null;
        try
        {
            var sub = new SubItem { Id = "s2", Url = "https://x.example/sub/abc" };
            await Assert.That(sub.UrlStored).IsEqualTo("https://x.example/sub/abc");
            await Assert.That(sub.Url).IsEqualTo("https://x.example/sub/abc");
            await Assert.That(TermitusSecretStore.IsProtected(sub.UrlStored)).IsFalse();
        }
        finally
        {
            TermitusSecretStore.Backend = new FakeSecretBackend();
        }
    }

    [Test]
    public async Task EmptyAndNull_PassThrough()
    {
        await Assert.That(TermitusSecretStore.Conceal(null, "")).IsEqualTo("");
        await Assert.That(TermitusSecretStore.Conceal(null, null)).IsNull();
        await Assert.That(TermitusSecretStore.Reveal(null)).IsNull();
        await Assert.That(TermitusSecretStore.Reveal("https://plain")).IsEqualTo("https://plain");
    }

    [Test]
    public async Task RealKeychain_RoundTrip()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var kc = new TermitusKeychainBackend();
        string? r = null;
        try
        {
            r = kc.Protect("https://termitus.online/sub/TEST-" + Guid.NewGuid().ToString("N"), null);
            if (r == null)
            {
                await Assert.That(Environment.GetEnvironmentVariable("TER_REQUIRE_KEYCHAIN")).IsNull(); // set it to demand a usable Keychain
                return; // keychain not available in this session (locked / headless): nothing to assert
            }
            var plain = kc.Unprotect(r);
            await Assert.That(plain).StartsWith("https://termitus.online/sub/TEST-");
            await Assert.That(kc.Protect("https://termitus.online/sub/TEST-2", r)).IsEqualTo(r); // update in place
            await Assert.That(kc.Unprotect(r)).IsEqualTo("https://termitus.online/sub/TEST-2");
        }
        finally
        {
            if (r != null)
            {
                kc.Remove(r);
                await Assert.That(kc.Unprotect(r)).IsNull();
            }
        }
    }
}
