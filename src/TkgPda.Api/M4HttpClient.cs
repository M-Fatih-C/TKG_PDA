using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TkgPda.Api;

public sealed record M4Options(
    bool Enabled,
    bool ReadEnabled,
    bool DispatchEnabled,
    string? BaseUrl,
    IReadOnlyList<string> AllowedOrigins,
    bool AllowInsecureRemoteHttp,
    int TimeoutSeconds,
    int MaxReadRetries,
    int RetryBaseDelayMilliseconds,
    string? AppId,
    string? AppKey)
{
    public static M4Options FromConfiguration(IConfiguration configuration)
    {
        var options = new M4Options(
            configuration.GetValue("M4:Enabled", false),
            configuration.GetValue("M4:ReadEnabled", false),
            configuration.GetValue("M4:DispatchEnabled", false),
            configuration["M4:BaseUrl"],
            configuration.GetSection("M4:AllowedOrigins").Get<string[]>() ?? [],
            configuration.GetValue("M4:AllowInsecureRemoteHttp", false),
            configuration.GetValue("M4:TimeoutSeconds", 10),
            configuration.GetValue("M4:MaxReadRetries", 1),
            configuration.GetValue("M4:RetryBaseDelayMilliseconds", 100),
            TkgSecureConfig.UnprotectIfNeeded(configuration["M4:AppId"]),
            TkgSecureConfig.UnprotectIfNeeded(configuration["M4:AppKey"]));
        options.Validate();
        return options;
    }

    public Uri? BaseAddress => string.IsNullOrWhiteSpace(BaseUrl)
        ? null
        : new Uri(BaseUrl.Trim(), UriKind.Absolute);

    public string? SafeBaseAddress => BaseAddress is { } uri ? Origin(uri) : null;

    public void Validate()
    {
        if (TimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException("M4:TimeoutSeconds must be between 1 and 60.");
        }

        if (MaxReadRetries is < 0 or > 3 || RetryBaseDelayMilliseconds is < 0 or > 2000)
        {
            throw new InvalidOperationException("M4 read retry settings are outside the supported bounds.");
        }

        if (!Enabled)
        {
            if (ReadEnabled || DispatchEnabled)
            {
                throw new InvalidOperationException("M4 read or dispatch access requires M4:Enabled.");
            }
            return;
        }

        if (!ReadEnabled && !DispatchEnabled)
        {
            throw new InvalidOperationException("Enabled M4 integration requires read or dispatch access.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                "Enabled M4 integration requires an origin-only absolute HTTP(S) BaseUrl without embedded credentials.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !AllowInsecureRemoteHttp)
        {
            throw new InvalidOperationException(
                "M4 refuses insecure non-loopback HTTP unless the explicit field-network override is enabled.");
        }

        var allowedOrigins = AllowedOrigins
            .Select(value => ParseAllowedOrigin(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!allowedOrigins.Contains(Origin(uri)))
        {
            throw new InvalidOperationException("M4:BaseUrl is not present in M4:AllowedOrigins.");
        }

        if (string.IsNullOrWhiteSpace(AppId) || AppId.Trim().Length > 200
            || string.IsNullOrWhiteSpace(AppKey) || AppKey.Length > 512)
        {
            throw new InvalidOperationException(
                "Enabled M4 integration requires bounded AppId and AppKey values from protected configuration.");
        }
    }

    private static string ParseAllowedOrigin(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException("M4:AllowedOrigins must contain exact HTTP(S) origins.");
        }
        return Origin(uri);
    }

    private static string Origin(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
}

public sealed class M4HttpException(
    string code,
    string message,
    HttpStatusCode? statusCode = null,
    string? providerCode = null,
    bool transient = false,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? ProviderCode { get; } = providerCode;
    public bool IsTransient { get; } = transient;
}

public sealed record M4JsonResponse(
    JsonElement Body,
    HttpStatusCode StatusCode,
    int AttemptCount,
    DateTimeOffset ObservedAt);

public sealed record M4HealthResult(
    bool Reachable,
    string Code,
    string Message,
    int AttemptCount,
    DateTimeOffset ObservedAt);

public sealed class M4AuthenticationHandler(M4Options options) : DelegatingHandler
{
    public const string AppIdHeader = "xyy-app-id";
    public const string AppKeyHeader = "xyy-app-key";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.AppKey))
        {
            throw new M4HttpException(
                "M4_AUTH_CONFIGURATION_INVALID",
                "M4 application authentication is not configured.");
        }

        request.Headers.Remove(AppIdHeader);
        request.Headers.Remove(AppKeyHeader);
        request.Headers.TryAddWithoutValidation(AppIdHeader, options.AppId.Trim());
        request.Headers.TryAddWithoutValidation(AppKeyHeader, options.AppKey);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Read-only M4 transport. The endpoint allow-list prevents this client from
/// calling order, robot or scene mutation routes. Command APIs use a separate
/// client and dispatch gate in later work packages.
/// </summary>
public sealed class M4HttpClient(
    HttpClient httpClient,
    M4Options options,
    TimeProvider timeProvider,
    ILogger<M4HttpClient> logger)
{
    private const int MaxResponseBytes = 64 * 1024;

    public async Task<M4HealthResult> GetHealthAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var response = await GetAsync("/api/ping", correlationId, cancellationToken);
        if (response.Body.ValueKind != JsonValueKind.Object)
        {
            throw new M4HttpException(
                "M4_INVALID_RESPONSE",
                "M4 ping returned an unexpected JSON shape.",
                response.StatusCode);
        }

        return new M4HealthResult(
            true,
            "M4_REACHABLE",
            "M4 API authentication and ping succeeded.",
            response.AttemptCount,
            response.ObservedAt);
    }

    public Task<M4JsonResponse> GetAsync(
        string relativeUri,
        Guid correlationId,
        CancellationToken cancellationToken = default) =>
        SendReadAsync(HttpMethod.Get, relativeUri, null, correlationId, cancellationToken);

    public Task<M4JsonResponse> PostQueryAsync(
        string relativeUri,
        object payload,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendReadAsync(HttpMethod.Post, relativeUri, payload, correlationId, cancellationToken);
    }

    private async Task<M4JsonResponse> SendReadAsync(
        HttpMethod method,
        string relativeUri,
        object? payload,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        EnsureReadReady();
        M4ReadEndpointPolicy.EnsureAllowed(method, relativeUri);

        for (var attempt = 1; attempt <= options.MaxReadRetries + 1; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(method, relativeUri);
                request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId.ToString("D"));
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (payload is not null)
                {
                    request.Content = JsonContent.Create(payload);
                }

                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                var body = await ReadBoundedAsync(response.Content, cancellationToken);
                stopwatch.Stop();
                logger.LogInformation(
                    "M4 read {Method} {Path} returned {StatusCode} in {ElapsedMilliseconds}ms on attempt {Attempt} with correlation {CorrelationId}.",
                    method.Method,
                    SafePath(relativeUri),
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    attempt,
                    correlationId);

                if (response.IsSuccessStatusCode)
                {
                    return new M4JsonResponse(
                        ParseSuccess(body, response.StatusCode),
                        response.StatusCode,
                        attempt,
                        timeProvider.GetUtcNow());
                }

                var error = NormalizeError(response.StatusCode, body);
                if (error.IsTransient && attempt <= options.MaxReadRetries)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }
                throw error;
            }
            catch (M4HttpException)
            {
                throw;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt <= options.MaxReadRetries)
                {
                    logger.LogWarning(
                        "M4 read {Method} {Path} timed out on attempt {Attempt} with correlation {CorrelationId}.",
                        method.Method, SafePath(relativeUri), attempt, correlationId);
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }
                throw new M4HttpException(
                    "M4_TIMEOUT",
                    "M4 read request timed out.",
                    transient: true,
                    innerException: error);
            }
            catch (HttpRequestException error)
            {
                if (attempt <= options.MaxReadRetries)
                {
                    logger.LogWarning(
                        "M4 read {Method} {Path} was unavailable on attempt {Attempt} with correlation {CorrelationId}.",
                        method.Method, SafePath(relativeUri), attempt, correlationId);
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }
                throw new M4HttpException(
                    "M4_UNAVAILABLE",
                    "M4 read endpoint is unavailable.",
                    transient: true,
                    innerException: error);
            }
        }

        throw new UnreachableException();
    }

    private void EnsureReadReady()
    {
        if (!options.Enabled)
        {
            throw new M4HttpException("M4_DISABLED", "M4 integration is disabled.");
        }
        if (!options.ReadEnabled)
        {
            throw new M4HttpException("M4_READ_DISABLED", "M4 read access is disabled.");
        }
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelayMilliseconds * attempt);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    private static JsonElement ParseSuccess(byte[] body, HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new M4HttpException(
                "M4_INVALID_JSON",
                "M4 returned invalid JSON.",
                statusCode,
                innerException: error);
        }
    }

    private M4HttpException NormalizeError(HttpStatusCode statusCode, byte[] body)
    {
        var providerError = TryReadProviderError(body);
        var transient = statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

        return statusCode switch
        {
            HttpStatusCode.Unauthorized => new(
                "M4_AUTHENTICATION_FAILED",
                "M4 rejected application credentials.",
                statusCode,
                providerError.Code),
            HttpStatusCode.Forbidden => new(
                "M4_AUTHORIZATION_FAILED",
                "M4 application credentials lack permission for the read endpoint.",
                statusCode,
                providerError.Code),
            _ => new(
                transient ? "M4_TRANSIENT_HTTP_FAILURE" : "M4_HTTP_FAILURE",
                ProviderMessage(statusCode, providerError),
                statusCode,
                providerError.Code,
                transient)
        };
    }

    private (string? Code, string? Message) TryReadProviderError(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeNode) ? SafeProviderCode(codeNode.ToString()) : null;
            var message = root.TryGetProperty("message", out var messageNode)
                ? Redact(messageNode.ToString())
                : null;
            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string ProviderMessage(HttpStatusCode statusCode, (string? Code, string? Message) error)
    {
        var prefix = $"M4 returned HTTP {(int)statusCode}.";
        if (error.Code is null && error.Message is null)
        {
            return prefix;
        }
        return $"{prefix} Provider code: {error.Code ?? "unknown"}. {error.Message}".Trim();
    }

    private string? Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var redacted = value;
        foreach (var secret in new[] { options.AppId, options.AppKey })
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }
        return redacted.Length <= 512 ? redacted : redacted[..512];
    }

    private static string? SafeProviderCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
        {
            return null;
        }
        return value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            ? value
            : null;
    }

    private static string SafePath(string relativeUri)
    {
        var parsed = new Uri(new Uri("http://m4.invalid"), relativeUri);
        return parsed.AbsolutePath;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new M4HttpException("M4_RESPONSE_TOO_LARGE", "M4 response exceeded the configured read limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > MaxResponseBytes)
            {
                throw new M4HttpException("M4_RESPONSE_TOO_LARGE", "M4 response exceeded the configured read limit.");
            }
            output.Write(buffer, 0, read);
        }
    }
}

internal static class M4ReadEndpointPolicy
{
    public static void EnsureAllowed(HttpMethod method, string relativeUri)
    {
        if (string.IsNullOrWhiteSpace(relativeUri)
            || !Uri.TryCreate(relativeUri, UriKind.Relative, out _)
            || !relativeUri.StartsWith("/", StringComparison.Ordinal)
            || relativeUri.StartsWith("//", StringComparison.Ordinal)
            || relativeUri.Contains('\\'))
        {
            throw Blocked();
        }

        var uri = new Uri(new Uri("http://m4.invalid"), relativeUri);
        if (uri.Host != "m4.invalid" || uri.Fragment.Length > 0)
        {
            throw Blocked();
        }

        var path = uri.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var allowed = method == HttpMethod.Get && (
                path == "/api/ping"
                || path == "/api/fleet/scenes/list"
                || path == "/api/fleet/robots/all-all"
                || segments is ["api", "fleet", "scenes", "schema", var schemaId]
                   && IsSafeSegment(schemaId))
            || method == HttpMethod.Post && (
                path == "/api/entity/find/many"
                || path == "/api/entity/find/page"
                || segments is ["api", "fleet", "scenes", var sceneId, "query-all"]
                   && IsSafeSegment(sceneId));

        if (!allowed)
        {
            throw Blocked();
        }
    }

    private static M4HttpException Blocked() => new(
        "M4_READ_ENDPOINT_NOT_ALLOWED",
        "The requested M4 endpoint is outside the read-only allow-list.");

    private static bool IsSafeSegment(string value) =>
        value.Length is > 0 and <= 200
        && value is not "." and not ".."
        && !value.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("%5c", StringComparison.OrdinalIgnoreCase);
}
