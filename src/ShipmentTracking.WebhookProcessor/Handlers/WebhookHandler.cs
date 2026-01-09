using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Extensions;
using ShipmentTracking.Common.Models;
using ShipmentTracking.WebhookProcessor.Carriers;
using ShipmentTracking.WebhookProcessor.Models;
using ShipmentTracking.WebhookProcessor.Services;
using ShipmentTracking.WebhookProcessor.Translation;
using ShipmentTracking.WebhookProcessor.Validation;

namespace ShipmentTracking.WebhookProcessor.Handlers;

/// <summary>
/// Main webhook processing handler implementing the Validation → Parse → Correlate → Translate → Publish flow.
/// </summary>
public class WebhookHandler
{
    private readonly Dictionary<string, IWebhookValidator> _validators;
    private readonly Dictionary<string, ICarrierPayloadParser> _parsers;
    private readonly CorrelationService _correlationService;
    private readonly StatusTranslationService _translationService;
    private readonly EventPublisher _eventPublisher;
    private readonly ILogger<WebhookHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookHandler"/> class.
    /// </summary>
    public WebhookHandler(
        IEnumerable<IWebhookValidator> validators,
        IEnumerable<ICarrierPayloadParser> parsers,
        CorrelationService correlationService,
        StatusTranslationService translationService,
        EventPublisher eventPublisher,
        ILogger<WebhookHandler> logger)
    {
        _validators = validators?.ToDictionary(v => v.CarrierCode, StringComparer.OrdinalIgnoreCase) 
            ?? throw new ArgumentNullException(nameof(validators));
        _parsers = parsers?.ToDictionary(p => p.CarrierCode, StringComparer.OrdinalIgnoreCase) 
            ?? throw new ArgumentNullException(nameof(parsers));
        _correlationService = correlationService ?? throw new ArgumentNullException(nameof(correlationService));
        _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Processes a webhook request through the complete pipeline.
    /// </summary>
    /// <param name="request">Webhook request containing carrier, payload, and headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Processing result with status code and message.</returns>
    public async Task<WebhookProcessingResult> ProcessWebhookAsync(
        WebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var correlationId = $"webhook_{request.Carrier}_{Guid.NewGuid():N}";

        try
        {
            using (_logger.TrackPerformance("ProcessWebhook", correlationId, new Dictionary<string, object>
            {
                ["Carrier"] = request.Carrier
            }))
            {
                // Step 1: Validate webhook signature
                var validationResult = await ValidateWebhookAsync(request, correlationId, cancellationToken);
                if (!validationResult.IsValid)
                {
                    return new WebhookProcessingResult
                    {
                        Success = false,
                        StatusCode = validationResult.StatusCode,
                        Message = validationResult.ErrorMessage ?? "Validation failed"
                    };
                }

                // Step 2: Parse carrier payload
                CarrierTrackingEvent carrierEvent;
                try
                {
                    carrierEvent = await ParsePayloadAsync(request, correlationId, cancellationToken);
                }
                catch (PayloadParsingException ex)
                {
                    _logger.LogWarningWithCorrelation(
                        $"Failed to parse webhook payload: {ex.Message}",
                        correlationId);
                    
                    return new WebhookProcessingResult
                    {
                        Success = false,
                        StatusCode = 400,
                        Message = $"Invalid payload format: {ex.Message}"
                    };
                }

                // Step 3: Retrieve correlation data
                var correlation = await _correlationService.GetCorrelationDataAsync(
                    request.Carrier,
                    carrierEvent.TrackingNumber,
                    cancellationToken);

                if (correlation == null)
                {
                    _logger.LogWarningWithCorrelation(
                        $"No subscription found for {request.Carrier}/{carrierEvent.TrackingNumber}",
                        correlationId);
                    
                    // Return 200 OK to acknowledge receipt but don't process further
                    return new WebhookProcessingResult
                    {
                        Success = true,
                        StatusCode = 200,
                        Message = "Webhook received but no matching subscription found"
                    };
                }

                // Update correlation ID with fulfillment order context
                correlationId = $"{correlation.FulfillmentOrderId}_{correlation.PackageId}";

                // Step 4: Translate to canonical status
                var canonicalStatus = _translationService.TranslateStatus(
                    request.Carrier,
                    carrierEvent.StatusCode,
                    carrierEvent.SubStatusCode);

                _logger.LogInformationWithCorrelation(
                    $"Translated {request.Carrier} status {carrierEvent.StatusCode} to {canonicalStatus}",
                    correlationId);

                // Step 5: Build canonical event
                var canonicalEvent = BuildCanonicalEvent(carrierEvent, correlation, canonicalStatus);

                // Step 6: Publish to SNS
                var messageId = await _eventPublisher.PublishAsync(canonicalEvent, correlationId, cancellationToken);

                _logger.LogInformationWithCorrelation(
                    "Successfully processed webhook and published event",
                    correlationId,
                    new Dictionary<string, object>
                    {
                        ["MessageId"] = messageId,
                        ["Status"] = canonicalStatus.ToString()
                    });

                return new WebhookProcessingResult
                {
                    Success = true,
                    StatusCode = 200,
                    Message = "Webhook processed successfully",
                    MessageId = messageId
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(
                ex,
                "Unexpected error processing webhook",
                correlationId);

            return new WebhookProcessingResult
            {
                Success = false,
                StatusCode = 500,
                Message = "Internal server error processing webhook"
            };
        }
    }

    private async Task<ValidationResult> ValidateWebhookAsync(
        WebhookRequest request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!_validators.TryGetValue(request.Carrier, out var validator))
        {
            _logger.LogWarningWithCorrelation(
                $"No validator found for carrier: {request.Carrier}",
                correlationId);
            
            return ValidationResult.Failure($"Unsupported carrier: {request.Carrier}", 400);
        }

        return await validator.ValidateAsync(request.Payload, request.Headers, cancellationToken);
    }

    private async Task<CarrierTrackingEvent> ParsePayloadAsync(
        WebhookRequest request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!_parsers.TryGetValue(request.Carrier, out var parser))
        {
            throw new PayloadParsingException($"No parser found for carrier: {request.Carrier}");
        }

        return await parser.ParseAsync(request.Payload, cancellationToken);
    }

    private ShipmentTrackingStatusEvent BuildCanonicalEvent(
        CarrierTrackingEvent carrierEvent,
        SubscriptionCorrelation correlation,
        TrackingStatus canonicalStatus)
    {
        var canonicalEvent = new ShipmentTrackingStatusEvent
        {
            FulfillmentOrderId = correlation.FulfillmentOrderId,
            OrderId = correlation.OrderId,
            PackageId = correlation.PackageId,
            Carrier = carrierEvent.Carrier,
            TrackingNumber = carrierEvent.TrackingNumber,
            Status = canonicalStatus,
            StatusDescription = carrierEvent.StatusDescription,
            EventTimestamp = carrierEvent.EventTimestamp,
            CarrierStatusCode = carrierEvent.StatusCode,
            CarrierSubStatusCode = carrierEvent.SubStatusCode,
            EstimatedDeliveryDate = carrierEvent.EstimatedDeliveryDate,
            ProcessedAt = DateTime.UtcNow
        };

        // Map location if available
        if (carrierEvent.Location != null)
        {
            canonicalEvent.Location = new TrackingLocation
            {
                City = carrierEvent.Location.City,
                State = carrierEvent.Location.State,
                PostalCode = carrierEvent.Location.PostalCode,
                Country = carrierEvent.Location.Country,
                FacilityName = carrierEvent.Location.FacilityName
            };
        }

        // Set delivered timestamp if status is Delivered
        if (canonicalStatus == TrackingStatus.Delivered)
        {
            canonicalEvent.DeliveredAt = carrierEvent.EventTimestamp;
        }

        // Merge metadata
        canonicalEvent.Metadata = new Dictionary<string, string>();
        
        if (correlation.Metadata != null)
        {
            foreach (var kvp in correlation.Metadata)
            {
                canonicalEvent.Metadata[kvp.Key] = kvp.Value;
            }
        }

        if (carrierEvent.AdditionalData != null)
        {
            foreach (var kvp in carrierEvent.AdditionalData)
            {
                canonicalEvent.Metadata[$"Carrier_{kvp.Key}"] = kvp.Value;
            }
        }

        return canonicalEvent;
    }
}

/// <summary>
/// Result of webhook processing.
/// </summary>
public class WebhookProcessingResult
{
    /// <summary>
    /// Indicates if processing was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// HTTP status code to return.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Message describing the result.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// SNS message ID if event was published.
    /// </summary>
    public string? MessageId { get; set; }
}
