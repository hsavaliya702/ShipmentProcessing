using System.Text.Json.Serialization;

namespace ShipmentTracking.WebhookProcessor.Models;

/// <summary>
/// Generic webhook request model.
/// </summary>
public class WebhookRequest
{
    /// <summary>
    /// Carrier identifier.
    /// </summary>
    public string Carrier { get; set; } = string.Empty;

    /// <summary>
    /// Raw webhook payload from carrier.
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Request headers for validation.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Timestamp when webhook was received.
    /// </summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Parsed tracking event from carrier webhook.
/// </summary>
public class CarrierTrackingEvent
{
    /// <summary>
    /// Carrier identifier.
    /// </summary>
    public string Carrier { get; set; } = string.Empty;

    /// <summary>
    /// Tracking number.
    /// </summary>
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>
    /// Carrier-specific status code.
    /// </summary>
    public string StatusCode { get; set; } = string.Empty;

    /// <summary>
    /// Carrier-specific sub-status code (optional).
    /// </summary>
    public string? SubStatusCode { get; set; }

    /// <summary>
    /// Status description from carrier.
    /// </summary>
    public string StatusDescription { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the tracking event.
    /// </summary>
    public DateTime EventTimestamp { get; set; }

    /// <summary>
    /// Location information if available.
    /// </summary>
    public EventLocation? Location { get; set; }

    /// <summary>
    /// Estimated delivery date if available.
    /// </summary>
    public DateTime? EstimatedDeliveryDate { get; set; }

    /// <summary>
    /// Additional carrier-specific data.
    /// </summary>
    public Dictionary<string, string>? AdditionalData { get; set; }
}

/// <summary>
/// Location information from tracking event.
/// </summary>
public class EventLocation
{
    /// <summary>
    /// City name.
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    /// State or province.
    /// </summary>
    public string? State { get; set; }

    /// <summary>
    /// Postal/ZIP code.
    /// </summary>
    public string? PostalCode { get; set; }

    /// <summary>
    /// Country code.
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// Facility name.
    /// </summary>
    public string? FacilityName { get; set; }
}

/// <summary>
/// Status translation mapping configuration.
/// </summary>
public class StatusMapping
{
    /// <summary>
    /// Carrier identifier.
    /// </summary>
    public string Carrier { get; set; } = string.Empty;

    /// <summary>
    /// Carrier status code.
    /// </summary>
    public string CarrierStatusCode { get; set; } = string.Empty;

    /// <summary>
    /// Carrier sub-status code (optional, for more specific mapping).
    /// </summary>
    public string? CarrierSubStatusCode { get; set; }

    /// <summary>
    /// Canonical status to map to.
    /// </summary>
    public string CanonicalStatus { get; set; } = string.Empty;

    /// <summary>
    /// Priority for matching (higher = checked first).
    /// </summary>
    public int Priority { get; set; }
}

/// <summary>
/// Correlation data retrieved from DynamoDB.
/// </summary>
public class SubscriptionCorrelation
{
    /// <summary>
    /// Fulfillment order ID.
    /// </summary>
    public string FulfillmentOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Original order ID.
    /// </summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>
    /// Package ID.
    /// </summary>
    public string PackageId { get; set; } = string.Empty;

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; set; }
}
