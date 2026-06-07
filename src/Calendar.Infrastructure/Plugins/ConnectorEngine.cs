using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// The declarative connector engine (PLUGIN-HOST.md §5, SDK-CONTRACT.md §8). It turns a manifest +
/// OpenAPI document + JSONata mapping expressions into a real <see cref="IPlugin"/> that the registry
/// cannot tell apart from a hand-written assembly plugin. There is no ALC — a declarative connector is data.
///
/// At load it parses the OpenAPI document (Microsoft.OpenApi), resolves each <c>operations.&lt;name&gt;</c>
/// <c>call</c> to a concrete operation (verb + path + server base), and compiles an <see cref="OperationPlan"/>
/// per capability method. At call time the synthesized plugin binds the capability arguments into the request
/// query/path/headers/body, applies auth via <see cref="IAuthBroker.ApplyAsync"/>, sends through
/// <see cref="IPluginHost.CreateClient"/>, and maps the JSON response to the SDK DTO via the JSONata
/// <c>map</c> expression.
/// </summary>
public sealed class ConnectorEngine
{
    /// <summary>Whether the engine can materialize declarative connectors. Always true now (was a Phase 0 stub).</summary>
    public bool IsImplemented => true;

    /// <summary>
    /// Build an in-process <see cref="IPluginInstance"/> for a declarative bundle. Throws
    /// <see cref="ConnectorLoadException"/> on a malformed OpenAPI doc, an unbound capability method, or a
    /// <c>call</c> that does not resolve to an operation — the host turns that into a faulted registration.
    /// </summary>
    public IPluginInstance Load(PluginBundle bundle)
    {
        if (bundle.OpenApiPath is null)
            throw new ConnectorLoadException(
                $"declarative connector '{bundle.Manifest.Id}' has no openapi document.");
        if (!File.Exists(bundle.OpenApiPath))
            throw new ConnectorLoadException($"openapi document not found: {bundle.OpenApiPath}");

        var document = ParseOpenApi(bundle.OpenApiPath, bundle.Manifest.Id);
        var baseUri = ResolveBaseUri(document, bundle.Manifest.Id);

        // §3.5 capability binding: each declared capability must bind every required SDK method to an operation.
        RequireBoundOperations(bundle);

        // Compile one OperationPlan per declared operation name (= SDK method name, e.g. "geocode").
        var plans = new Dictionary<string, OperationPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, spec) in bundle.Operations)
            plans[name] = OperationPlan.Compile(name, spec, document, baseUri, bundle.Manifest.Id);

        var plugin = new DeclarativeConnectorPlugin(bundle.Manifest, plans);
        return new DeclarativeConnectorInstance(bundle.Manifest.Id, plugin);
    }

    /// <summary>
    /// Verify the manifest binds the operation each declared capability needs (PLUGIN-HOST.md §3.5). The engine
    /// supports geo.geocode (requires a <c>geocode</c> operation) and geo.route (requires a <c>route</c>
    /// operation) today; any other capability on a declarative bundle is unsupported by the engine.
    /// </summary>
    private static void RequireBoundOperations(PluginBundle bundle)
    {
        foreach (var id in bundle.Manifest.Capabilities)
        {
            switch (id)
            {
                case CapabilityIds.GeoGeocode:
                    Require(bundle, "geocode", CapabilityIds.GeoGeocode);
                    break;
                case CapabilityIds.GeoRoute:
                    Require(bundle, "route", CapabilityIds.GeoRoute);
                    break;
                default:
                    throw new ConnectorLoadException(
                        $"connector '{bundle.Manifest.Id}': the engine does not support declarative capability '{id}' yet.");
            }
        }

        static void Require(PluginBundle bundle, string operation, string capability)
        {
            if (!bundle.Operations.ContainsKey(operation))
                throw new ConnectorLoadException(
                    $"connector '{bundle.Manifest.Id}': capability '{capability}' requires an 'operations.{operation}' block.");
        }
    }

    private static OpenApiDocument ParseOpenApi(string path, string pluginId)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new ConnectorLoadException($"connector '{pluginId}': cannot read openapi document: {ex.Message}", ex);
        }

        OpenApiDocument document;
        Microsoft.OpenApi.Readers.OpenApiDiagnostic diagnostic;
        try
        {
            document = new OpenApiStringReader().Read(text, out diagnostic);
        }
        catch (Exception ex)
        {
            throw new ConnectorLoadException($"connector '{pluginId}': openapi document is not parseable: {ex.Message}", ex);
        }

        if (diagnostic.Errors.Count > 0)
            throw new ConnectorLoadException(
                $"connector '{pluginId}': openapi document has errors: {string.Join("; ", diagnostic.Errors.Select(e => e.Message))}");

        return document;
    }

    private static Uri ResolveBaseUri(OpenApiDocument document, string pluginId)
    {
        var server = document.Servers?.FirstOrDefault()?.Url;
        if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server, UriKind.Absolute, out var baseUri))
            throw new ConnectorLoadException(
                $"connector '{pluginId}': openapi document has no absolute server url to send requests to.");
        return baseUri;
    }
}

/// <summary>Thrown when a declarative bundle cannot be compiled into a connector (→ a faulted registration).</summary>
public sealed class ConnectorLoadException : Exception
{
    public ConnectorLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Thrown when a declarative connector fails to map an upstream response (PLUGIN-HOST.md §5.8 connector-mapping).</summary>
public sealed class ConnectorMappingException : Exception
{
    public ConnectorMappingException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// In-process handle for a declarative connector (PLUGIN-HOST.md §5 — no ALC). Mirrors
/// <see cref="InProcPluginInstance"/> so the registry treats engine-backed and assembly plugins identically.
/// </summary>
public sealed class DeclarativeConnectorInstance : IPluginInstance
{
    internal DeclarativeConnectorInstance(string pluginId, IPlugin plugin)
    {
        PluginId = pluginId;
        Plugin = plugin;
    }

    public string PluginId { get; }
    public IPlugin? Plugin { get; }
}
