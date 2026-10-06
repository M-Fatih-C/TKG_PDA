using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Npgsql;
using TkgPda.Api;
using TkgPda.Application;
using TkgPda.Infrastructure.Postgres;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("TkgPda")
    ?? throw new InvalidOperationException("ConnectionStrings:TkgPda is required.");
var jwtOptions = new JwtOptions(
    builder.Configuration["Authentication:Jwt:Issuer"] ?? "tkg-pda",
    builder.Configuration["Authentication:Jwt:Audience"] ?? "tkg-pda-clients",
    builder.Configuration["Authentication:Jwt:SigningKey"] ?? string.Empty,
    builder.Configuration.GetValue("Authentication:Jwt:ExpiryMinutes", 15),
    builder.Configuration.GetValue("Authentication:Jwt:ClockSkewSeconds", 30));
jwtOptions.Validate();
var fieldSessionOptions = new FieldSessionOptions(
    builder.Configuration.GetValue("Authentication:FieldSession:LeaseSeconds", 120),
    builder.Configuration.GetValue("Authentication:FieldSession:HeartbeatSeconds", 30));
fieldSessionOptions.Validate();
var rdsOptions = RdsOptions.FromConfiguration(builder.Configuration);
var rdsCoreDiscoveryOptions = RdsCoreDiscoveryOptions.FromConfiguration(builder.Configuration);
var rdsCoreExecutionOptions = RdsCoreExecutionOptions.FromConfiguration(builder.Configuration);
var operatorWebOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()?
    .Select(value => value.Trim())
    .Where(value => value.Length > 0)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray() ?? [];
foreach (var origin in operatorWebOrigins)
{
    if (origin.Contains('*', StringComparison.Ordinal)
        || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        || uri.UserInfo.Length > 0
        || uri.Query.Length > 0
        || uri.Fragment.Length > 0
        || uri.AbsolutePath != "/")
    {
        throw new InvalidOperationException(
            $"Cors:AllowedOrigins contains an invalid exact HTTP(S) origin: {origin}");
    }
}

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(fieldSessionOptions);
builder.Services.AddSingleton(rdsOptions);
builder.Services.AddSingleton(rdsCoreDiscoveryOptions);
builder.Services.AddSingleton(rdsCoreExecutionOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IPasswordHashService>(services => services.GetRequiredService<Pbkdf2PasswordHasher>());
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PostgresFieldSessionStore>();
builder.Services.AddSingleton<IFieldSessionStore>(services => services.GetRequiredService<PostgresFieldSessionStore>());
builder.Services.AddSingleton<PostgresApiDataStore>();
builder.Services.AddSingleton<IApiDataStore>(services => services.GetRequiredService<PostgresApiDataStore>());
builder.Services.AddSingleton<IOperatorTransportQueueStore>(services => services.GetRequiredService<PostgresApiDataStore>());
builder.Services.AddSingleton<IAdminConfigurationStore, PostgresAdminConfigurationStore>();
builder.Services.AddSingleton<IRdsIntegrationStore, PostgresRdsIntegrationStore>();
builder.Services.AddSingleton<IRdsExecutionStore, PostgresRdsExecutionStore>();
builder.Services.AddSingleton<IRdsCommissioningConfigurationStore, PostgresRdsCommissioningConfigurationStore>();
builder.Services.AddHttpClient("RdsReadOnly", client =>
{
    client.Timeout = TimeSpan.FromSeconds(rdsOptions.TimeoutSeconds);
    if (rdsOptions.BaseAddress is { } baseAddress)
    {
        client.BaseAddress = baseAddress;
    }
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = false,
    AllowAutoRedirect = false
});
builder.Services.AddSingleton<IRdsReadClient, RdsReadClient>();
builder.Services.AddHttpClient("RdsCoreDiscovery", client =>
{
    client.Timeout = TimeSpan.FromSeconds(rdsCoreDiscoveryOptions.TimeoutSeconds);
    if (rdsCoreDiscoveryOptions.BaseAddress is { } baseAddress)
    {
        client.BaseAddress = baseAddress;
    }
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = false,
    AllowAutoRedirect = false
});
builder.Services.AddSingleton<IRdsCoreDiscoveryClient, RdsCoreDiscoveryClient>();
builder.Services.AddSingleton<RdsCommissioningDiscoveryService>();
builder.Services.AddHttpClient("RdsCoreNativeOrder", client =>
{
    client.Timeout = TimeSpan.FromSeconds(rdsCoreExecutionOptions.TimeoutSeconds);
    if (rdsCoreExecutionOptions.BaseAddress is { } baseAddress)
    {
        client.BaseAddress = baseAddress;
    }
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = false,
    AllowAutoRedirect = false
});
builder.Services.AddSingleton<IRdsCoreNativeOrderClient, RdsCoreNativeOrderClient>();
builder.Services.AddSingleton<IRobotTelemetryProvider, RdsCoreRobotTelemetryProvider>();
builder.Services.AddHttpClient("RdsMutation", client =>
{
    client.Timeout = TimeSpan.FromSeconds(rdsOptions.TimeoutSeconds);
    if (rdsOptions.BaseAddress is { } baseAddress)
    {
        client.BaseAddress = baseAddress;
    }
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = false,
    AllowAutoRedirect = false
});
builder.Services.AddSingleton<IRdsExecutionClient, RdsExecutionClient>();
builder.Services.AddSingleton<RdsIntegrationService>();
builder.Services.AddSingleton<IRdsWorkSiteReconciler>(services =>
    services.GetRequiredService<RdsIntegrationService>());
builder.Services.AddSingleton<AdminConfigurationService>();
builder.Services.AddSingleton(services =>
    new PostgresTransportWorkflowStore(services.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<ITransportWorkflowStore>(services => services.GetRequiredService<PostgresTransportWorkflowStore>());
builder.Services.AddSingleton<IContainerActionStore>(services => services.GetRequiredService<PostgresTransportWorkflowStore>());
builder.Services.AddSingleton<IPartDemandStore>(services => services.GetRequiredService<PostgresTransportWorkflowStore>());
builder.Services.AddSingleton<PartDemandService>();
builder.Services.AddSingleton<TransportWorkflowService>();
builder.Services.AddSingleton<IProductionSessionStore>(services => services.GetRequiredService<PostgresTransportWorkflowStore>());
builder.Services.AddSingleton<ProductionSessionService>();
builder.Services.AddSingleton<IWarehousePreparationStore>(services => services.GetRequiredService<PostgresTransportWorkflowStore>());
builder.Services.AddSingleton<WarehousePreparationService>();
builder.Services.Configure<DeviceNotificationOptions>(builder.Configuration.GetSection("DeviceNotifications"));
builder.Services.AddSingleton<IDeviceNotificationStore, PostgresDeviceNotificationStore>();
builder.Services.AddSingleton<IFinishedGoodsStore>(services => services.GetRequiredService<PostgresTransportWorkflowStore>());
builder.Services.AddSingleton<FinishedGoodsStoreService>();
builder.Services.AddSingleton<ContainerActionService>();
builder.Services.AddSingleton<OperatorTransportQueueService>();
builder.Services.AddSingleton<MachineAccessService>();
builder.Services.AddSingleton<PdaOperationalContextService>();
builder.Services.AddSingleton(new RdsExecutionSettings(
    rdsOptions.DispatchEnabled,
    TimeSpan.FromSeconds(rdsOptions.WorkSiteFreshnessSeconds),
    rdsOptions.EmptyContentValue,
    rdsOptions.FullContentValue));
builder.Services.AddSingleton<RdsExecutionCoordinator>();
builder.Services.AddHostedService<RdsExecutionWorker>();
builder.Services.AddCors(options => options.AddPolicy("OperatorWeb", policy =>
{
    if (operatorWebOrigins.Length > 0)
    {
        policy.WithOrigins(operatorWebOrigins);
    }
    policy
        .WithMethods("GET", "POST", "PUT", "OPTIONS")
        .WithHeaders(
            "Accept",
            "Authorization",
            "Content-Type",
            ApiHeaders.DeviceCode,
            ApiHeaders.IdempotencyKey,
            ApiHeaders.CorrelationId)
        .WithExposedHeaders(ApiHeaders.CorrelationId);
}));

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = JwtTokenService.ValidationParameters(jwtOptions);
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await ApiErrorWriter.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status401Unauthorized,
                    "AUTHENTICATION_REQUIRED",
                    "A valid bearer access token is required.");
            },
            OnForbidden = context => ApiErrorWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "ROLE_NOT_AUTHORIZED",
                "The authenticated user role is not allowed to perform this operation.")
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireRole(RoleCodes.OperatorRoles)
        .Build();
    options.AddPolicy(PolicyNames.Operator, policy => policy
        .RequireRole(RoleCodes.OperatorRoles)
        .RequireClaim(ApiClaimNames.ClientType, ClientTypes.DeviceBound));
    options.AddPolicy(PolicyNames.VariantManager, policy => policy
        .RequireRole(RoleCodes.VariantManagerRoles)
        .RequireClaim(ApiClaimNames.ClientType, ClientTypes.DeviceBound));
    options.AddPolicy(PolicyNames.WarehousePreparation, policy => policy
        .RequireRole(RoleCodes.WarehousePreparationRoles)
        .RequireClaim(ApiClaimNames.ClientType, ClientTypes.DeviceBound));
    options.AddPolicy(PolicyNames.SemiFinishedWarehouse, policy => policy
        .RequireRole(RoleCodes.SemiFinishedWarehouseRoles)
        .RequireClaim(ApiClaimNames.ClientType, ClientTypes.DeviceBound));
    options.AddPolicy(PolicyNames.FinishedGoodsWarehouse, policy => policy
        .RequireRole(RoleCodes.FinishedGoodsWarehouseRoles)
        .RequireClaim(ApiClaimNames.ClientType, ClientTypes.DeviceBound));
    options.AddPolicy(PolicyNames.ProductionOperator, policy => policy
        .RequireRole(RoleCodes.Admin, RoleCodes.Supervisor, RoleCodes.LineOperator)
        .RequireClaim(ApiClaimNames.ClientType, ClientTypes.DeviceBound));
    options.AddPolicy(PolicyNames.Administrator, policy => policy
        .RequireRole(RoleCodes.Admin)
        .RequireClaim(ApiClaimNames.ClientType, ClientTypes.AdminWeb));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) => new ValueTask(ApiErrorWriter.WriteAsync(
        context.HttpContext,
        StatusCodes.Status429TooManyRequests,
        "LOGIN_RATE_LIMITED",
        "Too many login attempts were made for this client partition."));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        $"{context.Connection.RemoteIpAddress}:{context.Request.Headers[ApiHeaders.DeviceCode]}",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        $"admin-web:{context.Connection.RemoteIpAddress}",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TkgPda.Startup");
startupLogger.LogInformation(
    "RDS Integration: {Status}, RDS Dispatch: {Dispatch}, RDSCore: {Endpoint}",
    rdsOptions.Enabled ? "ENABLED" : "DISABLED",
    rdsOptions.DispatchEnabled ? "ENABLED" : "DISABLED",
    rdsCoreExecutionOptions.BaseAddressDisplaySafe ?? "NONE");

app.UseMiddleware<ApiExceptionMiddleware>();
app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers[ApiHeaders.CorrelationId].ToString();
    var correlationId = Guid.TryParse(supplied, out var parsed) ? parsed : Guid.NewGuid();
    context.Items[ApiContextKeys.CorrelationId] = correlationId;
    context.Response.Headers[ApiHeaders.CorrelationId] = correlationId.ToString();
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseCors("OperatorWeb");
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<DeviceContextMiddleware>();
app.UseMiddleware<FieldSessionValidationMiddleware>();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (IApiDataStore store, CancellationToken cancellationToken) =>
{
    var ready = await store.IsReadyAsync(cancellationToken);
    return Results.Json(
        new { status = ready ? "ready" : "not-ready" },
        statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapTkgPdaApi();
app.MapProductionSessions();
app.MapWarehousePreparation();
app.MapFinishedGoodsStore();
app.MapDeviceNotifications();
app.MapTkgPdaAdminApi();
app.MapTkgPdaRdsIntegrationApi();

app.Run();

public partial class Program;
