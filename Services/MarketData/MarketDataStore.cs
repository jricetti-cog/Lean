/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.IO;
using System.Linq;
using QuantConnect.Services;

namespace QuantConnect.Services.MarketData
{
    /// <summary>
    /// File storage for LEAN formatted data. Keys are '/' separated paths relative to the root
    /// (for example <c>equity/usa/minute/spy/20131007_trade.zip</c>) and can not escape it.
    /// </summary>
    public class MarketDataStore
    {
        /// <summary>
        /// Absolute root folder
        /// </summary>
        public string Root { get; }

        /// <summary>
        /// Creates a store rooted at the given folder
        /// </summary>
        public MarketDataStore(string root)
        {
            Root = Path.GetFullPath(root);
            if (!Root.EndsWith(Path.DirectorySeparatorChar))
            {
                Root += Path.DirectorySeparatorChar;
            }
        }

        /// <summary>
        /// Resolves a key into an absolute path inside the root
        /// </summary>
        public string Resolve(string key)
        {
            if (key == null || Path.IsPathRooted(key))
            {
                throw new ArgumentException($"Invalid data key '{key}'", nameof(key));
            }
            var path = Path.GetFullPath(Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Root, StringComparison.Ordinal) && path + Path.DirectorySeparatorChar != Root)
            {
                throw new ArgumentException($"Data key '{key}' is outside of the data store", nameof(key));
            }
            return path;
        }

        /// <summary>
        /// Reads a file, null if it does not exist
        /// </summary>
        public byte[] Read(string key)
        {
            var path = Resolve(key);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        /// <summary>
        /// Lists files whose key starts with the prefix
        /// </summary>
        public DataListResponse List(string prefix, int maxResults, string contains = null)
        {
            prefix ??= string.Empty;
            var response = new DataListResponse();
            var lastSlash = prefix.LastIndexOf('/');
            var folder = Resolve(lastSlash < 0 ? string.Empty : prefix[..lastSlash]);
            if (!Directory.Exists(folder))
            {
                return response;
            }
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var key = path[Root.Length..].Replace(Path.DirectorySeparatorChar, '/');
                if (!key.StartsWith(prefix, StringComparison.Ordinal) || key.EndsWith(".tmp", StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(contains) && !key.Contains(contains, StringComparison.Ordinal)))
                {
                    continue;
                }
                if (response.Files.Count >= maxResults)
                {
                    response.Truncated = true;
                    break;
                }
                var info = new FileInfo(path);
                response.Files.Add(new DataFileInfo { Key = key, Size = info.Length, LastModifiedUtc = info.LastWriteTimeUtc });
            }
            return response;
        }

        /// <summary>
        /// Writes a file atomically
        /// </summary>
        /// <returns>False if the file exists and overwrite is disabled</returns>
        public bool Store(string key, byte[] content, bool overwrite)
        {
            var path = Resolve(key);
            if (!overwrite && File.Exists(path))
            {
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite);
            return true;
        }
    }
}
