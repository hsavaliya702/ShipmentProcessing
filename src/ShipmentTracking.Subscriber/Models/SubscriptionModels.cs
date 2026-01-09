using System.Text.Json.Serialization;

namespace ShipmentTracking.Subscriber.Models;

/// <summary>
/// Represents a subscription record stored in DynamoDB.
/// </summary>
public class SubscriptionRecord
{
    /// <summary>
    /// Partition key: TRACK#{Carrier}#{TrackingNumber}
    /// </summary>
    [JsonPropertyName("PK")]
    public string PK { get; set; } = string.Empty;

    /// <summary>
    /// Sort key: META
    /// </summary>
    [JsonPropertyName("SK")]
    public string SK { get; set; } = "META";

    /// <summary>
    /// Carrier identifier (USPS, UPS, FedEx).
    /// </summary>
    [JsonPropertyName("Carrier")]
    public string Carrier { get; set; } = string.Empty;

    /// <summary>
    /// Tracking number from the carrier.
    /// </summary>
    [JsonPropertyName("TrackingNumber")]
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>
    /// Fulfillment order ID for correlation.
    /// </summary>
    [JsonPropertyName("FulfillmentOrderId")]
    public string FulfillmentOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Original order ID for correlation.
    /// </summary>
    [JsonPropertyName("OrderId")]
    public string OrderId { get; set; } = string.Empty;

    /// <summary>
    /// Package ID for correlation.
    /// </summary>
    [JsonPropertyName("PackageId")]
    public string PackageId { get; set; } = string.Empty;

    /// <summary>
    /// Current subscription status.
    /// </summary>
    [JsonPropertyName("SubscriptionStatus")]
    public SubscriptionStatus SubscriptionStatus { get; set; }

    /// <summary>
    /// Subscription ID returned by the carrier (if successful).
    /// </summary>
    [JsonPropertyName("CarrierSubscriptionId")]
    public string? CarrierSubscriptionId { get; set; }

    /// <summary>
    /// Callback URL registered with the carrier.
    /// </summary>
    [JsonPropertyName("CallbackUrl")]
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>
    /// Number of subscription attempts made.
    /// </summary>
    [JsonPropertyName("AttemptCount")]
    public int AttemptCount { get; set; }

    /// <summary>
    /// Last error message if subscription failed.
    /// </summary>
    [JsonPropertyName("LastError")]
    public string? LastError { get; set; }

    /// <summary>
    /// Error classification for retry logic.
    /// </summary>
    [JsonPropertyName("ErrorType")]
    public ErrorType? ErrorType { get; set; }

    /// <summary>
    /// Timestamp when the subscription was created (ISO 8601).
    /// </summary>
    [JsonPropertyName("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when the subscription was last updated (ISO 8601).
    /// </summary>
    [JsonPropertyName("UpdatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when the subscription was successfully activated (ISO 8601).
    /// </summary>
    [JsonPropertyName("ActivatedAt")]
    public DateTime? ActivatedAt { get; set; }

    /// <summary>
    /// TTL timestamp for automatic cleanup (Unix epoch seconds).
    /// </summary>
    [JsonPropertyName("TTL")]
    public long? TTL { get; set; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    [JsonPropertyName("Metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Subscription status enumeration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubscriptionStatus
{
    /// <summary>
    /// Subscription is pending carrier registration.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Subscription is active and receiving updates.
    /// </summary>
    Active = 1,

    /// <summary>
    /// Subscription failed after retries.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// Subscription expired or was cancelled.
    /// </summary>
    Expired = 3
}

/// <summary>
/// Error classification for retry logic.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ErrorType
{
    /// <summary>
    /// Unknown error type.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Transient error that can be retried (network issues, timeouts).
    /// </summary>
    Transient = 1,

    /// <summary>
    /// Permanent error that should not be retried (invalid tracking number, authentication failure).
    /// </summary>
    Permanent = 2,

    /// <summary>
    /// Rate limit exceeded, retry after backoff.
    /// </summary>
    RateLimit = 3
}

/// <summary>
/// Request to subscribe to carrier tracking updates.
/// </summary>
public class CarrierSubscriptionRequest
{
    /// <summary>
    /// Tracking number to subscribe to.
    /// </summary>
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>
    /// Callback URL for webhook notifications.
    /// </summary>
    public string CallbackUrl { get; set; } = string.Empty;

    /// <summary>
    /// Correlation data for linking back to the order.
    /// </summary>
    public SubscriptionCorrelationData CorrelationData { get; set; } = new();
}

/// <summary>
/// Response from carrier subscription API.
/// </summary>
public class CarrierSubscriptionResponse
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
    /// Error message if subscription failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Error classification for retry logic.
    /// </summary>
    public ErrorType ErrorType { get; set; } = ErrorType.Unknown;

    /// <summary>
    /// HTTP status code from carrier API (if applicable).
    /// </summary>
    public int? StatusCode { get; set; }
}

/// <summary>
/// Correlation data for linking subscription back to the original order.
/// </summary>
public class SubscriptionCorrelationData
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
