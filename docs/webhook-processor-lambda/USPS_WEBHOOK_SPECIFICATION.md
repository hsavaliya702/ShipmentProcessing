# USPS Webhook Specification (API v3)

## Overview

This document describes the USPS webhook notification structure and validation requirements for API v3. USPS sends webhook notifications to the registered `listenerURL` when tracking events occur.

## Webhook Structure

### Notification Envelope

USPS webhooks use a two-level JSON structure:
1. **Outer envelope** - Contains metadata and subscription information
2. **Inner payload** - Contains actual tracking data as an escaped JSON string

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "subscriptionType": "TRACKING",
  "timestamp": "2024-01-15T14:30:00Z",
  "payload": "{\"trackingNumber\":\"9400111899562537883943\",\"statusCategory\":\"Delivered\",\"statusSummary\":\"Your item was delivered\",\"mailClass\":\"Priority Mail\",\"trackingEvents\":[{\"eventCode\":\"01\",\"eventType\":\"Delivered\",\"eventTimestamp\":\"2024-01-15T14:30:00Z\",\"eventCity\":\"NEW YORK\",\"eventState\":\"NY\",\"eventZIPCode\":\"10001\"}]}"
}
```

### Tracking Data (Payload Content)

When the `payload` field is deserialized, it contains:

```json
{
  "trackingNumber": "9400111899562537883943",
  "statusCategory": "Delivered",
  "statusSummary": "Your item was delivered at the front door",
  "mailClass": "Priority Mail",
  "serviceTypeCode": "PM",
  "destinationZIPCode": "10001",
  "trackingEvents": [
    {
      "eventCode": "01",
      "eventType": "Delivered",
      "eventTimestamp": "2024-01-15T14:30:00Z",
      "eventCity": "NEW YORK",
      "eventState": "NY",
      "eventZIPCode": "10001",
      "eventCountry": "US",
      "facilityName": "NEW YORK NY DISTRIBUTION CENTER",
      "actionCode": "DL",
      "reasonCode": "",
      "recipientName": "JOHN DOE",
      "firm": "ACME CORP"
    }
  ]
}
```

## HMAC Signature Validation

### Overview

USPS signs all webhook notifications using HMAC-SHA256 to ensure authenticity. The signature is sent in the `X-HMAC` header.

### Validation Algorithm

```
1. Extract X-HMAC header from webhook request
2. Parse webhook body to get timestamp and payload fields
3. Concatenate: message = timestamp + payload (string concatenation, no delimiter)
4. Compute HMAC-SHA256 using the 32-byte webhook secret as key
5. Encode result in Base64
6. Compare with X-HMAC header using constant-time comparison
```

### Step-by-Step Example

#### 1. Extract Headers

```http
POST /webhooks/usps
X-HMAC: a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6q7r8s9t0u1v2w3x4y5z6==
Content-Type: application/json
```

#### 2. Parse Webhook Body

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "subscriptionType": "TRACKING",
  "timestamp": "2024-01-15T14:30:00Z",
  "payload": "{\"trackingNumber\":\"9400111899562537883943\"...}"
}
```

#### 3. Construct Message

```
message = timestamp + payload
message = "2024-01-15T14:30:00Z" + "{\"trackingNumber\":\"9400111899562537883943\"...}"
```

Note: String concatenation with NO delimiter or separator.

#### 4. Compute HMAC

```csharp
using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
var expectedHmac = Convert.ToBase64String(hash);
```

#### 5. Compare (Constant-Time)

```csharp
bool isValid = ConstantTimeEquals(providedHmac, expectedHmac);
```

### Implementation Reference

```csharp
private string ComputeUspsHmac(string timestamp, string payload, string secret)
{
    // Concatenate timestamp and payload
    var message = timestamp + payload;
    var messageBytes = Encoding.UTF8.GetBytes(message);
    var keyBytes = Encoding.UTF8.GetBytes(secret);

    using var hmac = new HMACSHA256(keyBytes);
    var hashBytes = hmac.ComputeHash(messageBytes);

    // Encode in Base64
    return Convert.ToBase64String(hashBytes);
}

private static bool ConstantTimeEquals(string a, string b)
{
    if (a == null || b == null || a.Length != b.Length)
    {
        return false;
    }

    var result = 0;
    for (var i = 0; i < a.Length; i++)
    {
        result |= a[i] ^ b[i];
    }

    return result == 0;
}
```

## Timestamp Validation

### Purpose

Prevent replay attacks by ensuring webhooks are recent.

### Validation Rules

1. Parse timestamp from webhook body (ISO 8601 format)
2. Convert to UTC
3. Compare with current UTC time
4. Reject if difference exceeds allowed skew (default: 5 minutes)

### Implementation

```csharp
private bool ValidateTimestamp(string timestamp)
{
    if (!DateTime.TryParse(timestamp, out var webhookTime))
    {
        return false;
    }

    var now = DateTime.UtcNow;
    var difference = Math.Abs((now - webhookTime.ToUniversalTime()).TotalSeconds);

    return difference <= _allowedTimestampSkewSeconds; // Default: 300 seconds
}
```

### Configuration

Adjust allowed skew via environment variable:

```bash
ALLOWED_SKEW_SECONDS=300  # 5 minutes (default)
```

## Event Codes

### Common Event Codes

| Code | Event Type | Description |
|------|-----------|-------------|
| 01 | Delivered | Package delivered |
| 02 | Out for Delivery | Package is out for delivery |
| 03 | In Transit | Package in transit |
| 07 | Arrived at Hub | Arrived at distribution center |
| 08 | Departed | Departed from facility |
| 10 | Acceptance | Package accepted by USPS |
| 11 | Return to Sender | Being returned to sender |
| 12 | Held in Customs | Held in customs |

### Status Categories

- **Delivered** - Package delivered successfully
- **In Transit** - Package is moving through network
- **Out for Delivery** - Package on delivery vehicle
- **Attempted Delivery** - Delivery attempted but not completed
- **Alert** - Issue requiring attention
- **Pre-Shipment** - Label created, awaiting package

## Payload Parsing

### Two-Level Deserialization

The USPS webhook requires **two deserialization passes**:

1. **First pass**: Deserialize outer envelope
   ```csharp
   var notification = JsonSerializer.Deserialize<UspsWebhookNotification>(requestBody);
   ```

2. **Second pass**: Deserialize escaped JSON in payload field
   ```csharp
   var trackingData = JsonSerializer.Deserialize<UspsTrackingData>(notification.Payload);
   ```

### Extracting Latest Event

Multiple tracking events may be present. Extract the most recent:

```csharp
var latestEvent = trackingData.TrackingEvents
    .OrderByDescending(e => ParseTimestamp(e.EventTimestamp))
    .First();
```

### Timestamp Formats

USPS supports two timestamp formats:

1. **With timezone offset**: `2025-02-07T12:55:12-05:00`
2. **UTC (GMT)**: `2025-02-07T17:55:12Z`

Both should be converted to UTC for consistency:

```csharp
if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, 
    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, 
    out var result))
{
    return result.ToUniversalTime();
}
```

## Security Considerations

### 1. HMAC Validation

- **MUST** validate HMAC signature on all incoming webhooks
- **MUST** reject webhooks with missing or invalid signatures
- **MUST** use constant-time comparison to prevent timing attacks
- **MUST NOT** log or expose webhook secret

### 2. Timestamp Validation

- **MUST** validate timestamp is within allowed window
- **RECOMMENDED** window: 5 minutes (300 seconds)
- Prevents replay attacks where attacker reuses old webhooks

### 3. Webhook Secret Management

- **MUST** be exactly 32 bytes
- **MUST** be stored securely in AWS Secrets Manager
- **SHOULD** be rotated periodically
- **MUST NOT** be committed to source control
- **MUST NOT** be logged or exposed in error messages

### 4. Error Handling

When validation fails:
- Return appropriate HTTP status code (401 for auth failure)
- Log validation failure with minimal details
- **DO NOT** include secret or signature in logs
- **DO NOT** return detailed error messages to USPS

### 5. Rate Limiting

Consider implementing rate limiting to prevent abuse:
- Maximum webhooks per minute from USPS
- Maximum webhooks per tracking number
- Alert on unusual spike in webhook traffic

## Response Codes

### Expected Responses

| Status | Meaning | USPS Action |
|--------|---------|-------------|
| 200 | Success | Continue sending webhooks |
| 202 | Accepted | Continue sending webhooks |
| 400 | Bad Request | Log error, may retry |
| 401 | Unauthorized | Invalid signature, stop sending |
| 500 | Server Error | Retry with backoff |
| 503 | Unavailable | Retry with backoff |

### Best Practices

1. Return **200 OK** for successfully processed webhooks
2. Return **401 Unauthorized** for signature validation failures
3. Return **400 Bad Request** for malformed payloads
4. Return **500/503** for temporary processing errors

## Sample Webhook Payloads

### Delivered Package

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "subscriptionType": "TRACKING",
  "timestamp": "2024-01-15T14:30:00Z",
  "payload": "{\"trackingNumber\":\"9400111899562537883943\",\"statusCategory\":\"Delivered\",\"statusSummary\":\"Your item was delivered at the front door or porch at 2:30 pm on January 15, 2024 in NEW YORK, NY 10001.\",\"mailClass\":\"Priority Mail\",\"serviceTypeCode\":\"PM\",\"destinationZIPCode\":\"10001\",\"trackingEvents\":[{\"eventCode\":\"01\",\"eventType\":\"Delivered, Front Door/Porch\",\"eventTimestamp\":\"2024-01-15T14:30:00-05:00\",\"eventCity\":\"NEW YORK\",\"eventState\":\"NY\",\"eventZIPCode\":\"10001\",\"eventCountry\":\"US\",\"facilityName\":\"NEW YORK NY DISTRIBUTION CENTER\",\"actionCode\":\"DL\",\"reasonCode\":\"\",\"recipientName\":\"JOHN DOE\",\"firm\":\"ACME CORP\"}]}"
}
```

### Out for Delivery

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "subscriptionType": "TRACKING",
  "timestamp": "2024-01-15T08:15:00Z",
  "payload": "{\"trackingNumber\":\"9400111899562537883943\",\"statusCategory\":\"Out for Delivery\",\"statusSummary\":\"Your item is out for delivery on January 15, 2024.\",\"mailClass\":\"Priority Mail\",\"serviceTypeCode\":\"PM\",\"destinationZIPCode\":\"10001\",\"trackingEvents\":[{\"eventCode\":\"02\",\"eventType\":\"Out for Delivery\",\"eventTimestamp\":\"2024-01-15T08:15:00-05:00\",\"eventCity\":\"NEW YORK\",\"eventState\":\"NY\",\"eventZIPCode\":\"10001\",\"eventCountry\":\"US\",\"facilityName\":\"NEW YORK NY POST OFFICE\"}]}"
}
```

### In Transit

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "subscriptionType": "TRACKING",
  "timestamp": "2024-01-14T20:45:00Z",
  "payload": "{\"trackingNumber\":\"9400111899562537883943\",\"statusCategory\":\"In Transit\",\"statusSummary\":\"Your item departed from our facility in PHILADELPHIA, PA on January 14, 2024.\",\"mailClass\":\"Priority Mail\",\"serviceTypeCode\":\"PM\",\"destinationZIPCode\":\"10001\",\"trackingEvents\":[{\"eventCode\":\"08\",\"eventType\":\"Departed USPS Regional Facility\",\"eventTimestamp\":\"2024-01-14T20:45:00-05:00\",\"eventCity\":\"PHILADELPHIA\",\"eventState\":\"PA\",\"eventZIPCode\":\"19101\",\"eventCountry\":\"US\",\"facilityName\":\"PHILADELPHIA PA NETWORK DISTRIBUTION CENTER\"}]}"
}
```

## Testing

### Validation Testing

Test cases for HMAC validation:

1. **Valid signature**: Should return 200 OK
2. **Invalid signature**: Should return 401 Unauthorized
3. **Missing X-HMAC header**: Should return 401 Unauthorized
4. **Expired timestamp**: Should return 401 Unauthorized
5. **Malformed JSON**: Should return 400 Bad Request

### Integration Testing

1. Create test subscription in USPS sandbox
2. USPS will send verification webhook
3. Validate HMAC signature
4. Return 200 OK
5. Verify subscription status changes to ACTIVE

### Local Testing

Generate test webhook with valid HMAC:

```bash
# Generate HMAC signature
echo -n '2024-01-15T14:30:00Z{"trackingNumber":"9400111899562537883943"...}' | \
  openssl dgst -sha256 -hmac "your-32-byte-secret-here-abcdef" | \
  awk '{print $2}' | xxd -r -p | base64
```

## Troubleshooting

### Signature Validation Fails

**Symptoms**: All webhooks rejected with 401 error

**Possible Causes**:
- Incorrect webhook secret
- Wrong HMAC algorithm (must be SHA256, not SHA1)
- Incorrect message construction (missing timestamp or payload)
- Not using string concatenation (adding delimiter)
- Not encoding result in Base64

**Solutions**:
- Verify webhook secret is exactly 32 bytes
- Check HMAC computation matches specification
- Ensure timestamp + payload concatenation has no delimiter
- Use Base64 encoding for comparison

### Subscription Status Stays DISABLED

**Symptoms**: Subscription created but never becomes ACTIVE

**Possible Causes**:
- Webhook endpoint not reachable
- HTTPS certificate issues
- Firewall blocking USPS IP ranges
- Webhook validation failing

**Solutions**:
- Verify endpoint is publicly accessible
- Check SSL certificate is valid
- Review security group and firewall rules
- Check CloudWatch logs for incoming requests
- Test HMAC validation with sample payload

### Payload Parsing Errors

**Symptoms**: JSON deserialization fails

**Possible Causes**:
- Only deserializing once (missing second pass)
- Wrong JSON structure in models
- Case sensitivity issues

**Solutions**:
- Ensure two-level deserialization
- Use `PropertyNameCaseInsensitive = true` option
- Verify model properties match USPS structure
- Log raw payload for inspection

## References

- [USPS Developer Portal](https://developer.usps.com/)
- [HMAC-SHA256 RFC](https://tools.ietf.org/html/rfc2104)
- [ISO 8601 Timestamp Format](https://en.wikipedia.org/wiki/ISO_8601)
- [Constant-Time Comparison](https://codahale.com/a-lesson-in-timing-attacks/)
