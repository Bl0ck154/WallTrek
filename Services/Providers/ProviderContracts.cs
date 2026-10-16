using System.Text.Json.Serialization;

namespace WallTrek.Services.Providers;

// Stable additive contract shared with the Codex agent; do not modify the old
// IImageGenerationService used by built-in OpenAI/Google.
public enum ProviderReferenceRole { Identity, Content, Style }
public sealed record ProviderImageReference(byte[] Bytes, string FileName, ProviderReferenceRole Role);
public sealed record ProviderImageRequest(
    string Prompt, IReadOnlyList<ProviderImageReference>? References = null,
    string? Size = null, string? Quality = null);
public sealed record ProviderImageResult(
    byte[] ImageData, string MimeType, int Width, int Height,
    string ProviderId, string ModelId);
public sealed record ProviderCapabilities(bool ConnectionOk, bool GenerateVerified,
    bool EditVerified, string Status, DateTimeOffset CheckedAt);

public interface IImageProvider
{
    string ProviderId { get; }
    Task<ProviderCapabilities> ProbeAsync(CancellationToken ct = default);
    Task<ProviderImageResult> GenerateAsync(ProviderImageRequest request, CancellationToken ct = default);
}

public enum ProviderAuthMode { Bearer, ApiKeyHeader, None }
public enum ProviderEditShape { Image, ImageArray }
public enum ProviderFailure
{
    InvalidConfiguration, CredentialUnavailable, Unauthorized, Forbidden, RateLimited,
    NotFound, ServerError, TimedOut, Cancelled, InvalidImage, UnsupportedOperation,
    InvalidResponse, NetworkError, UnsafeRedirect
}
public sealed class ProviderException : Exception
{
    public ProviderFailure Reason { get; }
    public int? HttpStatus { get; }
    public TimeSpan? RetryAfter { get; }
    // Do not put remote response text, URLs, keys or prompts in exceptions/logging.
    public ProviderException(ProviderFailure reason, int? status = null, TimeSpan? retryAfter = null)
        : base(reason switch
        {
            ProviderFailure.Unauthorized => "API key was rejected (401).",
            ProviderFailure.Forbidden => "Provider refused access (403).",
            ProviderFailure.RateLimited => "Provider rate limit reached. Check account limits.",
            ProviderFailure.NotFound => "Selected API endpoint or model was not found.",
            ProviderFailure.UnsupportedOperation => "This provider has not enabled image editing.",
            ProviderFailure.InvalidImage => "The provider returned an invalid or oversized image.",
            ProviderFailure.CredentialUnavailable => "Encrypted provider credential is not accessible for this Windows user.",
            ProviderFailure.UnsafeRedirect => "A redirect or external image host was blocked.",
            ProviderFailure.TimedOut => "The image operation timed out; retry may be billed again.",
            ProviderFailure.Cancelled => "Request cancelled.",
            ProviderFailure.InvalidResponse => "The provider returned an unsupported image response.",
            ProviderFailure.NetworkError => "Could not connect to the configured API endpoint.",
            ProviderFailure.ServerError => "The provider reported a server error.",
            _ => "Check the provider configuration and capabilities."
        })
    {
        Reason = reason; HttpStatus = status; RetryAfter = retryAfter;
    }
}

public sealed class ProviderProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string GeneratePath { get; set; } = "images/generations";
    public string EditPath { get; set; } = "images/edits";
    public string ModelsPath { get; set; } = "models";
    public string ImageModel { get; set; } = "";
    public string EditModel { get; set; } = "";
    public string TextModel { get; set; } = "";
    public ProviderAuthMode AuthMode { get; set; } = ProviderAuthMode.Bearer;
    public ProviderEditShape EditShape { get; set; } = ProviderEditShape.ImageArray;
    public bool EnableEdits { get; set; }
    public bool AllowLoopbackHttp { get; set; }
    public bool Enabled { get; set; } = true;
    public bool GenerateVerified { get; set; }
    public bool EditVerified { get; set; }
    public DateTimeOffset? GenerateVerifiedAt { get; set; }
    public DateTimeOffset? EditVerifiedAt { get; set; }
    // Credential values NEVER serialized in profile JSON.
    public string SecretReference => "profile:" + Id.ToString("N");
    public ProviderProfile Clone() => (ProviderProfile)MemberwiseClone();
}

public static class ProviderProfileValidation
{
    public static Uri Validate(ProviderProfile profile)
    {
        if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) ||
            profile.Name.Length > 90 || string.IsNullOrWhiteSpace(profile.ImageModel) ||
            profile.ImageModel.Length > 160 || profile.EditModel.Length > 160 || profile.TextModel.Length > 160)
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var root) ||
            !string.IsNullOrEmpty(root.UserInfo) || !string.IsNullOrEmpty(root.Query) ||
            !string.IsNullOrEmpty(root.Fragment) || root.AbsolutePath.Contains("..", StringComparison.Ordinal) ||
            (root.Scheme != Uri.UriSchemeHttps &&
             !(root.Scheme == Uri.UriSchemeHttp && profile.AllowLoopbackHttp && root.IsLoopback)))
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
        foreach (var path in new[] { profile.GeneratePath, profile.EditPath, profile.ModelsPath })
            CheckPath(path);
        return root;
    }

    public static Uri Endpoint(ProviderProfile profile, string path)
    {
        var root = Validate(profile);
        CheckPath(path);
        // Append to Base URL *including* its /v1 prefix; never resolve rooted paths.
        return new Uri(root.AbsoluteUri.TrimEnd('/') + "/" + path.TrimStart('/'));
    }

    private static void CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 256 ||
            path.StartsWith('/') || path.Contains('?') || path.Contains('#') ||
            path.Contains('\\') || path.Contains("..") ||
            path.Contains("://") || path.Contains('%') ||
            path.Split('/').Any(s => s.Length == 0 || s == "."))
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
    }
}
