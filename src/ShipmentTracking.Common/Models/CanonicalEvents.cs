using System.Text.Json.Serialization;

namespace ShipmentTracking.Common.Models;

/// <summary>
/// Represents a fulfillment order shipped event received from SNS.
/// This is the input event for the Subscriber Lambda.
/// </summary>
public class FulfillmentOrderShippedEvent
{
    /// <summary>
    /// Unique identifier for the fulfillment order.
    /// </summary>
    [JsonPropertyName("fulfillmentOrderId")]
    public string FulfillmentOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier for the order.
    /// </summary>
    [JsonPropertyName("orderId")]
    public string OrderId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the order was shipped (ISO 8601 format).
    /// </summary>
    [JsonPropertyName("shippedAt")]
    public DateTime ShippedAt { get; set; }

    /// <summary>
    /// List of packages in this shipment.
    /// </summary>
    [JsonPropertyName("packages")]
    public List<ShipmentPackage> Packages { get; set; } = new();

    /// <summary>
    /// Customer delivery address information.
    /// </summary>
    [JsonPropertyName("deliveryAddress")]
    public DeliveryAddress? DeliveryAddress { get; set; }

    /// <summary>
    /// Additional metadata for the order.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Represents a package within a shipment.
/// </summary>
public class ShipmentPackage
{
    /// <summary>
    /// Unique identifier for the package.
    /// </summary>
    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    /// <summary>
    /// Carrier name (e.g., "USPS", "UPS", "FedEx").
    /// </summary>
    [JsonPropertyName("carrier")]
    public string Carrier { get; set; } = string.Empty;

    /// <summary>
    /// Tracking number provided by the carrier.
    /// </summary>
    [JsonPropertyName("trackingNumber")]
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>
    /// Shipping service level (e.g., "Ground", "Express", "Priority").
    /// </summary>
    [JsonPropertyName("serviceLevel")]
    public string? ServiceLevel { get; set; }

    /// <summary>
    /// List of items in this package.
    /// </summary>
    [JsonPropertyName("items")]
    public List<PackageItem>? Items { get; set; }
}

/// <summary>
/// Represents an item within a package.
/// </summary>
public class PackageItem
{
    /// <summary>
    /// SKU or product identifier.
    /// </summary>
    [JsonPropertyName("sku")]
    public string Sku { get; set; } = string.Empty;

    /// <summary>
    /// Quantity of this item.
    /// </summary>
    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    /// <summary>
    /// Product description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Represents a delivery address.
/// </summary>
public class DeliveryAddress
{
    /// <summary>
    /// Street address line 1.
    /// </summary>
    [JsonPropertyName("addressLine1")]
    public string AddressLine1 { get; set; } = string.Empty;

    /// <summary>
    /// Street address line 2 (optional).
    /// </summary>
    [JsonPropertyName("addressLine2")]
    public string? AddressLine2 { get; set; }

    /// <summary>
    /// City name.
    /// </summary>
    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    /// <summary>
    /// State or province code.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Postal/ZIP code.
    /// </summary>
    [JsonPropertyName("postalCode")]
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>
    /// Country code (ISO 3166-1 alpha-2).
    /// </summary>
    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;
}

/// <summary>
/// Represents a shipment tracking status event published to SNS.
/// This is the output event from the Webhook Processor Lambda.
/// </summary>
public class ShipmentTrackingStatusEvent
{
    /// <summary>
    /// Correlation identifier linking back to the original order.
    /// </summary>
    [JsonPropertyName("fulfillmentOrderId")]
    public string FulfillmentOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Original order identifier.
    /// </summary>
    [JsonPropertyName("orderId")]
    public string OrderId { get; set; } = string.Empty;

    /// <summary>
    /// Package identifier.
    /// </summary>
    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    /// <summary>
    /// Carrier name.
    /// </summary>
    [JsonPropertyName("carrier")]
    public string Carrier { get; set; } = string.Empty;

    /// <summary>
    /// Tracking number.
    /// </summary>
    [JsonPropertyName("trackingNumber")]
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>
    /// Canonical tracking status.
    /// </summary>
    [JsonPropertyName("status")]
    public TrackingStatus Status { get; set; }

    /// <summary>
    /// Human-readable status description.
    /// </summary>
    [JsonPropertyName("statusDescription")]
    public string StatusDescription { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the status event from the carrier (ISO 8601 format).
    /// </summary>
    [JsonPropertyName("eventTimestamp")]
    public DateTime EventTimestamp { get; set; }

    /// <summary>
    /// Location information where the status event occurred.
    /// </summary>
    [JsonPropertyName("location")]
    public TrackingLocation? Location { get; set; }

    /// <summary>
    /// Original carrier-specific status code.
    /// </summary>
    [JsonPropertyName("carrierStatusCode")]
    public string? CarrierStatusCode { get; set; }

    /// <summary>
    /// Original carrier-specific sub-status code.
    /// </summary>
    [JsonPropertyName("carrierSubStatusCode")]
    public string? CarrierSubStatusCode { get; set; }

    /// <summary>
    /// Estimated delivery date if available.
    /// </summary>
    [JsonPropertyName("estimatedDeliveryDate")]
    public DateTime? EstimatedDeliveryDate { get; set; }

    /// <summary>
    /// Actual delivery timestamp if package was delivered.
    /// </summary>
    [JsonPropertyName("deliveredAt")]
    public DateTime? DeliveredAt { get; set; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// Timestamp when this event was processed by our system.
    /// </summary>
    [JsonPropertyName("processedAt")]
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Represents a location in a tracking event.
/// </summary>
public class TrackingLocation
{
    /// <summary>
    /// City name.
    /// </summary>
    [JsonPropertyName("city")]
    public string? City { get; set; }

    /// <summary>
    /// State or province code.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>
    /// Postal/ZIP code.
    /// </summary>
    [JsonPropertyName("postalCode")]
    public string? PostalCode { get; set; }

    /// <summary>
    /// Country code (ISO 3166-1 alpha-2).
    /// </summary>
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    /// <summary>
    /// Facility or location name.
    /// </summary>
    [JsonPropertyName("facilityName")]
    public string? FacilityName { get; set; }
}

/// <summary>
/// Canonical tracking status enumeration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackingStatus
{
    /// <summary>
    /// Status is unknown or could not be determined.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Carrier has received electronic shipping information.
    /// </summary>
    PreTransit = 1,

    /// <summary>
    /// Package is in transit to the destination.
    /// </summary>
    InTransit = 2,

    /// <summary>
    /// Package is out for delivery.
    /// </summary>
    OutForDelivery = 3,

    /// <summary>
    /// Package has been delivered successfully.
    /// </summary>
    Delivered = 4,

    /// <summary>
    /// Delivery attempt failed.
    /// </summary>
    DeliveryAttemptFailed = 5,

    /// <summary>
    /// Package is being returned to sender.
    /// </summary>
    Returning = 6,

    /// <summary>
    /// Package has been returned to sender.
    /// </summary>
    Returned = 7,

    /// <summary>
    /// Package is on hold or delayed.
    /// </summary>
    OnHold = 8,

    /// <summary>
    /// Exception occurred during shipment.
    /// </summary>
    Exception = 9,

    /// <summary>
    /// Package is available for pickup.
    /// </summary>
    AvailableForPickup = 10,

    /// <summary>
    /// Shipment cancelled.
    /// </summary>
    Cancelled = 11
}

/// <summary>
/// Supported carrier identifiers.
/// </summary>
public static class CarrierCodes
{
    /// <summary>
    /// United States Postal Service.
    /// </summary>
    public const string Usps = "USPS";

    /// <summary>
    /// United Parcel Service.
    /// </summary>
    public const string Ups = "UPS";

    /// <summary>
    /// Federal Express.
    /// </summary>
    public const string FedEx = "FEDEX";
}
