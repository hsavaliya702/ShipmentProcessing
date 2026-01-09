# Webhook Processor Lambda - Architecture

## Overview

The Webhook Processor Lambda receives carrier webhook callbacks via API Gateway and processes tracking status updates. It validates webhook authenticity, parses carrier-specific payloads, translates statuses to canonical format, and publishes events to SNS for downstream consumers.

## Architecture Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│                  WEBHOOK PROCESSOR LAMBDA                         │
│                                                                    │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │              Function Handler (API Gateway)                  │ │
│  │  - Receives POST /webhook/{carrier}                         │ │
│  │  - Extracts carrier from path                                │ │
│  │  - Builds WebhookRequest with headers & payload             │ │
│  │  - Returns appropriate HTTP status codes                     │ │
│  └────────────────┬────────────────────────────────────────────┘ │
│                   │                                                │
│                   ▼                                                │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │                  WebhookHandler                              │ │
│  │  Pipeline: Validate → Parse → Correlate → Translate → Publish│ │
│  └───┬──────────┬──────────┬──────────┬──────────┬────────────┘ │
│      │          │          │          │          │                │
│      ▼          ▼          ▼          ▼          ▼                │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐        │
│  │Validate│ │ Parse  │ │Correlate│ │Translate│ │Publish │        │
│  │        │ │        │ │         │ │        │ │        │        │
│  │Webhook │ │Carrier │ │Get      │ │Status  │ │To SNS  │        │
│  │Signature│ │Payload │ │Order    │ │Mapping │ │Topic   │        │
│  │        │ │        │ │Context  │ │        │ │        │        │
│  └────────┘ └────────┘ └────────┘ └────────┘ └────────┘        │
│      │          │          │          │          │                │
│      ▼          ▼          ▼          ▼          ▼                │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │           Supporting Services                             │   │
│  │                                                            │   │
│  │  IWebhookValidator        ICarrierPayloadParser          │   │
│  │  ├─ UspsWebhookValidator  ├─ UspsPayloadParser           │   │
│  │  ├─ UpsWebhookValidator   ├─ UpsPayloadParser            │   │
│  │  └─ FedExWebhookValidator └─ FedExPayloadParser          │   │
│  │                                                            │   │
│  │  StatusTranslationService  CorrelationService            │   │
│  │  EventPublisher                                           │   │
│  └──────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────┘
```

## Processing Pipeline

### 1. Validation Phase

**Purpose**: Verify webhook authenticity using HMAC-SHA256 signature

**Implementation**:
```csharp
var validationResult = await validator.ValidateAsync(payload, headers, cancellationToken);
if (!validationResult.IsValid)
    return 401 Unauthorized;
```

**Security Checks**:
- Extract signature from `X-Carrier-Signature` header
- Extract timestamp from `X-Carrier-Timestamp` header
- Validate timestamp freshness (±5 minutes default)
- Retrieve webhook secret from Secrets Manager
- Compute expected signature: `HMAC-SHA256(timestamp + "." + payload, secret)`
- Constant-time comparison to prevent timing attacks

**Response Codes**:
- `401`: Invalid signature or timestamp
- `400`: Missing required headers
- `500`: Error retrieving secret

### 2. Parse Phase

**Purpose**: Convert carrier-specific JSON to normalized `CarrierTrackingEvent`

**Implementation**:
```csharp
var carrierEvent = await parser.ParseAsync(payload, cancellationToken);
```

**Carrier-Specific Parsing**:
- USPS: Maps `statusCode`, `statusCategory`, `eventTimestamp`
- UPS: Maps tracking response format (future)
- FedEx: Maps notification format (future)

**Extracted Fields**:
- Tracking number
- Status code and sub-status
- Event timestamp
- Location information
- Estimated delivery date
- Additional carrier-specific data

**Error Handling**:
- `PayloadParsingException`: Invalid JSON or missing required fields
- Returns `400 Bad Request` to carrier

### 3. Correlation Phase

**Purpose**: Retrieve order context from DynamoDB

**Implementation**:
```csharp
var correlation = await correlationService.GetCorrelationDataAsync(
    carrier, trackingNumber, cancellationToken);
```

**Retrieved Data**:
- `FulfillmentOrderId`: Original order identifier
- `OrderId`: Customer order ID
- `PackageId`: Specific package identifier
- `Metadata`: Additional context (service level, shipped date)

**No Subscription Found**:
- Returns `200 OK` (acknowledge webhook)
- Does not process further
- Logs warning for investigation

### 4. Translation Phase

**Purpose**: Map carrier status codes to canonical tracking status

**Implementation**:
```csharp
var canonicalStatus = translationService.TranslateStatus(
    carrier, statusCode, subStatusCode);
```

**Translation Logic**:
1. Try composite key: `statusCode:subStatusCode`
2. Fallback to status code only
3. Return `TrackingStatus.Unknown` if no mapping

**Canonical Status Values**:
```csharp
public enum TrackingStatus
{
    Unknown,
    PreTransit,
    InTransit,
    OutForDelivery,
    Delivered,
    DeliveryAttemptFailed,
    Returning,
    Returned,
    OnHold,
    Exception,
    AvailableForPickup,
    Cancelled
}
```

**Example Mappings** (USPS):
- `04` → `OutForDelivery`
- `05` → `Delivered`
- `06` → `DeliveryAttemptFailed`
- `DELIVERED` → `Delivered`

### 5. Publish Phase

**Purpose**: Publish canonical event to SNS topic

**Implementation**:
```csharp
var messageId = await eventPublisher.PublishAsync(
    canonicalEvent, correlationId, cancellationToken);
```

**Published Event**:
```json
{
  "fulfillmentOrderId": "FO-12345",
  "orderId": "ORD-67890",
  "packageId": "PKG-001",
  "carrier": "USPS",
  "trackingNumber": "9400116901490039382136",
  "status": "OutForDelivery",
  "statusDescription": "Out for Delivery",
  "eventTimestamp": "2026-01-10T09:30:00Z",
  "location": {
    "city": "Springfield",
    "state": "IL",
    "postalCode": "62701",
    "country": "US"
  },
  "carrierStatusCode": "04",
  "estimatedDeliveryDate": "2026-01-10T17:00:00Z",
  "processedAt": "2026-01-10T09:30:15Z"
}
```

**SNS Message Attributes**:
- `CorrelationId`: For tracing
- `EventType`: `ShipmentTrackingStatusEvent`
- `Carrier`: Carrier code
- `Status`: Canonical status
- `OrderId`: For filtering
- `FulfillmentOrderId`: For filtering

## Component Details

### WebhookHandler

**Orchestration Logic**:
```csharp
public async Task<WebhookProcessingResult> ProcessWebhookAsync(
    WebhookRequest request,
    CancellationToken cancellationToken)
{
    // 1. Validate signature
    var validationResult = await ValidateWebhookAsync(request, correlationId, cancellationToken);
    if (!validationResult.IsValid)
        return Failure(validationResult.StatusCode, validationResult.ErrorMessage);

    // 2. Parse payload
    var carrierEvent = await ParsePayloadAsync(request, correlationId, cancellationToken);

    // 3. Get correlation data
    var correlation = await _correlationService.GetCorrelationDataAsync(
        request.Carrier, carrierEvent.TrackingNumber, cancellationToken);
    
    if (correlation == null)
        return Success(200, "No matching subscription"); // Acknowledge but don't process

    // 4. Translate status
    var canonicalStatus = _translationService.TranslateStatus(
        request.Carrier, carrierEvent.StatusCode, carrierEvent.SubStatusCode);

    // 5. Build and publish canonical event
    var canonicalEvent = BuildCanonicalEvent(carrierEvent, correlation, canonicalStatus);
    var messageId = await _eventPublisher.PublishAsync(canonicalEvent, correlationId, cancellationToken);

    return Success(200, "Webhook processed successfully", messageId);
}
```

### UspsWebhookValidator

**HMAC-SHA256 Validation**:
```csharp
public async Task<ValidationResult> ValidateAsync(
    string payload,
    Dictionary<string, string> headers,
    CancellationToken cancellationToken)
{
    // Extract headers
    if (!headers.TryGetValue("X-USPS-Signature", out var signature))
        return Failure("Missing signature header", 401);
    
    if (!headers.TryGetValue("X-USPS-Timestamp", out var timestampStr))
        return Failure("Missing timestamp header", 401);

    // Validate timestamp freshness
    var timestamp = long.Parse(timestampStr);
    var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
    var now = DateTimeOffset.UtcNow;
    var timeDifference = Math.Abs((now - requestTime).TotalSeconds);
    
    if (timeDifference > _allowedTimestampSkewSeconds)
        return Failure("Timestamp outside allowed window", 401);

    // Get webhook secret
    var webhookSecret = await _secretManager.GetSecretStringAsync(
        "carrier/usps/webhook-secret", cancellationToken);

    // Compute expected signature
    var expectedSignature = ComputeSignature(payload, webhookSecret, timestampStr);

    // Constant-time comparison
    if (!ConstantTimeEquals(signature, expectedSignature))
        return Failure("Invalid signature", 401);

    return Success();
}

private static string ComputeSignature(string payload, string secret, string timestamp)
{
    var message = $"{timestamp}.{payload}";
    var keyBytes = Encoding.UTF8.GetBytes(secret);
    var messageBytes = Encoding.UTF8.GetBytes(message);

    using var hmac = new HMACSHA256(keyBytes);
    var hashBytes = hmac.ComputeHash(messageBytes);
    return Convert.ToBase64String(hashBytes);
}

private static bool ConstantTimeEquals(string a, string b)
{
    if (a.Length != b.Length)
        return false;

    var result = 0;
    for (var i = 0; i < a.Length; i++)
        result |= a[i] ^ b[i];

    return result == 0;
}
```

### StatusTranslationService

**Mapping Configuration**:
```csharp
private Dictionary<string, Dictionary<string, TrackingStatus>> InitializeStatusMappings()
{
    return new Dictionary<string, Dictionary<string, TrackingStatus>>
    {
        ["USPS"] = new Dictionary<string, TrackingStatus>
        {
            ["01"] = TrackingStatus.PreTransit,
            ["03"] = TrackingStatus.InTransit,
            ["04"] = TrackingStatus.OutForDelivery,
            ["05"] = TrackingStatus.Delivered,
            ["06"] = TrackingStatus.DeliveryAttemptFailed,
            ["08"] = TrackingStatus.AvailableForPickup,
            ["09"] = TrackingStatus.Returning,
            ["11"] = TrackingStatus.OnHold,
            ["DELIVERED"] = TrackingStatus.Delivered,
            ["EXCEPTION"] = TrackingStatus.Exception
            // ... more mappings
        }
        // Future carriers added here when implemented
    };
}

public TrackingStatus TranslateStatus(string carrier, string statusCode, string? subStatusCode)
{
    if (!_statusMappings.TryGetValue(carrier.ToUpperInvariant(), out var carrierMappings))
    {
        _logger.LogWarning("No status mappings found for carrier: {Carrier}", carrier);
        return TrackingStatus.Unknown;
    }

    // Try composite key first (statusCode:subStatusCode)
    if (!string.IsNullOrWhiteSpace(subStatusCode))
    {
        var compositeKey = $"{statusCode}:{subStatusCode}";
        if (carrierMappings.TryGetValue(compositeKey, out var compositeStatus))
            return compositeStatus;
    }

    // Fallback to status code only
    if (carrierMappings.TryGetValue(statusCode, out var status))
        return status;

    _logger.LogWarning("Unknown status code for {Carrier}: {StatusCode}:{SubStatusCode}",
        carrier, statusCode, subStatusCode ?? "null");
    
    return TrackingStatus.Unknown;
}
```

### EventPublisher

**SNS Publishing**:
```csharp
public async Task<string> PublishAsync(
    ShipmentTrackingStatusEvent trackingEvent,
    string correlationId,
    CancellationToken cancellationToken)
{
    var message = JsonSerializer.Serialize(trackingEvent);

    var request = new PublishRequest
    {
        TopicArn = _topicArn,
        Message = message,
        Subject = $"Shipment Status Update - {trackingEvent.Status}",
        MessageAttributes = new Dictionary<string, MessageAttributeValue>
        {
            ["CorrelationId"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = correlationId
            },
            ["EventType"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = "ShipmentTrackingStatusEvent"
            },
            ["Carrier"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = trackingEvent.Carrier
            },
            ["Status"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = trackingEvent.Status.ToString()
            },
            ["OrderId"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = trackingEvent.OrderId
            }
        }
    };

    var response = await _snsClient.PublishAsync(request, cancellationToken);
    return response.MessageId;
}
```

## API Gateway Integration

### Endpoint Configuration

**Base Path**: `/webhook/{carrier}`

**Supported Carriers**:
- `/webhook/usps` → USPS webhooks
- `/webhook/ups` → UPS webhooks (future)
- `/webhook/fedex` → FedEx webhooks (future)

**HTTP Method**: POST

**Authentication**: Signature validation (no API key)

### Request Format

**Headers** (USPS example):
```
X-USPS-Signature: base64-encoded-hmac-sha256
X-USPS-Timestamp: unix-timestamp-seconds
Content-Type: application/json
```

**Body** (USPS example):
```json
{
  "trackingNumber": "9400116901490039382136",
  "status": "Out for Delivery",
  "statusCode": "04",
  "eventTimestamp": "2026-01-10T09:30:00Z",
  "eventCity": "Springfield",
  "eventState": "IL"
}
```

### Response Codes

- **200 OK**: Webhook processed successfully
- **400 Bad Request**: Invalid payload format or missing fields
- **401 Unauthorized**: Invalid signature or timestamp
- **500 Internal Server Error**: Processing error (carrier should retry)

### Custom Domain

**Configuration**:
- Custom domain: `tracking-webhook.example.com`
- Certificate: ACM certificate ARN
- Base path mapping: `/` → API Gateway stage

**Benefits**:
- Professional appearance
- Easier to change underlying infrastructure
- Better for carrier onboarding

## Performance Characteristics

### Latency

**Average**: 50-150ms (warm)
- Signature validation: 5-10ms
- Payload parsing: 5-10ms
- DynamoDB lookup: 10-30ms
- Status translation: <1ms
- SNS publish: 20-50ms

**Cold Start**: 800ms-1.5s

### Throughput

- **Concurrent executions**: 500 (reserved)
- **API Gateway limit**: 10,000 requests/second (soft limit)
- **Typical load**: 10-100 webhooks/second
- **Peak capacity**: 1000+ webhooks/second

### Memory Usage

- **Allocated**: 1024 MB
- **Typical usage**: 150-200 MB
- **Recommendation**: 1024 MB for buffer

## Security Considerations

### Webhook Signature Validation

**HMAC-SHA256 Algorithm**:
1. Concatenate: `timestamp + "." + payload`
2. Compute: `HMAC-SHA256(message, secret)`
3. Encode: Base64
4. Compare: Constant-time

**Timestamp Validation**:
- Accept webhooks within ±5 minutes (configurable)
- Prevents replay attacks
- Protects against clock skew

**Constant-Time Comparison**:
```csharp
private static bool ConstantTimeEquals(string a, string b)
{
    if (a.Length != b.Length)
        return false;

    var result = 0;
    for (var i = 0; i < a.Length; i++)
        result |= a[i] ^ b[i];

    return result == 0; // Timing doesn't reveal position of mismatch
}
```

### IAM Policy

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["dynamodb:GetItem"],
      "Resource": "arn:aws:dynamodb:*:*:table/ShipmentTrackingSubscriptions"
    },
    {
      "Effect": "Allow",
      "Action": ["sns:Publish"],
      "Resource": "arn:aws:sns:*:*:shipment-tracking-status-events"
    },
    {
      "Effect": "Allow",
      "Action": ["secretsmanager:GetSecretValue"],
      "Resource": "arn:aws:secretsmanager:*:*:secret:carrier/*/webhook-secret*"
    },
    {
      "Effect": "Allow",
      "Action": ["logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents"],
      "Resource": "*"
    }
  ]
}
```

## Observability

### Structured Logging

**Log Levels**:
- `DEBUG`: Signature validation steps
- `INFO`: Successful processing
- `WARNING`: No subscription found, unknown status codes
- `ERROR`: Validation failures, processing errors

**Example Log**:
```json
{
  "timestamp": "2026-01-10T09:30:15Z",
  "level": "Information",
  "message": "Successfully processed webhook and published event",
  "CorrelationId": "FO-12345_PKG-001",
  "Carrier": "USPS",
  "TrackingNumber": "9400****2136",
  "Status": "OutForDelivery",
  "MessageId": "sns-msg-123",
  "DurationMs": 87
}
```

### Custom Metrics

- `WebhooksReceived`: Count by carrier
- `WebhooksProcessed`: Count by carrier, status
- `ValidationFailures`: Count by carrier, reason
- `StatusTranslations`: Count by carrier, canonical status
- `ProcessingDuration`: Milliseconds by carrier

### CloudWatch Alarms

- API 4xx error rate > 50 per 5 minutes
- API 5xx error rate > 10 per 5 minutes
- Lambda error rate > 20 per 5 minutes
- Processing duration > 25 seconds

## Troubleshooting

### Common Issues

**Issue**: 401 Unauthorized responses
- **Cause**: Invalid signature or expired timestamp
- **Solution**: Verify webhook secret, check timestamp generation

**Issue**: 400 Bad Request responses
- **Cause**: Invalid JSON or missing required fields
- **Solution**: Review carrier payload format, update parser

**Issue**: No events published
- **Cause**: Subscription not found in DynamoDB
- **Solution**: Verify Subscriber Lambda created subscription first

**Issue**: Unknown status translations
- **Cause**: New carrier status code not mapped
- **Solution**: Add mapping to `StatusTranslationService`

## Related Documentation

- [Implementation Guide](./IMPLEMENTATION_GUIDE.md)
- [Webhook Specifications](./WEBHOOK_SPECIFICATIONS.md)
- [Status Mapping](./STATUS_MAPPING.md)
- [Testing Strategy](./TESTING_STRATEGY.md)
