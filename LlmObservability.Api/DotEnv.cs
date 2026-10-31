namespace LlmObservability.Api;

/// <summary>
/// Loads <c>KEY=VALUE</c> lines from a local, git-ignored <c>.env</c> file into configuration, so keys stay
/// out of appsettings.json. Keys use the environment-variable form (<c>AzureAI__Foundry__ApiKey</c> →
/// <c>AzureAI:Foundry:ApiKey</c>). Empty values are skipped (appsettings.json applies), and real
/// environment variables still override the file.
/// </summary>
internal static class DotEnv
{
    public static void AddTo(ConfigurationManager configuration, string path)
    {
        if (!File.Exists(path)) return;

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line["export ".Length..].TrimStart();

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key   = line[..separator].Trim().Replace("__", ":");
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0], value[^1]) is ('"', '"') or ('\'', '\''))
                value = value[1..^1];

            if (value.Length > 0) values[key] = value;
        }

        configuration.AddInMemoryCollection(values);
        configuration.AddEnvironmentVariables();   // real environment variables keep the last word
    }
}
