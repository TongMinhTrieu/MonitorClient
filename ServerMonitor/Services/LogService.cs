using Serilog.Context;
using System.Runtime.CompilerServices;
using LogLevel = ServerMonitor.Enum.LogLevel;

namespace ServerMonitor.Services;

public class LogService : ILogService
{
    private readonly ILogger<LogService> logger;

    public LogService(ILogger<LogService> logger)
    {
        this.logger = logger;
    }

    public void Logging(LogLevel level, string message, string caller, [CallerMemberName] string method = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(caller)) caller = "UnknownCaller";
            if (string.IsNullOrWhiteSpace(method)) method = "UnknownMethod";
            LogContext.PushProperty("Caller", $"({caller}.{method})");
            switch (level)
            {
                case LogLevel.Debug:
                    logger.LogDebug(" {message}", message);
                    break;
                case LogLevel.Information:
                    logger.LogInformation(" {message}", message);
                    break;
                case LogLevel.Warning:
                    logger.LogWarning(" {message}", message);
                    break;
                case LogLevel.Error:
                    logger.LogError(" {message}", message);
                    break;
                case LogLevel.Fatal:
                    logger.LogCritical(" {message}", message);
                    break;
                default:
                    break;
            }
        }
        catch
        {

        }
    }
}

public interface ILogService
{
    void Logging(LogLevel level, string message, string caller, [CallerMemberName] string method = "");
}
