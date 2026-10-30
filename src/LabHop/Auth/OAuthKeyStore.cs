using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LabHop.Auth;

/// <summary>
/// Loads the OAuth signing and encryption certificates from disk, creating them on first run so that
/// tokens survive restarts. Certificates near expiry are replaced, and older files stay loaded until
/// they expire so tokens issued under them remain valid. The directory is readable only by the service
/// account.
/// </summary>
public static class OAuthKeyStore
{
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(5 * 365);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public static IReadOnlyList<X509Certificate2> LoadSigning(string keysPath) =>
        LoadOrCreate(keysPath, "oauth-signing", X509KeyUsageFlags.DigitalSignature);

    public static IReadOnlyList<X509Certificate2> LoadEncryption(string keysPath) =>
        LoadOrCreate(keysPath, "oauth-encryption", X509KeyUsageFlags.KeyEncipherment);

    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, OwnerOnlyDirectory);
        }
    }

    private static List<X509Certificate2> LoadOrCreate(string keysPath, string prefix, X509KeyUsageFlags usage)
    {
        EnsureDirectory(keysPath);

        var now = DateTimeOffset.UtcNow;
        var certificates = Directory.EnumerateFiles(keysPath, $"{prefix}-*.pfx")
            .Select(file => X509CertificateLoader.LoadPkcs12FromFile(file, password: null, X509KeyStorageFlags.EphemeralKeySet))
            .Where(certificate => certificate.NotAfter.ToUniversalTime() > now)
            .ToList();

        if (!certificates.Any(certificate => certificate.NotAfter.ToUniversalTime() > now + RenewBefore))
        {
            certificates.Add(Create(keysPath, prefix, usage, now));
        }

        return certificates;
    }

    private static X509Certificate2 Create(string keysPath, string prefix, X509KeyUsageFlags usage, DateTimeOffset now)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN=LabHop {prefix}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now + Lifetime);
        var pfx = certificate.Export(X509ContentType.Pkcs12);

        var path = Path.Combine(keysPath, $"{prefix}-{now:yyyyMMddHHmmss}.pfx");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnlyFile;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(pfx);
        }

        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
