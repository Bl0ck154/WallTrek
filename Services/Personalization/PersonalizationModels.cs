using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallTrek.Services.Personalization;

public enum ReferenceRole { Identity, Content, Style }

public sealed class ReferencePhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public ReferenceRole Role { get; set; } = ReferenceRole.Identity;
    public bool Enabled { get; set; } = true;
    public bool Primary { get; set; }
    // App-owned filename only, never absolute source path or personally identifying name.
    public string FileName { get; set; } = "";
}

public sealed class WallpaperScenario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "My scenario";
    public string Template { get; set; } = "A realistic cinematic landscape photo of the subject in {{place}}";
    public string Style { get; set; } = "Photorealistic, natural lighting, 16:9 landscape";
    public string Avoid { get; set; } = "No watermarks or text";
    public Dictionary<string, List<string>> Pools { get; set; } = new(StringComparer.Ordinal);
    public List<string> UsedCombinationKeys { get; set; } = new();
    public string? LastCombinationKey { get; set; }
    public long Cycle { get; set; }
}

public sealed class PersonalizedState
{
    public int Version { get; set; } = 1;
    public Guid? ActiveScenarioId { get; set; }
    public List<ReferencePhoto> Photos { get; set; } = new();
    public List<WallpaperScenario> Scenarios { get; set; } = new();
    // Explicit reference-upload grants, scoped to both scenario and exact destination origin.
    public Dictionary<string, string> ReferenceUploadApprovals { get; set; } = new();
}

public sealed record PreparedWallpaper(Guid ScenarioId, string FinalPrompt, string CombinationKey,
    IReadOnlyDictionary<string, string> Variables, IReadOnlyList<Guid> ReferenceIds);

public static class ScenarioComposer
{
    private static readonly Regex Tokens = new(@"{{\s*([a-zA-Z][a-zA-Z0-9_]{0,31})\s*}}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public const string IdentityClause = "Preserve the recognizable identity, face shape, eyes, nose, mouth, skin tone, and natural proportions of the person in the identity photographs. Keep the same individual, not a lookalike. Do not alter age or identity.";
    public static PreparedWallpaper Compose(WallpaperScenario scene, IReadOnlyList<ReferencePhoto> active)
    {
        if (string.IsNullOrWhiteSpace(scene.Template) || scene.Template.Length > 16000)
            throw new InvalidOperationException("Scenario template must be 1–16000 characters.");
        if (active.Count > 5) throw new InvalidOperationException("Select no more than 5 photographs.");
        var pools = scene.Pools.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        if (pools.Length > 12 || pools.Any(p => !Regex.IsMatch(p.Key, "^[A-Za-z][A-Za-z0-9_]{0,31}$") ||
            p.Value is null || p.Value.Count is < 1 or > 100 ||
            p.Value.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 300)))
            throw new InvalidOperationException("Invalid scenario variables: 1–100 nonempty values per variable.");
        var names = Tokens.Matches(scene.Template).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (names.Any(n => !scene.Pools.ContainsKey(n)) || pools.Any(p => !names.Contains(p.Key)))
            throw new InvalidOperationException("Every {{variable}} must have a pool and every pool must appear in the template.");
        long total = 1;
        foreach (var pool in pools)
        {
            total *= pool.Value.Distinct(StringComparer.Ordinal).Count();
            if (total > 4096) throw new InvalidOperationException("Too many variations. Reduce pools to at most 4096 combinations.");
        }
        var options = new List<Dictionary<string, string>> { new(StringComparer.Ordinal) };
        foreach (var pool in pools)
        {
            var next = new List<Dictionary<string, string>>();
            foreach (var prefix in options)
                foreach (var value in pool.Value.Distinct(StringComparer.Ordinal))
                {
                    var copy = new Dictionary<string, string>(prefix, StringComparer.Ordinal) { [pool.Key] = value };
                    next.Add(copy);
                }
            options = next;
        }

        string Key(Dictionary<string, string> values) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(values.OrderBy(p => p.Key).Select(p => new[] { p.Key, p.Value })))));
        // Never repeat a successful combination while any unused combination exists.
        var candidates = options.Select(o => (Values: o, Key: Key(o))).ToArray();
        var used = scene.UsedCombinationKeys.ToHashSet(StringComparer.Ordinal);
        var fresh = candidates.Where(c => !used.Contains(c.Key)).ToArray();
        if (fresh.Length == 0) fresh = candidates.Where(c =>
            candidates.Length == 1 || c.Key != scene.LastCombinationKey).ToArray();
        // Stable preview: same state produces exactly the same final prompt until CommitSuccess.
        var chosen = fresh.OrderBy(c => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            scene.Id.ToString("N") + ":" + scene.Cycle + ":" + scene.UsedCombinationKeys.Count + ":" + c.Key))),
            StringComparer.Ordinal).First();
        string prompt = Tokens.Replace(scene.Template, match => chosen.Values[match.Groups[1].Value]);
        var sections = new List<string> { prompt };
        if (!string.IsNullOrWhiteSpace(scene.Style)) sections.Add("Visual style: " + scene.Style.Trim());
        if (!string.IsNullOrWhiteSpace(scene.Avoid)) sections.Add("Avoid: " + scene.Avoid.Trim());
        if (active.Any(p => p.Role == ReferenceRole.Identity)) sections.Insert(0, IdentityClause);
        if (active.Any(p => p.Role == ReferenceRole.Style))
            sections.Add("Use style references for aesthetics only; do not copy a person from a style-only image.");
        return new PreparedWallpaper(scene.Id, string.Join("\n\n", sections), chosen.Key,
            chosen.Values, active.Select(p => p.Id).ToArray());
    }

    public static void CommitSuccess(WallpaperScenario scene, PreparedWallpaper prepared)
    {
        if (scene.Id != prepared.ScenarioId) throw new InvalidOperationException("Wrong scenario.");
        if (scene.UsedCombinationKeys.Contains(prepared.CombinationKey, StringComparer.Ordinal))
            throw new InvalidOperationException("This variation was already committed.");
        var count = Math.Max(1, scene.Pools.Aggregate(1, (n, pair) =>
            checked(n * pair.Value.Distinct(StringComparer.Ordinal).Count())));
        if (scene.UsedCombinationKeys.Count >= count)
        {
            scene.UsedCombinationKeys.Clear();
            scene.Cycle++;
        }
        scene.UsedCombinationKeys.Add(prepared.CombinationKey);
        scene.LastCombinationKey = prepared.CombinationKey;
    }
}
