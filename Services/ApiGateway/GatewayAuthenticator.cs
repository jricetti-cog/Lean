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
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace QuantConnect.Services.ApiGateway
{
    /// <summary>
    /// Validates the LEAN API authentication scheme (see <c>Api/ApiConnection.cs</c>):
    /// <c>Authorization: Basic base64(userId:sha256(token:timestamp))</c> plus a <c>Timestamp</c> header.
    /// WebSocket clients that can not set headers pass <c>userId</c>, <c>timestamp</c> and <c>hash</c> as query parameters.
    /// </summary>
    public class GatewayAuthenticator
    {
        private readonly IReadOnlyDictionary<string, string> _tokens;
        private readonly TimeSpan _tolerance;

        /// <summary>
        /// Creates a new authenticator
        /// </summary>
        /// <param name="tokens">Access token by user id</param>
        /// <param name="tolerance">Allowed clock skew of the timestamp</param>
        public GatewayAuthenticator(IReadOnlyDictionary<string, string> tokens, TimeSpan tolerance)
        {
            _tokens = tokens;
            _tolerance = tolerance;
        }

        /// <summary>
        /// Returns the authenticated user id or null
        /// </summary>
        public string Authenticate(HttpRequest request)
        {
            string userId = null, hash = null, timestamp = null;
            var authorization = request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
                    var separator = decoded.IndexOf(':', StringComparison.Ordinal);
                    if (separator > 0)
                    {
                        userId = decoded[..separator];
                        hash = decoded[(separator + 1)..];
                    }
                }
                catch (FormatException)
                {
                    return null;
                }
                timestamp = request.Headers["Timestamp"].ToString();
            }
            else if (request.Query.ContainsKey("hash"))
            {
                userId = request.Query["userId"];
                hash = request.Query["hash"];
                timestamp = request.Query["timestamp"];
            }
            return Validate(userId, hash, timestamp) ? userId : null;
        }

        /// <summary>
        /// Validates credentials
        /// </summary>
        public bool Validate(string userId, string hash, string timestamp)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(hash)
                || !int.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTime)
                || !_tokens.TryGetValue(userId, out var token))
            {
                return false;
            }
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixTime);
            if (age.Duration() > _tolerance)
            {
                return false;
            }
            var expected = Api.Api.CreateSecureHash(unixTime, token);
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(hash.ToLowerInvariant()));
        }
    }
}
