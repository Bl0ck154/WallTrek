using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WallTrek.Services.VideoGen
{
    public class VideoGenerationRequest
    {
        public required string ImagePath { get; init; }
        public required VideoModelOption Model { get; init; }
        public required int DurationSeconds { get; init; }
        public required string Prompt { get; init; }
        public VideoUpscaleOption? Upscale { get; init; }
    }

    /// <summary>
    /// Generates seamless loop videos from a still image via the fal.ai queue API: the image is
    /// sent as both the start and end frame, then the result is optionally upscaled. The finished
    /// mp4 is saved next to the source image.
    /// </summary>
    public class FalVideoService : IDisposable
    {
        private const string QueueBaseUrl = "https://queue.fal.run/";
        private const string UpscaleEndpoint = "fal-ai/bytedance-upscaler/upscale/video";
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(15);

        private readonly string apiKey;
        private readonly HttpClient httpClient;

        public FalVideoService(string apiKey)
        {
            this.apiKey = apiKey;
            this.httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        }

        public async Task<string> GenerateLoopVideoAsync(VideoGenerationRequest request, IProgress<string> progress, CancellationToken cancellationToken)
        {
            progress.Report("Preparing image...");
            var imageDataUri = BuildJpegDataUri(request.ImagePath);

            var input = BuildGenerationInput(request, imageDataUri);
            var videoUrl = await RunQueueJobAsync(request.Model.Endpoint, input, "Generating video", progress, cancellationToken);

            if (request.Upscale != null && !string.IsNullOrEmpty(request.Upscale.TargetResolution))
            {
                var upscaleInput = new Dictionary<string, object>
                {
                    ["video_url"] = videoUrl,
                    ["target_resolution"] = request.Upscale.TargetResolution,
                };
                videoUrl = await RunQueueJobAsync(UpscaleEndpoint, upscaleInput, $"Upscaling to {request.Upscale.Name}", progress, cancellationToken);
            }

            progress.Report("Downloading video...");
            var videoBytes = await httpClient.GetByteArrayAsync(videoUrl, cancellationToken);

            var outputPath = BuildOutputPath(request.ImagePath);
            await File.WriteAllBytesAsync(outputPath, videoBytes, cancellationToken);
            return outputPath;
        }

        private static Dictionary<string, object> BuildGenerationInput(VideoGenerationRequest request, string imageDataUri)
        {
            switch (request.Model.Kind)
            {
                case VideoEndpointKind.PixVerseTransition:
                    return new Dictionary<string, object>
                    {
                        ["prompt"] = request.Prompt,
                        ["first_image_url"] = imageDataUri,
                        ["end_image_url"] = imageDataUri,
                        ["resolution"] = request.Model.Resolution,
                        ["duration"] = request.DurationSeconds,
                        ["aspect_ratio"] = "16:9",
                    };
                case VideoEndpointKind.KlingStartEnd:
                    return new Dictionary<string, object>
                    {
                        ["prompt"] = request.Prompt,
                        ["start_image_url"] = imageDataUri,
                        ["end_image_url"] = imageDataUri,
                        ["duration"] = request.DurationSeconds.ToString(),
                    };
                default:
                    throw new InvalidOperationException($"Unknown video endpoint kind: {request.Model.Kind}");
            }
        }

        /// <summary>
        /// Submits a job to queue.fal.run, polls its status URL until completion, and returns the
        /// result video URL. Uses the status/response URLs from the submit response verbatim
        /// (nested endpoints report them under the parent app's path).
        /// </summary>
        private async Task<string> RunQueueJobAsync(string endpoint, Dictionary<string, object> input, string stageLabel, IProgress<string> progress, CancellationToken cancellationToken)
        {
            progress.Report($"{stageLabel}: submitting...");

            var submitJson = await SendAsync(HttpMethod.Post, QueueBaseUrl + endpoint, JsonSerializer.Serialize(input), cancellationToken);
            using var submitDoc = JsonDocument.Parse(submitJson);
            var statusUrl = submitDoc.RootElement.GetProperty("status_url").GetString()
                ?? throw new InvalidOperationException("fal queue submit did not return a status_url");
            var responseUrl = submitDoc.RootElement.GetProperty("response_url").GetString()
                ?? throw new InvalidOperationException("fal queue submit did not return a response_url");

            var deadline = DateTime.UtcNow + JobTimeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"{stageLabel} timed out after {JobTimeout.TotalMinutes:F0} minutes");
                }

                await Task.Delay(PollInterval, cancellationToken);

                var statusJson = await SendAsync(HttpMethod.Get, statusUrl, null, cancellationToken);
                using var statusDoc = JsonDocument.Parse(statusJson);
                var status = statusDoc.RootElement.GetProperty("status").GetString();

                if (status == "COMPLETED")
                {
                    break;
                }
                if (status == "IN_QUEUE")
                {
                    var position = statusDoc.RootElement.TryGetProperty("queue_position", out var pos) ? pos.GetInt32() : -1;
                    progress.Report(position >= 0 ? $"{stageLabel}: queued (position {position})..." : $"{stageLabel}: queued...");
                }
                else if (status == "IN_PROGRESS")
                {
                    progress.Report($"{stageLabel}: in progress...");
                }
                else
                {
                    throw new InvalidOperationException($"{stageLabel} failed with status '{status}'");
                }
            }

            var resultJson = await SendAsync(HttpMethod.Get, responseUrl, null, cancellationToken);
            using var resultDoc = JsonDocument.Parse(resultJson);
            if (!resultDoc.RootElement.TryGetProperty("video", out var video) ||
                video.TryGetProperty("url", out var url) is false ||
                url.GetString() is not string videoUrl)
            {
                throw new InvalidOperationException($"{stageLabel} returned no video url: {Truncate(resultJson, 300)}");
            }
            return videoUrl;
        }

        private async Task<string> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
            if (jsonBody != null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"fal API request failed: {(int)response.StatusCode} {response.StatusCode} - {Truncate(body, 300)}");
            }
            return body;
        }

        // fal generation endpoints reject input images larger than 4000x4000 (image_too_large).
        private const int MaxImageDimension = 4000;

        /// <summary>
        /// The generation endpoints take image URLs; a JPEG data URI avoids a separate storage
        /// upload. Images exceeding fal's 4000x4000 input limit (e.g. Stability-upscaled
        /// wallpapers) are downscaled to fit. Existing JPEGs within limits and under ~7MB pass
        /// through untouched (Kling caps inputs at 10MB and base64 adds ~33%); everything else
        /// is re-encoded.
        /// </summary>
        private static string BuildJpegDataUri(string imagePath)
        {
            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            using var bitmap = new Bitmap(imagePath);

            byte[] jpegBytes;
            if (bitmap.Width <= MaxImageDimension && bitmap.Height <= MaxImageDimension)
            {
                jpegBytes = (extension == ".jpg" || extension == ".jpeg") && new FileInfo(imagePath).Length < 7_000_000
                    ? File.ReadAllBytes(imagePath)
                    : EncodeJpeg(bitmap);
            }
            else
            {
                var scale = Math.Min((double)MaxImageDimension / bitmap.Width, (double)MaxImageDimension / bitmap.Height);
                var width = (int)(bitmap.Width * scale);
                var height = (int)(bitmap.Height * scale);
                Logger.Info($"Input image is {bitmap.Width}x{bitmap.Height}; downscaling to {width}x{height} for fal's {MaxImageDimension}x{MaxImageDimension} limit");

                using var resized = new Bitmap(width, height);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bitmap, 0, 0, width, height);
                }
                jpegBytes = EncodeJpeg(resized);
            }

            return "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes);
        }

        private static byte[] EncodeJpeg(Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
            bitmap.Save(ms, encoder, parameters);
            return ms.ToArray();
        }

        private static string BuildOutputPath(string imagePath)
        {
            var directory = Path.GetDirectoryName(imagePath) ?? Settings.Instance.OutputDirectory;
            var stem = Path.GetFileNameWithoutExtension(imagePath);
            var outputPath = Path.Combine(directory, $"{stem} - loop.mp4");
            for (int i = 2; File.Exists(outputPath); i++)
            {
                outputPath = Path.Combine(directory, $"{stem} - loop ({i}).mp4");
            }
            return outputPath;
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }
}
