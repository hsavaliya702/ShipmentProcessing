using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Extensions;
using ShipmentTracking.Subscriber.Carriers;
using ShipmentTracking.Subscriber.Data;
using ShipmentTracking.Subscriber.Models;

namespace ShipmentTracking.Subscriber.Services;

/// <summary>
/// Service that orchestrates carrier subscription operations.
/// Coordinates between carrier clients and the subscription repository.
/// </summary>
public class SubscriptionService
{
    private readonly CarrierSubscriptionClientFactory _clientFactory;
    private readonly SubscriptionRepository _repository;
    private readonly ILogger<SubscriptionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscriptionService"/> class.
    /// </summary>
    public SubscriptionService(
        CarrierSubscriptionClientFactory clientFactory,
        SubscriptionRepository repository,
        ILogger<SubscriptionService> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Processes a subscription request with idempotency and retry logic.
    /// Implements the pseudocode from the specification.
    /// </summary>
    public async Task<SubscriptionResult> ProcessSubscriptionAsync(
        string carrier,
        string trackingNumber,
        string callbackUrl,
        SubscriptionCorrelationData correlationData,
        CancellationToken cancellationToken = default)
    {
        var correlationId = $"{correlationData.FulfillmentOrderId}_{correlationData.PackageId}";

        using (_logger.TrackPerformance("ProcessSubscription", correlationId, new Dictionary<string, object>
        {
            ["Carrier"] = carrier,
            ["TrackingNumber"] = trackingNumber
        }))
        {
            try
            {
                // Step 1: Check if carrier is supported
                if (!_clientFactory.IsCarrierSupported(carrier))
                {
                    _logger.LogWarningWithCorrelation(
                        $"Unsupported carrier: {carrier}",
                        correlationId);

                    return new SubscriptionResult
                    {
                        Success = false,
                        Reason = $"Carrier '{carrier}' is not supported",
                        ShouldRetry = false
                    };
                }

                // Step 2: Check existing subscription (idempotency check)
                var existingRecord = await _repository.GetSubscriptionAsync(carrier, trackingNumber, cancellationToken);

                if (existingRecord != null)
                {
                    if (existingRecord.SubscriptionStatus == SubscriptionStatus.Active)
                    {
                        _logger.LogInformationWithCorrelation(
                            "Subscription already active, skipping",
                            correlationId,
                            new Dictionary<string, object>
                            {
                                ["CarrierSubscriptionId"] = existingRecord.CarrierSubscriptionId ?? "unknown"
                            });

                        return new SubscriptionResult
                        {
                            Success = true,
                            SubscriptionId = existingRecord.CarrierSubscriptionId,
                            Reason = "Subscription already active",
                            AlreadyExists = true
                        };
                    }

                    // Check if max attempts reached
                    if (await _repository.HasExceededMaxAttemptsAsync(carrier, trackingNumber, cancellationToken))
                    {
                        _logger.LogWarningWithCorrelation(
                            "Max retry attempts reached",
                            correlationId,
                            new Dictionary<string, object>
                            {
                                ["AttemptCount"] = existingRecord.AttemptCount
                            });

                        return new SubscriptionResult
                        {
                            Success = false,
                            Reason = "Max retry attempts exceeded",
                            ShouldRetry = false
                        };
                    }
                }

                // Step 3: Attempt subscription with carrier
                var client = _clientFactory.CreateClient(carrier);
                
                var request = new CarrierSubscriptionRequest
                {
                    TrackingNumber = trackingNumber,
                    CallbackUrl = callbackUrl,
                    CorrelationData = correlationData
                };

                await _repository.IncrementAttemptCountAsync(carrier, trackingNumber, cancellationToken);
                
                var response = await client.SubscribeAsync(request, cancellationToken);

                // Step 4: Handle response and update repository
                if (response.Success)
                {
                    var record = new SubscriptionRecord
                    {
                        Carrier = carrier,
                        TrackingNumber = trackingNumber,
                        FulfillmentOrderId = correlationData.FulfillmentOrderId,
                        OrderId = correlationData.OrderId,
                        PackageId = correlationData.PackageId,
                        SubscriptionStatus = SubscriptionStatus.Active,
                        CarrierSubscriptionId = response.SubscriptionId,
                        CallbackUrl = callbackUrl,
                        AttemptCount = existingRecord?.AttemptCount + 1 ?? 1,
                        ActivatedAt = DateTime.UtcNow,
                        Metadata = correlationData.Metadata
                    };

                    // Use conditional write to prevent race conditions
                    var saved = await _repository.CreateOrUpdateSubscriptionAsync(record, correlationId, cancellationToken);

                    if (!saved)
                    {
                        // Another process already created an active subscription
                        _logger.LogInformationWithCorrelation(
                            "Subscription was created by another process (race condition handled)",
                            correlationId);
                    }

                    return new SubscriptionResult
                    {
                        Success = true,
                        SubscriptionId = response.SubscriptionId,
                        Reason = "Successfully subscribed"
                    };
                }
                else
                {
                    // Subscription failed - update failure record
                    await UpdateFailureRecordAsync(
                        carrier,
                        trackingNumber,
                        correlationData,
                        callbackUrl,
                        response,
                        existingRecord,
                        correlationId,
                        cancellationToken);

                    var shouldRetry = response.ErrorType == ErrorType.Transient || response.ErrorType == ErrorType.RateLimit;

                    return new SubscriptionResult
                    {
                        Success = false,
                        Reason = response.ErrorMessage ?? "Unknown error",
                        ShouldRetry = shouldRetry,
                        ErrorType = response.ErrorType
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCorrelation(
                    ex,
                    "Unexpected error processing subscription",
                    correlationId);

                // For unexpected errors, allow Lambda to retry
                throw;
            }
        }
    }

    private async Task UpdateFailureRecordAsync(
        string carrier,
        string trackingNumber,
        SubscriptionCorrelationData correlationData,
        string callbackUrl,
        CarrierSubscriptionResponse response,
        SubscriptionRecord? existingRecord,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var record = new SubscriptionRecord
        {
            Carrier = carrier,
            TrackingNumber = trackingNumber,
            FulfillmentOrderId = correlationData.FulfillmentOrderId,
            OrderId = correlationData.OrderId,
            PackageId = correlationData.PackageId,
            SubscriptionStatus = SubscriptionStatus.Failed,
            CallbackUrl = callbackUrl,
            AttemptCount = existingRecord?.AttemptCount + 1 ?? 1,
            LastError = response.ErrorMessage,
            ErrorType = response.ErrorType,
            Metadata = correlationData.Metadata
        };

        try
        {
            // Don't use conditional write for failure records
            await _repository.CreateOrUpdateSubscriptionAsync(record, correlationId, cancellationToken);
            
            _logger.LogWarningWithCorrelation(
                $"Recorded subscription failure for {carrier}/{trackingNumber}",
                correlationId,
                new Dictionary<string, object>
                {
                    ["ErrorType"] = response.ErrorType.ToString(),
                    ["AttemptCount"] = record.AttemptCount
                });
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(
                ex,
                "Error recording subscription failure",
                correlationId);
            // Don't throw - this is a secondary operation
        }
    }
}

/// <summary>
/// Result of a subscription operation.
/// </summary>
public class SubscriptionResult
{
    /// <summary>
    /// Indicates if the subscription was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Carrier's subscription ID if successful.
    /// </summary>
    public string? SubscriptionId { get; set; }

    /// <summary>
    /// Human-readable reason for the result.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Indicates if the operation should be retried.
    /// </summary>
    public bool ShouldRetry { get; set; }

    /// <summary>
    /// Indicates if the subscription already existed.
    /// </summary>
    public bool AlreadyExists { get; set; }

    /// <summary>
    /// Error classification if failed.
    /// </summary>
    public ErrorType? ErrorType { get; set; }
}
