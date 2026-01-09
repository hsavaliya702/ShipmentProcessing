# Status Mapping Documentation

## Overview

This document defines the mapping between carrier-specific status codes and the canonical tracking status enumeration used throughout the Shipment Tracking Service. These mappings ensure consistent status representation regardless of the originating carrier.

## Canonical Tracking Status

The system uses a standardized set of tracking statuses that represent the lifecycle of a shipment across all carriers.

### TrackingStatus Enumeration

```csharp
public enum TrackingStatus
{
    Unknown = 0,              // Status cannot be determined
    PreTransit = 1,           // Label created, not yet in carrier possession
    InTransit = 2,            // Package is in transit
    OutForDelivery = 3,       // Package is out for delivery today
    Delivered = 4,            // Package successfully delivered
    DeliveryAttemptFailed = 5,// Delivery attempted but failed
    Returning = 6,            // Package is being returned to sender
    Returned = 7,             // Package returned to sender
    OnHold = 8,              // Package held (customs, weather, etc.)
    Exception = 9,            // Exception occurred (damage, lost, etc.)
    AvailableForPickup = 10, // Ready for customer pickup at facility
    Cancelled = 11            // Shipment cancelled
}
```

### Status Descriptions

| Status | Description | Customer-Facing Message |
|--------|-------------|------------------------|
| Unknown | Status cannot be determined or mapped | "We're tracking your package" |
| PreTransit | Shipping label created, package not yet scanned | "Shipping label created" |
| InTransit | Package is moving through carrier network | "In transit to destination" |
| OutForDelivery | Package loaded on delivery vehicle | "Out for delivery today" |
| Delivered | Package successfully delivered | "Delivered" |
| DeliveryAttemptFailed | Delivery attempted, recipient not available | "Delivery attempted - notice left" |
| Returning | Package is being returned to sender | "Package returning to sender" |
| Returned | Package returned to sender | "Package returned" |
| OnHold | Package held due to external factor | "Package on hold" |
| Exception | Problem occurred (damage, lost, etc.) | "Exception - contact carrier" |
| AvailableForPickup | Ready at post office/facility | "Ready for pickup" |
| Cancelled | Shipment cancelled before delivery | "Shipment cancelled" |

## USPS Status Mapping

### Numeric Status Codes

USPS uses two-digit numeric codes (01-99) to represent tracking events.

| USPS Code | USPS Description | Canonical Status | Notes |
|-----------|------------------|------------------|-------|
| 01 | Pre-Shipment Info Sent to USPS | PreTransit | Label created |
| 02 | Accepted at USPS Origin Facility | InTransit | Initial scan |
| 03 | Accepted at USPS Facility | InTransit | In network |
| 04 | Out for Delivery | OutForDelivery | Final mile |
| 05 | Delivered | Delivered | Success |
| 06 | Delivery Attempted - Notice Left | DeliveryAttemptFailed | Retry needed |
| 07 | Re-Routed | InTransit | Changed route |
| 08 | Available for Pickup | AvailableForPickup | At post office |
| 09 | Item Being Returned | Returning | RTS initiated |
| 10 | Delivered to Agent | Delivered | Delivered to authorized agent |
| 11 | Held in Customs | OnHold | International delay |
| 12 | Refused by Addressee | Returning | Delivery refused |
| 13 | Forward Expired | Returning | Forwarding service expired |
| 14 | Addressee Not Known | Exception | Invalid address |
| 15 | Damaged | Exception | Package damaged |
| 16 | Unclaimed | Returning | Not picked up |
| 17 | Undeliverable as Addressed | Exception | Cannot deliver |
| 18 | Forwarded | InTransit | Address forwarded |
| 19 | Return to Sender | Returning | Actively returning |
| 20 | Picked Up | InTransit | Collected from sender |
| 21 | Shipping Label Created | PreTransit | Electronic notification |
| 22 | In Transit to Next Facility | InTransit | Between facilities |
| 23 | Departed USPS Facility | InTransit | Left facility |
| 24 | Arrived at USPS Facility | InTransit | Reached facility |
| 25 | Departed Post Office | InTransit | Left post office |
| 26 | Arrived at Post Office | InTransit | At post office |
| 27 | Sorting Complete | InTransit | Ready for next step |
| 28 | Processed at USPS Origin Facility | InTransit | Origin processing |
| 29 | Processed at USPS Destination Facility | InTransit | Destination processing |
| 30 | USPS in Possession of Item | InTransit | Confirmed in system |

### Text-Based Status Codes

USPS also uses text-based status codes in some API responses.

| USPS Text Code | Description | Canonical Status |
|----------------|-------------|------------------|
| DELIVERED | Package delivered | Delivered |
| IN_TRANSIT | Package in transit | InTransit |
| OUT_FOR_DELIVERY | Out for delivery | OutForDelivery |
| DELIVERY_ATTEMPT | Delivery attempt failed | DeliveryAttemptFailed |
| AVAILABLE_FOR_PICKUP | Available at facility | AvailableForPickup |
| RETURNED | Returned to sender | Returned |
| EXCEPTION | Exception occurred | Exception |
| PRE_TRANSIT | Pre-shipment | PreTransit |
| HELD | Package held | OnHold |
| RETURNING | Being returned | Returning |

### Status Categories

USPS groups statuses into high-level categories:

| Category | Canonical Mapping | Description |
|----------|-------------------|-------------|
| PRE_TRANSIT | PreTransit | Before carrier pickup |
| IN_TRANSIT | InTransit | Moving through network |
| DELIVERED | Delivered | Successfully delivered |
| OUT_FOR_DELIVERY | OutForDelivery | Final delivery attempt |
| DELIVERY_EXCEPTION | Exception | Delivery problem |
| RETURN_TO_SENDER | Returning or Returned | Going back to sender |
| AVAILABLE_FOR_PICKUP | AvailableForPickup | At facility |

### Composite Mappings

Some USPS statuses require both status code and sub-code for accurate mapping:

| Status Code | Sub Code | Canonical Status | Notes |
|-------------|----------|------------------|-------|
| 06 | 01 | DeliveryAttemptFailed | No access to delivery location |
| 06 | 02 | DeliveryAttemptFailed | No authorized recipient |
| 06 | 03 | DeliveryAttemptFailed | No secure location |
| 11 | 01 | OnHold | Held at customs |
| 11 | 02 | OnHold | Clearance in progress |
| 17 | 01 | Exception | Insufficient address |
| 17 | 02 | Exception | No such number |
| 17 | 03 | Exception | Moved, left no address |

## UPS Status Mapping (Future)

### Status Type Codes

UPS uses single-letter status type codes combined with detailed status codes.

| Type | Code | Description | Canonical Status | Notes |
|------|------|-------------|------------------|-------|
| I | - | In Transit | InTransit | General transit |
| X | - | Exception | Exception | Delivery exception |
| M | - | Manifest | PreTransit | Pickup scheduled |
| P | - | Pickup | InTransit | Picked up |
| D | - | Delivered | Delivered | Successfully delivered |
| RS | - | Return to Sender | Returning | Being returned |

### Detailed Status Codes

| UPS Code | Description | Canonical Status |
|----------|-------------|------------------|
| FS | Delivered | Delivered |
| KB | Held | OnHold |
| OT | Out for Delivery | OutForDelivery |
| CC | Clearance in Progress | OnHold |
| NA | Customer Not Available | DeliveryAttemptFailed |
| RS | Return to Sender | Returning |
| DD | Damaged | Exception |

**Note**: Complete UPS mappings will be added during Phase 2 implementation based on UPS API documentation.

## FedEx Status Mapping (Future)

### Status Codes

FedEx uses two-letter status codes.

| FedEx Code | Description | Canonical Status |
|------------|-------------|------------------|
| DL | Delivered | Delivered |
| IT | In Transit | InTransit |
| OD | Out for Delivery | OutForDelivery |
| PU | Picked Up | InTransit |
| AR | At Destination | InTransit |
| DP | Departed FedEx Location | InTransit |
| AX | At Delivery | InTransit |
| DE | Delivery Exception | Exception |
| HL | Held at Location | OnHold |
| RS | Return to Shipper | Returning |

**Note**: Complete FedEx mappings will be added during Phase 3 implementation based on FedEx API documentation.

## Implementation

### StatusTranslationService

The `StatusTranslationService` class handles all status translations:

```csharp
public class StatusTranslationService
{
    private readonly Dictionary<string, Dictionary<string, TrackingStatus>> _statusMappings;
    private readonly ILogger<StatusTranslationService> _logger;

    public StatusTranslationService(ILogger<StatusTranslationService> logger)
    {
        _logger = logger;
        _statusMappings = InitializeStatusMappings();
    }

    public TrackingStatus TranslateStatus(
        string carrier, 
        string statusCode, 
        string? subStatusCode = null)
    {
        var carrierKey = carrier.ToUpperInvariant();

        if (!_statusMappings.TryGetValue(carrierKey, out var carrierMappings))
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
                _logger.LogDebug("Translated {Carrier} status {StatusCode}:{SubStatusCode} to {Status}",
                    carrier, statusCode, subStatusCode, compositeStatus);
                return compositeStatus;
            }
        }

        // Fallback to status code only
        if (carrierMappings.TryGetValue(statusCode, out var status))
        {
            _logger.LogDebug("Translated {Carrier} status {StatusCode} to {Status}",
                carrier, statusCode, status);
            return status;
        }

        // Unknown status code
        _logger.LogWarning("Unknown status code for {Carrier}: {StatusCode}:{SubStatusCode}",
            carrier, statusCode, subStatusCode ?? "null");
        
        return TrackingStatus.Unknown;
    }

    private Dictionary<string, Dictionary<string, TrackingStatus>> InitializeStatusMappings()
    {
        return new Dictionary<string, Dictionary<string, TrackingStatus>>
        {
            ["USPS"] = new Dictionary<string, TrackingStatus>
            {
                // Numeric codes
                ["01"] = TrackingStatus.PreTransit,
                ["02"] = TrackingStatus.InTransit,
                ["03"] = TrackingStatus.InTransit,
                ["04"] = TrackingStatus.OutForDelivery,
                ["05"] = TrackingStatus.Delivered,
                ["06"] = TrackingStatus.DeliveryAttemptFailed,
                ["07"] = TrackingStatus.InTransit,
                ["08"] = TrackingStatus.AvailableForPickup,
                ["09"] = TrackingStatus.Returning,
                ["10"] = TrackingStatus.Delivered,
                ["11"] = TrackingStatus.OnHold,
                ["12"] = TrackingStatus.Returning,
                ["13"] = TrackingStatus.Returning,
                ["14"] = TrackingStatus.Exception,
                ["15"] = TrackingStatus.Exception,
                ["16"] = TrackingStatus.Returning,
                ["17"] = TrackingStatus.Exception,
                ["18"] = TrackingStatus.InTransit,
                ["19"] = TrackingStatus.Returning,
                ["20"] = TrackingStatus.InTransit,
                ["21"] = TrackingStatus.PreTransit,
                ["22"] = TrackingStatus.InTransit,
                ["23"] = TrackingStatus.InTransit,
                ["24"] = TrackingStatus.InTransit,
                ["25"] = TrackingStatus.InTransit,
                ["26"] = TrackingStatus.InTransit,
                ["27"] = TrackingStatus.InTransit,
                ["28"] = TrackingStatus.InTransit,
                ["29"] = TrackingStatus.InTransit,
                ["30"] = TrackingStatus.InTransit,

                // Text codes
                ["DELIVERED"] = TrackingStatus.Delivered,
                ["IN_TRANSIT"] = TrackingStatus.InTransit,
                ["OUT_FOR_DELIVERY"] = TrackingStatus.OutForDelivery,
                ["DELIVERY_ATTEMPT"] = TrackingStatus.DeliveryAttemptFailed,
                ["AVAILABLE_FOR_PICKUP"] = TrackingStatus.AvailableForPickup,
                ["RETURNED"] = TrackingStatus.Returned,
                ["EXCEPTION"] = TrackingStatus.Exception,
                ["PRE_TRANSIT"] = TrackingStatus.PreTransit,
                ["HELD"] = TrackingStatus.OnHold,
                ["RETURNING"] = TrackingStatus.Returning,

                // Composite codes (statusCode:subCode)
                ["06:01"] = TrackingStatus.DeliveryAttemptFailed,
                ["06:02"] = TrackingStatus.DeliveryAttemptFailed,
                ["06:03"] = TrackingStatus.DeliveryAttemptFailed,
                ["11:01"] = TrackingStatus.OnHold,
                ["11:02"] = TrackingStatus.OnHold,
                ["17:01"] = TrackingStatus.Exception,
                ["17:02"] = TrackingStatus.Exception,
                ["17:03"] = TrackingStatus.Exception
            }
            // Future carriers added here
        };
    }
}
```

### Configuration-Based Mapping (Alternative)

For environments requiring frequent status mapping updates without code deployment:

**DynamoDB Table**: `StatusMappings`

```
PK: CARRIER#USPS
SK: STATUS#{statusCode}[#{subCode}]

Attributes:
- CanonicalStatus: "Delivered"
- Description: "Package delivered successfully"
- CustomerMessage: "Your package has been delivered"
- Priority: 100 (for conflict resolution)
- EffectiveDate: "2026-01-01"
- ExpirationDate: null
```

**Benefits**:
- Update mappings without code deployment
- Version mappings over time
- Support A/B testing of status messages
- Audit trail of mapping changes

**Tradeoffs**:
- Additional DynamoDB read for each webhook
- Caching required for performance
- More complex testing

## Testing Status Mappings

### Unit Tests

```csharp
[Theory]
[InlineData("USPS", "05", null, TrackingStatus.Delivered)]
[InlineData("USPS", "04", null, TrackingStatus.OutForDelivery)]
[InlineData("USPS", "06", "01", TrackingStatus.DeliveryAttemptFailed)]
[InlineData("USPS", "99", null, TrackingStatus.Unknown)]
public void TranslateStatus_WithVariousInputs_ReturnsExpectedStatus(
    string carrier, 
    string statusCode, 
    string? subCode, 
    TrackingStatus expected)
{
    // Arrange
    var service = new StatusTranslationService(Mock.Of<ILogger<StatusTranslationService>>());

    // Act
    var result = service.TranslateStatus(carrier, statusCode, subCode);

    // Assert
    Assert.Equal(expected, result);
}
```

### Integration Tests

Test complete webhook flow with various status codes:

```csharp
[Fact]
public async Task ProcessWebhook_WithDeliveredStatus_PublishesCanonicalEvent()
{
    // Arrange
    var payload = @"{
        ""trackingNumber"": ""9400116901490039382136"",
        ""statusCode"": ""05"",
        ""eventTimestamp"": ""2026-01-10T14:30:00Z""
    }";

    // Act
    var result = await ProcessWebhookWithPayload("usps", payload);

    // Assert
    Assert.True(result.Success);
    var publishedEvent = GetPublishedEvent();
    Assert.Equal(TrackingStatus.Delivered, publishedEvent.Status);
}
```

## Monitoring and Alerts

### Metrics

Track status translation patterns:

```csharp
// Emit custom metric
await _metrics.PutMetricAsync(
    metricName: "StatusTranslation",
    value: 1,
    unit: StandardUnit.Count,
    dimensions: new Dictionary<string, string>
    {
        ["Carrier"] = carrier,
        ["CanonicalStatus"] = canonicalStatus.ToString(),
        ["CarrierStatusCode"] = statusCode
    });
```

### CloudWatch Alarms

**Unknown Status Alert**:
- Metric: `StatusTranslation` where `CanonicalStatus = Unknown`
- Threshold: > 10 per 5 minutes
- Action: SNS notification to operations team
- Indicates new carrier status codes need mapping

**Status Distribution Anomaly**:
- Metric: Compare current status distribution to baseline
- Detect unusual patterns (e.g., spike in exceptions)
- May indicate carrier API changes or integration issues

## Adding New Status Mappings

When a new carrier status code is discovered:

1. **Log Warning**: System automatically logs unknown codes
2. **Research**: Review carrier documentation
3. **Determine Mapping**: Choose appropriate canonical status
4. **Update Code**: Add mapping to `StatusTranslationService`
5. **Test**: Write unit tests for new mapping
6. **Deploy**: Deploy to dev → uat → prod
7. **Monitor**: Watch for correct translation
8. **Document**: Update this file

## Best Practices

1. **Default to Unknown**: When in doubt, map to `Unknown` rather than guessing
2. **Use Composite Keys**: When sub-codes provide important context
3. **Log All Unknowns**: Track unmapped codes for future updates
4. **Version Mappings**: Consider effective dates for mapping changes
5. **Test Thoroughly**: Test all mapping paths
6. **Monitor Actively**: Watch for new status codes in production
7. **Document Changes**: Keep this file updated

## Related Documentation

- [Architecture](./ARCHITECTURE.md)
- [Implementation Guide](./IMPLEMENTATION_GUIDE.md)
- [Webhook Specifications](./WEBHOOK_SPECIFICATIONS.md)
- [Testing Strategy](./TESTING_STRATEGY.md)
