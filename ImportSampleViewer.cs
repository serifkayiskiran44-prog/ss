using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TrMarketplaceHubDesktop;

public enum SampleKind { Empty, Text, Truncated, Binary, RawPayload }

/// <summary>A bounded specimen of a supplier value: what is shown, why it looks the way it does, and how long the source was.</summary>
public sealed record BoundedSample(string Text, SampleKind Kind, int SourceLength);

/// <summary>The bounds of the sample viewer: characters shown per field, characters kept per field in the sample item, fields kept per item, nesting depth kept.</summary>
public sealed record SampleLimits(int MaxChars = ImportSampleViewer.DefaultMaxChars, int MaxFieldChars = ImportSampleViewer.DefaultMaxFieldChars, int MaxFields = ImportSampleViewer.DefaultMaxFields, int MaxDepth = ImportSampleViewer.DefaultMaxDepth);

/// <summary>The first item of a feed, bounded: the element (null when the path matched nothing or is not a plain path), whether anything was left out, and how many fields were kept.</summary>
public sealed record SampleItem(XElement? Element, bool Truncated, int Fields);

/// <summary>
/// Source sample viewer limits (#882). The mapping table's samples come from the feed's first item, and a supplier's
/// file can hold a row with thousands of fields or a field of megabytes: the sample item is read by streaming the
/// document only as far as the first item and cloning it within bounds — at most <see cref="DefaultMaxFields"/>
/// fields, <see cref="DefaultMaxDepth"/> levels, <see cref="DefaultMaxFieldChars"/> characters per value — so the
/// whole document is never parsed for a sample and a huge value never materializes. Each shown sample is bounded
/// again: binary-looking content is replaced by a fixed word, a raw body by the hidden-payload word, the rest passes
/// the central redaction, collapses its whitespace and is cut at a grapheme boundary (an emoji or a combining
/// sequence is never split) to <see cref="DefaultMaxChars"/> characters with an explicit "[kısaltıldı: N karakter]"
/// mark — the truncated state is visible, never silent. Nothing here logs a value.
/// </summary>
public static class ImportSampleViewer
{
    public const int DefaultMaxChars = 60;
    public const int DefaultMaxFieldChars = 4096;
    public const int DefaultMaxFields = 200;
    public const int DefaultMaxDepth = 8;
    public const string BinaryHidden = "[ikili içerik gösterilmez]";
    public const string ItemTruncatedNote = "örnek kayıt kısaltıldı (alan veya uzunluk sınırı)";
    const int BinaryProbe = 512;
    const double BinaryRatio = 0.05;

    public static string TruncatedMark(int sourceLength) => $"… [kısaltıldı: {sourceLength.ToString("N0", CultureInfo.CurrentCulture)} karakter]";

    /// <summary>The bounded specimen of one value.</summary>
    public static BoundedSample Bound(string? raw, int maxChars = DefaultMaxChars)
    {
        var text = raw ?? ""; var length = text.Length;
        if (text.Trim().Length == 0) return new("", SampleKind.Empty, length);
        if (LooksBinary(text)) return new(BinaryHidden, SampleKind.Binary, length);
        if (StatusTooltip.LooksLikeRawPayload(text)) return new(StatusTooltip.RawPayloadHidden, SampleKind.RawPayload, length);
        var clean = Regex.Replace(AuditStore.Redact(text), @"\s+", " ").Trim();
        if (clean.Length <= Math.Max(1, maxChars)) return new(clean, SampleKind.Text, length);
        return new(CutGraphemes(clean, Math.Max(1, maxChars)) + TruncatedMark(length), SampleKind.Truncated, length);
    }

    /// <summary>Binary-looking text: a NUL anywhere in the probe, or more than 5 % control characters (tab, newline and carriage return excepted) or replacement characters in it.</summary>
    public static bool LooksBinary(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var probe = text.Length > BinaryProbe ? text[..BinaryProbe] : text; if (probe.Length == 0) return false;
        var suspicious = 0;
        foreach (var c in probe)
        {
            if (c == '\0') return true;
            if ((c < ' ' && c is not '\t' and not '\n' and not '\r') || c == '' || c == '�') suspicious++;
        }
        return suspicious / (double)probe.Length > BinaryRatio;
    }

    /// <summary>The longest prefix of whole text elements (graphemes) that fits: an emoji, a surrogate pair or a letter with its combining marks is kept or dropped as one.</summary>
    public static string CutGraphemes(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxChars) return text;
        var builder = new StringBuilder(maxChars);
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (builder.Length + element.Length > maxChars) break;
            builder.Append(element);
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>Whether an item path is a plain element path ("/Products/Product", "items/item") the streaming reader can follow; anything else (a predicate, an attribute, a wildcard) needs the XPath fallback.</summary>
    public static bool IsPlainPath(string? itemPath) => !string.IsNullOrWhiteSpace(itemPath) && itemPath.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segments && segments.All(s => Regex.IsMatch(s, "^[A-Za-z_][A-Za-z0-9_.-]*$"));

    /// <summary>
    /// The feed's first item under a plain item path, cloned within the limits: the reader streams only as far as
    /// the first match and stops; values longer than the field limit are cut, fields beyond the field limit and
    /// levels beyond the depth limit are left out, and the result says so. A path that is not plain, or matches
    /// nothing, gives no element.
    /// </summary>
    public static SampleItem ReadFirstItem(string xml, string? itemPath, SampleLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        limits ??= new SampleLimits();
        if (!IsPlainPath(itemPath)) return new SampleItem(null, false, 0);
        var segments = itemPath!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreProcessingInstructions = true, IgnoreWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var stack = new List<string>();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    stack.Add(reader.LocalName);
                    if (stack.Count == segments.Length && stack.Zip(segments).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)))
                        return Clone(reader, limits);
                    if (reader.IsEmptyElement) stack.RemoveAt(stack.Count - 1);
                }
                else if (reader.NodeType == XmlNodeType.EndElement && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
        }
        catch (XmlException) { return new SampleItem(null, false, 0); }
        return new SampleItem(null, false, 0);
    }

    // The reader stands on the item's start element; the clone keeps bounded attributes and children and drains the rest of the subtree without materializing it.
    static SampleItem Clone(XmlReader reader, SampleLimits limits)
    {
        var fields = 0; var truncated = false;
        var root = new XElement(reader.LocalName);
        CopyAttributes(reader, root, limits, ref fields, ref truncated);
        if (reader.IsEmptyElement) return new SampleItem(root, truncated, fields);
        var open = new Stack<XElement>(); open.Push(root);
        var depth = 1; var skipping = 0; // skipping > 0: inside a subtree that is left out
        var buffer = new char[1024];
        while (open.Count > 0 && reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (skipping > 0 || depth >= limits.MaxDepth || fields >= limits.MaxFields)
                    {
                        truncated = true;
                        if (!reader.IsEmptyElement) skipping++;
                        break;
                    }
                    var child = new XElement(reader.LocalName); fields++;
                    CopyAttributes(reader, child, limits, ref fields, ref truncated);
                    open.Peek().Add(child);
                    if (!reader.IsEmptyElement) { open.Push(child); depth++; }
                    break;
                case XmlNodeType.EndElement:
                    if (skipping > 0) { skipping--; break; }
                    open.Pop(); depth--;
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                    var text = ReadBounded(reader, buffer, limits.MaxFieldChars, out var cut);
                    if (cut) truncated = true;
                    if (skipping == 0 && text.Length > 0) open.Peek().Add(new XText(text));
                    break;
            }
        }
        return new SampleItem(root, truncated, fields);
    }

    static void CopyAttributes(XmlReader reader, XElement element, SampleLimits limits, ref int fields, ref bool truncated)
    {
        if (!reader.HasAttributes) return;
        while (reader.MoveToNextAttribute())
        {
            if (fields >= limits.MaxFields) { truncated = true; continue; }
            var value = reader.Value; fields++;
            if (value.Length > limits.MaxFieldChars) { value = value[..limits.MaxFieldChars]; truncated = true; }
            element.SetAttributeValue(reader.LocalName, value);
        }
        reader.MoveToElement();
    }

    // Reads a text node in chunks: at most the field limit is kept, the remainder is drained without being kept.
    static string ReadBounded(XmlReader reader, char[] buffer, int maxFieldChars, out bool cut)
    {
        cut = false;
        if (!reader.CanReadValueChunk)
        {
            var value = reader.Value;
            if (value.Length <= maxFieldChars) return value;
            cut = true; return value[..maxFieldChars];
        }
        var builder = new StringBuilder(); int read;
        while ((read = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
        {
            if (builder.Length >= maxFieldChars) { cut = true; continue; }
            var take = Math.Min(read, maxFieldChars - builder.Length);
            builder.Append(buffer, 0, take);
            if (take < read) cut = true;
        }
        return builder.ToString();
    }
}
