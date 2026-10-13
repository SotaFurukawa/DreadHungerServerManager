namespace DreadHungerServerManager.Core.Models;

public enum LogLineSource
{
    StandardOutput,
    StandardError,
    LogFile,
    Manager,
}

public enum LogLineKind
{
    Normal,
    Warning,
    Error,
    Success,
    Manager,
}

public sealed record ServerLogLine(DateTimeOffset Timestamp, string Text, LogLineSource Source, LogLineKind Kind);
