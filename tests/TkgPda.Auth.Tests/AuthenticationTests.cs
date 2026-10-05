using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using TkgPda.Api;
using TkgPda.Application;
using Xunit;

namespace TkgPda.Auth.Tests;

public sealed class PasswordHashTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_uses_random_salt()
    {
        Assert.NotEqual(_hasher.Hash("Correct Horse Battery Staple"), _hasher.Hash("Correct Horse Battery Staple"));
    }

    [Fact]
    public void Correct_password_verifies()
    {
        var hash = _hasher.Hash("Correct Horse Battery Staple");
        Assert.True(_hasher.Verify("Correct Horse Battery Staple", hash));
    }

    [Fact]
    public void Wrong_password_is_rejected()
    {
        var hash = _hasher.Hash("Correct Horse Battery Staple");
        Assert.False(_hasher.Verify("wrong password", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$tkg-pbkdf2-sha512$v=1$i=999999999$bad$bad")]
    public void Malformed_hash_is_rejected_without_throwing(string hash)
    {
        Assert.False(_hasher.Verify("password", hash));
    }

    [Fact]
    public void Hash_records_the_fixed_iteration_policy()
    {
        Assert.Contains($"$i={Pbkdf2PasswordHasher.Iterations}$", _hasher.Hash("password"), StringComparison.Ordinal);
    }
}

public sealed class JwtTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly JwtOptions Options = new(
        "tkg-pda-tests",
        "tkg-pda-test-clients",
        "phase-04-test-signing-key-with-at-least-thirty-two-bytes",
        15,
        0);

    [Fact]
    public void Token_contains_exact_roles_and_device_binding()
    {
        var user = User();
        var device = Device();
        var sessionId = Guid.NewGuid();
        var token = new JwtTokenService(Options, new FixedTimeProvider(Now)).Issue(user, device, sessionId);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Token);

        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal(device.Id.ToString(), jwt.Claims.Single(claim => claim.Type == ApiClaimNames.DeviceId).Value);
        Assert.Equal(device.Code, jwt.Claims.Single(claim => claim.Type == ApiClaimNames.DeviceCode).Value);
        Assert.Equal(sessionId.ToString(), jwt.Claims.Single(claim => claim.Type == ApiClaimNames.SessionId).Value);
        Assert.Equal(ClientTypes.DeviceBound, jwt.Claims.Single(claim => claim.Type == ApiClaimNames.ClientType).Value);
        Assert.Equal(user.Roles.Order(), jwt.Claims.Where(claim => claim.Type == ClaimTypes.Role).Select(claim => claim.Value).Order());
    }

    [Fact]
    public void Token_contains_session_id_when_supplied()
    {
        var user = User();
        var device = Device();
        var sessionId = Guid.NewGuid();
        var token = new JwtTokenService(Options, new FixedTimeProvider(Now)).Issue(user, device, sessionId);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Token);

        Assert.Equal(sessionId.ToString(), jwt.Claims.Single(claim => claim.Type == ApiClaimNames.SessionId).Value);
    }

    [Fact]
    public void Admin_web_token_has_explicit_client_type_without_device_or_session_claims()
    {
        var token = new JwtTokenService(Options, new FixedTimeProvider(Now)).IssueAdmin(AdminUser());
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Token);

        Assert.Equal(ClientTypes.AdminWeb, jwt.Claims.Single(claim => claim.Type == ApiClaimNames.ClientType).Value);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == ApiClaimNames.DeviceId);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == ApiClaimNames.DeviceCode);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == ApiClaimNames.SessionId);
        Assert.Contains(jwt.Claims, claim => claim.Type == ClaimTypes.Role && claim.Value == RoleCodes.Admin);
    }

    [Fact]
    public void Admin_web_token_validates_with_the_same_short_lived_security_parameters()
    {
        var token = new JwtTokenService(Options, TimeProvider.System).IssueAdmin(AdminUser());
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(
            token.Token,
            JwtTokenService.ValidationParameters(Options),
            out _);

        Assert.True(principal.IsInRole(RoleCodes.Admin));
        Assert.Equal(ClientTypes.AdminWeb, principal.FindFirstValue(ApiClaimNames.ClientType));
    }

    [Fact]
    public void Password_hasher_implements_the_application_hashing_port()
    {
        IPasswordHashService service = new Pbkdf2PasswordHasher();
        Assert.StartsWith("$tkg-pbkdf2-sha512$", service.Hash("Correct Horse Battery Staple"), StringComparison.Ordinal);
    }

    [Fact]
    public void Token_expiry_is_exactly_configured_lifetime()
    {
        var token = new JwtTokenService(Options, new FixedTimeProvider(Now)).Issue(User(), Device(), Guid.NewGuid());
        Assert.Equal(Now.AddMinutes(15), token.ExpiresAt);
    }

    [Fact]
    public void Token_validates_with_expected_issuer_audience_and_key()
    {
        var sessionId = Guid.NewGuid();
        var token = new JwtTokenService(Options, TimeProvider.System).Issue(User(), Device(), sessionId);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(
            token.Token,
            JwtTokenService.ValidationParameters(Options),
            out _);

        Assert.Equal("operator", principal.Identity?.Name);
        Assert.Equal(sessionId.ToString(), principal.FindFirstValue(ApiClaimNames.SessionId));
        Assert.True(principal.IsInRole(RoleCodes.LineOperator));
    }

    [Fact]
    public void Wrong_audience_is_rejected()
    {
        var token = new JwtTokenService(Options, TimeProvider.System).Issue(User(), Device(), Guid.NewGuid());
        var wrongOptions = Options with { Audience = "wrong-audience" };

        Assert.Throws<SecurityTokenInvalidAudienceException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(
                token.Token,
                JwtTokenService.ValidationParameters(wrongOptions),
                out _));
    }

    private static AuthUserRecord User() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "operator",
        "unused",
        "Test Operator",
        "E-001",
        true,
        [RoleCodes.LineOperator, RoleCodes.WarehouseOperator]);

    private static DeviceRecord Device() => new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "PDA-TEST-01",
        "Test PDA",
        "PDA",
        true);

    private static AuthUserRecord AdminUser() => new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "admin",
        "unused",
        "Test Administrator",
        "E-ADMIN",
        true,
        [RoleCodes.Admin]);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
