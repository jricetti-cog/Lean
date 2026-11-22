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
*/

using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.BacktestWorker;

/// <summary><c>S3__*</c>: MinIO endpoint and the service credentials (shared with the other services).</summary>
public sealed class S3Options
{
    public const string Section = "S3";
    public string ServiceUrl { get; set; } = "http://localhost:9000";
    public bool ForcePathStyle { get; set; } = true;
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
}

/// <summary>The results bucket as seen by the worker: confirms what the engine uploaded and stores the engine log.</summary>
public interface IResultStore
{
    /// <summary>Downloads <paramref name="key"/>; null when it does not exist.</summary>
    Task<byte[]?> TryGetAsync(string bucket, string key, CancellationToken cancellationToken);
    Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken);
}

public static class ResultStoreExtensions
{
    public static ObjectRef RefOf(string bucket, string key, byte[] content, string contentType) => new()
    {
        Bucket = bucket,
        Key = key,
        Size = content.LongLength,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
        ContentType = contentType,
    };

    public static async Task<ObjectRef?> TryRefAsync(this IResultStore store, string bucket, string key, string contentType, CancellationToken cancellationToken) =>
        await store.TryGetAsync(bucket, key, cancellationToken).ConfigureAwait(false) is { } content ? RefOf(bucket, key, content, contentType) : null;

    public static async Task<ObjectRef> PutRefAsync(this IResultStore store, string bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        await store.PutAsync(bucket, key, content, contentType, cancellationToken).ConfigureAwait(false);
        return RefOf(bucket, key, content, contentType);
    }
}

public sealed class S3ResultStore : IResultStore, IDisposable
{
    private readonly AmazonS3Client _client;

    public S3ResultStore(S3Options options)
    {
        _client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
        });
    }

    public async Task<byte[]?> TryGetAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = stream, ContentType = contentType }, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();
}
