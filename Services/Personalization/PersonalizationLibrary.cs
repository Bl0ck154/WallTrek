using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WallTrek.Services.Providers;

namespace WallTrek.Services.Personalization;

/// <summary>
/// Local library. All originals are copied by random ID. Only stripped/downscaled
/// JPEG derivatives can reach a provider. No original names or EXIF leave this store.
/// </summary>
public sealed class PersonalizationLibrary
{
    private readonly string _root;
    private readonly string _json;
    private readonly object _gate = new();
    private PersonalizedState _state;
    private static readonly Lazy<PersonalizationLibrary> Shared = new(() => new());
    public static PersonalizationLibrary Default => Shared.Value;

    public PersonalizationLibrary(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "WallTrek", "Personalization");
        _json = Path.Combine(_root, "library.json");
        try
        {
            _state = File.Exists(_json)
                ? JsonSerializer.Deserialize<PersonalizedState>(File.ReadAllText(_json))
                    ?? throw new InvalidDataException("Invalid personalization library.")
                : new PersonalizedState();
            if (_state.Version != 1 || _state.Photos is null || _state.Scenarios is null ||
                _state.Photos.Select(p => p.Id).Distinct().Count() != _state.Photos.Count ||
                _state.Scenarios.Select(s => s.Id).Distinct().Count() != _state.Scenarios.Count)
                throw new InvalidDataException("Unsupported or damaged personalization library.");
        }
        catch (JsonException) { throw new InvalidDataException("Personalization library JSON is invalid; original was not modified."); }
    }

    private static T Copy<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public PersonalizedState Snapshot()
    {
        lock (_gate) return Copy(_state);
    }

    public IReadOnlyList<ReferencePhoto> SelectedPhotos()
    {
        lock (_gate)
        {
            var selected = _state.Photos.Where(p => p.Enabled).ToArray();
            if (selected.Length > 5) throw new InvalidDataException("Too many enabled reference photos.");
            return Copy(selected);
        }
    }

    public ReferencePhoto Import(string sourcePath, ReferenceRole role)
    {
        if (!Path.IsPathFullyQualified(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidDataException("Select a local image file.");
        var info = new FileInfo(sourcePath);
        if (info.Length is < 32 or > 20 * 1024 * 1024)
            throw new InvalidDataException("Reference photos must be under 20 MiB.");
        if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(sourcePath).ToLowerInvariant()))
            throw new InvalidDataException("Only PNG/JPEG are supported for reference import.");
        // Fully decode and strip source metadata before using it as an outbound image.
        var photo = new ReferencePhoto { Role = role };
        photo.FileName = photo.Id.ToString("N") + ".jpg";
        Directory.CreateDirectory(Path.Combine(_root, "originals"));
        Directory.CreateDirectory(Path.Combine(_root, "uploads"));
        var original = Path.Combine(_root, "originals", photo.Id.ToString("N") +
            Path.GetExtension(sourcePath).ToLowerInvariant());
        var derivative = Path.Combine(_root, "uploads", photo.FileName);
        var temp = derivative + ".tmp";
        try
        {
            using (var image = Image.FromFile(sourcePath))
            {
                if (image.Width is < 16 or > 12000 || image.Height is < 16 or > 12000 ||
                    (long)image.Width * image.Height > 64_000_000)
                    throw new InvalidDataException("Reference image dimensions are not supported.");
                var ratio = Math.Min(1d, 2048d / Math.Max(image.Width, image.Height));
                var width = Math.Max(1, (int)Math.Round(image.Width * ratio));
                var height = Math.Max(1, (int)Math.Round(image.Height * ratio));
                using var sanitized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(sanitized))
                {
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(image, 0, 0, width, height);
                }
                sanitized.Save(temp, ImageFormat.Jpeg);
            }
            if (new FileInfo(temp).Length > 20 * 1024 * 1024)
                throw new InvalidDataException("Converted reference is too large.");
            File.Copy(sourcePath, original, overwrite: false);
            File.Move(temp, derivative);
            lock (_gate)
            {
                photo.Label = "Photo " + (_state.Photos.Count + 1);
                photo.Enabled = _state.Photos.Count(p => p.Enabled) < 5;
                photo.Primary = role == ReferenceRole.Identity && !_state.Photos.Any(p => p.Primary);
                _state.Photos.Add(photo);
                try { Persist(); } catch { _state.Photos.Remove(photo); throw; }
            }
            return Copy(photo);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(derivative)) File.Delete(derivative);
            if (File.Exists(original)) File.Delete(original);
            throw;
        }
    }

    public void UpdatePhoto(Guid id, string label, ReferenceRole role, bool enabled, bool primary)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 100)
            throw new InvalidDataException("Photo label must be 1–100 characters.");
        lock (_gate)
        {
            var candidate = Copy(_state);
            var photo = candidate.Photos.Single(p => p.Id == id);
            photo.Label = label.Trim();
            photo.Role = role;
            photo.Enabled = enabled;
            photo.Primary = primary && role == ReferenceRole.Identity;
            if (photo.Primary)
                foreach (var other in candidate.Photos.Where(p => p.Id != id)) other.Primary = false;
            if (candidate.Photos.Count(p => p.Enabled) > 5)
                throw new InvalidDataException("Select at most five enabled reference photos.");
            candidate.ReferenceUploadApprovals.Clear(); // any role/selection change requires renewed consent
            SaveCandidate(candidate);
        }
    }

    public void DeletePhoto(Guid id)
    {
        lock (_gate)
        {
            var candidate = Copy(_state);
            var item = candidate.Photos.SingleOrDefault(p => p.Id == id);
            if (item is null) return;
            candidate.Photos.Remove(item);
            candidate.ReferenceUploadApprovals.Clear();
            SaveCandidate(candidate);
            // metadata first, cleanup only files owned by this ID; never touch source files.
            var prefix = id.ToString("N");
            foreach (var directory in new[] { "uploads", "originals" })
            {
                foreach (var file in Directory.Exists(Path.Combine(_root, directory))
                    ? Directory.GetFiles(Path.Combine(_root, directory), prefix + ".*") : Array.Empty<string>())
                    File.Delete(file);
            }
        }
    }

    public IReadOnlyList<ProviderImageReference> ReadForProvider(IReadOnlyList<Guid> selectedIds)
    {
        lock (_gate)
        {
            if (selectedIds.Count is < 1 or > 5 || selectedIds.Distinct().Count() != selectedIds.Count)
                throw new InvalidDataException("Select 1–5 distinct photos.");
            var files = new List<ProviderImageReference>();
            foreach (var id in selectedIds)
            {
                var photo = _state.Photos.SingleOrDefault(p => p.Id == id && p.Enabled)
                    ?? throw new InvalidDataException("A selected photo is disabled or deleted.");
                var filename = photo.Id.ToString("N") + ".jpg";
                var path = Path.Combine(_root, "uploads", filename);
                if (!File.Exists(path) || new FileInfo(path).Length > 20 * 1024 * 1024)
                    throw new InvalidDataException("A selected reference is missing or too large.");
                var bytes = File.ReadAllBytes(path);
                var (mime, _, _) = ProviderImageValidator.Check(bytes);
                if (mime != "image/jpeg") throw new InvalidDataException("Invalid stored reference photo.");
                files.Add(new ProviderImageReference(bytes, filename, photo.Role switch
                {
                    ReferenceRole.Identity => ProviderReferenceRole.Identity,
                    ReferenceRole.Content => ProviderReferenceRole.Content,
                    _ => ProviderReferenceRole.Style
                }));
            }
            return files;
        }
    }

    public string ImagePreviewPath(Guid id)
    {
        lock (_gate)
        {
            var photo = _state.Photos.Single(p => p.Id == id);
            return Path.Combine(_root, "uploads", photo.Id.ToString("N") + ".jpg");
        }
    }

    public void SaveScenario(WallpaperScenario scenario, bool activate)
    {
        if (scenario.Id == Guid.Empty || string.IsNullOrWhiteSpace(scenario.Name) || scenario.Name.Length > 120)
            throw new InvalidDataException("Scenario needs a name.");
        if (scenario.Template.Length > 16000 || scenario.Style.Length > 4000 || scenario.Avoid.Length > 4000)
            throw new InvalidDataException("Scenario instructions are too long.");
        _ = ScenarioComposer.Compose(scenario, Array.Empty<ReferencePhoto>()); // validate placeholders/pools
        lock (_gate)
        {
            var candidate = Copy(_state);
            var previous = candidate.Scenarios.FirstOrDefault(s => s.Id == scenario.Id);
            var copy = Copy(scenario);
            if (previous is not null)
            {
                if (previous.Template != copy.Template || previous.Style != copy.Style ||
                    previous.Avoid != copy.Avoid || JsonSerializer.Serialize(previous.Pools) != JsonSerializer.Serialize(copy.Pools))
                {
                    copy.UsedCombinationKeys.Clear(); copy.LastCombinationKey = null; copy.Cycle = 0;
                }
                candidate.Scenarios.Remove(previous);
            }
            candidate.Scenarios.Add(copy);
            if (activate) candidate.ActiveScenarioId = scenario.Id;
            candidate.ReferenceUploadApprovals.Clear(); // scenario destination approval is content-specific
            SaveCandidate(candidate);
        }
    }

    public void ActivateScenario(Guid? id)
    {
        lock (_gate)
        {
            var candidate = Copy(_state);
            if (id is Guid requested && !candidate.Scenarios.Any(s => s.Id == requested))
                throw new InvalidDataException("Scenario no longer exists.");
            candidate.ActiveScenarioId = id;
            SaveCandidate(candidate);
        }
    }

    public void DeleteScenario(Guid id)
    {
        lock (_gate)
        {
            var candidate = Copy(_state);
            candidate.Scenarios.RemoveAll(s => s.Id == id);
            if (candidate.ActiveScenarioId == id) candidate.ActiveScenarioId = null;
            candidate.ReferenceUploadApprovals.Clear();
            SaveCandidate(candidate);
        }
    }

    public PreparedWallpaper? Prepare()
    {
        lock (_gate)
        {
            if (_state.ActiveScenarioId is not Guid id) return null;
            var scenario = _state.Scenarios.Single(s => s.Id == id);
            return ScenarioComposer.Compose(scenario, _state.Photos.Where(p => p.Enabled)
                .OrderByDescending(p => p.Primary).ThenBy(p => p.Role == ReferenceRole.Style ? 1 : 0)
                .ToArray());
        }
    }

    public void CommitSuccess(PreparedWallpaper prepared)
    {
        lock (_gate)
        {
            var candidate = Copy(_state);
            var scene = candidate.Scenarios.Single(s => s.Id == prepared.ScenarioId);
            var current = ScenarioComposer.Compose(scene, candidate.Photos.Where(p => p.Enabled)
                .OrderByDescending(p => p.Primary).ThenBy(p => p.Role == ReferenceRole.Style ? 1 : 0)
                .ToArray());
            if (current.CombinationKey != prepared.CombinationKey || current.FinalPrompt != prepared.FinalPrompt ||
                current.ReferenceSignature != prepared.ReferenceSignature)
                throw new InvalidOperationException("Scenario or references changed during generation.");
            ScenarioComposer.CommitSuccess(scene, prepared);
            SaveCandidate(candidate);
        }
    }

    private static string GrantKey(Guid scenarioId, Guid profileId, string origin) =>
        scenarioId.ToString("N") + "|" + profileId.ToString("N") + "|" + origin;

    private string GrantFingerprint(IReadOnlyList<Guid> ids)
    {
        var descriptions = ids.Select(id =>
        {
            var p = _state.Photos.Single(x => x.Id == id);
            return id.ToString("N") + ":" + p.Role + ":" + p.FileName;
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", descriptions))));
    }

    public void ApproveReferences(Guid scenarioId, Guid profileId, string origin, IReadOnlyList<Guid> ids)
    {
        if (ids.Count is < 1 or > 5 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.GetLeftPart(UriPartial.Authority) != origin)
            throw new InvalidDataException("Invalid destination or reference selection.");
        lock (_gate)
        {
            var candidate = Copy(_state);
            candidate.ReferenceUploadApprovals[GrantKey(scenarioId, profileId, origin)] = GrantFingerprint(ids);
            SaveCandidate(candidate);
        }
    }

    public bool HasReferenceApproval(PreparedWallpaper prepared, Guid profileId, string origin)
    {
        lock (_gate) return _state.ReferenceUploadApprovals.TryGetValue(
            GrantKey(prepared.ScenarioId, profileId, origin), out var stored) &&
            stored == GrantFingerprint(prepared.ReferenceIds);
    }

    private void SaveCandidate(PersonalizedState candidate)
    {
        var old = _state;
        _state = candidate;
        try { Persist(); } catch { _state = old; throw; }
    }

    private void Persist()
    {
        Directory.CreateDirectory(_root);
        var temp = _json + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, new JsonSerializerOptions
            { WriteIndented = true }));
            File.Move(temp, _json, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
