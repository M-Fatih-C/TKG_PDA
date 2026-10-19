using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TkgPda.Api.Tests;

public sealed class M4HttpClientTests
{
    [Fact]
    public void Configuration_fails_closed_for_unsafe_or_unlisted_origins()
    {
        Assert.Throws<InvalidOperationException>(() => Options(
            baseUrl: "http://10.20.30.40:5800",
            allowedOrigins: ["http://10.20.30.40:5800"],
            allowInsecureRemoteHttp: false).Validate());

        Assert.Throws<InvalidOperationException>(() => Options(
            baseUrl: "https://m4.example.test",
            allowedOrigins: ["https://other.example.test"]).Validate());

        Assert.Throws<InvalidOperationException>(() => Options(
            baseUrl: "https://user:secret@m4.example.test",
            allowedOrigins: ["https://m4.example.test"]).Validate());

        Options(
            baseUrl: "http://10.20.30.40:5800",
            allowedOrigins: ["http://10.20.30.40:5800"],
            allowInsecureRemoteHttp: true).Validate();
    }

    [Fact]
    public async Task Health_uses_application_headers_and_correlation_without_human_session_auth()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, "{\"status\":\"ok\"}"));
        var (client, options) = Client(handler);
        var correlationId = Guid.NewGuid();

        var result = await client.GetHealthAsync(correlationId);

        Assert.True(result.Reachable);
        Assert.Equal("M4_REACHABLE", result.Code);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal("unit-app", handler.LastRequest!.Headers.GetValues(M4AuthenticationHandler.AppIdHeader).Single());
        Assert.Equal(options.AppKey, handler.LastRequest.Headers.GetValues(M4AuthenticationHandler.AppKeyHeader).Single());
        Assert.Equal(correlationId.ToString("D"), handler.LastRequest.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Null(handler.LastRequest.Headers.Authorization);
        Assert.False(handler.LastRequest.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task Transient_read_failure_is_retried_with_a_fresh_request()
    {
        var handler = new RecordingHandler((attempt, _) => attempt == 1
            ? Json(HttpStatusCode.ServiceUnavailable, "{\"code\":\"temporarilyUnavailable\"}")
            : Json(HttpStatusCode.OK, "{\"status\":\"ok\"}"));
        var (client, _) = Client(handler, maxReadRetries: 1);

        var result = await client.GetHealthAsync(Guid.NewGuid());

        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(2, handler.Requests.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public async Task Authentication_and_provider_errors_are_normalized_without_secret_leakage()
    {
        var unauthorized = new RecordingHandler((_, _) => Json(
            HttpStatusCode.Unauthorized,
            "{\"code\":\"badAuth\",\"message\":\"unit-app unit-test-key-value\"}"));
        var (unauthorizedClient, unauthorizedOptions) = Client(unauthorized);

        var authError = await Assert.ThrowsAsync<M4HttpException>(
            () => unauthorizedClient.GetHealthAsync(Guid.NewGuid()));

        Assert.Equal("M4_AUTHENTICATION_FAILED", authError.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, authError.StatusCode);
        Assert.DoesNotContain(unauthorizedOptions.AppId!, authError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(unauthorizedOptions.AppKey!, authError.ToString(), StringComparison.Ordinal);

        var rejected = new RecordingHandler((_, _) => Json(
            HttpStatusCode.BadRequest,
            "{\"code\":\"errMissingHttpQueryParam\",\"message\":\"unit-app unit-test-key-value missing scene\"}"));
        var (rejectedClient, rejectedOptions) = Client(rejected);

        var providerError = await Assert.ThrowsAsync<M4HttpException>(
            () => rejectedClient.GetAsync("/api/fleet/robots/all-all", Guid.NewGuid()));

        Assert.Equal("M4_HTTP_FAILURE", providerError.Code);
        Assert.Equal("errMissingHttpQueryParam", providerError.ProviderCode);
        Assert.Contains("[REDACTED]", providerError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rejectedOptions.AppId!, providerError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rejectedOptions.AppKey!, providerError.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, rejected.CallCount);
    }

    [Theory]
    [InlineData("/api/fleet/orders/create")]
    [InlineData("//attacker.invalid/api/ping")]
    [InlineData("/api/fleet/scenes/demo/extra/query-all")]
    [InlineData("/api/fleet/scenes/demo%2Fescape/query-all")]
    public async Task Read_client_blocks_mutation_and_path_override_routes(string path)
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var (client, _) = Client(handler);

        var error = await Assert.ThrowsAsync<M4HttpException>(
            () => client.GetAsync(path, Guid.NewGuid()));

        Assert.Equal("M4_READ_ENDPOINT_NOT_ALLOWED", error.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Read_only_post_query_is_allowed_and_authenticated()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, "{\"total\":0,\"data\":[]}"));
        var (client, _) = Client(handler);

        var response = await client.PostQueryAsync(
            "/api/entity/find/page",
            new { model = "TransportOrder", page = 1, pageSize = 20 },
            Guid.NewGuid());

        Assert.Equal(0, response.Body.GetProperty("total").GetInt32());
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        using var payload = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("TransportOrder", payload.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Invalid_or_oversized_success_payload_is_rejected()
    {
        var invalid = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json")
        });
        var (invalidClient, _) = Client(invalid);
        var invalidError = await Assert.ThrowsAsync<M4HttpException>(
            () => invalidClient.GetHealthAsync(Guid.NewGuid()));
        Assert.Equal("M4_INVALID_JSON", invalidError.Code);

        var oversized = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 70 * 1024))
        });
        var (oversizedClient, _) = Client(oversized);
        var oversizedError = await Assert.ThrowsAsync<M4HttpException>(
            () => oversizedClient.GetHealthAsync(Guid.NewGuid()));
        Assert.Equal("M4_RESPONSE_TOO_LARGE", oversizedError.Code);
    }

    [Fact]
    public async Task Disabled_read_fails_before_network_access()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var options = Options(readEnabled: false) with { DispatchEnabled = true };
        options.Validate();
        var client = BuildClient(handler, options);

        var error = await Assert.ThrowsAsync<M4HttpException>(
            () => client.GetHealthAsync(Guid.NewGuid()));

        Assert.Equal("M4_READ_DISABLED", error.Code);
        Assert.Equal(0, handler.CallCount);
    }

    private static (M4HttpClient Client, M4Options Options) Client(
        RecordingHandler handler,
        int maxReadRetries = 0)
    {
        var options = Options(maxReadRetries: maxReadRetries);
        options.Validate();
        return (BuildClient(handler, options), options);
    }

    private static M4HttpClient BuildClient(RecordingHandler terminalHandler, M4Options options)
    {
        var authentication = new M4AuthenticationHandler(options) { InnerHandler = terminalHandler };
        var httpClient = new HttpClient(authentication)
        {
            BaseAddress = options.BaseAddress,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
        return new M4HttpClient(httpClient, options, TimeProvider.System, NullLogger<M4HttpClient>.Instance);
    }

    private static M4Options Options(
        bool readEnabled = true,
        string baseUrl = "http://127.0.0.1:5800",
        IReadOnlyList<string>? allowedOrigins = null,
        bool allowInsecureRemoteHttp = false,
        int maxReadRetries = 0) => new(
            Enabled: true,
            ReadEnabled: readEnabled,
            DispatchEnabled: false,
            BaseUrl: baseUrl,
            AllowedOrigins: allowedOrigins ?? ["http://127.0.0.1:5800"],
            AllowInsecureRemoteHttp: allowInsecureRemoteHttp,
            TimeoutSeconds: 5,
            MaxReadRetries: maxReadRetries,
            RetryBaseDelayMilliseconds: 0,
            AppId: "unit-app",
            AppKey: "unit-test-key-value");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body)
    };

    private sealed class RecordingHandler(
        Func<int, HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            Requests.Add(request);
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return route(CallCount, request);
        }
    }
}
