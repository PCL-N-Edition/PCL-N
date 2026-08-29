using System.Globalization;
using System.Xml;

namespace PCL.Pxml;

/// <summary>
/// Reports one deterministic PXML parse failure. The message carries the failing construct;
/// no exception details leak beyond the grammar rule.
/// </summary>
public sealed class PxmlParseException(string message) : InvalidOperationException(message)
{
}

/// <summary>
/// Parses PXML authoring text into the structural DOM. The parser enforces grammar only:
/// exactly one root, the PXML namespace, unique attribute names, attribute-only content (no
/// mixed text), and the `{state path}` binding grammar. Element and attribute semantics are
/// the compiler's job.
/// </summary>
public static class PxmlParser
{
    public static PxmlDocument Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new PxmlParseException("The PXML document is empty.");
        }

        XmlDocument document = new();
        try
        {
            document.LoadXml(text);
        }
        catch (XmlException exception)
        {
            throw new PxmlParseException($"The PXML document is not well-formed XML: {exception.Message}");
        }

        XmlElement root = document.DocumentElement
            ?? throw new PxmlParseException("The PXML document has no root element.");
        if (document.DocumentElement!.NextSibling is not null)
        {
            throw new PxmlParseException("The PXML document must contain exactly one root element.");
        }

        ValidateNamespace(root);
        return new PxmlDocument(ReadElement(root));
    }

    private static void ValidateNamespace(XmlElement element)
    {
        string namespaceUri = element.NamespaceURI;
        if (!string.IsNullOrEmpty(namespaceUri) && namespaceUri != PxmlWellKnown.Namespace)
        {
            throw new PxmlParseException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The element '{element.LocalName}' declares namespace '{namespaceUri}' but PXML requires '{PxmlWellKnown.Namespace}'."));
        }

        foreach (XmlNode child in element.ChildNodes)
        {
            if (child is XmlElement childElement)
            {
                ValidateNamespace(childElement);
            }
        }
    }

    private static PxmlElement ReadElement(XmlElement element)
    {
        string name = element.LocalName;
        List<PxmlProperty> attributes = ReadAttributes(element);
        List<PxmlElement> children = ReadChildren(element);
        return new PxmlElement(name, attributes, children);
    }

    private static List<PxmlProperty> ReadAttributes(XmlElement element)
    {
        List<PxmlProperty> attributes = [];
        HashSet<string> seen = [];
        foreach (XmlAttribute attribute in element.Attributes)
        {
            if (attribute.Prefix == "xmlns" || attribute.Name == "xmlns")
            {
                continue;
            }

            if (!seen.Add(attribute.LocalName))
            {
                throw new PxmlParseException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The element '{element.LocalName}' declares attribute '{attribute.LocalName}' twice."));
            }

            attributes.Add(new PxmlProperty(attribute.LocalName, ReadValue(element.LocalName, attribute.LocalName, attribute.Value)));
        }

        return attributes;
    }

    private static List<PxmlElement> ReadChildren(XmlElement element)
    {
        List<PxmlElement> children = [];
        foreach (XmlNode node in element.ChildNodes)
        {
            switch (node)
            {
                case XmlElement child:
                    children.Add(ReadElement(child));
                    break;
                case XmlText or XmlCDataSection or XmlSignificantWhitespace:
                    throw new PxmlParseException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The element '{element.LocalName}' carries text content; PXML elements are attribute-only."));
            }
        }

        return children;
    }

    private static PxmlValue ReadValue(string elementName, string attributeName, string raw)
    {
        const string statePrefix = "{state ";
        if (!raw.StartsWith(statePrefix, StringComparison.Ordinal))
        {
            if (raw.Contains('{', StringComparison.Ordinal) || raw.Contains('}', StringComparison.Ordinal))
            {
                throw new PxmlParseException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The attribute '{attributeName}' of '{elementName}' contains braces but is not a {{state ...}} binding."));
            }

            return PxmlValue.Literal(raw);
        }

        if (!raw.EndsWith('}'))
        {
            throw new PxmlParseException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The state binding on '{elementName}.{attributeName}' is not closed."));
        }

        string path = raw[statePrefix.Length..^1];
        if (path.Length == 0 || path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new PxmlParseException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The state binding on '{elementName}.{attributeName}' needs one non-empty path without whitespace."));
        }

        return PxmlValue.StateBinding(path);
    }
}
