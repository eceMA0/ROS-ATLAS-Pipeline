namespace RosAtlasBridge.Bridge;

public sealed record AtlasParameter(
    string Identifier, string Name, string Description, string Units, string FormatString, double Min, double Max);

/// <summary>One ROS topic as an ATLAS group: parameters in the column order every row uses.</summary>
public sealed record TopicLayout(
    string Topic, string TypeName, string ApplicationName, IReadOnlyList<AtlasParameter> Parameters, uint FrequencyHz);

/// <summary>Where bridged rows go. AtlasSession is the real one; tests use a fake.</summary>
public interface IAtlasSink
{
    /// <summary>Declares these topics in one configuration packet; returns each topic's data format id, in order.</summary>
    IReadOnlyList<ulong> DefineTopics(IReadOnlyList<TopicLayout> layouts);

    /// <summary>Writes rows for one topic. NaN values are sent as missing samples.</summary>
    void WriteRows(ulong dataFormatId, IReadOnlyList<ulong> timestampsNs, IReadOnlyList<double[]> rows);
}
