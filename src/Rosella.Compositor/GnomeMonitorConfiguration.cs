using System.Globalization;
using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace Rosella.Compositor;

/// <summary>A single, unambiguous GNOME primary monitor and its requested mode.</summary>
internal sealed record GnomePrimaryMonitor(string Connector, int Width, int Height, double Rate, bool Interlaced = false);

/// <summary>
/// Reads GNOME's version 2 monitor store without accessing display hardware.
/// Connector keys supplied by the caller must be canonical GNOME names. Matching
/// is ordinal, irrespective of the dictionary's comparer. Unusable matching
/// configurations or more than one matching configuration fail closed.
/// </summary>
internal static class GnomeMonitorConfiguration
{
    private const long MaxXmlCharacters = 4 * 1024 * 1024;

    internal static GnomePrimaryMonitor? Select<T>(string xml, IReadOnlyDictionary<string, T> connected)
    {
        try
        {
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, ReaderSettings());
            return Select(reader, connected);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    internal static GnomePrimaryMonitor? SelectFile<T>(string path, IReadOnlyDictionary<string, T> connected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, ReaderSettings());
            return Select(reader, connected);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException
                                          or SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxXmlCharacters,
    };

    private static GnomePrimaryMonitor? Select<T>(XmlReader reader, IReadOnlyDictionary<string, T> connected)
    {
        var document = XDocument.Load(reader);
        var root = document.Root;
        if (root is null || root.Name != "monitors" || (string?)root.Attribute("version") != "2" ||
            !HasValidStructure(root))
        {
            return null;
        }

        var connectedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in connected.Keys)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || !connectedNames.Add(name))
            {
                return null;
            }
        }

        GnomePrimaryMonitor? selected = null;
        foreach (var configuration in root.Elements("configuration"))
        {
            var usable = TryReadConfiguration(configuration, out var names, out var primary);
            if (names.SetEquals(connectedNames))
            {
                if (!usable || selected is not null)
                {
                    return null;
                }
                selected = primary;
            }
        }
        return selected;
    }

    // Unknown GNOME extensions can be ignored, but recognised selection fields
    // must not be hidden at the wrong level or in a namespace.
    private static bool HasValidStructure(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.Namespace != XNamespace.None)
            {
                return false;
            }
            var parent = element.Parent?.Name.LocalName;
            var valid = element.Name.LocalName switch
            {
                "monitors" => element == root,
                "configuration" => parent == "monitors",
                "logicalmonitor" or "disabled" or "forlease" => parent == "configuration",
                "primary" or "monitor" => parent == "logicalmonitor",
                "monitorspec" => parent is "monitor" or "disabled" or "forlease",
                "connector" => parent == "monitorspec",
                "mode" => parent == "monitor",
                "width" or "height" or "rate" => parent == "mode",
                _ => true,
            };
            if (!valid)
            {
                return false;
            }
            if (element.Name.LocalName is "monitors" or "configuration" or "logicalmonitor" or
                "monitor" or "monitorspec" or "mode" or "disabled" or "forlease")
            {
                if (element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TryReadConfiguration(XElement configuration, out HashSet<string> names,
        out GnomePrimaryMonitor? primary)
    {
        names = new HashSet<string>(StringComparer.Ordinal);
        primary = null;
        var primaryCount = 0;
        foreach (var logical in configuration.Elements("logicalmonitor"))
        {
            var primaryElements = logical.Elements("primary").ToArray();
            if (primaryElements.Length > 1 || (primaryElements.Length == 1 &&
                (!TryText(primaryElements[0], out var value) || (value != "yes" && value != "no"))))
            {
                return false;
            }
            var isPrimary = primaryElements.Length == 1 && primaryElements[0].Value.Trim() == "yes";
            if (isPrimary)
            {
                primaryCount++;
            }

            var monitors = logical.Elements("monitor").ToArray();
            if (monitors.Length == 0)
            {
                return false;
            }
            foreach (var monitor in monitors)
            {
                if (!TrySingle(monitor, "monitorspec", out var spec) || !TryConnector(spec, out var connector) ||
                    !names.Add(connector) || !TrySingle(monitor, "mode", out var mode) ||
                    !TryMode(mode, connector, out var parsed))
                {
                    return false;
                }
                // A mirrored primary does not identify one physical connector.
                if (isPrimary && monitors.Length == 1)
                {
                    primary = parsed;
                }
            }
        }

        // Mutter lists leased monitors in both disabled and forlease.
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var leased = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in configuration.Elements().Where(element => element.Name == "disabled" || element.Name == "forlease"))
        {
            var specs = group.Elements("monitorspec").ToArray();
            if (specs.Length == 0 || group.Elements().Count() != specs.Length)
            {
                return false;
            }
            foreach (var spec in specs)
            {
                var set = group.Name == "disabled" ? disabled : leased;
                if (!TryConnector(spec, out var connector) || !set.Add(connector) ||
                    (names.Contains(connector) && !(group.Name == "forlease" ? disabled : leased).Contains(connector)))
                {
                    return false;
                }
                names.Add(connector);
            }
        }
        return primaryCount == 1 && primary is not null;
    }

    private static bool TryConnector(XElement spec, out string connector)
    {
        connector = string.Empty;
        return TrySingle(spec, "connector", out var element) && TryText(element, out connector) &&
               connector.Length > 0;
    }

    private static bool TryMode(XElement mode, string connector, out GnomePrimaryMonitor? result)
    {
        result = null;
        if (!TrySingle(mode, "width", out var widthElement) || !TryText(widthElement, out var widthText) ||
            !int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width <= 0 ||
            !TrySingle(mode, "height", out var heightElement) || !TryText(heightElement, out var heightText) ||
            !int.TryParse(heightText, NumberStyles.None, CultureInfo.InvariantCulture, out var height) || height <= 0 ||
            !TrySingle(mode, "rate", out var rateElement) || !TryText(rateElement, out var rateText) ||
            !double.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ||
            !double.IsFinite(rate) || rate <= 0)
        {
            return false;
        }
        var flags = mode.Elements("flag").ToArray();
        if (flags.Length > 1 || (flags.Length == 1 && (!TryText(flags[0], out var flag) || flag != "interlace")))
        {
            return false;
        }
        result = new GnomePrimaryMonitor(connector, width, height, rate, flags.Length == 1);
        return true;
    }

    private static bool TrySingle(XElement parent, XName name, out XElement element)
    {
        var elements = parent.Elements(name).ToArray();
        element = elements.Length == 1 ? elements[0] : null!;
        return elements.Length == 1;
    }

    private static bool TryText(XElement element, out string text)
    {
        text = element.Value.Trim();
        return !element.HasElements;
    }
}
