using System.Text.Json;

namespace WallTrek.Services.Providers;

/// <summary>Only public profile configuration is serialized; secrets have opaque references.</summary>
public sealed class ProviderProfileStore
{
    private sealed class ProfileDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ProviderProfile> Profiles { get; set; } = new();
    }
    private readonly string _path;
    private readonly IProviderSecretStore _secrets;
    private readonly object _sync = new();
    private ProfileDocument _document;

    public static ProviderProfileStore Default { get; } = new ProviderProfileStore(
        new WindowsProviderSecretStore());

    public ProviderProfileStore(IProviderSecretStore secrets, string? path = null)
    {
        _secrets = secrets;
        _path = path ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "WallTrek", "providers.json");
        if (!File.Exists(_path)) { _document = new(); return; }
        try
        {
            _document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Invalid provider profile data.");
            if (_document.SchemaVersion != 1 || _document.Profiles is null ||
                _document.Profiles.Select(p => p.Id).Distinct().Count() != _document.Profiles.Count)
                throw new InvalidDataException("Unsupported provider profile schema.");
            foreach (var p in _document.Profiles) ProviderProfileValidation.Validate(p);
        }
        catch (JsonException)
        {
            // Don't silently overwrite corrupt/mismatched configuration.
            throw new InvalidDataException("Provider configuration is corrupt. Restore providers.json from backup.");
        }
    }

    public IReadOnlyList<ProviderProfile> List()
    {
        lock (_sync) return _document.Profiles.Select(p => p.Clone()).ToArray();
    }
    public ProviderProfile? Find(Guid id)
    {
        lock (_sync) return _document.Profiles.FirstOrDefault(p => p.Id == id)?.Clone();
    }
    public string? ReadKey(Guid id)
    {
        var profile = Find(id) ?? throw new ProviderException(ProviderFailure.InvalidConfiguration);
        return _secrets.Read(profile.SecretReference);
    }

    public void Save(ProviderProfile profile, string? newKey)
    {
        ProviderProfileValidation.Validate(profile);
        if (profile.AuthMode == ProviderAuthMode.None &&
            !new Uri(profile.BaseUrl).IsLoopback)
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
        lock (_sync)
        {
            var old = _document.Profiles.FirstOrDefault(p => p.Id == profile.Id);
            var existingKey = old is null ? null : _secrets.Read(old.SecretReference);
            if (profile.AuthMode != ProviderAuthMode.None &&
                string.IsNullOrWhiteSpace(newKey) && string.IsNullOrWhiteSpace(existingKey))
                throw new ProviderException(ProviderFailure.CredentialUnavailable);
            var updated = profile.Clone();
            if (old is null || old.BaseUrl != updated.BaseUrl ||
                old.ImageModel != updated.ImageModel || old.EditModel != updated.EditModel ||
                old.GeneratePath != updated.GeneratePath || old.EditPath != updated.EditPath ||
                old.AuthMode != updated.AuthMode || old.EditShape != updated.EditShape ||
                !string.IsNullOrEmpty(newKey))
            {
                updated.GenerateVerified = false; updated.EditVerified = false;
                updated.GenerateVerifiedAt = null; updated.EditVerifiedAt = null;
            }
            // Save the secret before metadata. A vault failure cannot persist an unusable profile.
            // On metadata failure restore old key, if any, without logging it.
            var previous = _document;
            try
            {
                if (!string.IsNullOrEmpty(newKey)) _secrets.Write(profile.SecretReference, newKey);
                _document = new ProfileDocument
                {
                    Profiles = previous.Profiles.Where(p => p.Id != profile.Id)
                        .Select(p => p.Clone()).Append(updated).ToList()
                };
                Persist();
                if (profile.AuthMode == ProviderAuthMode.None) _secrets.Delete(profile.SecretReference);
            }
            catch
            {
                _document = previous;
                if (!string.IsNullOrEmpty(newKey))
                {
                    if (existingKey is null) _secrets.Delete(profile.SecretReference);
                    else _secrets.Write(profile.SecretReference, existingKey);
                }
                throw;
            }
        }
    }
    public void RecordVerified(Guid id, bool edit, bool success)
    {
        lock (_sync)
        {
            var previous = _document;
            var entries = previous.Profiles.Select(p => p.Clone()).ToList();
            var profile = entries.FirstOrDefault(p => p.Id == id)
                ?? throw new ProviderException(ProviderFailure.InvalidConfiguration);
            if (edit) { profile.EditVerified = success; profile.EditVerifiedAt = DateTimeOffset.UtcNow; }
            else { profile.GenerateVerified = success; profile.GenerateVerifiedAt = DateTimeOffset.UtcNow; }
            _document = new ProfileDocument { Profiles = entries };
            try { Persist(); } catch { _document = previous; throw; }
        }
    }
    public void Delete(Guid id)
    {
        lock (_sync)
        {
            var target = _document.Profiles.FirstOrDefault(p => p.Id == id);
            if (target is null) return;
            var previous = _document;
            _document = new ProfileDocument
            { Profiles = previous.Profiles.Where(p => p.Id != id).Select(p => p.Clone()).ToList() };
            try { Persist(); } catch { _document = previous; throw; }
            _secrets.Delete(target.SecretReference);
        }
    }
    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_document,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
