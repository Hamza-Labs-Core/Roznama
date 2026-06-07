using System.Globalization;
using System.Text;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Json;
using Microsoft.OpenApi.Models;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// A compiled declarative operation (PLUGIN-HOST.md §5.2/§5.3). Resolved once at load: the HTTP verb, the
/// path template, the base URI, the query/path/header placeholder bindings, an optional body template, and the
/// pre-parsed JSONata <c>map</c> expression. At call time <see cref="BuildRequest"/> binds a JSON argument
/// context (<c>q</c>) into a concrete <see cref="HttpRequestMessage"/>, and <see cref="MapResponse"/> projects
/// the response JSON through <see cref="MapQuery"/>.
/// </summary>
public sealed class OperationPlan
{
    private readonly Uri _baseUri;
    private readonly HttpMethod _method;
    private readonly string _pathTemplate;
    private readonly IReadOnlyDictionary<string, string> _query;
    private readonly IReadOnlyDictionary<string, string> _path;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly string? _bodyTemplate;

    private OperationPlan(
        string name,
        Uri baseUri,
        HttpMethod method,
        string pathTemplate,
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> path,
        IReadOnlyDictionary<string, string> headers,
        string? bodyTemplate,
        JsonataQuery? mapQuery)
    {
        Name = name;
        _baseUri = baseUri;
        _method = method;
        _pathTemplate = pathTemplate;
        _query = query;
        _path = path;
        _headers = headers;
        _bodyTemplate = bodyTemplate;
        MapQuery = mapQuery;
    }

    /// <summary>The operation/method name (e.g. <c>geocode</c>).</summary>
    public string Name { get; }

    /// <summary>The compiled JSONata <c>map</c> expression, or null if the operation declares none.</summary>
    public JsonataQuery? MapQuery { get; }

    /// <summary>
    /// Resolve <paramref name="spec"/>'s <c>call</c> (an operationId or a <c>"METHOD path"</c> pair) against the
    /// OpenAPI document and compile the binding maps + JSONata expression. Throws
    /// <see cref="ConnectorLoadException"/> on an unresolvable call or an unparseable map.
    /// </summary>
    public static OperationPlan Compile(
        string name,
        ConnectorOperationSpec spec,
        OpenApiDocument document,
        Uri baseUri,
        string pluginId)
    {
        var (method, pathTemplate) = ResolveCall(spec.Call, document, pluginId, name);

        JsonataQuery? mapQuery = null;
        if (!string.IsNullOrWhiteSpace(spec.Map))
        {
            try
            {
                mapQuery = new JsonataQuery(spec.Map);
            }
            catch (Exception ex)
            {
                throw new ConnectorLoadException(
                    $"connector '{pluginId}': operation '{name}' has an unparseable JSONata map: {ex.Message}", ex);
            }
        }

        return new OperationPlan(
            name, baseUri, method, pathTemplate,
            spec.Query, spec.Path, spec.Headers, spec.Body, mapQuery);
    }

    /// <summary>
    /// Resolve the operation's verb + path template. A <c>call</c> is either an <c>operationId</c> or a
    /// <c>"METHOD path"</c> pair (e.g. <c>"GET /search"</c>); the latter must exist in the document.
    /// </summary>
    private static (HttpMethod Method, string Path) ResolveCall(
        string call, OpenApiDocument document, string pluginId, string opName)
    {
        var parts = call.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && TryParseMethod(parts[0], out var verb))
        {
            var path = parts[1];
            if (!PathExists(document, verb, path))
                throw new ConnectorLoadException(
                    $"connector '{pluginId}': operation '{opName}' call '{call}' is not present in the openapi document.");
            return (verb, path);
        }

        // Otherwise treat the whole call as an operationId and search for it.
        foreach (var (path, item) in document.Paths)
        foreach (var (opType, operation) in item.Operations)
        {
            if (string.Equals(operation.OperationId, call, StringComparison.Ordinal))
                return (ToHttpMethod(opType), path);
        }

        throw new ConnectorLoadException(
            $"connector '{pluginId}': operation '{opName}' call '{call}' did not resolve to an operationId or 'METHOD path'.");
    }

    private static bool PathExists(OpenApiDocument document, HttpMethod method, string path)
    {
        if (!document.Paths.TryGetValue(path, out var item))
            return false;
        var opType = ToOperationType(method);
        return item.Operations.ContainsKey(opType);
    }

    private static bool TryParseMethod(string verb, out HttpMethod method)
    {
        method = verb.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            "DELETE" => HttpMethod.Delete,
            "HEAD" => HttpMethod.Head,
            _ => HttpMethod.Get
        };
        return verb.ToUpperInvariant() is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD";
    }

    private static HttpMethod ToHttpMethod(OperationType type) => type switch
    {
        OperationType.Get => HttpMethod.Get,
        OperationType.Post => HttpMethod.Post,
        OperationType.Put => HttpMethod.Put,
        OperationType.Patch => HttpMethod.Patch,
        OperationType.Delete => HttpMethod.Delete,
        OperationType.Head => HttpMethod.Head,
        _ => HttpMethod.Get
    };

    private static OperationType ToOperationType(HttpMethod method) =>
        method == HttpMethod.Get ? OperationType.Get
        : method == HttpMethod.Post ? OperationType.Post
        : method == HttpMethod.Put ? OperationType.Put
        : method == HttpMethod.Patch ? OperationType.Patch
        : method == HttpMethod.Delete ? OperationType.Delete
        : method == HttpMethod.Head ? OperationType.Head
        : OperationType.Get;

    /// <summary>
    /// Bind the argument context <paramref name="q"/> into a concrete request: path segments (required — a null
    /// path param is a binding error), query string (null/absent placeholders omitted, PLUGIN-HOST.md §5.3),
    /// headers (omitted when null), and an optional JSON body built via JSONata over <c>q</c>.
    /// </summary>
    public HttpRequestMessage BuildRequest(JToken q)
    {
        var path = _pathTemplate;
        foreach (var (segment, expression) in _path)
        {
            var value = PlaceholderBinder.Resolve(expression, q);
            if (value is null)
                throw new ConnectorMappingException(
                    $"operation '{Name}': required path parameter '{segment}' bound to null.");
            path = path.Replace("{" + segment + "}", EscapePathSegment(value), StringComparison.Ordinal);
        }

        var builder = new StringBuilder();
        foreach (var (key, expression) in _query)
        {
            var value = PlaceholderBinder.Resolve(expression, q);
            if (value is null)
                continue; // a placeholder resolving to null/absent is omitted (§5.3)
            builder.Append(builder.Length == 0 ? '?' : '&')
                   .Append(Uri.EscapeDataString(key))
                   .Append('=')
                   .Append(Uri.EscapeDataString(value));
        }

        var relative = path.StartsWith('/') ? path[1..] : path;
        var uri = new Uri(_baseUri, relative + builder);
        var request = new HttpRequestMessage(_method, uri);

        foreach (var (name, expression) in _headers)
        {
            var value = PlaceholderBinder.Resolve(expression, q);
            if (value is null)
                continue; // omitted when null
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (!string.IsNullOrWhiteSpace(_bodyTemplate))
        {
            var bodyJson = PlaceholderBinder.EvaluateToJson(_bodyTemplate, q);
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>
    /// Escape a value for a single path segment. Unlike <c>Uri.EscapeDataString</c>, RFC 3986
    /// path sub-delimiters (<c>, ; : @ + $ ! ' ( ) *</c>) are preserved so compound path params — e.g. OSRM's
    /// <c>lng,lat;lng,lat</c> coordinate string — survive intact; only characters that would break the path
    /// structure (<c>/ ? # %</c>, whitespace, controls) are percent-encoded.
    /// </summary>
    private static string EscapePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            var safe = ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '.' or '_' or '~'                       // RFC 3986 unreserved
                or ',' or ';' or ':' or '@' or '+' or '$'         // sub-delims valid in a path segment
                or '!' or '\'' or '(' or ')' or '*' or '&' or '=';
            if (safe)
                builder.Append(ch);
            else
                builder.Append(Uri.EscapeDataString(ch.ToString()));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Project the response body through <see cref="MapQuery"/> into a JSON token. Returns <c>Undefined</c>
    /// (the normal "no result" case) when the map yields nothing. A throwing map surfaces as a
    /// <see cref="ConnectorMappingException"/> so the aggregator can fail over (PLUGIN-HOST.md §5.8).
    /// </summary>
    public JToken MapResponse(string responseBody)
    {
        JToken data;
        try
        {
            data = JToken.Parse(responseBody);
        }
        catch (Exception ex)
        {
            throw new ConnectorMappingException(
                $"operation '{Name}': upstream response was not valid JSON: {ex.Message}", ex);
        }

        if (MapQuery is null)
            return data;

        try
        {
            return MapQuery.Eval(data);
        }
        catch (Exception ex)
        {
            throw new ConnectorMappingException(
                $"operation '{Name}': JSONata map failed: {ex.Message}", ex);
        }
    }
}
