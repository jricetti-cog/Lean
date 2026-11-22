using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Results;

/// <summary>MinIO (S3) access for result objects: the final result JSON and other artifacts under <c>lean-results/</c>.</summary>
public interface IResultObjectStore
{
    Task<byte[]> GetAsync(string bucket, string key, CancellationToken cancellationToken = default);
    Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken = default);
    /// <summary>Objects under <paramref name="prefix"/>; <paramref name="known"/> references (by key, same size) are reused instead of re-hashing.</summary>
    Task<List<ObjectRef>> ListAsync(string bucket, string prefix, IReadOnlyDictionary<string, ObjectRef>? known = null, CancellationToken cancellationToken = default);
}

public static class ResultObjectStoreExtensions
{
    /// <summary>Downloads <paramref name="reference"/> and checks its size and SHA-256.</summary>
    public static async Task<byte[]> GetVerifiedAsync(this IResultObjectStore store, ObjectRef reference, CancellationToken cancellationToken = default)
    {
        var content = await store.GetAsync(reference.Bucket, reference.Key, cancellationToken).ConfigureAwait(false);
        if (content.LongLength != reference.Size || !string.Equals(Sha256(content), reference.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"s3://{reference.Bucket}/{reference.Key}: content does not match its ObjectRef (size {content.LongLength} vs {reference.Size})");
        }
        return content;
    }

    /// <summary>Uploads <paramref name="content"/> and returns its <see cref="ObjectRef"/>.</summary>
    public static async Task<ObjectRef> PutRefAsync(this IResultObjectStore store, string bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        await store.PutAsync(bucket, key, content, contentType, cancellationToken).ConfigureAwait(false);
        return new ObjectRef { Bucket = bucket, Key = key, Size = content.LongLength, Sha256 = Sha256(content), ContentType = contentType };
    }

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

/// <summary>Path-style S3 client for MinIO (<c>S3:ServiceUrl</c>, <c>S3:AccessKey</c>, <c>S3:SecretKey</c>).</summary>
public sealed class S3ResultObjectStore : IResultObjectStore, IDisposable
{
    private readonly AmazonS3Client _client;

    public S3ResultObjectStore(string serviceUrl, string accessKey, string secretKey, string region = "us-east-1")
    {
        _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public static S3ResultObjectStore FromConfiguration(IConfiguration configuration) => new(
        configuration["S3:ServiceUrl"] ?? throw new InvalidOperationException("S3:ServiceUrl is required"),
        configuration["S3:AccessKey"] ?? throw new InvalidOperationException("S3:AccessKey is required"),
        configuration["S3:SecretKey"] ?? throw new InvalidOperationException("S3:SecretKey is required"),
        configuration["S3:Region"] is { Length: > 0 } region ? region : "us-east-1");

    public async Task<byte[]> GetAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ObjectRef>> ListAsync(string bucket, string prefix, IReadOnlyDictionary<string, ObjectRef>? known = null, CancellationToken cancellationToken = default)
    {
        var refs = new List<ObjectRef>();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                if (known != null && known.TryGetValue(item.Key, out var reference) && reference.Size == item.Size)
                {
                    refs.Add(reference);
                    continue;
                }
                // S3 has no SHA-256 for arbitrary objects: the ETag (MD5 for single-part uploads) is not one, so
                // fetch the object to report a verifiable reference. Result prefixes hold a handful of objects.
                var content = await GetAsync(bucket, item.Key, cancellationToken).ConfigureAwait(false);
                refs.Add(new ObjectRef
                {
                    Bucket = bucket,
                    Key = item.Key,
                    Size = content.LongLength,
                    Sha256 = ResultObjectStoreExtensions.Sha256(content),
                    ContentType = ContentTypeFor(item.Key),
                });
            }
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
        return refs;
    }

    internal static string ContentTypeFor(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".txt" or ".log" => "text/plain",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    public void Dispose() => _client.Dispose();
}

/// <summary>Store rooted at a local folder (<c>&lt;root&gt;/&lt;bucket&gt;/&lt;key&gt;</c>), for tests and single-host runs.</summary>
public sealed class FileSystemResultObjectStore(string root) : IResultObjectStore
{
    private string PathOf(string bucket, string key) => Path.Combine(root, bucket, key.Replace('/', Path.DirectorySeparatorChar));

    public async Task<byte[]> GetAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var path = PathOf(bucket, key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"s3://{bucket}/{key} not found", path);
        }
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        var path = PathOf(bucket, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ObjectRef>> ListAsync(string bucket, string prefix, IReadOnlyDictionary<string, ObjectRef>? known = null, CancellationToken cancellationToken = default)
    {
        var bucketRoot = Path.Combine(root, bucket);
        var refs = new List<ObjectRef>();
        if (!Directory.Exists(bucketRoot))
        {
            return refs;
        }
        foreach (var file in Directory.EnumerateFiles(bucketRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var key = Path.GetRelativePath(bucketRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            if (known != null && known.TryGetValue(key, out var reference) && reference.Size == new FileInfo(file).Length)
            {
                refs.Add(reference);
                continue;
            }
            var content = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            refs.Add(new ObjectRef
            {
                Bucket = bucket,
                Key = key,
                Size = content.LongLength,
                Sha256 = ResultObjectStoreExtensions.Sha256(content),
                ContentType = S3ResultObjectStore.ContentTypeFor(key),
            });
        }
        return refs;
    }
}
