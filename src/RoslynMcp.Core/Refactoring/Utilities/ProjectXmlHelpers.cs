using System.Xml;
using System.Xml.Linq;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared .csproj XDocument re-serialization used by Move / Rename / Extract
/// operations that rewrite project XML while preserving the original XML
/// declaration and newline style. Same body as the four
/// MoveTypeToNamespace / RenameFileToMatchType / RenameNamespace /
/// ExtractBaseClass copies.
/// </summary>
internal static class ProjectXmlHelpers
{
    /// <summary>
    /// Serializes <paramref name="document"/> with XmlWriterSettings matching
    /// <paramref name="originalXml"/>: omit declaration when the original has
    /// no <c>&lt;?xml</c>, Replace newline handling, LF vs CRLF from the
    /// original, no indent, and restore a trailing newline when the original
    /// ended with <c>\n</c> but the writer omitted it.
    /// </summary>
    internal static string SerializeProjectXml(XDocument document, string originalXml)
    {
        var writerSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = !originalXml.Contains("<?xml", StringComparison.OrdinalIgnoreCase),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = originalXml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            Indent = false
        };

        using var writer = new StringWriter();
        using (var xmlWriter = XmlWriter.Create(writer, writerSettings))
        {
            document.Save(xmlWriter);
        }

        var serialized = writer.ToString();
        if (originalXml.EndsWith('\n') && !serialized.EndsWith('\n'))
            serialized += writerSettings.NewLineChars;
        return serialized;
    }
}
