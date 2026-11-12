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
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace QuantConnect.Services
{
    /// <summary>
    /// The unit of transfer on the message bus. Metadata and the JSON <see cref="Body"/> travel in one frame,
    /// the optional binary <see cref="Payload"/> (e.g. a raw data file) travels in a separate frame so it is never base64 encoded.
    /// </summary>
    public class MessageEnvelope
    {
        /// <summary>Header set on a reply when the handler failed</summary>
        public const string ErrorHeader = "error";

        /// <summary>Unique message id</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The topic the message is published on</summary>
        public string Topic { get; set; }

        /// <summary>The contract version of the body, see <see cref="ContractVersions"/></summary>
        public string ContractVersion { get; set; }

        /// <summary>The topic the reply should be published on, set for requests</summary>
        public string ReplyTo { get; set; }

        /// <summary>For replies, the <see cref="Id"/> of the request</summary>
        public string CorrelationId { get; set; }

        /// <summary>Id of the sending bus client</summary>
        public string SenderId { get; set; }

        /// <summary>Creation time</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Free form headers</summary>
        public Dictionary<string, string> Headers { get; set; } = new();

        /// <summary>JSON body</summary>
        public string Body { get; set; }

        /// <summary>Optional binary payload</summary>
        [JsonIgnore]
        public byte[] Payload { get; set; }

        /// <summary>The error message of a failed reply, null otherwise</summary>
        [JsonIgnore]
        public string Error => Headers != null && Headers.TryGetValue(ErrorHeader, out var error) ? error : null;

        /// <summary>
        /// Creates a new envelope with a serialized body
        /// </summary>
        public static MessageEnvelope Create<T>(string topic, T body, string contractVersion, byte[] payload = null)
        {
            return new MessageEnvelope
            {
                Topic = topic,
                ContractVersion = contractVersion,
                Body = body == null ? null : ServiceJson.Serialize(body),
                Payload = payload
            };
        }

        /// <summary>
        /// Creates the reply of this request
        /// </summary>
        public MessageEnvelope CreateReply<T>(T body, byte[] payload = null)
        {
            var reply = Create(ReplyTo, body, ContractVersion, payload);
            reply.CorrelationId = Id;
            return reply;
        }

        /// <summary>
        /// Creates an error reply of this request
        /// </summary>
        public MessageEnvelope CreateErrorReply(string error)
        {
            var reply = new MessageEnvelope { Topic = ReplyTo, ContractVersion = ContractVersion, CorrelationId = Id };
            reply.Headers[ErrorHeader] = error;
            return reply;
        }

        /// <summary>
        /// Deserializes the body
        /// </summary>
        public T GetBody<T>()
        {
            return string.IsNullOrEmpty(Body) ? default : ServiceJson.Deserialize<T>(Body);
        }

        /// <summary>
        /// Serializes the metadata frame (everything but the payload)
        /// </summary>
        public byte[] SerializeMetadata()
        {
            return Encoding.UTF8.GetBytes(ServiceJson.Serialize(this));
        }

        /// <summary>
        /// Rebuilds an envelope from its metadata and payload frames
        /// </summary>
        public static MessageEnvelope Deserialize(byte[] metadata, byte[] payload)
        {
            var envelope = ServiceJson.Deserialize<MessageEnvelope>(Encoding.UTF8.GetString(metadata));
            envelope.Payload = payload == null || payload.Length == 0 ? null : payload;
            envelope.Headers ??= new Dictionary<string, string>();
            return envelope;
        }

        /// <summary>
        /// Returns a string that represents the current object
        /// </summary>
        public override string ToString()
        {
            return $"{Topic} id:{Id} corr:{CorrelationId} payload:{Payload?.Length ?? 0}b";
        }
    }

    /// <summary>
    /// The JSON settings shared by every service contract. Explicit settings are used so that a component
    /// changing <see cref="JsonConvert.DefaultSettings"/> (e.g. QuantConnect.Api) does not change the wire format.
    /// </summary>
    public static class ServiceJson
    {
        /// <summary>
        /// The contract serializer settings: camelCase properties, string enums, ISO dates in UTC
        /// </summary>
        public static readonly JsonSerializerSettings Settings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            TypeNameHandling = TypeNameHandling.None
        };

        /// <summary>
        /// Serializes a contract
        /// </summary>
        public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value, Settings);

        /// <summary>
        /// Deserializes a contract
        /// </summary>
        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
    }
}
