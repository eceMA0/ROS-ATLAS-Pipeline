using System.Security.Cryptography;

using Google.Protobuf;

using MA.DataPlatforms.Streaming.Support.Lib.Core.Abstractions;
using MA.DataPlatforms.Streaming.Support.Lib.Core.Contracts;
using MA.DataPlatforms.Streaming.Support.Lib.Core.Contracts.DataFormatInfoModule;
using MA.DataPlatforms.Streaming.Support.Lib.Core.Contracts.SessionInfoModule;
using MA.DataPlatforms.Streaming.Support.Lib.Core.Contracts.SessionInfoModule.Abstractions;
using MA.DataPlatforms.Streaming.Support.Lib.Core.Contracts.WritingModule;
using MA.DataPlatforms.Streaming.Support.Lib.Core.Shared.Abstractions;
using MA.Streaming.Abstraction;
using MA.Streaming.API;
using MA.Streaming.Core.Configs;
using MA.Streaming.OpenData;

using RosAtlasBridge.Bridge;

namespace RosAtlasBridge.Atlas;

/// <summary>
/// One ATLAS session for the bridge's lifetime, written through the Support Library in the same
/// sequence as FormulaStudent-Atlas-Example's MockDataWriter: NewSession, a Configuration packet
/// per DefineTopics call, PeriodicData packets, then EndOfSession + EndSession on dispose.
/// Each ROS message becomes one single-sample PeriodicData packet whose start time is the
/// message's own timestamp, so irregular ROS timing is kept.
/// </summary>
public sealed class AtlasSession : IAtlasSink, IDisposable
{
    private readonly BridgeConfig config;
    private readonly ILogger logger;
    private readonly ISupportLibApi supportLibApi;
    private readonly IPacketWriterService packetWriter;
    private readonly ISessionManagementService sessionManagement;
    private readonly IDataFormatManagementService dataFormatManagement;
    private readonly ISessionInfo session;
    private readonly PacketIdGenerator packetIds = new();
    private readonly object writeGate = new(); // packet ids + writes from the receive loop and the flush timer
    private readonly Dictionary<ulong, int[]> columnOrders = new();
    private readonly Dictionary<ulong, uint> intervalsNs = new();
    private int configurationCount;
    private bool disposed;

    public AtlasSession(BridgeConfig config, ILogger logger)
    {
        this.config = config;
        this.logger = logger;

        var streamApiConfig = new StreamingApiConfiguration(StreamCreationStrategy.TopicBased, config.KafkaBroker, []);
        this.supportLibApi = new SupportLibApiFactory().Create(logger, streamApiConfig);
        this.supportLibApi.Initialise();
        this.supportLibApi.Start();

        var writerResult = (this.supportLibApi.GetWritingPacketApi()
            ?? throw new InvalidOperationException("writing packet API unavailable")).CreateService();
        this.packetWriter = writerResult.Success && writerResult.Data is not null
            ? writerResult.Data
            : throw new InvalidOperationException($"packet writer: {writerResult.Message}");
        this.packetWriter.Initialise();
        this.packetWriter.Start();

        var sessionResult = (this.supportLibApi.GetSessionManagerApi()
            ?? throw new InvalidOperationException("session manager API unavailable")).CreateService();
        this.sessionManagement = sessionResult.Success && sessionResult.Data is not null
            ? sessionResult.Data
            : throw new InvalidOperationException($"session manager: {sessionResult.Message}");
        this.sessionManagement.Initialise();
        this.sessionManagement.Start();

        var formatResult = (this.supportLibApi.GetDataFormatManagerApi()
            ?? throw new InvalidOperationException("data format manager API unavailable")).CreateService();
        this.dataFormatManagement = formatResult.Success && formatResult.Data is not null
            ? formatResult.Data
            : throw new InvalidOperationException($"data format manager: {formatResult.Message}");
        this.dataFormatManagement.Initialise();
        this.dataFormatManagement.Start();

        var identifier = $"{config.SessionName} {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        var created = this.sessionManagement.CreateNewSession(new SessionCreationDto(
            dataSource: config.DataSource,
            identifier: identifier,
            type: "Session",
            version: 1,
            utcOffset: DateTimeOffset.Now.Offset));
        this.session = created.Success && created.Data is not null
            ? created.Data
            : throw new InvalidOperationException($"couldn't create the ATLAS session: {created.Message}");

        var newSession = new NewSessionPacket
        {
            DataSource = this.session.DataSource,
            UtcOffset = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(this.session.UtcOffset),
            SessionInfo = new SessionInfoPacket
            {
                Identifier = this.session.Identifier,
                Type = this.session.Type,
                Version = this.session.Version,
            },
        };
        lock (this.writeGate)
        {
            this.WriteToStreams("NewSession", newSession.ToByteString(), essential: false);
        }

        logger.Info($"ATLAS session '{identifier}' started (key {this.session.SessionKey})");
    }

    public IReadOnlyList<ulong> DefineTopics(IReadOnlyList<TopicLayout> layouts)
    {
        var configuration = new ConfigurationPacket();
        foreach (var layout in layouts)
        {
            configuration.GroupDefinitions.Add(new GroupDefinition
            {
                Identifier = layout.ApplicationName,
                Name = layout.ApplicationName,
                ApplicationName = layout.ApplicationName,
                Description = $"{layout.Topic} ({layout.TypeName})",
            });
            foreach (var parameter in layout.Parameters)
            {
                configuration.ParameterDefinitions.Add(new ParameterDefinition
                {
                    Identifier = parameter.Identifier,
                    ApplicationName = layout.ApplicationName,
                    Name = parameter.Name,
                    Description = parameter.Description,
                    Units = parameter.Units,
                    FormatString = parameter.FormatString,
                    DataType = DataType.Float64,
                    MinValue = parameter.Min,
                    MaxValue = parameter.Max,
                    WarningMinValue = parameter.Min,
                    WarningMaxValue = parameter.Max,
                    Frequencies = { layout.FrequencyHz },
                    IncludesSynchroData = false,
                    IncludesRowData = false,
                });
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(configuration.ToByteArray()))[..16];
        configuration.ConfigId = $"{this.config.SessionName}-{++this.configurationCount}-{hash}";

        lock (this.writeGate)
        {
            if (!this.WriteToStreams("Configuration", configuration.ToByteString(), essential: true))
            {
                throw new InvalidOperationException("writing the configuration packet failed");
            }
        }

        var formatIds = new List<ulong>(layouts.Count);
        foreach (var layout in layouts)
        {
            var identifiers = layout.Parameters.Select(p => p.Identifier).ToList();
            var result = this.dataFormatManagement.GetParameterDataFormatId(this.config.DataSource, identifiers);
            if (!result.Success || result.Data is null)
            {
                throw new InvalidOperationException($"no data format id for {layout.Topic}: {result.Message}");
            }

            // Samples must follow the format's registered parameter order, which may differ from ours.
            var columnOrder = result.Data.ParameterList.Select(id => identifiers.IndexOf(id)).ToArray();
            if (columnOrder.Contains(-1))
            {
                throw new InvalidOperationException($"data format for {layout.Topic} doesn't match its parameters");
            }

            lock (this.writeGate)
            {
                this.columnOrders[result.Data.DataFormatId] = columnOrder;
                this.intervalsNs[result.Data.DataFormatId] = (uint)(1e9 / Math.Max(1u, layout.FrequencyHz));
            }

            formatIds.Add(result.Data.DataFormatId);
            this.logger.Info($"Defined {layout.Topic}: {identifiers.Count} parameters in group '{layout.ApplicationName}' at ~{layout.FrequencyHz} Hz");
        }

        return formatIds;
    }

    public void WriteRows(ulong dataFormatId, IReadOnlyList<ulong> timestampsNs, IReadOnlyList<double[]> rows)
    {
        lock (this.writeGate)
        {
            if (this.disposed)
            {
                return;
            }

            var columns = this.columnOrders[dataFormatId];
            var interval = this.intervalsNs[dataFormatId];
            for (var i = 0; i < rows.Count; i++)
            {
                var packet = new PeriodicDataPacket
                {
                    DataFormat = new SampleDataFormat { DataFormatIdentifier = dataFormatId },
                    StartTime = timestampsNs[i],
                    Interval = interval,
                };
                foreach (var column in columns)
                {
                    packet.Columns.Add(new SampleColumn { DoubleSamples = new DoubleSampleList { Samples = { Sample(rows[i][column]) } } });
                }

                this.WriteToStreams("PeriodicData", packet.ToByteString(), essential: false);
            }
        }
    }

    public void Dispose()
    {
        lock (this.writeGate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.WriteToStreams("EndOfSession", new EndOfSessionPacket { DataSource = this.config.DataSource }.ToByteString(), essential: false);
            var ended = this.sessionManagement.EndSession(this.config.DataSource, this.session.SessionKey);
            this.logger.Info(ended.Success ? "ATLAS session ended" : $"Ending the ATLAS session failed: {ended.Message}");
        }

        // Reverse of start order.
        this.dataFormatManagement.Stop();
        this.sessionManagement.Stop();
        this.packetWriter.Stop();
        this.supportLibApi.Stop();
    }

    /// <summary>NaN (a field this message lacked) becomes a Missing sample.</summary>
    private static DoubleSample Sample(double value) => double.IsNaN(value)
        ? new DoubleSample { Value = 0, Status = DataStatus.Missing }
        : new DoubleSample { Value = value, Status = DataStatus.Valid };

    private bool WriteToStreams(string type, ByteString content, bool essential)
    {
        var success = true;
        foreach (var stream in this.config.Streams)
        {
            var packet = new Packet
            {
                Type = type,
                SessionKey = this.session.SessionKey,
                IsEssential = essential,
                Content = content,
                Id = this.packetIds.GetPacketId(),
            };
            success &= this.packetWriter.WriteData(this.config.DataSource, stream, this.session.SessionKey, packet).Success;
        }

        return success;
    }
}
