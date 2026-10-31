namespace DriveMonitor.Core;

/// <summary>Monitoring could not start; <see cref="Exception.Message"/> is suitable for showing to the user.</summary>
public sealed class MonitorStartException(string message, Exception? innerException = null)
    : Exception(message, innerException);
