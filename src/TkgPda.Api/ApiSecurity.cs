using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TkgPda.Application;

namespace TkgPda.Api;

public static class RoleCodes
{
    public const string Admin = KnownRoleCodes.Admin;
    public const string Supervisor = KnownRoleCodes.Supervisor;
    public const string LineOperator = KnownRoleCodes.LineOperator;
    public const string WarehouseOperator = KnownRoleCodes.WarehouseOperator;
    public const string SemiFinishedWarehouseOperator = KnownRoleCodes.SemiFinishedWarehouseOperator;
    public const string FinishedGoodsWarehouseOperator = KnownRoleCodes.FinishedGoodsWarehouseOperator;

    public static readonly string[] OperatorRoles =
    [
        Admin,
        Supervisor,
        LineOperator,
        WarehouseOperator,
        SemiFinishedWarehouseOperator,
        FinishedGoodsWarehouseOperator
    ];
    public static readonly string[] VariantManagerRoles = [Admin, Supervisor];

    public static readonly string[] SemiFinishedWarehouseRoles =
    [
        Admin,
        Supervisor,
        SemiFinishedWarehouseOperator,
        WarehouseOperator
    ];

    public static readonly string[] FinishedGoodsWarehouseRoles =
    [
        Admin,
        Supervisor,
        FinishedGoodsWarehouseOperator,
        WarehouseOperator
    ];

    // Kept for backward compatibility
    public static readonly string[] WarehousePreparationRoles = SemiFinishedWarehouseRoles;
}

public static class PolicyNames
{
    public const string Operator = "operator";
    public const string VariantManager = "variant-manager";
    public const string Administrator = "administrator";
    public const string WarehousePreparation = "warehouse-preparation";
    public const string SemiFinishedWarehouse = "semi-finished-warehouse";
    public const string FinishedGoodsWarehouse = "finished-goods-warehouse";
    public const string ProductionOperator = "production-operator";
}

public static class ApiClaimNames
{
    public const string DeviceId = "device_id";
    public const string DeviceCode = "device_code";
    public const string FullName = "full_name";
    public const string ClientType = "client_type";
}

public static class ClientTypes
{
    public const string DeviceBound = "DEVICE_BOUND";
    public const string AdminWeb = "ADMIN_WEB";
}

public sealed record JwtOptions(
    string Issuer,
    string Audience,
    string SigningKey,
    int ExpiryMinutes,
    int ClockSkewSeconds)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException("JWT issuer and audience are required.");
        }

        if (Encoding.UTF8.GetByteCount(SigningKey) < 32)
        {
            throw new InvalidOperationException("JWT signing key must contain at least 32 UTF-8 bytes.");
        }

        if (ExpiryMinutes is < 1 or > 60)
        {
            throw new InvalidOperationException("JWT expiry must be between 1 and 60 minutes.");
        }

        if (ClockSkewSeconds is < 0 or > 300)
        {
            throw new InvalidOperationException("JWT clock skew must be between 0 and 300 seconds.");
        }
    }
}

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAt);

public sealed class JwtTokenService(JwtOptions options, TimeProvider timeProvider)
{
    public AccessTokenResult Issue(AuthUserRecord user, DeviceRecord device) =>
        Issue(user, ClientTypes.DeviceBound, device);

    public AccessTokenResult IssueAdmin(AuthUserRecord user) =>
        Issue(user, ClientTypes.AdminWeb, device: null);

    private AccessTokenResult Issue(AuthUserRecord user, string clientType, DeviceRecord? device)
    {
        var issuedAt = timeProvider.GetUtcNow();
        var expiresAt = issuedAt.AddMinutes(options.ExpiryMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ApiClaimNames.FullName, user.FullName),
            new(ApiClaimNames.ClientType, clientType)
        };
        if (device is not null)
        {
            claims.Add(new Claim(ApiClaimNames.DeviceId, device.Id.ToString()));
            claims.Add(new Claim(ApiClaimNames.DeviceCode, device.Code));
        }

        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            claims,
            notBefore: issuedAt.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new AccessTokenResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static TokenValidationParameters ValidationParameters(JwtOptions options) => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
        NameClaimType = JwtRegisteredClaimNames.UniqueName,
        RoleClaimType = ClaimTypes.Role
    };
}

public sealed class Pbkdf2PasswordHasher : IPasswordHashService
{
    public const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Algorithm = "tkg-pbkdf2-sha512";
    private readonly string _dummyHash;

    public Pbkdf2PasswordHasher()
    {
        _dummyHash = Hash("TKG-PDA-invalid-user-timing-value");
    }

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA512,
            HashBytes);
        return $"${Algorithm}$v=1$i={Iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        try
        {
            var parts = encodedHash.Split('$');
            if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != Algorithm || parts[2] != "v=1")
            {
                return false;
            }

            if (!parts[3].StartsWith("i=", StringComparison.Ordinal))
            {
                return false;
            }

            var iterations = int.Parse(parts[3].AsSpan(2), CultureInfo.InvariantCulture);
            if (iterations is < 100_000 or > 1_000_000)
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[4]);
            var expected = Convert.FromBase64String(parts[5]);
            if (salt.Length < SaltBytes || expected.Length != HashBytes)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA512,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public bool VerifyUnknownUser(string password)
    {
        _ = Verify(password, _dummyHash);
        return false;
    }
}

public static class PrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new ApiException(StatusCodes.Status401Unauthorized, "INVALID_TOKEN_SUBJECT", "The access token has no valid user subject.");

    public static bool IsAdministrator(this ClaimsPrincipal principal) => principal.IsInRole(RoleCodes.Admin);

    public static bool IsDeviceBound(this ClaimsPrincipal principal) =>
        principal.HasClaim(ApiClaimNames.ClientType, ClientTypes.DeviceBound);

    public static bool IsAdminWeb(this ClaimsPrincipal principal) =>
        principal.HasClaim(ApiClaimNames.ClientType, ClientTypes.AdminWeb);
}

public sealed class MachineAccessService(IApiDataStore dataStore)
{
    public Task<MachinePermissionRecord> EnsureAsync(
        HttpContext context,
        Guid machineId,
        MachineAccessKind access,
        CancellationToken cancellationToken)
    {
        var device = context.Items.TryGetValue(ApiContextKeys.Device, out var value) && value is DeviceRecord d ? d : null;
        return EnsureAsync(context.User, device, machineId, access, cancellationToken);
    }

    public Task<MachinePermissionRecord> EnsureAsync(
        ClaimsPrincipal principal,
        Guid machineId,
        MachineAccessKind access,
        CancellationToken cancellationToken) =>
        EnsureAsync(principal, null, machineId, access, cancellationToken);

    public async Task<MachinePermissionRecord> EnsureAsync(
        ClaimsPrincipal principal,
        DeviceRecord? device,
        Guid machineId,
        MachineAccessKind access,
        CancellationToken cancellationToken)
    {
        // 1. Role Safety (WP-03 Section 18):
        // Machine View/Operate business roles are strictly ADMIN, SUPERVISOR, LINE_OPERATOR.
        // Warehouse operators cannot access machine operations even if the device is bound.
        var isAdmin = principal.IsAdministrator();
        var isSupervisor = principal.IsInRole(RoleCodes.Supervisor);
        var isLineOperator = principal.IsInRole(RoleCodes.LineOperator);

        if (!isAdmin && !isSupervisor && !isLineOperator)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "ROLE_NOT_AUTHORIZED",
                "Kullanıcı bu makine işlemi için yetkili bir role sahip değil.");
        }

        // 2. Admin Web (device-independent):
        if (principal.IsAdminWeb())
        {
            if (!isAdmin)
            {
                throw new ApiException(
                    StatusCodes.Status403Forbidden,
                    "ADMIN_WEB_ROLE_REQUIRED",
                    "Admin Web tokens require the ADMIN role.");
            }

            return new MachinePermissionRecord(machineId, true, true, true, true);
        }

        // 3. Device-bound context:
        if (principal.IsDeviceBound())
        {
            if (device is null)
            {
                throw new ApiException(
                    StatusCodes.Status401Unauthorized,
                    "DEVICE_CONTEXT_REQUIRED",
                    "An authenticated request must include its token-bound device code.");
            }

            // A: LINE_OPERATOR (normal field operator)
            if (isLineOperator && !isAdmin && !isSupervisor)
            {
                if (device.MachineId is null)
                {
                    // Section 16 & 30D: Unbound line terminal fails closed
                    throw new ApiException(
                        StatusCodes.Status403Forbidden,
                        "DEVICE_MACHINE_BINDING_REQUIRED",
                        "Bu terminal henüz bir makineye tanımlanmamış. Lütfen sistem yöneticisine başvurun.");
                }

                if (device.MachineId.Value != machineId)
                {
                    // Section 15 & 30B: Cross-machine scope mismatch
                    throw new ApiException(
                        StatusCodes.Status403Forbidden,
                        "DEVICE_MACHINE_SCOPE_MISMATCH",
                        "Bu cihaz başka bir makineye atanmıştır.");
                }

                // Section 7 & 17:
                // Normal field LINE_OPERATOR machine View/Operate scope is determined by Device Machine Binding.
                // If a legacy user_machine_permissions row exists, respect its explicit restrictions.
                // If no row exists, grant View and Operate for the bound machine.
                var legacyPermission = await dataStore.FindMachinePermissionAsync(principal.UserId(), machineId, cancellationToken);
                if (legacyPermission is not null)
                {
                    var allowed = access switch
                    {
                        MachineAccessKind.View => legacyPermission.CanView,
                        MachineAccessKind.Operate => legacyPermission.CanOperate,
                        MachineAccessKind.ChangeVariant => legacyPermission.CanChangeVariant,
                        MachineAccessKind.ManageSlots => legacyPermission.CanManageSlots,
                        _ => false
                    };

                    return allowed
                        ? legacyPermission
                        : throw new ApiException(
                            StatusCodes.Status403Forbidden,
                            "MACHINE_ACCESS_DENIED",
                            "The user does not have the required permission for this machine.");
                }

                return access switch
                {
                    MachineAccessKind.View => new MachinePermissionRecord(machineId, true, true, false, false),
                    MachineAccessKind.Operate => new MachinePermissionRecord(machineId, true, true, false, false),
                    _ => throw new ApiException(
                        StatusCodes.Status403Forbidden,
                        "MACHINE_ACCESS_DENIED",
                        "The user does not have the required permission for this machine.")
                };
            }

            // B: SUPERVISOR
            if (isSupervisor)
            {
                // Section 25: Device-bound saha uygulamasında ADMIN/SUPERVISOR kullanılıyorsa
                // machine-specific operation server-side device scope dışına çıkmamalıdır.
                if (device.MachineId.HasValue && device.MachineId.Value != machineId)
                {
                    throw new ApiException(
                        StatusCodes.Status403Forbidden,
                        "DEVICE_MACHINE_SCOPE_MISMATCH",
                        "Bu cihaz başka bir makineye atanmıştır.");
                }

                var permission = await dataStore.FindMachinePermissionAsync(principal.UserId(), machineId, cancellationToken);
                var allowed = permission is not null && access switch
                {
                    MachineAccessKind.View => permission.CanView,
                    MachineAccessKind.Operate => permission.CanOperate,
                    MachineAccessKind.ChangeVariant => permission.CanChangeVariant,
                    MachineAccessKind.ManageSlots => permission.CanManageSlots,
                    _ => false
                };

                return allowed
                    ? permission!
                    : throw new ApiException(
                        StatusCodes.Status403Forbidden,
                        "MACHINE_ACCESS_DENIED",
                        "The user does not have the required permission for this machine.");
            }

            // C: ADMIN on device
            if (isAdmin)
            {
                if (device.MachineId.HasValue && device.MachineId.Value != machineId)
                {
                    throw new ApiException(
                        StatusCodes.Status403Forbidden,
                        "DEVICE_MACHINE_SCOPE_MISMATCH",
                        "Bu cihaz başka bir makineye atanmıştır.");
                }

                return new MachinePermissionRecord(machineId, true, true, true, true);
            }
        }

        // Fallback for non-device-bound:
        if (isAdmin)
        {
            return new MachinePermissionRecord(machineId, true, true, true, true);
        }

        var fallbackPermission = await dataStore.FindMachinePermissionAsync(principal.UserId(), machineId, cancellationToken);
        var fallbackAllowed = fallbackPermission is not null && access switch
        {
            MachineAccessKind.View => fallbackPermission.CanView,
            MachineAccessKind.Operate => fallbackPermission.CanOperate,
            MachineAccessKind.ChangeVariant => fallbackPermission.CanChangeVariant,
            MachineAccessKind.ManageSlots => fallbackPermission.CanManageSlots,
            _ => false
        };

        return fallbackAllowed
            ? fallbackPermission!
            : throw new ApiException(
                StatusCodes.Status403Forbidden,
                "MACHINE_ACCESS_DENIED",
                "The user does not have the required permission for this machine.");
    }
}

public sealed class DeviceContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IApiDataStore dataStore)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (context.User.IsAdminWeb())
        {
            if (!context.User.IsAdministrator())
            {
                throw new ApiException(StatusCodes.Status403Forbidden, "ADMIN_WEB_ROLE_REQUIRED", "Admin Web tokens require the ADMIN role.");
            }

            await next(context);
            return;
        }

        if (!context.User.IsDeviceBound())
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "INVALID_CLIENT_CONTEXT", "The access token has no supported client context.");
        }

        var tokenDeviceCode = context.User.FindFirstValue(ApiClaimNames.DeviceCode);
        var headerDeviceCode = context.Request.Headers[ApiHeaders.DeviceCode].ToString().Trim();
        if (string.IsNullOrWhiteSpace(tokenDeviceCode) || string.IsNullOrWhiteSpace(headerDeviceCode))
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "DEVICE_CONTEXT_REQUIRED", "An authenticated request must include its token-bound device code.");
        }

        if (!string.Equals(tokenDeviceCode, headerDeviceCode, StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "DEVICE_CONTEXT_MISMATCH", "The request device does not match the access token.");
        }

        var device = await dataStore.FindDeviceAsync(headerDeviceCode, context.RequestAborted);
        if (device is null || !device.IsActive)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "DEVICE_NOT_AUTHORIZED", "The client device is unknown or inactive.");
        }

        context.Items[ApiContextKeys.Device] = device;
        await next(context);
    }
}

public static class ApiHeaders
{
    public const string DeviceCode = "X-Device-Code";
    public const string CorrelationId = "X-Correlation-ID";
    public const string IdempotencyKey = "Idempotency-Key";
    public const string IdempotencyReplayed = "Idempotency-Replayed";
}

public static class ApiContextKeys
{
    public const string Device = "tkg.device";
    public const string CorrelationId = "tkg.correlation-id";
}
