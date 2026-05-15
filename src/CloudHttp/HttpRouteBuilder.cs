using System.Text;

namespace CloudHttp;

/// <summary>
/// Small helpers for building HTTP request paths from templates and appending
/// query-string parameters to an existing <see cref="Uri"/>. Everything is
/// URI-escaped.
/// </summary>
public static class HttpRouteBuilder
{
    /// <summary>
    /// Substitutes <c>{name}</c> placeholders in <paramref name="template"/> with the
    /// matching values from <paramref name="parameters"/>. Values are converted via
    /// <see cref="object.ToString"/> and URI-escaped. A <see langword="null"/> value
    /// substitutes an empty string. Placeholders that have no matching parameter are
    /// left untouched.
    /// </summary>
    /// <param name="template">Path template, e.g. <c>"/api/v{ver}/users/{id}"</c>.</param>
    /// <param name="parameters">Map of placeholder name to value.</param>
    /// <returns>The expanded path.</returns>
    public static string BuildPath(string template, IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(parameters);

        var result = template;
        foreach (var (key, value) in parameters)
        {
            var placeholder = $"{{{key}}}";
            if (!result.Contains(placeholder, StringComparison.Ordinal)) continue;
            var encoded = value is null ? string.Empty : Uri.EscapeDataString(value.ToString()!);
            result = result.Replace(placeholder, encoded);
        }
        return result;
    }

    /// <summary>
    /// Returns a new <see cref="Uri"/> whose query string is <paramref name="baseUri"/>'s
    /// query merged with the supplied <paramref name="parameters"/>. Existing query
    /// parameters are preserved. Keys and values are URI-escaped.
    /// </summary>
    /// <param name="baseUri">Absolute URI to append to.</param>
    /// <param name="parameters">Query parameters to append.</param>
    /// <param name="skipNulls">
    /// When <see langword="true"/> (default), parameters whose value is <see langword="null"/>
    /// or empty are skipped. When <see langword="false"/>, they are emitted as
    /// <c>key=</c> (empty value).
    /// </param>
    public static Uri AddQuery(this Uri baseUri, IEnumerable<KeyValuePair<string, string?>> parameters, bool skipNulls = true)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(parameters);

        var existing = baseUri.Query; // includes leading '?' if present
        var hasExisting = !string.IsNullOrEmpty(existing) && existing != "?";

        var qb = new StringBuilder();
        if (hasExisting) qb.Append(existing);

        var first = !hasExisting;
        foreach (var (key, value) in parameters)
        {
            if (skipNulls && string.IsNullOrEmpty(value)) continue;
            qb.Append(first ? '?' : '&');
            qb.Append(Uri.EscapeDataString(key));
            qb.Append('=');
            qb.Append(Uri.EscapeDataString(value ?? string.Empty));
            first = false;
        }

        var path = baseUri.GetLeftPart(UriPartial.Path);
        var fragment = baseUri.Fragment; // includes leading '#' if present
        return new Uri(path + qb + fragment, UriKind.Absolute);
    }
}