using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ShipmentTracking.Common.Extensions;

/// <summary>
/// Extension methods for structured logging with correlation IDs and performance tracking.
/// </summary>
public static class LoggingExtensions
{
    private const string CorrelationIdKey = "CorrelationId";
    private const string OperationKey = "Operation";
    private const string DurationMsKey = "DurationMs";
    private const string CarrierKey = "Carrier";
    private const string TrackingNumberKey = "TrackingNumber";

    /// <summary>
    /// Logs a message with correlation ID and additional structured properties.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="logLevel">The log level.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Correlation identifier for distributed tracing.</param>
    /// <param name="properties">Additional structured properties.</param>
    public static void LogWithCorrelation(
        this ILogger logger,
        LogLevel logLevel,
        string message,
        string correlationId,
        Dictionary<string, object>? properties = null)
    {
        var state = new Dictionary<string, object>
        {
            [CorrelationIdKey] = correlationId
        };

        if (properties != null)
        {
            foreach (var kvp in properties)
            {
                state[kvp.Key] = kvp.Value;
            }
        }

        logger.Log(logLevel, new EventId(), state, null, (s, ex) => message);
    }

    /// <summary>
    /// Logs an informational message with correlation ID.
    /// </summary>
    public static void LogInformationWithCorrelation(
        this ILogger logger,
        string message,
        string correlationId,
        Dictionary<string, object>? properties = null)
    {
        logger.LogWithCorrelation(LogLevel.Information, message, correlationId, properties);
    }

    /// <summary>
    /// Logs a warning message with correlation ID.
    /// </summary>
    public static void LogWarningWithCorrelation(
        this ILogger logger,
        string message,
        string correlationId,
        Dictionary<string, object>? properties = null)
    {
        logger.LogWithCorrelation(LogLevel.Warning, message, correlationId, properties);
    }

    /// <summary>
    /// Logs an error message with correlation ID and exception.
    /// </summary>
    public static void LogErrorWithCorrelation(
        this ILogger logger,
        Exception exception,
        string message,
        string correlationId,
        Dictionary<string, object>? properties = null)
    {
        var state = new Dictionary<string, object>
        {
            [CorrelationIdKey] = correlationId,
            ["ExceptionType"] = exception.GetType().Name,
            ["ExceptionMessage"] = exception.Message
        };

        if (properties != null)
        {
            foreach (var kvp in properties)
            {
                state[kvp.Key] = kvp.Value;
            }
        }

        logger.Log(LogLevel.Error, new EventId(), state, exception, (s, ex) => message);
    }

    /// <summary>
    /// Logs the start of an operation with correlation ID.
    /// </summary>
    public static void LogOperationStart(
        this ILogger logger,
        string operationName,
        string correlationId,
        Dictionary<string, object>? properties = null)
    {
        var allProperties = new Dictionary<string, object>
        {
            [OperationKey] = operationName
        };

        if (properties != null)
        {
            foreach (var kvp in properties)
            {
                allProperties[kvp.Key] = kvp.Value;
            }
        }

        logger.LogInformationWithCorrelation(
            $"Starting operation: {operationName}",
            correlationId,
            allProperties);
    }

    /// <summary>
    /// Logs the completion of an operation with correlation ID and duration.
    /// </summary>
    public static void LogOperationComplete(
        this ILogger logger,
        string operationName,
        string correlationId,
        long durationMs,
        Dictionary<string, object>? properties = null)
    {
        var allProperties = new Dictionary<string, object>
        {
            [OperationKey] = operationName,
            [DurationMsKey] = durationMs
        };

        if (properties != null)
        {
            foreach (var kvp in properties)
            {
                allProperties[kvp.Key] = kvp.Value;
            }
        }

        logger.LogInformationWithCorrelation(
            $"Completed operation: {operationName} in {durationMs}ms",
            correlationId,
            allProperties);
    }

    /// <summary>
    /// Logs a carrier-specific operation with tracking information.
    /// </summary>
    public static void LogCarrierOperation(
        this ILogger logger,
        LogLevel logLevel,
        string message,
        string correlationId,
        string carrier,
        string trackingNumber,
        Dictionary<string, object>? properties = null)
    {
        var allProperties = new Dictionary<string, object>
        {
            [CarrierKey] = carrier,
            [TrackingNumberKey] = MaskTrackingNumber(trackingNumber)
        };

        if (properties != null)
        {
            foreach (var kvp in properties)
            {
                allProperties[kvp.Key] = kvp.Value;
            }
        }

        logger.LogWithCorrelation(logLevel, message, correlationId, allProperties);
    }

    /// <summary>
    /// Creates a performance logger that automatically tracks operation duration.
    /// Usage: using (logger.TrackPerformance("OperationName", correlationId)) { ... }
    /// </summary>
    public static IDisposable TrackPerformance(
        this ILogger logger,
        string operationName,
        string correlationId,
        Dictionary<string, object>? properties = null)
    {
        return new PerformanceTracker(logger, operationName, correlationId, properties);
    }

    /// <summary>
    /// Masks sensitive parts of a tracking number for logging.
    /// Shows first 4 and last 4 characters, masks the middle.
    /// </summary>
    private static string MaskTrackingNumber(string trackingNumber)
    {
        if (string.IsNullOrEmpty(trackingNumber) || trackingNumber.Length <= 8)
        {
            return "****";
        }

        var prefix = trackingNumber.Substring(0, 4);
        var suffix = trackingNumber.Substring(trackingNumber.Length - 4);
        var maskedLength = trackingNumber.Length - 8;

        return $"{prefix}{new string('*', maskedLength)}{suffix}";
    }

    /// <summary>
    /// Performance tracking disposable wrapper.
    /// </summary>
    private class PerformanceTracker : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _operationName;
        private readonly string _correlationId;
        private readonly Dictionary<string, object>? _properties;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        public PerformanceTracker(
            ILogger logger,
            string operationName,
            string correlationId,
            Dictionary<string, object>? properties)
        {
            _logger = logger;
            _operationName = operationName;
            _correlationId = correlationId;
            _properties = properties;
            _stopwatch = Stopwatch.StartNew();

            _logger.LogOperationStart(_operationName, _correlationId, _properties);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _stopwatch.Stop();
                _logger.LogOperationComplete(
                    _operationName,
                    _correlationId,
                    _stopwatch.ElapsedMilliseconds,
                    _properties);

                _disposed = true;
            }
        }
    }
}

/// <summary>
/// Extension methods for masking sensitive data in logs.
/// </summary>
public static class DataMaskingExtensions
{
    /// <summary>
    /// Masks an email address for logging (keeps first character and domain).
    /// </summary>
    public static string MaskEmail(this string email)
    {
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            return "****";
        }

        var parts = email.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0)
        {
            return "****";
        }

        return $"{parts[0][0]}***@{parts[1]}";
    }

    /// <summary>
    /// Masks a phone number for logging (keeps last 4 digits).
    /// </summary>
    public static string MaskPhoneNumber(this string phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber) || phoneNumber.Length <= 4)
        {
            return "****";
        }

        return $"***-***-{phoneNumber.Substring(phoneNumber.Length - 4)}";
    }

    /// <summary>
    /// Masks a credit card number for logging (keeps last 4 digits).
    /// </summary>
    public static string MaskCreditCard(this string cardNumber)
    {
        if (string.IsNullOrEmpty(cardNumber) || cardNumber.Length <= 4)
        {
            return "****";
        }

        return $"****-****-****-{cardNumber.Substring(cardNumber.Length - 4)}";
    }
}
