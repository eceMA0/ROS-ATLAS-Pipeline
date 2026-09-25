using System.Text.RegularExpressions;

namespace RosAtlasBridge.Ros;

public enum PrimitiveType
{
    Bool, Byte, Char, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32, Float64, String, WString,
}

/// <summary>None = single value; Fixed = T[N] (no length on the wire); Sequence = T[] or T[&lt;=N] (uint32 length first).</summary>
public enum ArrayKind { None, Fixed, Sequence }

/// <summary>One field of a ROS 2 message definition.</summary>
public sealed class MsgField
{
    public required string Name { get; init; }

    /// <summary>Primitive name (e.g. "float64") or full message type (e.g. "geometry_msgs/msg/Point").</summary>
    public required string TypeName { get; init; }

    public PrimitiveType? Primitive { get; init; }
    public ArrayKind Array { get; init; }
    public int FixedLength { get; init; }

    /// <summary>The nested definition for message-typed fields; null for primitives.</summary>
    public MsgDefinition? Complex { get; internal set; }
}

public sealed class MsgDefinition(string fullName, IReadOnlyList<MsgField> fields)
{
    public string FullName { get; } = fullName;
    public IReadOnlyList<MsgField> Fields { get; } = fields;
}

/// <summary>
/// Parses the "ros2msg" schema text that foxglove_bridge advertises for each topic: the root .msg
/// definition, then every dependency after a line of '=' and a "MSG: pkg/Type" (or pkg/msg/Type) line.
/// </summary>
public static class MsgSchema
{
    private static readonly Regex FieldLine =
        new(@"^(?<type>\S+)\s+(?<name>[A-Za-z][A-Za-z0-9_]*)(?<rest>.*)$", RegexOptions.Compiled);

    private static readonly Regex TypeToken =
        new(@"^(?<base>[^\[\]]+)(?:\[(?<bounded><=)?(?<size>\d*)\])?$", RegexOptions.Compiled);

    private static readonly Dictionary<string, PrimitiveType> Primitives = new()
    {
        ["bool"] = PrimitiveType.Bool,
        ["byte"] = PrimitiveType.Byte,
        ["char"] = PrimitiveType.Char,
        ["int8"] = PrimitiveType.Int8,
        ["uint8"] = PrimitiveType.UInt8,
        ["int16"] = PrimitiveType.Int16,
        ["uint16"] = PrimitiveType.UInt16,
        ["int32"] = PrimitiveType.Int32,
        ["uint32"] = PrimitiveType.UInt32,
        ["int64"] = PrimitiveType.Int64,
        ["uint64"] = PrimitiveType.UInt64,
        ["float32"] = PrimitiveType.Float32,
        ["float64"] = PrimitiveType.Float64,
        ["string"] = PrimitiveType.String,
        ["wstring"] = PrimitiveType.WString,
    };

    public static MsgDefinition Parse(string schemaName, string schemaText)
    {
        var rootName = NormalizeTypeName(schemaName);
        var definitions = new Dictionary<string, MsgDefinition>();
        foreach (var (name, body) in SplitSections(rootName, schemaText))
        {
            definitions[name] = ParseDefinition(name, body);
        }

        foreach (var definition in definitions.Values)
        {
            foreach (var field in definition.Fields.Where(f => f.Primitive is null))
            {
                field.Complex = definitions.TryGetValue(field.TypeName, out var nested)
                    ? nested
                    : throw new FormatException($"schema for {rootName} is missing the definition of {field.TypeName}");
            }
        }

        return definitions[rootName];
    }

    /// <summary>"pkg/Type" or "pkg/msg/Type" -> "pkg/msg/Type".</summary>
    public static string NormalizeTypeName(string name)
    {
        var parts = name.Trim().Split('/');
        return parts.Length == 2 ? $"{parts[0]}/msg/{parts[1]}" : name.Trim();
    }

    private static List<(string Name, string Body)> SplitSections(string rootName, string text)
    {
        var sections = new List<(string, string)>();
        var name = rootName;
        var body = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length < 3 || trimmed.Any(c => c != '='))
            {
                body.Add(lines[i]);
                continue;
            }

            sections.Add((name, string.Join('\n', body)));
            body.Clear();
            do
            {
                i++;
            }
            while (i < lines.Length && lines[i].Trim().Length == 0);

            if (i >= lines.Length || !lines[i].TrimStart().StartsWith("MSG:", StringComparison.Ordinal))
            {
                throw new FormatException("expected 'MSG: <type>' after a separator line");
            }

            name = NormalizeTypeName(lines[i].Trim()[4..]);
        }

        sections.Add((name, string.Join('\n', body)));
        return sections;
    }

    private static MsgDefinition ParseDefinition(string fullName, string body)
    {
        var package = fullName.Split('/')[0];
        var fields = new List<MsgField>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = FieldLine.Match(line);
            if (!match.Success)
            {
                throw new FormatException($"{fullName}: can't parse line '{rawLine.Trim()}'");
            }

            if (match.Groups["rest"].Value.TrimStart().StartsWith('='))
            {
                continue; // a constant - not part of the serialized message
            }

            fields.Add(ParseField(package, match.Groups["type"].Value, match.Groups["name"].Value, fullName));
        }

        return new MsgDefinition(fullName, fields);
    }

    private static MsgField ParseField(string package, string typeToken, string name, string owner)
    {
        var match = TypeToken.Match(typeToken);
        if (!match.Success)
        {
            throw new FormatException($"{owner}: can't parse type '{typeToken}'");
        }

        var baseType = match.Groups["base"].Value;
        var bound = baseType.IndexOf("<=", StringComparison.Ordinal); // bounded string: string<=10
        if (bound >= 0)
        {
            baseType = baseType[..bound];
        }

        var (array, length) = !typeToken.Contains('[') ? (ArrayKind.None, 0)
            : match.Groups["bounded"].Success || match.Groups["size"].Value.Length == 0 ? (ArrayKind.Sequence, 0)
            : (ArrayKind.Fixed, int.Parse(match.Groups["size"].Value));

        if (Primitives.TryGetValue(baseType, out var primitive))
        {
            return new MsgField { Name = name, TypeName = baseType, Primitive = primitive, Array = array, FixedLength = length };
        }

        var typeName = baseType.Contains('/') ? NormalizeTypeName(baseType) : $"{package}/msg/{baseType}"; // no '/' = same package
        return new MsgField { Name = name, TypeName = typeName, Array = array, FixedLength = length };
    }

    /// <summary>Drops a trailing '#' comment, ignoring '#' inside quoted default values.</summary>
    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#')
            {
                return line[..i];
            }
        }

        return line;
    }
}
