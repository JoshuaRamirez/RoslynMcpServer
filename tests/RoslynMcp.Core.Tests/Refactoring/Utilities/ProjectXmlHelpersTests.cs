using System.Xml.Linq;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class ProjectXmlHelpersTests
{
    private static readonly XDocument Sample = XDocument.Parse(
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """,
        LoadOptions.PreserveWhitespace);

    [Fact]
    public void SerializeProjectXml_OmitsDeclaration_WhenOriginalLacksXmlHeader()
    {
        const string original =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";

        var serialized = ProjectXmlHelpers.SerializeProjectXml(Sample, original);

        Assert.DoesNotContain("<?xml", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<Project", serialized, StringComparison.Ordinal);
        Assert.EndsWith("\n", serialized);
        Assert.DoesNotContain("\r\n", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeProjectXml_KeepsDeclaration_WhenOriginalHasXmlHeader()
    {
        const string original =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n";

        var serialized = ProjectXmlHelpers.SerializeProjectXml(Sample, original);

        Assert.StartsWith("<?xml", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("\n", serialized);
    }

    [Fact]
    public void SerializeProjectXml_UsesCrlf_WhenOriginalContainsCrlf()
    {
        const string original =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <PropertyGroup>\r\n    <TargetFramework>net8.0</TargetFramework>\r\n  </PropertyGroup>\r\n</Project>\r\n";

        var serialized = ProjectXmlHelpers.SerializeProjectXml(Sample, original);

        Assert.Contains("\r\n", serialized, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", serialized);
        Assert.DoesNotContain("<?xml", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerializeProjectXml_RestoresTrailingNewline_WhenWriterOmitsIt()
    {
        // Original ends with LF; ensure helper restores trailing newline when needed.
        const string original = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n";

        var serialized = ProjectXmlHelpers.SerializeProjectXml(Sample, original);

        Assert.EndsWith("\n", serialized);
        Assert.False(serialized.EndsWith("\r\n", StringComparison.Ordinal));
    }
}
