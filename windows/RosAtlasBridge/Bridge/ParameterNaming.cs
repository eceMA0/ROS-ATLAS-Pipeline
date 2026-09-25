using System.Text;

namespace RosAtlasBridge.Bridge;

public static class ParameterNaming
{
    /// <summary>
    /// ATLAS-safe name: letters, digits and single underscores.
    /// "/caninput/inputs" -> "caninput_inputs", "axis_percent[0]" -> "axis_percent_0",
    /// "pose.position.x" -> "pose_position_x".
    /// </summary>
    public static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                if (c != '_' || (builder.Length > 0 && builder[^1] != '_'))
                {
                    builder.Append(c);
                }
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().TrimEnd('_');
    }
}
