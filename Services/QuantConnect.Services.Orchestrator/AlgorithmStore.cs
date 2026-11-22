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

namespace QuantConnect.Services.Orchestrator;

/// <summary>The <c>S3</c> configuration section (MinIO in compose).</summary>
public sealed class S3Options
{
    public const string Section = "S3";
    public string ServiceUrl { get; set; } = "http://localhost:9000";
    public bool ForcePathStyle { get; set; } = true;
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string AlgorithmsBucket { get; set; } = Buckets.Algorithms;
}

/// <summary>Algorithm sources/packages in <c>lean-algorithms</c>, content addressed as <c>sha256/&lt;hash&gt;/&lt;file&gt;</c> (ADR 0003).</summary>
public interface IAlgorithmStore
{
    string Bucket { get; }

    Task<ObjectRef> PutAsync(string fileName, byte[] content, string contentType, CancellationToken ct);

    Task<bool> ExistsAsync(ObjectRef obj, CancellationToken ct);

    public static string KeyFor(string sha256, string fileName) => $"sha256/{sha256}/{fileName}";

    /// <summary>A safe object file name: the last path segment, restricted to <c>[A-Za-z0-9._-]</c>.</summary>
    public static string? SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        if (name.Length is 0 or > 200 || name is "." or "..") return null;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return null;
        }
        return name;
    }

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

public sealed class S3AlgorithmStore : IAlgorithmStore, IDisposable
{
    private readonly AmazonS3Client _client;

    public S3AlgorithmStore(S3Options options)
    {
        Bucket = options.AlgorithmsBucket;
        _client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public string Bucket { get; }

    public async Task<ObjectRef> PutAsync(string fileName, byte[] content, string contentType, CancellationToken ct)
    {
        var sha = IAlgorithmStore.Sha256(content);
        var obj = new ObjectRef { Bucket = Bucket, Key = IAlgorithmStore.KeyFor(sha, fileName), Size = content.LongLength, Sha256 = sha, ContentType = contentType };
        if (await ExistsAsync(obj, ct)) return obj;
        using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket, Key = obj.Key, InputStream = stream, ContentType = contentType, AutoCloseStream = false,
        }, ct);
        return obj;
    }

    public async Task<bool> ExistsAsync(ObjectRef obj, CancellationToken ct)
    {
        try
        {
            var head = await _client.GetObjectMetadataAsync(obj.Bucket, obj.Key, ct);
            return head.ContentLength == obj.Size;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
