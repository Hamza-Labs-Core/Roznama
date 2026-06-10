using System.Globalization;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Json;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// Resolves the manifest's <c>{...}</c> binding placeholders against a JSON argument context (PLUGIN-HOST.md
/// §5.3). A capability method's argument record is exposed to the binder as <c>q</c> (with nested members,
/// e.g. <c>q.bias.lang</c>); a placeholder like <c>"{q.query}"</c> is evaluated as a JSONata path against that
/// context. A placeholder that resolves to null/undefined returns <c>null</c> so the caller can omit it.
/// </summary>
public static class PlaceholderBinder
{
    /// <summary>
    /// Resolve a binding expression to a scalar string, or null when it resolves to null/absent. If the whole
    /// expression is one <c>{...}</c> placeholder it is evaluated as JSONata; otherwise <c>{...}</c> segments
    /// are substituted inline into the literal text (a null segment makes the whole value null/omitted).
    /// </summary>
    public static string? Resolve(string expression, JToken q)
    {
        if (string.IsNullOrEmpty(expression))
            return expression;

        var trimmed = expression.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}') && trimmed.IndexOf('{', 1) < 0)
        {
            var path = trimmed[1..^1].Trim();
            return EvaluateScalar(path, q);
        }

        // Mixed literal + placeholders: substitute each {path}. A null/absent placeholder voids the value.
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < expression.Length)
        {
            var open = expression.IndexOf('{', i);
            if (open < 0)
            {
                result.Append(expression, i, expression.Length - i);
                break;
            }
            result.Append(expression, i, open - i);
            var close = expression.IndexOf('}', open + 1);
            if (close < 0)
            {
                result.Append(expression, open, expression.Length - open);
                break;
            }
            var path = expression[(open + 1)..close].Trim();
            var value = EvaluateScalar(path, q);
            if (value is null)
                return null; // a missing placeholder omits the whole binding
            result.Append(value);
            i = close + 1;
        }

        return result.ToString();
    }

    /// <summary>
    /// Evaluate a body template as JSONata over <paramref name="q"/> and return the structural JSON result.
    /// Used for request bodies (PLUGIN-HOST.md §5.3).
    /// </summary>
    public static string EvaluateToJson(string expression, JToken q)
    {
        var token = new JsonataQuery(expression).Eval(q);
        return token.ToFlatString();
    }

    private static string? EvaluateScalar(string path, JToken q)
    {
        JToken token;
        try
        {
            token = new JsonataQuery(path).Eval(q);
        }
        catch
        {
            return null;
        }

        return token.Type switch
        {
            JTokenType.Undefined or JTokenType.Null => null,
            JTokenType.String => token.ToObject<string>(),
            JTokenType.Boolean => token.ToObject<bool>() ? "true" : "false",
            JTokenType.Integer => token.ToObject<long>().ToString(CultureInfo.InvariantCulture),
            JTokenType.Float => token.ToObject<double>().ToString(CultureInfo.InvariantCulture),
            _ => token.ToFlatString()
        };
    }
}
