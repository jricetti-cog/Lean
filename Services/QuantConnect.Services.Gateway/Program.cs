namespace QuantConnect.Services.Gateway;

public static class Program
{
    public static void Main(string[] args) => GatewayHost.Build(args).Run();
}
