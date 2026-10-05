using System.Runtime.CompilerServices;
using System.Text;

namespace Catalogue.Application.Streaming;

/// <summary>
/// Streams EDIFACT segments (terminated by <c>'</c>) from a reader without loading the file,
/// so multi-GB interchanges are processed in constant memory. Honours the <c>?</c> release
/// character so an escaped terminator inside data does not split a segment.
/// </summary>
public static class EdiSegmentReader
{
    public static async IAsyncEnumerable<string> ReadAsync(TextReader reader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new char[64 * 1024];
        var sb = new StringBuilder(256);
        var released = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (released) { sb.Append(c); released = false; continue; }
                if (c == '?') { sb.Append(c); released = true; continue; }
                if (c == '\'')
                {
                    var segment = Trim(sb);
                    sb.Clear();
                    if (segment.Length > 0) yield return segment;
                    continue;
                }
                sb.Append(c);
            }
        }
        var tail = Trim(sb);
        if (tail.Length > 0) yield return tail;
    }

    private static string Trim(StringBuilder sb)
    {
        int start = 0, end = sb.Length - 1;
        while (start <= end && char.IsWhiteSpace(sb[start])) start++;
        while (end >= start && char.IsWhiteSpace(sb[end])) end--;
        return start > end ? "" : sb.ToString(start, end - start + 1);
    }

    /// <summary>
    /// Splits a segment (or element) on a separator, honouring the <c>?</c> release character, and
    /// unescapes each part. <c>CAV+ENERGY_CLASS:A?+?+?+</c> → <c>["CAV", "ENERGY_CLASS:A+++"]</c> on '+'.
    /// Components keep their escapes until split on ':' so nested separators stay intact.
    /// </summary>
    public static string[] Split(string text, char separator, bool unescape)
    {
        if (text.IndexOf('?') < 0)
            return text.Split(separator);
        var parts = new List<string>();
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '?' && i + 1 < text.Length)
            {
                if (!unescape) sb.Append(c);
                sb.Append(text[++i]);
                continue;
            }
            if (c == separator) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }

    /// <summary>Escapes EDIFACT service characters in a data value.</summary>
    public static string Escape(string value) =>
        value.IndexOfAny(['?', '+', ':', '\'']) < 0 ? value
            : value.Replace("?", "??").Replace("+", "?+").Replace(":", "?:").Replace("'", "?'");

    /// <summary>Segment tag, e.g. <c>LIN</c> for <c>LIN+SKU+Name</c>.</summary>
    public static ReadOnlySpan<char> Tag(string segment)
    {
        var plus = segment.IndexOf('+');
        return plus < 0 ? segment.AsSpan() : segment.AsSpan(0, plus);
    }
}
