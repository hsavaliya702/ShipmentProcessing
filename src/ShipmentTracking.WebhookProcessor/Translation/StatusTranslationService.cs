using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Models;
using ShipmentTracking.WebhookProcessor.Models;

namespace ShipmentTracking.WebhookProcessor.Translation;

/// <summary>
/// Service for translating carrier-specific status codes to canonical status values.
/// Implements configurable mapping with fallback logic.
/// </summary>
public class StatusTranslationService
{
    private readonly ILogger<StatusTranslationService> _logger;
    private readonly Dictionary<string, Dictionary<string, TrackingStatus>> _statusMappings;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusTranslationService"/> class.
    /// </summary>
    public StatusTranslationService(ILogger<StatusTranslationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _statusMappings = InitializeStatusMappings();
    }

    /// <summary>
    /// Translates a carrier status code to canonical tracking status.
    /// </summary>
    /// <param name="carrier">Carrier identifier.</param>
    /// <param name="statusCode">Carrier-specific status code.</param>
    /// <param name="subStatusCode">Carrier-specific sub-status code (optional).</param>
    /// <returns>Canonical tracking status.</returns>
    public TrackingStatus TranslateStatus(string carrier, string statusCode, string? subStatusCode = null)
    {
        if (string.IsNullOrWhiteSpace(carrier))
        {
            throw new ArgumentException("Carrier cannot be null or empty", nameof(carrier));
        }

        if (string.IsNullOrWhiteSpace(statusCode))
        {
            throw new ArgumentException("Status code cannot be null or empty", nameof(statusCode));
        }

        var carrierUpper = carrier.ToUpperInvariant();

        // Get carrier-specific mappings
        if (!_statusMappings.TryGetValue(carrierUpper, out var carrierMappings))
        {
            _logger.LogWarning("No status mappings found for carrier: {Carrier}", carrier);
            return TrackingStatus.Unknown;
        }

        // Try composite key first (statusCode:subStatusCode)
        if (!string.IsNullOrWhiteSpace(subStatusCode))
        {
            var compositeKey = $"{statusCode}:{subStatusCode}";
            if (carrierMappings.TryGetValue(compositeKey, out var compositeStatus))
            {
                _logger.LogDebug(
                    "Translated {Carrier} status {CompositeKey} to {CanonicalStatus}",
                    carrier, compositeKey, compositeStatus);
                return compositeStatus;
            }
        }

        // Fallback to status code only
        if (carrierMappings.TryGetValue(statusCode, out var status))
        {
            _logger.LogDebug(
                "Translated {Carrier} status {StatusCode} to {CanonicalStatus}",
                carrier, statusCode, status);
            return status;
        }

        // No mapping found
        _logger.LogWarning(
            "Unknown status code for {Carrier}: {StatusCode}:{SubStatusCode}",
            carrier, statusCode, subStatusCode ?? "null");
        
        return TrackingStatus.Unknown;
    }

    /// <summary>
    /// Initializes default status mappings for supported carriers.
    /// In production, these could be loaded from configuration or database.
    /// </summary>
    private Dictionary<string, Dictionary<string, TrackingStatus>> InitializeStatusMappings()
    {
        return new Dictionary<string, Dictionary<string, TrackingStatus>>(StringComparer.OrdinalIgnoreCase)
        {
            ["USPS"] = new Dictionary<string, TrackingStatus>(StringComparer.OrdinalIgnoreCase)
            {
                // Pre-Transit
                ["01"] = TrackingStatus.PreTransit,
                ["02"] = TrackingStatus.PreTransit,
                ["ACCEPTED"] = TrackingStatus.PreTransit,
                ["ELECTRONIC_SHIPPING_INFO_RECEIVED"] = TrackingStatus.PreTransit,
                
                // In Transit
                ["03"] = TrackingStatus.InTransit,
                ["07"] = TrackingStatus.InTransit,
                ["10"] = TrackingStatus.InTransit,
                ["IN_TRANSIT"] = TrackingStatus.InTransit,
                ["ARRIVED_AT_FACILITY"] = TrackingStatus.InTransit,
                ["DEPARTED_FACILITY"] = TrackingStatus.InTransit,
                ["PROCESSED_AT_FACILITY"] = TrackingStatus.InTransit,
                
                // Out for Delivery
                ["04"] = TrackingStatus.OutForDelivery,
                ["OUT_FOR_DELIVERY"] = TrackingStatus.OutForDelivery,
                
                // Delivered
                ["05"] = TrackingStatus.Delivered,
                ["DELIVERED"] = TrackingStatus.Delivered,
                
                // Delivery Attempt Failed
                ["06"] = TrackingStatus.DeliveryAttemptFailed,
                ["NOTICE_LEFT"] = TrackingStatus.DeliveryAttemptFailed,
                ["DELIVERY_ATTEMPTED"] = TrackingStatus.DeliveryAttemptFailed,
                
                // Available for Pickup
                ["08"] = TrackingStatus.AvailableForPickup,
                ["AVAILABLE_FOR_PICKUP"] = TrackingStatus.AvailableForPickup,
                
                // Return to Sender
                ["09"] = TrackingStatus.Returning,
                ["RETURN_TO_SENDER"] = TrackingStatus.Returning,
                ["RETURNED_TO_SENDER"] = TrackingStatus.Returned,
                
                // On Hold / Exception
                ["11"] = TrackingStatus.OnHold,
                ["ALERT"] = TrackingStatus.Exception,
                ["EXCEPTION"] = TrackingStatus.Exception,
                ["HELD_AT_FACILITY"] = TrackingStatus.OnHold,
                
                // Cancelled
                ["CANCELLED"] = TrackingStatus.Cancelled,
                ["VOIDED"] = TrackingStatus.Cancelled
            }
            
            // Future carriers will be added here when implementations are ready:
            // ["UPS"] = new Dictionary<string, TrackingStatus>() { ... }
            // ["FEDEX"] = new Dictionary<string, TrackingStatus>() { ... }
        };
    }

    /// <summary>
    /// Gets all supported carriers for status translation.
    /// </summary>
    public IEnumerable<string> GetSupportedCarriers()
    {
        return _statusMappings.Keys;
    }

    /// <summary>
    /// Checks if a carrier is supported for status translation.
    /// </summary>
    public bool IsCarrierSupported(string carrier)
    {
        return _statusMappings.ContainsKey(carrier.ToUpperInvariant());
    }
}
