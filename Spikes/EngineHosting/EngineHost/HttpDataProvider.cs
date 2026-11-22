using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Logging;

namespace QuantConnect.Spikes.EngineHosting
{
    /// <summary>
    /// <see cref="IDataProvider"/> that fetches every file from the market-data HTTP service
    /// (GET {market-data-url}/data/{path relative to data-folder}) instead of the local disk.
    /// </summary>
    public class HttpDataProvider : IDataProvider, IDisposable
    {
        private static readonly HttpClient _client = new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable("MARKET_DATA_URL") ?? Config.Get("market-data-url", "http://market-data:8080/")) };
        private readonly string _dataFolder = Path.GetFullPath(Globals.DataFolder);
        private long _requests;
        private long _bytes;

        public event EventHandler<DataProviderNewDataRequestEventArgs> NewDataRequest;

        public Stream Fetch(string key)
        {
            var relative = Path.GetRelativePath(_dataFolder, Path.GetFullPath(key)).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal))
            {
                NewDataRequest?.Invoke(this, new DataProviderNewDataRequestEventArgs(key, false, "outside data folder"));
                return null;
            }

            Interlocked.Increment(ref _requests);
            using var response = _client.GetAsync("data/" + Uri.EscapeDataString(relative).Replace("%2F", "/")).GetAwaiter().GetResult();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                NewDataRequest?.Invoke(this, new DataProviderNewDataRequestEventArgs(key, false, "404"));
                return null;
            }
            response.EnsureSuccessStatusCode();

            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Interlocked.Add(ref _bytes, bytes.Length);
            NewDataRequest?.Invoke(this, new DataProviderNewDataRequestEventArgs(key, true, string.Empty));
            return new MemoryStream(bytes, writable: false);
        }

        public void Dispose()
        {
            Log.Trace($"HttpDataProvider.Dispose(): {_requests} requests, {_bytes} bytes");
        }
    }
}
