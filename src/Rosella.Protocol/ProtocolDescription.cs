using System.Xml;
using System.Xml.Linq;

namespace Rosella.Protocol;

/// <summary>
/// A parsed protocol definition (such as the core <c>wayland.xml</c>): the
/// interfaces, their versions, and every request/event with its wire
/// signature and opcodes.
/// </summary>
public sealed class ProtocolDescription
{
    private static readonly Lazy<ProtocolDescription> CoreInstance = new(LoadCore, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ProtocolDescription> XdgShellInstance = new(LoadXdgShell, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ProtocolDescription> CombinedInstance = new(
        () => Combine(Core, XdgShell),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, InterfaceDescription> _interfacesByName;

    public ProtocolDescription(string name, IReadOnlyList<InterfaceDescription> interfaces)
    {
        Name = name;
        Interfaces = interfaces;
        _interfacesByName = interfaces.ToDictionary(i => i.Name);
    }

    public string Name { get; }

    public IReadOnlyList<InterfaceDescription> Interfaces { get; }

    /// <summary>The core Wayland protocol, parsed from the embedded wayland.xml.</summary>
    public static ProtocolDescription Core
    {
        get { return CoreInstance.Value; }
    }

    /// <summary>The xdg-shell protocol, parsed from the embedded xdg-shell.xml.</summary>
    public static ProtocolDescription XdgShell
    {
        get { return XdgShellInstance.Value; }
    }

    /// <summary>Core plus xdg-shell: every interface Rosella speaks.</summary>
    public static ProtocolDescription Combined
    {
        get { return CombinedInstance.Value; }
    }

    /// <summary>Merges protocol definitions into one interface set.</summary>
    public static ProtocolDescription Combine(params ProtocolDescription[] protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        var interfaces = new List<InterfaceDescription>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var protocol in protocols)
        {
            foreach (var iface in protocol.Interfaces)
            {
                if (!seen.Add(iface.Name))
                {
                    throw new InvalidOperationException($"interface '{iface.Name}' is defined twice");
                }

                interfaces.Add(iface);
            }
        }

        return new ProtocolDescription(string.Join("+", protocols.Select(p => p.Name)), interfaces);
    }

    /// <summary>Returns the named interface, or null when the protocol does not define it.</summary>
    public InterfaceDescription? FindInterface(string name)
    {
        return _interfacesByName.GetValueOrDefault(name);
    }

    /// <summary>Parses a protocol definition from a stream of protocol XML.</summary>
    public static ProtocolDescription Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(stream, settings);
        return Parse(XDocument.Load(reader));
    }

    private static ProtocolDescription LoadCore()
    {
        return LoadResource("Rosella.Protocol.Resources.wayland.xml");
    }

    private static ProtocolDescription LoadXdgShell()
    {
        return LoadResource("Rosella.Protocol.Resources.xdg-shell.xml");
    }

    private static ProtocolDescription LoadResource(string resourceName)
    {
        var assembly = typeof(ProtocolDescription).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded {resourceName} resource is missing.");
        return Load(stream);
    }

    private static ProtocolDescription Parse(XDocument document)
    {
        var root = document.Root ?? throw new FormatException("Protocol XML has no root element.");
        var interfaces = new List<InterfaceDescription>();

        foreach (var interfaceElement in root.Elements("interface"))
        {
            var name = (string?)interfaceElement.Attribute("name")
                ?? throw new FormatException("Interface has no name.");
            var versionText = (string?)interfaceElement.Attribute("version")
                ?? throw new FormatException($"Interface '{name}' has no version.");
            if (!uint.TryParse(versionText, out var version))
            {
                throw new FormatException($"Interface '{name}' has invalid version '{versionText}'.");
            }

            interfaces.Add(new InterfaceDescription(
                name,
                version,
                ParseMessages(interfaceElement, "request"),
                ParseMessages(interfaceElement, "event")));
        }

        var protocolName = (string?)root.Attribute("name") ?? "unnamed";
        return new ProtocolDescription(protocolName, interfaces);
    }

    private static IReadOnlyList<MessageDescription> ParseMessages(XElement interfaceElement, string elementName)
    {
        var messages = new List<MessageDescription>();
        ushort opcode = 0;

        foreach (var messageElement in interfaceElement.Elements(elementName))
        {
            var name = (string?)messageElement.Attribute("name")
                ?? throw new FormatException($"{elementName} has no name.");
            var arguments = new List<ArgumentDescription>();

            foreach (var argumentElement in messageElement.Elements("arg"))
            {
                var argumentName = (string?)argumentElement.Attribute("name")
                    ?? throw new FormatException($"{elementName} '{name}' has an argument with no name.");
                var typeText = (string?)argumentElement.Attribute("type")
                    ?? throw new FormatException($"{elementName} '{name}' argument '{argumentName}' has no type.");

                arguments.Add(new ArgumentDescription(
                    argumentName,
                    ParseKind(typeText, name, argumentName),
                    (string?)argumentElement.Attribute("allow-null") == "true",
                    (string?)argumentElement.Attribute("interface")));
            }

            var sinceText = (string?)messageElement.Attribute("since");
            uint since = 1;
            if (sinceText is not null && (!uint.TryParse(sinceText, out since) || since == 0))
            {
                throw new FormatException($"{elementName} '{name}' has invalid since '{sinceText}'.");
            }

            messages.Add(new MessageDescription(name, opcode, arguments, (string?)messageElement.Attribute("type") == "destructor", since));
            opcode++;
        }

        return messages;
    }

    private static ArgumentKind ParseKind(string typeText, string messageName, string argumentName)
    {
        switch (typeText)
        {
            case "int":
                return ArgumentKind.Int32;
            case "uint":
                return ArgumentKind.UInt32;
            case "fixed":
                return ArgumentKind.Fixed;
            case "string":
                return ArgumentKind.String;
            case "object":
                return ArgumentKind.Object;
            case "new_id":
                return ArgumentKind.NewId;
            case "array":
                return ArgumentKind.Array;
            case "fd":
                return ArgumentKind.Fd;
            default:
                throw new FormatException(
                    $"Message '{messageName}' argument '{argumentName}' has unknown type '{typeText}'.");
        }
    }
}
