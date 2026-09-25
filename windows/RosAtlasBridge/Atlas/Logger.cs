using MA.DataPlatforms.Streaming.Support.Lib.Core.Shared.Abstractions;

namespace RosAtlasBridge.Atlas;

/// <summary>Console logger for the Support Library (adapted from FormulaStudent-Atlas-Example).</summary>
internal sealed class Logger(LoggingLevel loggingLevel) : ILogger
{
    public void Debug(string message) => this.Write(LoggingLevel.Debug, "DEBUG", message);

    public void Error(string message) => this.Write(LoggingLevel.Error, "ERROR", message);

    public void Info(string message) => this.Write(LoggingLevel.Info, "INFO", message);

    public void Warning(string message) => this.Write(LoggingLevel.Warning, "WARNING", message);

    public void Trace(string message) => this.Write(LoggingLevel.Trace, "TRACE", message);

    private void Write(LoggingLevel level, string label, string message)
    {
        if (loggingLevel >= level)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} ATLAS {label}: {message}");
        }
    }
}
