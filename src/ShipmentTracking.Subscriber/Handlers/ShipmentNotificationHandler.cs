using Amazon.Lambda.SNSEvents;
using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Extensions;
using ShipmentTracking.Common.Models;
using ShipmentTracking.Subscriber.Models;
using ShipmentTracking.Subscriber.Services;
using System.Text.Json;

namespace ShipmentTracking.Subscriber.Handlers;

/// <summary>
/// Handles SNS notification events for fulfillment order shipped events.
/// Processes multi-package shipments and orchestrates carrier subscriptions.
/// </summary>
public class ShipmentNotificationHandler
{
    private readonly SubscriptionService _subscriptionService;
    private readonly ILogger<ShipmentNotificationHandler> _logger;
    private readonly string _webhookBaseUrl;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShipmentNotificationHandler"/> class.
    /// </summary>
    public ShipmentNotificationHandler(
        SubscriptionService subscriptionService,
        ILogger<ShipmentNotificationHandler> logger,
        string webhookBaseUrl)
    {
        _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _webhookBaseUrl = webhookBaseUrl ?? throw new ArgumentNullException(nameof(webhookBaseUrl));
    }

    /// <summary>
    /// Processes an SNS event containing fulfillment order shipped notification.
    /// </summary>
    public async Task<HandlerResult> HandleAsync(
        SNSEvent snsEvent,
        CancellationToken cancellationToken = default)
    {
        if (snsEvent == null || snsEvent.Records == null || !snsEvent.Records.Any())
        {
            _logger.LogWarning("Received empty or invalid SNS event");
            return new HandlerResult
            {
                Success = false,
                ProcessedCount = 0,
                FailedCount = 0,
                Errors = new List<string> { "Empty or invalid SNS event" }
            };
        }

        var result = new HandlerResult
        {
            Success = true,
            ProcessedCount = 0,
            FailedCount = 0,
            Errors = new List<string>()
        };

        foreach (var record in snsEvent.Records)
        {
            try
            {
                var messageId = record.Sns.MessageId;
                _logger.LogInformation("Processing SNS message: {MessageId}", messageId);

                // Parse the SNS message
                var shippedEvent = ParseShippedEvent(record.Sns.Message);
                
                if (shippedEvent == null)
                {
                    _logger.LogWarning("Failed to parse SNS message: {MessageId}", messageId);
                    result.FailedCount++;
                    result.Errors.Add($"Failed to parse message {messageId}");
                    continue;
                }

                // Validate the event
                var validationErrors = ValidateEvent(shippedEvent);
                if (validationErrors.Any())
                {
                    _logger.LogWarning(
                        "Invalid shipped event: {MessageId}, Errors: {Errors}",
                        messageId,
                        string.Join(", ", validationErrors));
                    
                    result.FailedCount++;
                    result.Errors.AddRange(validationErrors);
                    continue;
                }

                // Process each package in the shipment
                foreach (var package in shippedEvent.Packages)
                {
                    var packageResult = await ProcessPackageAsync(shippedEvent, package, cancellationToken);
                    
                    if (packageResult.Success)
                    {
                        result.ProcessedCount++;
                    }
                    else
                    {
                        result.FailedCount++;
                        result.Errors.Add(packageResult.ErrorMessage ?? "Unknown error");
                        
                        // If subscription should be retried, mark overall result as needing retry
                        if (packageResult.ShouldRetry)
                        {
                            result.Success = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing SNS record");
                result.FailedCount++;
                result.Errors.Add($"Unexpected error: {ex.Message}");
                result.Success = false;
            }
        }

        return result;
    }

    private async Task<PackageProcessingResult> ProcessPackageAsync(
        FulfillmentOrderShippedEvent shippedEvent,
        ShipmentPackage package,
        CancellationToken cancellationToken)
    {
        var correlationId = $"{shippedEvent.FulfillmentOrderId}_{package.PackageId}";

        try
        {
            _logger.LogInformationWithCorrelation(
                $"Processing package subscription for carrier {package.Carrier}",
                correlationId,
                new Dictionary<string, object>
                {
                    ["TrackingNumber"] = package.TrackingNumber,
                    ["Carrier"] = package.Carrier
                });

            // Build callback URL with carrier identifier
            var callbackUrl = $"{_webhookBaseUrl}/webhook/{package.Carrier.ToLowerInvariant()}";

            // Build correlation data
            var correlationData = new SubscriptionCorrelationData
            {
                FulfillmentOrderId = shippedEvent.FulfillmentOrderId,
                OrderId = shippedEvent.OrderId,
                PackageId = package.PackageId,
                Metadata = new Dictionary<string, string>
                {
                    ["ServiceLevel"] = package.ServiceLevel ?? "Standard",
                    ["ShippedAt"] = shippedEvent.ShippedAt.ToString("O")
                }
            };

            // Merge event metadata if present
            if (shippedEvent.Metadata != null)
            {
                foreach (var kvp in shippedEvent.Metadata)
                {
                    correlationData.Metadata[$"Event_{kvp.Key}"] = kvp.Value;
                }
            }

            // Process subscription
            var subscriptionResult = await _subscriptionService.ProcessSubscriptionAsync(
                package.Carrier,
                package.TrackingNumber,
                callbackUrl,
                correlationData,
                cancellationToken);

            if (subscriptionResult.Success)
            {
                _logger.LogInformationWithCorrelation(
                    "Successfully processed package subscription",
                    correlationId,
                    new Dictionary<string, object>
                    {
                        ["SubscriptionId"] = subscriptionResult.SubscriptionId ?? "N/A",
                        ["AlreadyExists"] = subscriptionResult.AlreadyExists
                    });
            }
            else
            {
                _logger.LogWarningWithCorrelation(
                    $"Package subscription failed: {subscriptionResult.Reason}",
                    correlationId,
                    new Dictionary<string, object>
                    {
                        ["ShouldRetry"] = subscriptionResult.ShouldRetry,
                        ["ErrorType"] = subscriptionResult.ErrorType?.ToString() ?? "Unknown"
                    });
            }

            return new PackageProcessingResult
            {
                Success = subscriptionResult.Success,
                ErrorMessage = subscriptionResult.Success ? null : subscriptionResult.Reason,
                ShouldRetry = subscriptionResult.ShouldRetry
            };
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(
                ex,
                "Unexpected error processing package",
                correlationId);

            return new PackageProcessingResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ShouldRetry = true // Allow Lambda to retry unexpected errors
            };
        }
    }

    private FulfillmentOrderShippedEvent? ParseShippedEvent(string message)
    {
        try
        {
            return JsonSerializer.Deserialize<FulfillmentOrderShippedEvent>(message);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize SNS message");
            return null;
        }
    }

    private List<string> ValidateEvent(FulfillmentOrderShippedEvent shippedEvent)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(shippedEvent.FulfillmentOrderId))
        {
            errors.Add("FulfillmentOrderId is required");
        }

        if (string.IsNullOrWhiteSpace(shippedEvent.OrderId))
        {
            errors.Add("OrderId is required");
        }

        if (shippedEvent.Packages == null || !shippedEvent.Packages.Any())
        {
            errors.Add("At least one package is required");
        }
        else
        {
            for (int i = 0; i < shippedEvent.Packages.Count; i++)
            {
                var package = shippedEvent.Packages[i];

                if (string.IsNullOrWhiteSpace(package.PackageId))
                {
                    errors.Add($"Package[{i}].PackageId is required");
                }

                if (string.IsNullOrWhiteSpace(package.Carrier))
                {
                    errors.Add($"Package[{i}].Carrier is required");
                }

                if (string.IsNullOrWhiteSpace(package.TrackingNumber))
                {
                    errors.Add($"Package[{i}].TrackingNumber is required");
                }
            }
        }

        return errors;
    }
}

/// <summary>
/// Result of handling an SNS event.
/// </summary>
public class HandlerResult
{
    /// <summary>
    /// Indicates if all packages were processed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Number of packages successfully processed.
    /// </summary>
    public int ProcessedCount { get; set; }

    /// <summary>
    /// Number of packages that failed processing.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// List of error messages encountered.
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Result of processing a single package.
/// </summary>
internal class PackageProcessingResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool ShouldRetry { get; set; }
}
