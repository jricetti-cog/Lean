using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace QuantConnect.Services.Gateway;

/// <summary>API keys from configuration plus JWT bearer validation behind one policy scheme, and the per-key limiter.</summary>
public static class GatewayAuth
{
    public const string PolicyScheme = "LeanGateway";
    public const string ApiKeyScheme = "ApiKey";
    public const string JwtScheme = JwtBearerDefaults.AuthenticationScheme;
    public const string ApiKeyHeader = "X-Api-Key";
    public const string AdminClaim = "lean:admin";
    public const string PartitionClaim = "lean:partition";

    public static IServiceCollection AddGatewayAuth(this IServiceCollection services, GatewayOptions options)
    {
        services.AddSingleton<ApiKeyRegistry>();
        services.AddAuthentication(PolicyScheme)
            .AddPolicyScheme(PolicyScheme, "API key or JWT bearer", o => o.ForwardDefaultSelector = http =>
                options.Jwt.Enabled && LooksLikeJwt(BearerToken(http.Request)) ? JwtScheme : ApiKeyScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyScheme, null)
            .AddJwtBearer(JwtScheme, o =>
            {
                var jwt = options.Jwt;
                o.MapInboundClaims = false;
                if (!string.IsNullOrWhiteSpace(jwt.Authority))
                {
                    o.Authority = jwt.Authority;
                    o.RequireHttpsMetadata = jwt.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                }
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = !string.IsNullOrWhiteSpace(jwt.Issuer),
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = !string.IsNullOrWhiteSpace(jwt.Audience),
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = string.IsNullOrWhiteSpace(jwt.SigningKey) ? null : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    NameClaimType = jwt.UserClaim,
                    RoleClaimType = jwt.RoleClaim,
                };
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        ctx.Token ??= BearerToken(ctx.Request);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = ctx =>
                    {
                        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
                        identity.AddClaim(new Claim(PartitionClaim, "jwt:" + identity.Name));
                        if (ctx.Principal.IsInRole(jwt.AdminRole)) identity.AddClaim(new Claim(AdminClaim, "true"));
                        return Task.CompletedTask;
                    },
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        ctx.Response.Headers.WWWAuthenticate = "Bearer";
                        await Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, "Unauthorized",
                            ctx.AuthenticateFailure?.Message ?? "a valid bearer token or API key is required");
                    },
                };
            });
        services.AddAuthorization();
        services.AddRateLimiter(o =>
        {
            var limit = options.RateLimit;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                IsExempt(http.Request.Path)
                    ? RateLimitPartition.GetNoLimiter("exempt")
                    : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(http), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit.PermitLimit,
                        Window = TimeSpan.FromSeconds(limit.WindowSeconds),
                        QueueLimit = limit.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    }));
            o.OnRejected = async (ctx, _) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }
                await Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status429TooManyRequests, "Too Many Requests",
                    $"rate limit of {limit.PermitLimit} requests per {limit.WindowSeconds}s exceeded for this API key");
            };
        });
        return services;
    }

    /// <summary>The caller's user name as sent upstream in <c>X-Lean-User</c>.</summary>
    public static string User(this ClaimsPrincipal principal) => principal.Identity?.Name ?? "anonymous";

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.HasClaim(AdminClaim, "true");

    public static string PartitionKey(HttpContext http) =>
        http.User.FindFirst(PartitionClaim)?.Value ?? "ip:" + http.Connection.RemoteIpAddress;

    private static bool IsExempt(PathString path) =>
        path.StartsWithSegments("/healthz") || path.StartsWithSegments("/readyz") || path.StartsWithSegments("/openapi");

    public static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header["Bearer ".Length..].Trim();
        // EventSource cannot send headers, so the SSE endpoint also takes the token as ?access_token=.
        return IsEventStream(request.Path) && request.Query.TryGetValue("access_token", out var token) && !string.IsNullOrEmpty(token) ? token.ToString() : null;
    }

    private static bool IsEventStream(PathString path) =>
        path.StartsWithSegments("/api/v1/jobs") && path.Value!.EndsWith("/events", StringComparison.Ordinal);

    private static bool LooksLikeJwt(string? token) => token != null && token.Count(c => c == '.') == 2;

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Hashes of the configured keys; lookups compare hashes in fixed time.</summary>
public sealed class ApiKeyRegistry
{
    private readonly List<(byte[] Hash, ApiKeyOptions Key)> _keys;

    public ApiKeyRegistry(IOptions<GatewayOptions> options)
    {
        _keys = options.Value.ApiKeys
            .Where(k => !string.IsNullOrWhiteSpace(k.User) && (!string.IsNullOrEmpty(k.Key) || !string.IsNullOrEmpty(k.KeySha256)))
            .Select(k => (Convert.FromHexString(k.KeySha256 ?? GatewayAuth.Sha256Hex(k.Key!)), k))
            .ToList();
    }

    public int Count => _keys.Count;

    public (ApiKeyOptions Key, string Id)? Find(string presented)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        (ApiKeyOptions, string)? match = null;
        foreach (var (candidate, key) in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(candidate, hash)) match = (key, Convert.ToHexStringLower(candidate)[..12]);
        }
        return match;
    }
}

/// <summary><c>X-Api-Key: &lt;key&gt;</c> or <c>Authorization: Bearer &lt;key&gt;</c>.</summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApiKeyRegistry _registry;

    public ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyRegistry registry)
        : base(options, logger, encoder)
    {
        _registry = registry;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[GatewayAuth.ApiKeyHeader].FirstOrDefault() ?? GatewayAuth.BearerToken(Request);
        if (string.IsNullOrEmpty(presented)) return Task.FromResult(AuthenticateResult.NoResult());
        if (_registry.Find(presented) is not var (key, id)) return Task.FromResult(AuthenticateResult.Fail("invalid API key"));
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, key.User),
            new(GatewayAuth.PartitionClaim, "key:" + id),
        };
        if (key.Admin) claims.Add(new Claim(GatewayAuth.AdminClaim, "true"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failure = (await HandleAuthenticateOnceSafeAsync()).Failure?.Message;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Problems.WriteAsync(Context, StatusCodes.Status401Unauthorized, "Unauthorized", failure ?? "an API key (X-Api-Key or Authorization: Bearer) is required");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Problems.WriteAsync(Context, StatusCodes.Status403Forbidden, "Forbidden", "this API key may not perform the operation");
}
