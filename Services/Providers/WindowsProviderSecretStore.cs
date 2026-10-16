using System.Security.Cryptography;
using System.Text;

namespace WallTrek.Services.Providers;

public interface IProviderSecretStore
{
    string? Read(string reference);
    void Write(string reference, string value);
    void Delete(string reference);
}

/// <summary>DPAPI user-scoped encryption, no secrets in settings.json or profiles.json.</summary>
public sealed class WindowsProviderSecretStore : IProviderSecretStore
{
    private readonly string _directory;
    public WindowsProviderSecretStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallTrek", "Secrets");
    }
    private string PathFor(string reference)
    {
        // Do not use untrusted IDs directly in file paths.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("WallTrek:" + reference));
        return Path.Combine(_directory, Convert.ToHexString(hash) + ".bin");
    }
    public string? Read(string reference)
    {
        var path = PathFor(reference);
        if (!File.Exists(path)) return null;
        // Fail closed if the vault cannot decrypt as the current Windows user.
        var raw = File.ReadAllBytes(path);
        try
        {
            var bytes = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (CryptographicException) { throw new ProviderException(ProviderFailure.CredentialUnavailable); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    public void Write(string reference, string value)
    {
        if (value.Length > 16_384) throw new ProviderException(ProviderFailure.InvalidConfiguration);
        Directory.CreateDirectory(_directory);
        var bytes = Encoding.UTF8.GetBytes(value);
        byte[]? encrypted = null;
        try { encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var temp = System.IO.Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, PathFor(reference), true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
    public void Delete(string reference)
    {
        var path = PathFor(reference);
        if (File.Exists(path)) File.Delete(path);
    }
}
