using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.MarketData;

/// <summary>Contract enums (PascalCase names shared with LEAN) to and from LEAN types.</summary>
public static class ContractMapping
{
    public static SecurityType ToLean(this ContractSecurityType value) => Enum.Parse<SecurityType>(value.ToString());
    public static Resolution ToLean(this ContractResolution value) => Enum.Parse<Resolution>(value.ToString());
    public static TickType ToLean(this ContractTickType value) => Enum.Parse<TickType>(value.ToString());
    public static ContractSecurityType ToContract(this SecurityType value) => Enum.Parse<ContractSecurityType>(value.ToString());
    public static ContractTickType ToContract(this TickType value) => Enum.Parse<ContractTickType>(value.ToString());

    public static bool TryParse<T>(string? value, out T result) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out result) && Enum.IsDefined(result) && !int.TryParse(value, out _);

    /// <summary>LEAN's default tick type for the security type when none is given.</summary>
    public static TickType DefaultTickType(SecurityType securityType) =>
        securityType is SecurityType.Forex or SecurityType.Cfd ? TickType.Quote : TickType.Trade;

    public static Symbol CreateSymbol(SecurityType securityType, string market, string ticker) =>
        Symbol.Create(ticker.ToUpperInvariant(), securityType, market.ToLowerInvariant());
}
