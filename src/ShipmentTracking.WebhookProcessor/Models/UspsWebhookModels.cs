using System.Text.Json.Serialization;

namespace ShipmentTracking.WebhookProcessor.Models;

/// <summary>
/// USPS webhook notification envelope per API v3 specification.
/// This is the outer structure sent by USPS containing metadata and the actual tracking payload.
/// </summary>
public class UspsWebhookNotification
{
    /// <summary>
    /// Unique subscription identifier.
    /// </summary>
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>
    /// Type of subscription (e.g., "TRACKING").
    /// </summary>
    [JsonPropertyName("subscriptionType")]
    public string? SubscriptionType { get; set; }

    /// <summary>
    /// Timestamp when the webhook was generated (ISO 8601 format).
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>
    /// The actual tracking data as an escaped JSON string.
    /// This needs to be deserialized a second time to get the tracking data.
    /// </summary>
    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;
}

/// <summary>
/// USPS tracking data contained within the webhook payload.
/// This is the result of deserializing the escaped JSON in the payload field.
/// </summary>
public class UspsTrackingData
{
    /// <summary>
    /// Tracking number.
    /// </summary>
    [JsonPropertyName("trackingNumber")]
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>
    /// Overall status category (e.g., "Delivered", "In Transit").
    /// </summary>
    [JsonPropertyName("statusCategory")]
    public string? StatusCategory { get; set; }

    /// <summary>
    /// Human-readable status summary.
    /// </summary>
    [JsonPropertyName("statusSummary")]
    public string? StatusSummary { get; set; }

    /// <summary>
    /// Mail class (e.g., "Priority Mail", "First-Class Package").
    /// </summary>
    [JsonPropertyName("mailClass")]
    public string? MailClass { get; set; }

    /// <summary>
    /// Service type code.
    /// </summary>
    [JsonPropertyName("serviceTypeCode")]
    public string? ServiceTypeCode { get; set; }

    /// <summary>
    /// Destination ZIP code.
    /// </summary>
    [JsonPropertyName("destinationZIPCode")]
    public string? DestinationZIPCode { get; set; }

    /// <summary>
    /// Array of tracking events. May contain multiple events.
    /// </summary>
    [JsonPropertyName("trackingEvents")]
    public List<UspsTrackingEvent> TrackingEvents { get; set; } = new();
}

/// <summary>
/// Individual USPS tracking event.
/// </summary>
public class UspsTrackingEvent
{
    /// <summary>
    /// Event code (carrier-specific status code).
    /// </summary>
    [JsonPropertyName("eventCode")]
    public string EventCode { get; set; } = string.Empty;

    /// <summary>
    /// Event type/description.
    /// </summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the event occurred (ISO 8601 format).
    /// </summary>
    [JsonPropertyName("eventTimestamp")]
    public string EventTimestamp { get; set; } = string.Empty;

    /// <summary>
    /// City where the event occurred.
    /// </summary>
    [JsonPropertyName("eventCity")]
    public string? EventCity { get; set; }

    /// <summary>
    /// State where the event occurred.
    /// </summary>
    [JsonPropertyName("eventState")]
    public string? EventState { get; set; }

    /// <summary>
    /// ZIP code where the event occurred.
    /// </summary>
    [JsonPropertyName("eventZIPCode")]
    public string? EventZIPCode { get; set; }

    /// <summary>
    /// Country where the event occurred.
    /// </summary>
    [JsonPropertyName("eventCountry")]
    public string? EventCountry { get; set; }

    /// <summary>
    /// Facility name where the event occurred.
    /// </summary>
    [JsonPropertyName("facilityName")]
    public string? FacilityName { get; set; }

    /// <summary>
    /// Action code associated with the event.
    /// </summary>
    [JsonPropertyName("actionCode")]
    public string? ActionCode { get; set; }

    /// <summary>
    /// Reason code for the action.
    /// </summary>
    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }

    /// <summary>
    /// Name of recipient (if available).
    /// </summary>
    [JsonPropertyName("recipientName")]
    public string? RecipientName { get; set; }

    /// <summary>
    /// Firm/company name at delivery location.
    /// </summary>
    [JsonPropertyName("firm")]
    public string? Firm { get; set; }
}
