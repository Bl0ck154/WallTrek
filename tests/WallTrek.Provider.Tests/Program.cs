using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WallTrek.Services.Providers;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Profiles and secrets remain separate", Profiles),
    ("Base path /v1 and Bearer auth", Generation),
    ("x-api-key isolated by profile", ApiKeys),
    ("Model connection probe is not an image capability test", Probe),
    ("Signed image URL read without Authorization", UrlImage),
    ("Cross-origin URLs and redirects blocked", UnsafeMedia),
    ("Edit multipart image[] and image variants", Edits),
    ("Reference silently ignored is forbidden", UnsupportedRefs),
    ("HTTP typed errors and Retry-After", Errors),
    ("Malformed output rejected", InvalidImage),
    ("Cancellation propagates", Cancellation),
    ("Non-loopback HTTP and credential-less remote blocked", Validation)
};
var failed = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.GetType().Name); }
}
Environment.ExitCode = failed > 0 ? 1 : 0;

static readonly byte[] ValidPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/xU4AAAAASUVORK5CYII=");

static ProviderProfile Profile(string host = "https://example.test/v1") => new()
{
    Name = "Test Profile",
    BaseUrl = host,
    ImageModel = "image-model-test",
    EditModel = "edit-model-test",
    EnableEdits = true
};
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static HttpResponseMessage OkImage() => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(new
    {
        data = new[] { new { b64_json = Convert.ToBase64String(ValidPng) } }
    }), Encoding.UTF8, "application/json")
};
static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) =>
    new(new StubHandler(fn));
static Task<HttpResponseMessage> Respond(HttpResponseMessage r) => Task.FromResult(r);

static Task Profiles()
{
    var temp = Path.Combine(Path.GetTempPath(), "walltrek-provider-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var vault = new FakeSecrets();
        var store = new ProviderProfileStore(vault, Path.Combine(temp, "providers.json"));
        var a = Profile();
        var b = Profile("https://other.test/host/v1");
        b.Name = "Second";
        store.Save(a, "KEY-A-DO-NOT-SERIALIZE");
        store.Save(b, "KEY-B-DO-NOT-SERIALIZE");
        Check(store.List().Count == 2, "unlimited independent entries");
        Check(!File.ReadAllText(Path.Combine(temp, "providers.json")).Contains("KEY-A"), "secret leaked into profile JSON");
        Check(store.ReadKey(a.Id) == "KEY-A-DO-NOT-SERIALIZE", "key ref A missing");
        Check(store.ReadKey(b.Id) == "KEY-B-DO-NOT-SERIALIZE", "key ref B missing");
        store.Delete(a.Id);
        Check(store.Find(a.Id) is null && vault.Read(a.SecretReference) is null, "deletion failed");
        var reloaded = new ProviderProfileStore(vault, Path.Combine(temp, "providers.json"));
        Check(reloaded.List().Count == 1, "profile persistence broken");
    }
    finally { Directory.Delete(temp, true); }
    return Task.CompletedTask;
}

static async Task Generation()
{
    var p = Profile();
    var sent = 0;
    using var http = Client(async (req, ct) =>
    {
        sent++;
        Check(req.RequestUri?.AbsolutePath == "/v1/images/generations", "lost base URL prefix");
        Check(req.Headers.Authorization?.ToString() == "Bearer KEY-ONE", "bearer key");
        Check(req.Method == HttpMethod.Post, "method");
        var json = await req.Content!.ReadAsStringAsync(ct);
        Check(json.Contains("image-model-test") && json.Contains("mountains"), "model or prompt omitted");
        return OkImage();
    });
    var provider = new OpenAiCompatibleProvider(p, new FakeSecrets(), http, "KEY-ONE");
    var output = await provider.GenerateAsync(new ProviderImageRequest("mountains"));
    Check(output.Width == 1 && output.MimeType == "image/png", "image decoded");
    Check(sent == 1, "unexpected retry");
}
static async Task ApiKeys()
{
    var p1 = Profile();
    var p2 = Profile();
    var secrets = new FakeSecrets();
    secrets.Write(p1.SecretReference, "KEY-AAA");
    secrets.Write(p2.SecretReference, "KEY-BBB");
    p2.AuthMode = ProviderAuthMode.ApiKeyHeader;
    using var http = Client((req, ct) =>
    {
        if (req.Headers.Authorization is not null)
            Check(req.Headers.Authorization.Parameter == "KEY-AAA" &&
                  !req.Headers.Contains("x-api-key"), "mixed Bearer");
        else
            Check(req.Headers.GetValues("x-api-key").Single() == "KEY-BBB", "mixed x-api-key");
        return Respond(OkImage());
    });
    await new OpenAiCompatibleProvider(p1, secrets, http).GenerateAsync(new("lake"));
    await new OpenAiCompatibleProvider(p2, secrets, http).GenerateAsync(new("lake"));
}
static async Task Probe()
{
    using var http = Client((req, ct) =>
    {
        Check(req.RequestUri?.AbsolutePath == "/v1/models", "incorrect models path");
        return Respond(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json") });
    });
    var cap = await new OpenAiCompatibleProvider(Profile(), new FakeSecrets(), http, "KEY")
        .ProbeAsync();
    Check(cap.ConnectionOk && !cap.GenerateVerified && !cap.EditVerified, "probe overclaimed capability");
}
static async Task UrlImage()
{
    var calls = 0;
    using var http = Client((req, ct) =>
    {
        calls++;
        if (calls == 1)
            return Respond(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[{\"url\":\"https://example.test/signed/image.png?token=opaque\"}]}", Encoding.UTF8, "application/json") });
        Check(req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/signed/image.png", "image fetch");
        Check(req.Headers.Authorization is null && !req.Headers.Contains("x-api-key"), "key leaked into image GET");
        return Respond(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ValidPng) });
    });
    var result = await new OpenAiCompatibleProvider(Profile(), new FakeSecrets(), http, "KEY")
        .GenerateAsync(new("lake"));
    Check(result.Width == 1 && calls == 2, "URL image not fetched");
}
static async Task UnsafeMedia()
{
    var calls = 0;
    using var http = Client((req, ct) =>
    {
        calls++;
        return Respond(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"data\":[{\"url\":\"https://evil.test/image.png\"}]}", Encoding.UTF8, "application/json") });
    });
    await Expect(ProviderFailure.UnsafeRedirect, () =>
        new OpenAiCompatibleProvider(Profile(), new FakeSecrets(), http, "KEY").GenerateAsync(new("lake")));
    Check(calls == 1, "external URL was fetched");

    using var redirected = Client((req, ct) => Respond(new HttpResponseMessage(HttpStatusCode.Redirect)
    { Headers = { Location = new Uri("https://evil.test") } }));
    await Expect(ProviderFailure.UnsafeRedirect, () =>
        new OpenAiCompatibleProvider(Profile(), new FakeSecrets(), redirected, "KEY").GenerateAsync(new("lake")));
}
static async Task Edits()
{
    foreach (var shape in new[] { ProviderEditShape.Image, ProviderEditShape.ImageArray })
    {
        var p = Profile();
        p.EditShape = shape;
        using var http = Client(async (req, ct) =>
        {
            Check(req.RequestUri?.AbsolutePath == "/v1/images/edits", "wrong edit route");
            Check(req.Content is MultipartFormDataContent, "not multipart");
            var body = await req.Content!.ReadAsStringAsync(ct);
            Check(body.Contains("edit-model-test") && body.Contains("Keep person"), "edit model/prompt absent");
            Check(body.Contains(shape == ProviderEditShape.Image ? "name=\"image\"" : "name=\"image[]\""), "wrong multipart field");
            return OkImage();
        });
        var refs = new[] { new ProviderImageReference(ValidPng, "portrait.png", ProviderReferenceRole.Identity) };
        var result = await new OpenAiCompatibleProvider(p, new FakeSecrets(), http, "KEY")
            .EditAsync(new ProviderImageRequest("Keep person", refs));
        Check(result.Width == 1, "edit returned invalid result");
    }
}
static async Task UnsupportedRefs()
{
    var p = Profile();
    p.EnableEdits = false;
    using var http = Client((req, ct) => throw new Exception("Network should not run"));
    var provider = new OpenAiCompatibleProvider(p, new FakeSecrets(), http, "KEY");
    var refs = new[] { new ProviderImageReference(ValidPng, "x.png", ProviderReferenceRole.Content) };
    await Expect(ProviderFailure.UnsupportedOperation, () =>
        provider.GenerateAsync(new ProviderImageRequest("ref", refs)));
    await Expect(ProviderFailure.UnsupportedOperation, () =>
        provider.EditAsync(new ProviderImageRequest("ref", refs)));
}
static async Task Errors()
{
    var map = new[] {
        (HttpStatusCode.Unauthorized, ProviderFailure.Unauthorized),
        (HttpStatusCode.Forbidden, ProviderFailure.Forbidden),
        (HttpStatusCode.NotFound, ProviderFailure.NotFound),
        (HttpStatusCode.TooManyRequests, ProviderFailure.RateLimited),
        (HttpStatusCode.InternalServerError, ProviderFailure.ServerError)
    };
    foreach (var (status, reason) in map)
    {
        using var http = Client((req, ct) =>
        {
            var r = new HttpResponseMessage(status);
            if (status == HttpStatusCode.TooManyRequests)
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return Respond(r);
        });
        await Expect(reason, () =>
            new OpenAiCompatibleProvider(Profile(), new FakeSecrets(), http, "KEY").GenerateAsync(new("lake")));
    }
}
static async Task InvalidImage()
{
    using var http = Client((req, ct) =>
        Respond(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"data\":[{\"b64_json\":\"YmFk\"}]}",
            Encoding.UTF8, "application/json") }));
    await Expect(ProviderFailure.InvalidImage, () =>
        new OpenAiCompatibleProvider(Profile(), new FakeSecrets(), http, "KEY").GenerateAsync(new("lake")));
}
static async Task Cancellation()
{
    using var http = Client(async (req, ct) =>
    {
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        return OkImage();
    });
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
    await Expect(ProviderFailure.Cancelled, () =>
        new OpenAiCompatibleProvider(Profile(), new FakeSecrets(), http, "KEY")
            .GenerateAsync(new("lake"), cancel.Token));
}
static Task Validation()
{
    var p = Profile("http://remote.test/v1");
    try { ProviderProfileValidation.Validate(p); throw new Exception("Unsafe HTTP accepted"); }
    catch (ProviderException ex) when (ex.Reason == ProviderFailure.InvalidConfiguration) { }
    p.BaseUrl = "http://localhost:8080/v1";
    p.AllowLoopbackHttp = true;
    ProviderProfileValidation.Validate(p);
    return Task.CompletedTask;
}
static async Task Expect(ProviderFailure reason, Func<Task> action)
{
    try { await action(); throw new Exception("Expected failure not thrown"); }
    catch (ProviderException ex) when (ex.Reason == reason) { }
}
sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => _handler = handler;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        => _handler(req, ct);
}
sealed class FakeSecrets : IProviderSecretStore
{
    private readonly Dictionary<string, string> _values = new();
    public string? Read(string id) => _values.GetValueOrDefault(id);
    public void Write(string id, string value) => _values[id] = value;
    public void Delete(string id) => _values.Remove(id);
}
