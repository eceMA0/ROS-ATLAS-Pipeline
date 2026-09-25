namespace RosAtlasBridge.Ros;

public sealed class FlatMessage
{
    /// <summary>Top-level header.stamp in nanoseconds since the Unix epoch, if the message has a header.</summary>
    public long? StampNs { get; set; }

    public List<(string Path, double Value)> Values { get; } = new();
}

/// <summary>
/// Decodes a CDR payload against its definition and flattens it into named numbers, the shape
/// ATLAS wants. Rules (keep in sync with tools/make_cdr_goldens.py):
///   - a top-level std_msgs/Header becomes the sample time, not a parameter
///   - numbers become one value each; bools become 0/1
///   - builtin_interfaces Time/Duration become one value in seconds
///   - nested messages recurse: "parent.child"
///   - arrays up to maxArrayLength give one value per element, "name[i]"; longer arrays are skipped
///   - strings are skipped
/// </summary>
public static class MessageFlattener
{
    public const int DefaultMaxArrayLength = 32;

    private const string HeaderType = "std_msgs/msg/Header";
    private const string TimeType = "builtin_interfaces/msg/Time";
    private const string DurationType = "builtin_interfaces/msg/Duration";

    public static FlatMessage Flatten(
        MsgDefinition definition, byte[] buffer, int offset, int count, int maxArrayLength = DefaultMaxArrayLength)
    {
        var result = new FlatMessage();
        new Walker(new CdrReader(buffer, offset, count), result, maxArrayLength)
            .Struct(definition, prefix: "", top: true, emit: true);
        return result;
    }

    private sealed class Walker(CdrReader reader, FlatMessage result, int maxArrayLength)
    {
        public void Struct(MsgDefinition definition, string prefix, bool top, bool emit)
        {
            if (definition.Fields.Count == 0)
            {
                reader.ReadByte(); // ROS 2 gives field-less messages a one-byte placeholder member
                return;
            }

            foreach (var field in definition.Fields)
            {
                if (top && field is { Name: "header", TypeName: HeaderType, Array: ArrayKind.None })
                {
                    result.StampNs = this.HeaderStamp(field.Complex!);
                    continue;
                }

                var path = prefix + field.Name;
                if (field.Array == ArrayKind.None)
                {
                    this.Value(field, path, emit);
                    continue;
                }

                var count = field.Array == ArrayKind.Fixed ? field.FixedLength : checked((int)reader.ReadUInt32());
                var emitElements = emit && count <= maxArrayLength;
                if (!emitElements && field.Primitive is { } primitive &&
                    primitive is not (PrimitiveType.String or PrimitiveType.WString))
                {
                    reader.SkipPrimitives(primitive, count);
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    this.Value(field, $"{path}[{i}]", emitElements);
                }
            }
        }

        private void Value(MsgField field, string path, bool emit)
        {
            switch (field.Primitive)
            {
                case PrimitiveType.String:
                    reader.SkipString();
                    return;
                case PrimitiveType.WString:
                    reader.SkipWString();
                    return;
                case { } primitive:
                    var number = reader.ReadPrimitive(primitive);
                    if (emit)
                    {
                        result.Values.Add((path, number));
                    }

                    return;
            }

            var nested = field.Complex!;
            if (nested.FullName is TimeType or DurationType)
            {
                var sec = reader.ReadInt32();
                var nanosec = reader.ReadUInt32();
                if (emit)
                {
                    result.Values.Add((path, sec + nanosec * 1e-9));
                }

                return;
            }

            this.Struct(nested, path + ".", top: false, emit);
        }

        private long HeaderStamp(MsgDefinition header)
        {
            long stamp = 0;
            foreach (var field in header.Fields)
            {
                if (field is { Name: "stamp", TypeName: TimeType, Array: ArrayKind.None })
                {
                    var sec = reader.ReadInt32();
                    var nanosec = reader.ReadUInt32();
                    stamp = sec * 1_000_000_000L + nanosec;
                }
                else
                {
                    this.Value(field, field.Name, emit: false); // frame_id etc.: consume, don't emit
                }
            }

            return stamp;
        }
    }
}
