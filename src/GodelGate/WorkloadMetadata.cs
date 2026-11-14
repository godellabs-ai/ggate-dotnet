using System.Net;
using System.Text.Json.Nodes;

namespace GodelGate;
internal static class WorkloadMetadata
{
    private static readonly Dictionary<string, string[]> Fields = new() {
        ["cloud_provider"] = ["GGATE_CLOUD_PROVIDER"],
        ["cloud_account"] = ["GGATE_CLOUD_ACCOUNT", "GOOGLE_CLOUD_PROJECT", "GCP_PROJECT", "AWS_ACCOUNT_ID", "AZURE_SUBSCRIPTION_ID"],
        ["cloud_region"] = ["GGATE_CLOUD_REGION", "GOOGLE_CLOUD_REGION", "CLOUD_RUN_REGION", "AWS_REGION", "AWS_DEFAULT_REGION", "AZURE_REGION"],
        ["cloud_zone"] = ["GOOGLE_CLOUD_ZONE"],
        ["cloud_instance_id"] = ["GGATE_CLOUD_INSTANCE_ID"],
        ["cloud_instance_name"] = ["GGATE_CLOUD_INSTANCE_NAME"],
        ["cloud_instance_type"] = ["GGATE_CLOUD_INSTANCE_TYPE"],
        ["cloud_resource_group"] = ["GGATE_CLOUD_RESOURCE_GROUP", "AZURE_RESOURCE_GROUP"],
        ["service_identity"] = ["GGATE_WORKLOAD_SERVICE_IDENTITY", "GGATE_WORKLOAD_SERVICE_ACCOUNT", "GOOGLE_SERVICE_ACCOUNT_EMAIL", "AWS_ROLE_ARN", "AZURE_CLIENT_ID"],
        ["cloud_run_service"] = ["K_SERVICE"],
        ["cloud_run_revision"] = ["K_REVISION"],
        ["kubernetes_namespace"] = ["POD_NAMESPACE", "KUBERNETES_NAMESPACE"],
        ["kubernetes_pod_name"] = ["POD_NAME"],
        ["kubernetes_pod_uid"] = ["POD_UID"],
        ["kubernetes_node_name"] = ["NODE_NAME"],
        ["kubernetes_service_account"] = ["POD_SERVICE_ACCOUNT", "KUBERNETES_SERVICE_ACCOUNT"],
        ["function_name"] = ["AWS_LAMBDA_FUNCTION_NAME", "FUNCTION_NAME", "WEBSITE_SITE_NAME"],
        ["function_version"] = ["AWS_LAMBDA_FUNCTION_VERSION", "K_REVISION"],
        ["workload_instance_id"] = ["AWS_LAMBDA_LOG_STREAM_NAME", "WEBSITE_INSTANCE_ID"],
        ["cloud_task_arn"] = ["ECS_TASK_ARN"]
    };
    internal static Dictionary<string, string> Sanitize(IEnumerable<KeyValuePair<string, string>> values) => values
        .Where(p => Fields.ContainsKey(p.Key) && !string.IsNullOrWhiteSpace(p.Value) && System.Text.Encoding.UTF8.GetByteCount(p.Value) <= 253 && !p.Value.Any(char.IsControl))
        .ToDictionary(p => p.Key, p => p.Value.Trim());
    internal static Dictionary<string, string> EnvironmentIdentity()
    {
        var values = Fields.ToDictionary(p => p.Key, p => p.Value.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "");
        if (values["cloud_provider"] == "") {
            if (Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME") is not null || Environment.GetEnvironmentVariable("AWS_ROLE_ARN") is not null || Environment.GetEnvironmentVariable("ECS_TASK_ARN") is not null) values["cloud_provider"] = "aws";
            else if (Environment.GetEnvironmentVariable("K_SERVICE") is not null || Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") is not null) values["cloud_provider"] = "gcp";
            else if (Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is not null || Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") is not null) values["cloud_provider"] = "azure";
        }
        if (Environment.GetEnvironmentVariable("K_SERVICE") is not null && values["workload_instance_id"] == "") values["workload_instance_id"] = Environment.GetEnvironmentVariable("HOSTNAME") ?? "";
        return Sanitize(values);
    }
    internal static async Task<Dictionary<string, string>> DiscoverAsync(IReadOnlyDictionary<string, string> declared)
    {
        var result = new Dictionary<string, string>();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(1) };
            async Task<Dictionary<string, string>> Safe(Func<Task<Dictionary<string, string>>> probe) { try { return await probe(); } catch { return new(); } }
            var probes = await Task.WhenAll(Safe(() => Gcp(http)), Safe(() => Aws(http)), Safe(() => Azure(http)));
            result = probes.FirstOrDefault(p => p.Count != 0) ?? new();
        }
        foreach (var p in EnvironmentIdentity()) result[p.Key] = p.Value;
        foreach (var p in Sanitize(declared)) result[p.Key] = p.Value;
        return result;
    }
    private static async Task<string> Read(HttpClient http, string path, string? header = null, string? value = null, bool put = false, bool google = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var request = new HttpRequestMessage(put ? HttpMethod.Put : HttpMethod.Get, "http://169.254.169.254" + path);
        if (header is not null) request.Headers.Add(header, value);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (google && (!response.Headers.TryGetValues("Metadata-Flavor", out var flavor) || !flavor.Contains("Google"))) throw new IOException("Not GCP metadata");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[16385]; var count = 0;
        while (count < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token); if (read == 0) break; count += read; }
        if (count == buffer.Length) throw new IOException("Metadata too large");
        return System.Text.Encoding.UTF8.GetString(buffer, 0, count).Trim();
    }
    private static Dictionary<string, string> Map(JsonNode doc, string provider, Dictionary<string, string> fields) => Sanitize(fields
        .Select(p => new KeyValuePair<string, string>(p.Value, doc[p.Key]?.ToString() ?? ""))
        .Append(new("cloud_provider", provider)));
    private static async Task<Dictionary<string, string>> Aws(HttpClient http)
    {
        var token = await Read(http, "/latest/api/token", "X-aws-ec2-metadata-token-ttl-seconds", "60", true);
        var doc = JsonNode.Parse(await Read(http, "/latest/dynamic/instance-identity/document", "X-aws-ec2-metadata-token", token))!;
        var result = Map(doc, "aws", new() { ["accountId"]="cloud_account", ["instanceId"]="cloud_instance_id", ["region"]="cloud_region", ["availabilityZone"]="cloud_zone", ["instanceType"]="cloud_instance_type" });
        try { result["service_identity"] = await Read(http, "/latest/meta-data/iam/security-credentials/", "X-aws-ec2-metadata-token", token); } catch { }
        return Sanitize(result);
    }
    private static async Task<Dictionary<string, string>> Azure(HttpClient http) => Map(JsonNode.Parse(await Read(http,
        "/metadata/instance/compute?api-version=2021-02-01&format=json", "Metadata", "true"))!, "azure", new() {
            ["subscriptionId"]="cloud_account", ["vmId"]="cloud_instance_id", ["location"]="cloud_region", ["zone"]="cloud_zone", ["vmSize"]="cloud_instance_type", ["name"]="cloud_instance_name", ["resourceGroupName"]="cloud_resource_group" });
    private static async Task<Dictionary<string, string>> Gcp(HttpClient http)
    {
        Task<string> Get(string path) => Read(http, "/computeMetadata/v1/" + path, "Metadata-Flavor", "Google", google: true);
        var result = new Dictionary<string, string> { ["cloud_provider"]="gcp", ["cloud_account"]=await Get("project/project-id") };
        var fields = new Dictionary<string, string> { ["instance/id"]="cloud_instance_id", ["instance/name"]="cloud_instance_name", ["instance/zone"]="cloud_zone", ["instance/machine-type"]="cloud_instance_type", ["instance/service-accounts/default/email"]="service_identity" };
        var values = await Task.WhenAll(fields.Select(async pair => { try { return new KeyValuePair<string,string>(pair.Value, await Get(pair.Key)); } catch { return new(pair.Value, ""); } }));
        foreach (var pair in values) result[pair.Key] = pair.Value;
        foreach (var key in new[] { "cloud_zone", "cloud_instance_type" }) result[key] = result[key].Split('/').Last();
        if (result["cloud_zone"].LastIndexOf('-') is var split && split > 0) result["cloud_region"] = result["cloud_zone"][..split];
        return Sanitize(result);
    }
}
