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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantConnect.Util.Json;

namespace QuantConnect.Notifications
{
    /// <summary>
    /// Defines a <see cref="JsonConverter"/> to be used when deserializing to the <see cref="Notification"/> class.
    /// </summary>
    public class NotificationJsonConverter : JsonConverter<Notification>
    {
        /// <summary>
        /// Writes the notification using the default serialization of its runtime type
        /// </summary>
        public override void Write(Utf8JsonWriter writer, Notification value, JsonSerializerOptions options)
        {
            writer.WriteDefault(value, options);
        }

        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        public override Notification Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var jObject = JsonNode.Parse(ref reader) as JsonObject;

            JsonNode token;
            if (jObject.TryGetValue("PhoneNumber", StringComparison.InvariantCultureIgnoreCase, out token))
            {
                var message = jObject.GetValue("Message", StringComparison.InvariantCultureIgnoreCase);

                return new NotificationSms(token.AsText(), message?.AsText());
            }
            else if (jObject.TryGetValue("Subject", StringComparison.InvariantCultureIgnoreCase, out token))
            {
                var data = jObject.GetValue("Data", StringComparison.InvariantCultureIgnoreCase);
                var message = jObject.GetValue("Message", StringComparison.InvariantCultureIgnoreCase);
                var address = jObject.GetValue("Address", StringComparison.InvariantCultureIgnoreCase);
                var headers = jObject.GetValue("Headers", StringComparison.InvariantCultureIgnoreCase);

                return new NotificationEmail(address?.AsText(), token.AsText(), message?.AsText(), data?.AsText(), headers?.Value<Dictionary<string, string>>());
            }
            else if (jObject.TryGetValue("Address", StringComparison.InvariantCultureIgnoreCase, out token))
            {
                var headers = jObject.GetValue("Headers", StringComparison.InvariantCultureIgnoreCase);
                var data = jObject.GetValue("Data", StringComparison.InvariantCultureIgnoreCase);

                return new NotificationWeb(token.AsText(), data?.AsText(), headers?.Value<Dictionary<string, string>>());
            }
            else if (jObject.TryGetValue("Id", StringComparison.InvariantCultureIgnoreCase, out token))
            {
                var message = jObject.GetValue("Message", StringComparison.InvariantCultureIgnoreCase);
                var botToken = jObject.GetValue("Token", StringComparison.InvariantCultureIgnoreCase);
                return new NotificationTelegram(token.AsText(), message?.AsText(), botToken?.AsText());
            }
            else if (jObject.TryGetValue("host", StringComparison.InvariantCultureIgnoreCase, out token))
            {
                // This is an FTP notification
                var hostname = token.AsText();
                var port = jObject.GetValue("port", StringComparison.InvariantCultureIgnoreCase)?.Value<int?>();
                var username = jObject.GetValue("username", StringComparison.InvariantCultureIgnoreCase)?.AsText();
                var filePath = jObject.GetValue("fileDestinationPath", StringComparison.InvariantCultureIgnoreCase)?.AsText();
                var fileContent = jObject.GetValue("fileContent", StringComparison.InvariantCultureIgnoreCase)?.AsText();

                if (jObject.TryGetValue("password", StringComparison.InvariantCultureIgnoreCase, out var password))
                {
                    var secure = jObject.GetValue("secure", StringComparison.InvariantCultureIgnoreCase)?.Value<bool>() ?? true;
                    return NotificationFtp.FromEncodedData(hostname, username, password.AsText(), filePath, fileContent, secure, port);
                }

                var privateKey = jObject.GetValue("privateKey", StringComparison.InvariantCultureIgnoreCase)?.AsText();
                var passphrase = jObject.GetValue("passphrase", StringComparison.InvariantCultureIgnoreCase)?.AsText();
                return NotificationFtp.FromEncodedData(hostname, username, privateKey, passphrase, filePath, fileContent, port);
            }

            throw new NotImplementedException(Messages.NotificationJsonConverter.UnexpectedJsonObject(jObject));
        }

        /// <summary>
        /// Determines whether this instance can convert the specified object type.
        /// </summary>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Notification);
        }
    }
}
