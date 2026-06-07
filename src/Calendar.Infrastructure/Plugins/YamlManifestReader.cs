using Calendar.Plugin.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Calendar.Infrastructure.Plugins;

/// <summary>The outcome of parsing a <c>plugin.yaml</c>: the SDK manifest plus on-disk file hints.</summary>
public sealed record ManifestParseResult(
    PluginManifest Manifest,
    string? AssemblyFile,
    string? OpenApiFile,
    IReadOnlyDictionary<string, ConnectorOperationSpec> Operations);

/// <summary>
/// One declarative <c>operations.&lt;name&gt;</c> block (PLUGIN-HOST.md §5.2/§5.3). <see cref="Call"/> is either
/// an <c>operationId</c> or a <c>"METHOD path"</c> pair; the query/path/header maps bind capability arguments
/// into the request (values are <c>{q...}</c> placeholders); <see cref="Map"/> is the JSONata expression that
/// shapes the response JSON into the SDK return DTO.
/// </summary>
public sealed record ConnectorOperationSpec(
    string Call,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Path,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    string? Map);

/// <summary>
/// Reads <c>plugin.yaml</c> into a <see cref="PluginManifest"/> (PLUGIN-HOST.md §3.2). Tolerant of unknown
/// keys (forward-compat) but fails closed on missing required fields or an out-of-range enum, which the
/// validator reports as <c>bad-manifest</c>.
/// </summary>
public sealed class YamlManifestReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Parse YAML text. Throws <see cref="ManifestFormatException"/> on a structurally invalid manifest.</summary>
    public ManifestParseResult Parse(string yaml)
    {
        YamlManifest dto;
        try
        {
            dto = Deserializer.Deserialize<YamlManifest>(yaml)
                  ?? throw new ManifestFormatException("plugin.yaml is empty.");
        }
        catch (ManifestFormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ManifestFormatException($"plugin.yaml is not valid YAML: {ex.Message}", ex);
        }

        Require(dto.Id, "id");
        Require(dto.Name, "name");
        Require(dto.Version, "version");
        Require(dto.SdkVersion, "sdkVersion");

        var kind = ParseKind(dto.Kind);
        var capabilities = (IReadOnlyList<string>?)dto.Capabilities ?? Array.Empty<string>();
        var auth = ParseAuth(dto.Auth);
        var network = new NetworkSpec(dto.Network?.Allow ?? new List<string>());
        var config = new ConfigSchema(
            dto.Config?.JsonSchema ?? """{"type":"object"}""",
            dto.Config?.Required ?? new List<string>());
        var publisher = dto.Publisher is null
            ? null
            : new PluginPublisher(dto.Publisher.Name ?? string.Empty, dto.Publisher.Signature);

        var manifest = new PluginManifest(
            dto.Id!, dto.Name!, dto.Version!, dto.SdkVersion!, kind,
            capabilities, publisher, auth, network, config);

        var operations = ParseOperations(dto.Operations);

        return new ManifestParseResult(manifest, dto.Assembly, dto.OpenApi, operations);
    }

    private static IReadOnlyDictionary<string, ConnectorOperationSpec> ParseOperations(
        Dictionary<string, YamlOperation>? operations)
    {
        if (operations is null || operations.Count == 0)
            return new Dictionary<string, ConnectorOperationSpec>(StringComparer.Ordinal);

        var result = new Dictionary<string, ConnectorOperationSpec>(StringComparer.Ordinal);
        foreach (var (name, op) in operations)
        {
            if (op is null)
                continue;
            if (string.IsNullOrWhiteSpace(op.Call))
                throw new ManifestFormatException(
                    $"plugin.yaml operation '{name}' is missing required field 'call'.");

            result[name] = new ConnectorOperationSpec(
                op.Call!.Trim(),
                op.Query ?? new Dictionary<string, string>(StringComparer.Ordinal),
                op.Path ?? new Dictionary<string, string>(StringComparer.Ordinal),
                op.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
                op.Body,
                op.Map);
        }

        return result;
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ManifestFormatException($"plugin.yaml is missing required field '{field}'.");
    }

    private static PluginKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "declarative" => PluginKind.Declarative,
        "assembly" => PluginKind.Assembly,
        null or "" => throw new ManifestFormatException("plugin.yaml is missing required field 'kind'."),
        _ => throw new ManifestFormatException($"plugin.yaml has unknown kind '{kind}'.")
    };

    private static AuthSpec ParseAuth(YamlAuth? auth)
    {
        if (auth is null)
            return new AuthSpec(AuthScheme.None);

        var scheme = auth.Scheme?.Trim().ToLowerInvariant() switch
        {
            null or "" or "none" => AuthScheme.None,
            "apikey" or "api-key" => AuthScheme.ApiKey,
            "basic" => AuthScheme.Basic,
            "app-password" or "apppassword" => AuthScheme.AppPassword,
            "oauth2-pkce" or "oauth2pkce" => AuthScheme.OAuth2Pkce,
            "oauth2-cc" or "oauth2-client-credentials" => AuthScheme.OAuth2ClientCredentials,
            var other => throw new ManifestFormatException($"plugin.yaml has unknown auth.scheme '{other}'.")
        };

        return new AuthSpec(
            scheme,
            auth.AuthorizationUrl, auth.TokenUrl, auth.Authority,
            auth.Scopes, auth.In, auth.Name, auth.Format, auth.Params);
    }

    // YAML DTOs — camelCase mapped, unknown keys ignored.
#pragma warning disable CS8618 // populated by the deserializer
    private sealed class YamlManifest
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? SdkVersion { get; set; }
        public string? Kind { get; set; }
        public List<string>? Capabilities { get; set; }
        public YamlPublisher? Publisher { get; set; }
        public YamlAuth? Auth { get; set; }
        public YamlNetwork? Network { get; set; }
        public YamlConfig? Config { get; set; }
        public string? Assembly { get; set; }   // main DLL filename (assembly plugins)
        public string? OpenApi { get; set; }     // OpenAPI document filename (declarative plugins)
        public Dictionary<string, YamlOperation>? Operations { get; set; } // declarative capability operations
    }

    private sealed class YamlOperation
    {
        public string? Call { get; set; }
        public Dictionary<string, string>? Query { get; set; }
        public Dictionary<string, string>? Path { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string? Body { get; set; }
        public string? Map { get; set; }
    }

    private sealed class YamlPublisher
    {
        public string? Name { get; set; }
        public string? Signature { get; set; }
    }

    private sealed class YamlAuth
    {
        public string? Scheme { get; set; }
        public string? AuthorizationUrl { get; set; }
        public string? TokenUrl { get; set; }
        public string? Authority { get; set; }
        public List<string>? Scopes { get; set; }
        public string? In { get; set; }
        public string? Name { get; set; }
        public string? Format { get; set; }
        public Dictionary<string, string>? Params { get; set; }
    }

    private sealed class YamlNetwork
    {
        public List<string>? Allow { get; set; }
    }

    private sealed class YamlConfig
    {
        public string? JsonSchema { get; set; }
        public List<string>? Required { get; set; }
    }
#pragma warning restore CS8618
}

/// <summary>Thrown when <c>plugin.yaml</c> is structurally invalid (→ <c>bad-manifest</c> fault).</summary>
public sealed class ManifestFormatException : Exception
{
    public ManifestFormatException(string message, Exception? inner = null) : base(message, inner) { }
}
