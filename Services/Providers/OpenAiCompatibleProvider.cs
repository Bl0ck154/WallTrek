using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WallTrek.Services.Providers;

/// <summary>OpenAI-style image endpoints; endpoint shapes are configurable but verified per-operation.</summary>
public sealed class OpenAiCompatibleProvider : IImageProvider
{
    private readonly ProviderProfile _profile;
    private readonly HttpClient _http;
    private readonly string? _key;
    private const int MaxOutputBytes = 24 * 1024 * 1024;
    private const int MaxResponseBytes = 36 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public string ProviderId => _profile.Id.ToString("N");
    public OpenAiCompatibleProvider(ProviderProfile profile, IProviderSecretStore secrets,
        HttpClient? http = null, string? keyOverride = null)
    {
        _profile = profile.Clone();
        ProviderProfileValidation.Validate(_profile);
        _key = keyOverride ?? secrets.Read(_profile.SecretReference);
        if (_profile.AuthMode != ProviderAuthMode.None && string.IsNullOrWhiteSpace(_key))
            throw new ProviderException(ProviderFailure.CredentialUnavailable);
        // No cookies or automatic redirects (especially with Authorization headers).
        _http = http ?? new HttpClient(new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: true);
        if (http is null) _http.Timeout = Timeout.InfiniteTimeSpan; // operation CTS owns the 5-minute limit
    }

    public async Task<ProviderCapabilities> ProbeAsync(CancellationToken ct = default)
    {
        // A list-models response checks auth/host only, not image operation support.
        using var message = CreateMessage(HttpMethod.Get,
            ProviderProfileValidation.Endpoint(_profile, _profile.ModelsPath));
        using var response = await SendAsync(message, ct, TimeSpan.FromSeconds(20));
        if (!response.IsSuccessStatusCode) ThrowHttp(response);
        return new ProviderCapabilities(true, false, false,
            "Connection only; image generation and edits still unverified",
            DateTimeOffset.UtcNow);
    }

    public Task<ProviderImageResult> GenerateAsync(ProviderImageRequest request, CancellationToken ct = default)
        => ExecuteAsync(request, false, ct);

    public Task<ProviderImageResult> EditAsync(ProviderImageRequest request, CancellationToken ct = default)
        => ExecuteAsync(request, true, ct);

    private async Task<ProviderImageResult> ExecuteAsync(ProviderImageRequest request, bool edit, CancellationToken ct)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(RequestTimeout); // includes response body and signed-image download
        try { return await ExecuteCoreAsync(request, edit, operation.Token); }
        catch (ProviderException ex) when (ex.Reason == ProviderFailure.Cancelled && !ct.IsCancellationRequested)
        { throw new ProviderException(ProviderFailure.TimedOut); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { throw new ProviderException(ProviderFailure.Cancelled); }
        catch (OperationCanceledException)
        { throw new ProviderException(ProviderFailure.TimedOut); }
    }

    private async Task<ProviderImageResult> ExecuteCoreAsync(ProviderImageRequest request, bool edit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 32_000)
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
        var references = request.References ?? Array.Empty<ProviderImageReference>();
        if (edit && !_profile.EnableEdits) throw new ProviderException(ProviderFailure.UnsupportedOperation);
        if (edit && (references.Count is < 1 or > 5))
            throw new ProviderException(ProviderFailure.InvalidConfiguration);
        if (!edit && references.Count != 0)
            throw new ProviderException(ProviderFailure.UnsupportedOperation);
        string prompt = request.Prompt;
        if (references.Any(r => r.Role == ProviderReferenceRole.Style))
            prompt = "Use the style reference for aesthetic style only, not identity or content. " + prompt;
        if (references.Any(r => r.Role == ProviderReferenceRole.Identity))
            prompt = "Preserve the recognizable facial identity and physical features of the person in the identity reference. " + prompt;
        var model = edit
            ? string.IsNullOrWhiteSpace(_profile.EditModel) ? _profile.ImageModel : _profile.EditModel
            : _profile.ImageModel;
        using var message = CreateMessage(HttpMethod.Post,
            ProviderProfileValidation.Endpoint(_profile, edit ? _profile.EditPath : _profile.GeneratePath));
        if (!edit)
        {
            var json = new Dictionary<string, object> { ["model"] = model, ["prompt"] = prompt, ["n"] = 1 };
            if (!string.IsNullOrWhiteSpace(request.Size)) json["size"] = request.Size;
            if (!string.IsNullOrWhiteSpace(request.Quality)) json["quality"] = request.Quality;
            message.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        }
        else
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent(prompt), "prompt");
            if (!string.IsNullOrWhiteSpace(request.Size)) form.Add(new StringContent(request.Size), "size");
            if (!string.IsNullOrWhiteSpace(request.Quality)) form.Add(new StringContent(request.Quality), "quality");
            // Preserve semantic order: identity/content first, style-only reference last.
            foreach (var reference in references.OrderBy(r => r.Role == ProviderReferenceRole.Style ? 1 : 0))
            {
                if (reference.Bytes.Length is < 16 or > 20 * 1024 * 1024)
                    throw new ProviderException(ProviderFailure.InvalidImage);
                var (mime, _, _) = ProviderImageValidator.Check(reference.Bytes);
                if (mime is not ("image/png" or "image/jpeg" or "image/webp"))
                    throw new ProviderException(ProviderFailure.InvalidImage);
                var part = new ByteArrayContent(reference.Bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue(mime);
                // The adapter's explicit field mode covers both common vendor variants.
                form.Add(part, _profile.EditShape == ProviderEditShape.ImageArray ? "image[]" : "image",
                    SanitizeFileName(reference.FileName, mime));
            }
            message.Content = form;
        }
        using var response = await SendAsync(message, ct, RequestTimeout);
        if (!response.IsSuccessStatusCode) ThrowHttp(response);
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media is not null && !media.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException(ProviderFailure.InvalidResponse);
        byte[] body = await ReadBoundedAsync(response.Content, MaxResponseBytes, ct);
        string? b64, mediaUrl;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("data")[0];
            b64 = first.TryGetProperty("b64_json", out var base64) ? base64.GetString() : null;
            mediaUrl = first.TryGetProperty("url", out var url) ? url.GetString() : null;
        }
        catch (JsonException) { throw new ProviderException(ProviderFailure.InvalidResponse); }
        catch (KeyNotFoundException) { throw new ProviderException(ProviderFailure.InvalidResponse); }
        catch (InvalidOperationException) { throw new ProviderException(ProviderFailure.InvalidResponse); }
        catch (IndexOutOfRangeException) { throw new ProviderException(ProviderFailure.InvalidResponse); }
        byte[] image;
        if (!string.IsNullOrWhiteSpace(b64))
        {
            // Reject an excessively long base64 response before allocating image bytes.
            if (b64.Length > MaxOutputBytes * 4L / 3 + 8)
                throw new ProviderException(ProviderFailure.InvalidImage);
            try { image = Convert.FromBase64String(b64); }
            catch (FormatException) { throw new ProviderException(ProviderFailure.InvalidImage); }
        }
        else if (!string.IsNullOrWhiteSpace(mediaUrl))
        {
            // Only same-origin HTTPS (or explicitly accepted loopback HTTP). Never attach keys to GET.
            if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var download) ||
                download.Scheme != message.RequestUri!.Scheme ||
                download.IdnHost != message.RequestUri.IdnHost ||
                download.Port != message.RequestUri.Port ||
                !string.IsNullOrEmpty(download.UserInfo) ||
                !string.IsNullOrEmpty(download.Fragment))
                throw new ProviderException(ProviderFailure.UnsafeRedirect);
            using var get = new HttpRequestMessage(HttpMethod.Get, download);
            using var fetched = await SendAsync(get, ct, RequestTimeout);
            if (!fetched.IsSuccessStatusCode) ThrowHttp(fetched);
            image = await ReadBoundedAsync(fetched.Content, MaxOutputBytes, ct);
        }
        else throw new ProviderException(ProviderFailure.InvalidResponse);

        var (imageMime, width, height) = ProviderImageValidator.Check(image);
        // Upstream FileService uses System.Drawing.Bitmap, which does not decode WebP.
        // Never advertise a successful image if the wallpaper pipeline cannot persist it.
        if (imageMime == "image/webp")
            throw new ProviderException(ProviderFailure.UnsupportedOperation);
        return new ProviderImageResult(image, imageMime, width, height, ProviderId, model);
    }

    private HttpRequestMessage CreateMessage(HttpMethod verb, Uri uri)
    {
        var request = new HttpRequestMessage(verb, uri);
        if (_profile.AuthMode == ProviderAuthMode.Bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        else if (_profile.AuthMode == ProviderAuthMode.ApiKeyHeader)
            request.Headers.TryAddWithoutValidation("x-api-key", _key);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken ct, TimeSpan timeout)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new ProviderException(ProviderFailure.UnsafeRedirect);
            }
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ProviderException(ProviderFailure.TimedOut); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { throw new ProviderException(ProviderFailure.Cancelled); }
        catch (HttpRequestException) { throw new ProviderException(ProviderFailure.NetworkError); }
    }

    private static void ThrowHttp(HttpResponseMessage response)
    {
        int code = (int)response.StatusCode;
        var delay = response.Headers.RetryAfter?.Delta;
        throw new ProviderException(code switch
        {
            401 => ProviderFailure.Unauthorized,
            403 => ProviderFailure.Forbidden,
            404 => ProviderFailure.NotFound,
            429 => ProviderFailure.RateLimited,
            >= 500 => ProviderFailure.ServerError,
            _ => ProviderFailure.InvalidResponse
        }, code, delay);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int max, CancellationToken ct)
    {
        if (content.Headers.ContentLength > max) throw new ProviderException(ProviderFailure.InvalidImage);
        await using var input = await content.ReadAsStreamAsync(ct);
        using var stream = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (stream.Length + n > max) throw new ProviderException(ProviderFailure.InvalidImage);
            stream.Write(buffer, 0, n);
        }
        return stream.ToArray();
    }
    private static string SanitizeFileName(string value, string mime)
    {
        var name = Path.GetFileName(value);
        if (name.Length is > 0 and <= 100 && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
            return name;
        return mime == "image/jpeg" ? "reference.jpg" : mime == "image/webp" ? "reference.webp" : "reference.png";
    }
}
