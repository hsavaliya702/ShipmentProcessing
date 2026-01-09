# Webhook Specifications

## Overview

This document specifies the webhook formats, authentication methods, and integration requirements for each carrier supported by the Shipment Tracking Service.

## General Webhook Architecture

### Endpoint Structure

```
Base URL: https://tracking-webhook.example.com
Pattern: POST /webhook/{carrier}
```

**Supported Carriers**:
- `/webhook/usps` - USPS webhook handler
- `/webhook/ups` - UPS webhook handler (future)
- `/webhook/fedex` - FedEx webhook handler (future)

### Common Requirements

All carrier webhooks must:
1. **Use HTTPS** with TLS 1.2 or higher
2. **Include authentication headers** (signature-based)
3. **Send POST requests** with JSON payload
4. **Include timestamp** for replay protection
5. **Retry failed requests** with exponential backoff

### Response Codes

| Code | Meaning | Carrier Action |
|------|---------|----------------|
| 200 | Success - Webhook processed | Stop retrying |
| 400 | Bad Request - Invalid payload | Stop retrying (permanent error) |
| 401 | Unauthorized - Invalid signature | Stop retrying (check credentials) |
| 500 | Internal Server Error | Retry with backoff |
| 503 | Service Unavailable | Retry with backoff |

## USPS Webhook Specification

### Authentication Method

**HMAC-SHA256 Signature Validation**

USPS includes a signature in the webhook request headers that we must validate to ensure authenticity.

### Request Format

**Endpoint**: `POST /webhook/usps`

**Headers**:
```http
Content-Type: application/json
X-USPS-Signature: base64(HMAC-SHA256(timestamp + "." + body, webhook_secret))
X-USPS-Timestamp: 1704804600  (Unix timestamp in seconds)
X-USPS-Event-Type: tracking.status_update
User-Agent: USPS-Webhook/1.0
```

**Signature Computation**:
```python
import hmac
import hashlib
import base64

# Components
timestamp = "1704804600"
payload = '{"trackingNumber":"9400116901490039382136","status":"Delivered"}'
webhook_secret = "your-webhook-secret"

# Compute signature
message = f"{timestamp}.{payload}"
signature = base64.b64encode(
    hmac.new(
        webhook_secret.encode('utf-8'),
        message.encode('utf-8'),
        hashlib.sha256
    ).digest()
).decode('utf-8')

# Include in request
headers = {
    "X-USPS-Signature": signature,
    "X-USPS-Timestamp": timestamp
}
```

### Payload Schema

**Status Update Event**:
```json
{
  "trackingNumber": "9400116901490039382136",
  "status": "Out for Delivery",
  "statusCode": "04",
  "statusCategory": "IN_TRANSIT",
  "statusSummary": "Your item is out for delivery on January 10, 2026",
  "eventTimestamp": "2026-01-10T09:30:00Z",
  "eventType": "status_update",
  "eventCity": "Springfield",
  "eventState": "IL",
  "eventZip": "62701",
  "facilityName": "SPRINGFIELD POST OFFICE",
  "expectedDeliveryDate": "2026-01-10T17:00:00Z",
  "serviceType": "Priority Mail",
  "destinationCity": "Springfield",
  "destinationState": "IL",
  "destinationZip": "62701"
}
```

**Field Definitions**:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| trackingNumber | string | Yes | USPS tracking number |
| status | string | Yes | Human-readable status |
| statusCode | string | Yes | USPS status code (01-99) |
| statusCategory | string | No | Status category (PRE_TRANSIT, IN_TRANSIT, etc.) |
| statusSummary | string | No | Detailed status description |
| eventTimestamp | string (ISO 8601) | Yes | When the event occurred |
| eventType | string | Yes | Type of event (status_update, exception, etc.) |
| eventCity | string | No | City where event occurred |
| eventState | string | No | State where event occurred |
| eventZip | string | No | ZIP code where event occurred |
| facilityName | string | No | USPS facility name |
| expectedDeliveryDate | string (ISO 8601) | No | Estimated delivery date/time |
| serviceType | string | No | Mail class (Priority Mail, First-Class, etc.) |
| destinationCity | string | No | Final destination city |
| destinationState | string | No | Final destination state |
| destinationZip | string | No | Final destination ZIP |

### USPS Status Codes

See [STATUS_MAPPING.md](./STATUS_MAPPING.md) for complete mapping of USPS status codes to canonical statuses.

**Common Status Codes**:
- `01` - Pre-Shipment Info Sent to USPS
- `03` - Accepted at USPS Facility
- `04` - Out for Delivery
- `05` - Delivered
- `06` - Delivery Attempted - Notice Left
- `08` - Available for Pickup
- `09` - Item Being Returned
- `10` - Delivered to Agent
- `11` - Held in Customs
- `12` - Refused by Addressee

### Validation Requirements

**Timestamp Validation**:
- Accept webhooks within ±5 minutes of current time
- Reject older webhooks to prevent replay attacks
- Use UTC for all timestamp comparisons

**Signature Validation**:
- Extract `X-USPS-Signature` and `X-USPS-Timestamp` headers
- Reconstruct message: `{timestamp}.{body}`
- Compute HMAC-SHA256 with webhook secret
- Compare using constant-time equality check
- Return 401 if signature doesn't match

**Payload Validation**:
- Verify JSON is well-formed
- Check required fields are present
- Validate tracking number format
- Return 400 for invalid payloads

### Error Scenarios

**Missing Signature Header**:
```json
Response: 401 Unauthorized
{
  "success": false,
  "message": "Missing signature header",
  "requestId": "abc-123-def-456"
}
```

**Invalid Signature**:
```json
Response: 401 Unauthorized
{
  "success": false,
  "message": "Invalid signature",
  "requestId": "abc-123-def-456"
}
```

**Expired Timestamp**:
```json
Response: 401 Unauthorized
{
  "success": false,
  "message": "Timestamp outside allowed window",
  "requestId": "abc-123-def-456"
}
```

**Invalid Payload**:
```json
Response: 400 Bad Request
{
  "success": false,
  "message": "Invalid payload format",
  "requestId": "abc-123-def-456"
}
```

### Example Webhook Requests

**Delivered Event**:
```bash
curl -X POST https://tracking-webhook.example.com/webhook/usps \
  -H "Content-Type: application/json" \
  -H "X-USPS-Signature: aGVsbG8gd29ybGQ=" \
  -H "X-USPS-Timestamp: 1704804600" \
  -d '{
    "trackingNumber": "9400116901490039382136",
    "status": "Delivered",
    "statusCode": "05",
    "eventTimestamp": "2026-01-10T14:30:00Z",
    "eventType": "status_update",
    "eventCity": "Springfield",
    "eventState": "IL"
  }'
```

**Expected Response**:
```json
HTTP/1.1 200 OK
Content-Type: application/json

{
  "success": true,
  "message": "Webhook processed successfully",
  "messageId": "sns-msg-123456",
  "requestId": "lambda-req-789"
}
```

## UPS Webhook Specification (Future)

### Authentication Method

**OAuth 2.0 Bearer Token + HMAC Signature**

UPS uses a combination of OAuth bearer tokens and HMAC signatures for webhook authentication.

### Request Format

**Endpoint**: `POST /webhook/ups`

**Headers**:
```http
Content-Type: application/json
Authorization: Bearer {oauth_token}
X-UPS-Signature: base64(HMAC-SHA256(timestamp + body, webhook_secret))
X-UPS-Timestamp: 1704804600
X-UPS-Tracking-Number: 1Z999AA10123456784
```

### Payload Schema (Preliminary)

```json
{
  "trackingNumber": "1Z999AA10123456784",
  "statusType": {
    "code": "D",
    "description": "Delivered"
  },
  "statusCode": "FS",
  "dateTime": "2026-01-10T14:30:00Z",
  "location": {
    "address": {
      "city": "New York",
      "stateProvinceCode": "NY",
      "postalCode": "10001",
      "countryCode": "US"
    }
  },
  "package": {
    "referenceNumber": [{
      "number": "INV-12345",
      "type": "01"
    }]
  }
}
```

**Note**: UPS integration is planned for Phase 2. Specifications will be finalized based on UPS API documentation.

## FedEx Webhook Specification (Future)

### Authentication Method

**API Key + HMAC Signature**

FedEx uses API keys in combination with HMAC signatures.

### Request Format

**Endpoint**: `POST /webhook/fedex`

**Headers**:
```http
Content-Type: application/json
X-FedEx-API-Key: {api_key}
X-FedEx-Signature: base64(HMAC-SHA256(timestamp + body, webhook_secret))
X-FedEx-Timestamp: 1704804600
```

### Payload Schema (Preliminary)

```json
{
  "trackingNumber": "794608631234",
  "status": "DL",
  "statusDescription": "Delivered",
  "scanTimestamp": "2026-01-10T14:30:00-05:00",
  "scanLocation": {
    "city": "Los Angeles",
    "stateOrProvinceCode": "CA",
    "postalCode": "90001",
    "countryCode": "US"
  },
  "estimatedDeliveryTimestamp": "2026-01-10T14:30:00-05:00",
  "serviceType": "FEDEX_GROUND"
}
```

**Note**: FedEx integration is planned for Phase 3. Specifications will be finalized based on FedEx API documentation.

## Webhook Security Best Practices

### 1. Signature Validation

**Always validate signatures**:
- Never skip signature validation, even in development
- Use constant-time comparison to prevent timing attacks
- Rotate webhook secrets regularly (every 90 days)

**Implementation**:
```csharp
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

### 2. Timestamp Validation

**Prevent replay attacks**:
- Accept webhooks within ±5 minutes (configurable)
- Store and check webhook IDs for duplicates (optional)
- Use monotonically increasing timestamps

**Implementation**:
```csharp
var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
var now = DateTimeOffset.UtcNow;
var timeDifference = Math.Abs((now - requestTime).TotalSeconds);

if (timeDifference > 300) // 5 minutes
{
    return ValidationResult.Failure("Timestamp outside allowed window", 401);
}
```

### 3. Payload Validation

**Validate all inputs**:
- Check JSON structure
- Verify required fields
- Validate data types and formats
- Sanitize string inputs

**Implementation**:
```csharp
if (string.IsNullOrWhiteSpace(payload.TrackingNumber))
    throw new PayloadParsingException("Missing tracking number");

if (!IsValidTrackingNumber(payload.TrackingNumber))
    throw new PayloadParsingException("Invalid tracking number format");
```

### 4. Rate Limiting

**Protect against abuse**:
- Implement rate limiting per carrier
- Track failed validation attempts
- Block IPs with excessive failures

**AWS WAF Rules** (recommended):
- Rate limit: 1000 requests per 5 minutes per IP
- Block after 10 failed signature validations
- Geo-blocking for non-US traffic (optional)

### 5. Logging and Monitoring

**Log all webhook attempts**:
- Log successful and failed validations
- Include correlation IDs
- Mask sensitive data (tracking numbers partially)
- Monitor validation failure rates

**CloudWatch Metrics**:
- `WebhookValidationFailures` by carrier
- `WebhookProcessingLatency` by carrier
- `InvalidSignatureCount` per hour
- `ExpiredTimestampCount` per hour

## Testing Webhooks

### Local Testing

**Generate valid signature**:
```python
import hmac
import hashlib
import base64
import time

def generate_signature(payload, secret):
    timestamp = str(int(time.time()))
    message = f"{timestamp}.{payload}"
    signature = base64.b64encode(
        hmac.new(
            secret.encode('utf-8'),
            message.encode('utf-8'),
            hashlib.sha256
        ).digest()
    ).decode('utf-8')
    return signature, timestamp

# Example
payload = '{"trackingNumber":"9400116901490039382136","status":"Delivered"}'
secret = "test-webhook-secret"
signature, timestamp = generate_signature(payload, secret)

print(f"X-USPS-Signature: {signature}")
print(f"X-USPS-Timestamp: {timestamp}")
```

**Test with curl**:
```bash
# Set variables
PAYLOAD='{"trackingNumber":"9400116901490039382136","status":"Delivered","statusCode":"05","eventTimestamp":"2026-01-10T14:30:00Z"}'
TIMESTAMP=$(date +%s)
SECRET="test-webhook-secret"
MESSAGE="${TIMESTAMP}.${PAYLOAD}"
SIGNATURE=$(echo -n "$MESSAGE" | openssl dgst -sha256 -hmac "$SECRET" -binary | base64)

# Send request
curl -X POST https://your-api-gateway-url/webhook/usps \
  -H "Content-Type: application/json" \
  -H "X-USPS-Signature: $SIGNATURE" \
  -H "X-USPS-Timestamp: $TIMESTAMP" \
  -d "$PAYLOAD"
```

### Integration Testing

Use sample payloads from `/tests/test-data/`:

```bash
# Test USPS delivered event
./scripts/test-webhook.sh usps delivered

# Test USPS out-for-delivery event
./scripts/test-webhook.sh usps out-for-delivery

# Test invalid signature
./scripts/test-webhook.sh usps delivered --invalid-signature
```

## Carrier Onboarding Checklist

When adding a new carrier:

- [ ] Review carrier's webhook documentation
- [ ] Determine authentication method
- [ ] Document payload schema
- [ ] Create `IWebhookValidator` implementation
- [ ] Create `ICarrierPayloadParser` implementation
- [ ] Add status code mappings
- [ ] Update API Gateway routes
- [ ] Create test payloads
- [ ] Write unit tests
- [ ] Write integration tests
- [ ] Update this documentation
- [ ] Deploy to dev environment
- [ ] Test with carrier sandbox
- [ ] Get carrier approval
- [ ] Deploy to production
- [ ] Monitor for errors

## Troubleshooting

### Common Issues

**Issue**: Webhooks failing validation
- **Check**: Secret is correct in Secrets Manager
- **Check**: Timestamp is within allowed window
- **Check**: Signature computation matches carrier's method

**Issue**: Webhooks not being received
- **Check**: Carrier has correct webhook URL
- **Check**: API Gateway is deployed
- **Check**: WAF rules not blocking requests

**Issue**: Duplicate webhook processing
- **Check**: Idempotency handling in downstream consumers
- **Check**: Lambda not being invoked multiple times
- **Check**: Carrier retry logic

## Related Documentation

- [Architecture](./ARCHITECTURE.md)
- [Implementation Guide](./IMPLEMENTATION_GUIDE.md)
- [Status Mapping](./STATUS_MAPPING.md)
- [Testing Strategy](./TESTING_STRATEGY.md)
