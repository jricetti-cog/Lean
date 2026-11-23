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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace QuantConnect.Services.Orchestrator;

/// <summary>
/// Kubernetes API (in-cluster service account): one single-replica Deployment per live deployment, translated from the
/// Docker-shaped spec <see cref="LiveDeployer.ContainerSpec"/> builds (image, env, labels, memory/CPU limits); lean-data is the
/// <see cref="LiveDeploymentOptions.DataClaim"/> PVC mounted read-only. The Deployment restarts a crashed worker; a finished
/// deployment is deleted by <see cref="LiveDeployer.SweepAsync"/> once its job is terminal. Needs a namespaced Role with
/// get/list/create/patch/delete on <c>apps/deployments</c>.
/// </summary>
public sealed class KubernetesLiveContainerRuntime : ILiveContainerRuntime, IDisposable
{
    public const string ServiceAccountDirectory = "/var/run/secrets/kubernetes.io/serviceaccount";

    private readonly HttpClient _http;
    private readonly LiveDeploymentOptions _options;
    private readonly Func<string?> _token;
    private readonly string _namespace;

    public KubernetesLiveContainerRuntime(IOptions<LiveDeploymentOptions> options)
        : this(options.Value, CreateInClusterHandler(), () => ReadServiceAccountFile("token"),
            options.Value.KubernetesNamespace ?? ReadServiceAccountFile("namespace") ?? "default")
    {
    }

    public KubernetesLiveContainerRuntime(LiveDeploymentOptions options, HttpMessageHandler handler, Func<string?> token, string ns)
    {
        _options = options;
        _token = token;
        _namespace = ns;
        _http = new HttpClient(handler) { BaseAddress = new Uri(options.KubernetesApiUri.TrimEnd('/') + "/") };
    }

    public bool RestartsFinishedWorkers => true;

    private string Deployments => $"apis/apps/v1/namespaces/{_namespace}/deployments";

    public async Task<LiveContainer?> InspectAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Deployments}/{Uri.EscapeDataString(name)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, "get deployment", ct);
        return ToContainer(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject());
    }

    public async Task<string> CreateAsync(string name, JsonObject spec, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, Deployments, DeploymentManifest(name, spec), ct);
        await EnsureAsync(response, "create deployment", ct);
        return name;
    }

    /// <summary>Scales the deployment back to one replica (a no-op when it already runs).</summary>
    public async Task StartAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Patch, $"{Deployments}/{Uri.EscapeDataString(id)}",
            new JsonObject { ["spec"] = new JsonObject { ["replicas"] = 1 } }, ct, "application/merge-patch+json");
        await EnsureAsync(response, "scale deployment", ct);
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"{Deployments}/{Uri.EscapeDataString(id)}",
            new JsonObject { ["kind"] = "DeleteOptions", ["apiVersion"] = "v1", ["propagationPolicy"] = "Background" }, ct);
        if (response.StatusCode != HttpStatusCode.NotFound) await EnsureAsync(response, "delete deployment", ct);
    }

    public async Task<IReadOnlyList<LiveContainer>> ListAsync(string label, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Deployments}?labelSelector={Uri.EscapeDataString(label)}", null, ct);
        await EnsureAsync(response, "list deployments", ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return (body["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(ToContainer).ToList();
    }

    /// <summary>The <c>apps/v1</c> Deployment for one live-worker, from the Docker <c>containers/create</c> body.</summary>
    public JsonObject DeploymentManifest(string name, JsonObject spec)
    {
        var labels = new JsonObject();
        foreach (var (key, value) in (spec["Labels"] as JsonObject ?? new JsonObject()).Select(p => (p.Key, (string?)p.Value ?? ""))) labels[key] = value;
        var podLabels = labels.DeepClone().AsObject();
        podLabels["lean.deployment"] = name;
        foreach (var pair in (_options.PodLabels ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = pair.IndexOf('=');
            if (i > 0) podLabels[pair[..i]] = pair[(i + 1)..];
        }

        var env = new JsonArray();
        foreach (var entry in (spec["Env"] as JsonArray ?? new JsonArray()).Select(e => (string?)e).OfType<string>())
        {
            var i = entry.IndexOf('=');
            if (i > 0) env.Add(new JsonObject { ["name"] = entry[..i], ["value"] = entry[(i + 1)..] });
        }

        var host = spec["HostConfig"] as JsonObject ?? new JsonObject();
        var limits = new JsonObject();
        if ((long?)host["Memory"] is long memory and > 0) limits["memory"] = memory.ToString(CultureInfo.InvariantCulture);
        if ((long?)host["NanoCpus"] is long nano and > 0) limits["cpu"] = $"{nano / 1_000_000}m";

        var container = new JsonObject
        {
            ["name"] = "live-worker",
            ["image"] = (string?)spec["Image"] ?? _options.Image,
            ["imagePullPolicy"] = _options.ImagePullPolicy,
            ["env"] = env,
            ["securityContext"] = new JsonObject
            {
                ["runAsNonRoot"] = true, ["runAsUser"] = _options.RunAsUser, ["runAsGroup"] = _options.RunAsUser,
                ["allowPrivilegeEscalation"] = false,
            },
        };
        if (limits.Count > 0) container["resources"] = new JsonObject { ["limits"] = limits, ["requests"] = limits.DeepClone() };
        var pod = new JsonObject
        {
            ["restartPolicy"] = "Always",
            ["terminationGracePeriodSeconds"] = 60,
            ["containers"] = new JsonArray(container),
        };
        if (_options.ServiceAccount is { Length: > 0 } account) pod["serviceAccountName"] = account;
        if (_options.DataClaim is { Length: > 0 } claim)
        {
            container["volumeMounts"] = new JsonArray(new JsonObject { ["name"] = "lean-data", ["mountPath"] = _options.DataMountPath, ["readOnly"] = true });
            pod["volumes"] = new JsonArray(new JsonObject
            {
                ["name"] = "lean-data", ["persistentVolumeClaim"] = new JsonObject { ["claimName"] = claim, ["readOnly"] = true },
            });
        }

        return new JsonObject
        {
            ["apiVersion"] = "apps/v1",
            ["kind"] = "Deployment",
            ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = _namespace, ["labels"] = labels },
            ["spec"] = new JsonObject
            {
                ["replicas"] = 1,
                // One engine per deployment: never run the old and the new pod side by side.
                ["strategy"] = new JsonObject { ["type"] = "Recreate" },
                ["selector"] = new JsonObject { ["matchLabels"] = new JsonObject { ["lean.deployment"] = name } },
                ["template"] = new JsonObject { ["metadata"] = new JsonObject { ["labels"] = podLabels }, ["spec"] = pod },
            },
        };
    }

    private static LiveContainer ToContainer(JsonObject deployment)
    {
        var labels = deployment["metadata"]?["labels"] is JsonObject o
            ? o.ToDictionary(p => p.Key, p => (string?)p.Value ?? "")
            : new Dictionary<string, string>();
        var name = (string?)deployment["metadata"]?["name"] ?? "";
        var running = ((int?)deployment["spec"]?["replicas"] ?? 0) > 0 && ((int?)deployment["status"]?["readyReplicas"] ?? 0) > 0;
        // Exit codes stay with the pods; -1 keeps the exit-code sweep rule off (finished deployments go by job state).
        return new LiveContainer(name, name, running, -1, labels);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (_token() is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        return await _http.SendAsync(request, ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"kubernetes {what}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
        }
    }

    /// <summary>The token is read per request: projected service account tokens rotate.</summary>
    private static string? ReadServiceAccountFile(string file)
    {
        var path = Path.Combine(ServiceAccountDirectory, file);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static HttpMessageHandler CreateInClusterHandler()
    {
        var caPath = Path.Combine(ServiceAccountDirectory, "ca.crt");
        var handler = new SocketsHttpHandler();
        if (File.Exists(caPath))
        {
            var trust = new X509Certificate2Collection();
            trust.ImportFromPemFile(caPath);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(trust);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate));
            };
        }
        return handler;
    }

    public void Dispose() => _http.Dispose();
}
