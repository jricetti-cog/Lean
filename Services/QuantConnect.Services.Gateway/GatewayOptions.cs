namespace QuantConnect.Services.Gateway;

/// <summary>The <c>Gateway</c> configuration section.</summary>
public sealed class GatewayOptions
{
    public const string Section = "Gateway";

    public UpstreamOptions Upstreams { get; set; } = new();

    /// <summary>API keys issued from configuration (<c>Gateway__ApiKeys__0__Key</c>, ...).</summary>
    public List<ApiKeyOptions> ApiKeys { get; set; } = new();

    public JwtOptions Jwt { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary>Largest algorithm file or project zip accepted by the upload endpoints.</summary>
    public long MaxUploadBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>MinIO URL that clients can reach, used to sign download redirects (defaults to <c>S3:ServiceUrl</c>).</summary>
    public string? PublicS3Url { get; set; }

    public int PresignedUrlSeconds { get; set; } = 900;
}

/// <summary>Base URLs of the internal services the gateway fronts.</summary>
public sealed class UpstreamOptions
{
    public string Orchestrator { get; set; } = "http://job-orchestrator:8080";
    public string Results { get; set; } = "http://results:8080";
    public string MarketData { get; set; } = "http://market-data:8080";
    public string Report { get; set; } = "http://report:8080";
    public string Optimizer { get; set; } = "http://optimizer:8080";

    public IReadOnlyDictionary<string, string> All() => new Dictionary<string, string>
    {
        [GatewayProxy.Orchestrator] = Orchestrator,
        [GatewayProxy.Results] = Results,
        [GatewayProxy.MarketData] = MarketData,
        [GatewayProxy.Report] = Report,
        [GatewayProxy.Optimizer] = Optimizer,
    };
}

/// <summary>One issued key: the plaintext <see cref="Key"/> or its lowercase hex <see cref="KeySha256"/>.</summary>
public sealed class ApiKeyOptions
{
    public string? Key { get; set; }
    public string? KeySha256 { get; set; }
    public string User { get; set; } = "";
    public bool Admin { get; set; }
}

/// <summary>
/// JWT bearer validation for an external identity provider: set <see cref="Authority"/> (OIDC discovery) or a
/// symmetric <see cref="SigningKey"/>. Tokens are recognised by their three-part shape; anything else is an API key.
/// </summary>
public sealed class JwtOptions
{
    public string? Authority { get; set; }
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? SigningKey { get; set; }
    public string UserClaim { get; set; } = "sub";
    public string RoleClaim { get; set; } = "role";
    public string AdminRole { get; set; } = "lean-admin";

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) || !string.IsNullOrWhiteSpace(SigningKey);
}

/// <summary>Fixed-window limit applied per API key (or JWT subject; per client IP when unauthenticated).</summary>
public sealed class RateLimitOptions
{
    public int PermitLimit { get; set; } = 120;
    public int WindowSeconds { get; set; } = 60;
    public int QueueLimit { get; set; }
}
