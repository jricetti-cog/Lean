using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.EngineHandlers;

/// <summary>Object storage for algorithm uploads and result objects (MinIO in the stack, ADR 0003).</summary>
public interface IArtifactStore
{
    Task PutAsync(string bucket, string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default);
    Task DownloadAsync(string bucket, string key, Stream destination, CancellationToken cancellationToken = default);
}

public static class ArtifactStore
{
    /// <summary>The store named by <c>bus-artifact-store</c>: <c>s3</c> (default, MinIO) or <c>filesystem</c>.</summary>
    public static IArtifactStore FromConfig() =>
        (BusSettings.Get(BusSettings.ArtifactStore) ?? "s3").ToLowerInvariant() switch
        {
            "s3" or "minio" => new S3ArtifactStore(
                new Uri(BusSettings.Require(BusSettings.S3Endpoint)),
                BusSettings.Require(BusSettings.S3AccessKey),
                BusSettings.Require(BusSettings.S3SecretKey),
                BusSettings.Get(BusSettings.S3Region) ?? "us-east-1"),
            "filesystem" => new FileSystemArtifactStore(BusSettings.Require(BusSettings.ArtifactRoot)),
            var other => throw new InvalidOperationException($"Unknown {BusSettings.ArtifactStore} '{other}'"),
        };

    /// <summary>Uploads <paramref name="content"/> and returns its <see cref="ObjectRef"/> (size + SHA-256).</summary>
    public static async Task<ObjectRef> PutObjectAsync(this IArtifactStore store, string bucket, string key, ReadOnlyMemory<byte> content,
        string contentType = "application/json", CancellationToken cancellationToken = default)
    {
        await store.PutAsync(bucket, key, content, contentType, cancellationToken).ConfigureAwait(false);
        return new ObjectRef
        {
            Bucket = bucket,
            Key = key,
            Size = content.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content.Span)),
            ContentType = contentType,
        };
    }

    /// <summary>Downloads <paramref name="obj"/> to <paramref name="path"/>, failing if its size or SHA-256 differ from the reference.</summary>
    public static async Task DownloadVerifiedAsync(this IArtifactStore store, ObjectRef obj, string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var buffer = new MemoryStream();
        await store.DownloadAsync(obj.Bucket, obj.Key, buffer, cancellationToken).ConfigureAwait(false);
        var sha = Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        if (buffer.Length != obj.Size || !string.Equals(sha, obj.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{obj.Bucket}/{obj.Key}: expected {obj.Size} bytes sha256 {obj.Sha256}, got {buffer.Length} bytes sha256 {sha}");
        }
        await File.WriteAllBytesAsync(path, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>S3 API store; path-style addressing for MinIO.</summary>
public sealed class S3ArtifactStore : IArtifactStore, IDisposable
{
    private readonly AmazonS3Client _client;

    public S3ArtifactStore(Uri endpoint, string accessKey, string secretKey, string region = "us-east-1")
    {
        _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = endpoint.ToString(),
            ForcePathStyle = true,
            AuthenticationRegion = region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public async Task PutAsync(string bucket, string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string bucket, string key, Stream destination, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        await response.ResponseStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Store rooted at a local folder (<c>&lt;root&gt;/&lt;bucket&gt;/&lt;key&gt;</c>); for tests and single-host runs.</summary>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    private readonly string _root;

    public FileSystemArtifactStore(string root) => _root = Path.GetFullPath(root);

    public string PathOf(string bucket, string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, bucket, key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{bucket}/{key}' escapes the store root");
        }
        return path;
    }

    public async Task PutAsync(string bucket, string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        var path = PathOf(bucket, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string bucket, string key, Stream destination, CancellationToken cancellationToken = default)
    {
        await using var source = File.OpenRead(PathOf(bucket, key));
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }
}
