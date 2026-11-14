namespace GodelGate;

internal static class CredentialSource
{
    internal static string? Resolve(GateOptions options)
    {
        if (options.ApiKeyProvider is not null) return options.ApiKeyProvider()?.Trim();
        if (!string.IsNullOrWhiteSpace(options.ApiKeyEnv)) return Environment.GetEnvironmentVariable(options.ApiKeyEnv)?.Trim();
        if (!string.IsNullOrWhiteSpace(options.ApiKeyFile)) {
            try { return File.ReadAllText(options.ApiKeyFile).Trim(); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
        }
        return options.ApiKey ?? ConfigScalar("api_key") ?? Environment.GetEnvironmentVariable("GGATE_API_KEY");
    }
    internal static string? ConfigScalar(string name)
    {
        var home = Environment.GetEnvironmentVariable("GGATE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ggate");
        var file = Environment.GetEnvironmentVariable("GGATE_CONFIG") ?? Path.Combine(home, "config.yaml");
        try {
            foreach (var line in File.ReadLines(file)) {
                var colon = line.IndexOf(':');
                if (colon < 0 || line[..colon].Trim() != name || char.IsWhiteSpace(line[0])) continue;
                var value = line[(colon + 1)..].Trim();
                if (value.StartsWith('"')) {
                    var reader = new System.Text.Json.Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(value));
                    if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.String) return null;
                    var trailing = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(value).AsSpan((int)reader.BytesConsumed)).Trim();
                    return trailing.Length == 0 || trailing.StartsWith('#') ? reader.GetString() : null;
                }
                if (value.StartsWith("'")) {
                    var match = System.Text.RegularExpressions.Regex.Match(value, @"^'((?:[^']|'')*)'\s*(?:#.*)?$");
                    return match.Success ? match.Groups[1].Value.Replace("''", "'") : null;
                }
                value = System.Text.RegularExpressions.Regex.Replace(value, @"\s+#.*$", "").Trim();
                return value is "" or "null" or "Null" or "NULL" or "~" ? null : value;
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (System.Text.Json.JsonException) { }
        return null;
    }
}
