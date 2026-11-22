using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Util;

namespace QuantConnect.Services.MarketData;

/// <summary>
/// LEAN's auxiliary-data providers, composed and initialized once like the engine does at startup: <see cref="Symbol.Create"/>
/// and <c>FakeDataQueue</c> resolve the map file provider from <see cref="Composer"/>, so it must exist (initialized) before
/// the first symbol is created. Map files, factor files, market hours and symbol properties are read from <see cref="Globals.DataFolder"/>.
/// </summary>
public static class LeanComposition
{
    private static readonly object Lock = new();
    private static (IDataProvider Data, IMapFileProvider Map, IFactorFileProvider Factor)? _providers;

    public static (IDataProvider Data, IMapFileProvider Map, IFactorFileProvider Factor) Providers
    {
        get
        {
            lock (Lock)
            {
                if (_providers == null)
                {
                    var data = Composer.Instance.GetExportedValueByTypeName<IDataProvider>(Config.Get("data-provider", "DefaultDataProvider"));
                    var map = Composer.Instance.GetExportedValueByTypeName<IMapFileProvider>(Config.Get("map-file-provider", "LocalDiskMapFileProvider"));
                    map.Initialize(data);
                    var factor = Composer.Instance.GetExportedValueByTypeName<IFactorFileProvider>(Config.Get("factor-file-provider", "LocalDiskFactorFileProvider"));
                    factor.Initialize(map, data);
                    _providers = (data, map, factor);
                }
                return _providers.Value;
            }
        }
    }
}
