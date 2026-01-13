# USPS Tracking API v3 Integration

## Overview

This document describes the integration with USPS Tracking API v3 for webhook-based shipment tracking subscriptions. The implementation uses OAuth 2.0 for authentication and supports real-time tracking updates via webhooks.

## API Endpoints

### Production
- **Base URL**: `https://api.usps.com`
- **OAuth Endpoint**: `POST /oauth2/v3/token`
- **Subscription Endpoint**: `POST /subscriptions/v3/subscriptions`
- **Unsubscribe Endpoint**: `DELETE /subscriptions/v3/subscriptions/{subscriptionId}`

### Test/CAT Environment
- **Base URL**: `https://api-cat.usps.com`
- **OAuth Endpoint**: `POST /oauth2/v3/token`
- **Subscription Endpoint**: `POST /subscriptions/v3/subscriptions`
- **Unsubscribe Endpoint**: `DELETE /subscriptions/v3/subscriptions/{subscriptionId}`

## Authentication

### OAuth 2.0 Client Credentials Flow

The USPS API v3 uses OAuth 2.0 with client credentials grant type.

#### Token Request

```http
POST /oauth2/v3/token
Content-Type: application/json

{
  "grant_type": "client_credentials",
  "client_id": "{clientId}",
  "client_secret": "{clientSecret}",
  "scope": "subscriptions"
}
```

#### Token Response

```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...",
  "token_type": "Bearer",
  "expires_in": 3600,
  "scope": "subscriptions"
}
```

#### Token Caching

Tokens are cached with the following strategy:
- Token expiration is tracked based on `expires_in` response field
- Tokens are refreshed automatically when within 5 minutes of expiration
- Cache key: `{environment}/carrier/usps/access-token`

## Subscription API

### Create Subscription

#### Request

```http
POST /subscriptions/v3/subscriptions
Authorization: Bearer {access_token}
Content-Type: application/json

{
  "listenerURL": "https://carriertracking.healthdyne.com/webhooks/usps",
  "secret": "your-32-byte-webhook-secret-here",
  "adminNotification": [
    {
      "email": "admin@example.com"
    }
  ],
  "filterProperties": {
    "trackingNumber": "9400111899562537883943",
    "trackingEventTypes": ["ALL_UPDATES"]
  }
}
```

**Important Notes:**
- `secret` must be exactly 32 bytes (characters)
- `listenerURL` must be publicly accessible via HTTPS
- `trackingEventTypes` supports `["ALL_UPDATES"]` or specific event types
- `adminNotification` is optional

#### Response

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "listenerURL": "https://carriertracking.healthdyne.com/webhooks/usps",
  "secret": "your-32-byte-webhook-secret-here",
  "status": "ACTIVE",
  "statusReason": "Subscription is active",
  "creationTimestamp": "2024-01-15T10:00:00Z",
  "expirationTimestamp": "2024-02-15T10:00:00Z",
  "filterProperties": {
    "trackingNumber": "9400111899562537883943",
    "trackingEventTypes": ["ALL_UPDATES"]
  }
}
```

#### Status Values

- **ACTIVE**: Subscription is active and receiving updates
- **DISABLED**: USPS cannot reach the webhook endpoint (initial status is common)
- **EXPIRED**: Subscription has expired

**Note**: It's normal for subscriptions to initially have `DISABLED` status. USPS will change the status to `ACTIVE` once they successfully verify the webhook endpoint is reachable.

### Delete Subscription

```http
DELETE /subscriptions/v3/subscriptions/{subscriptionId}
Authorization: Bearer {access_token}
```

Returns HTTP 200/204 on success.

## Credentials Configuration

Credentials are stored in AWS Secrets Manager with the following structure:

### Secret Name
```
{environment}/carrier/usps/credentials
```

### Secret Structure
```json
{
  "clientId": "your-client-id",
  "clientSecret": "your-client-secret",
  "webhookSecret": "your-32-byte-webhook-secret"
}
```

**Important**:
- `webhookSecret` must be exactly 32 bytes
- Same webhook secret is used for both subscription requests and HMAC validation
- All credentials are cached for 5 minutes to reduce API calls

## Error Handling

### Error Classification

Errors are classified into three categories for retry logic:

1. **Permanent Errors** (Do not retry)
   - 400 Bad Request - Invalid tracking number or malformed request
   - 401 Unauthorized - Invalid credentials
   - 403 Forbidden - Insufficient permissions
   - 404 Not Found - Resource not found
   - 409 Conflict - Duplicate subscription
   - 422 Unprocessable Entity - Invalid data

2. **Transient Errors** (Retry with backoff)
   - 500 Internal Server Error
   - 502 Bad Gateway
   - 503 Service Unavailable
   - 504 Gateway Timeout
   - Network errors
   - Timeouts

3. **Rate Limit Errors** (Retry with exponential backoff)
   - 429 Too Many Requests

### Example Error Response

```json
{
  "errors": [
    {
      "code": "INVALID_TRACKING_NUMBER",
      "message": "The tracking number format is invalid",
      "field": "filterProperties.trackingNumber"
    }
  ]
}
```

## Implementation Details

### Tracking Number Validation

USPS tracking numbers follow these patterns:
- 20-22 digit numbers starting with specific prefixes (94, 93, 92, etc.)
- International format: `[A-Z]{2}\d{9}US`

Example valid tracking numbers:
- `9400111899562537883943` (Priority Mail)
- `9205596900128506542592` (First-Class Package)
- `EA123456789US` (International)

### Subscription Workflow

1. Validate tracking number format
2. Retrieve USPS credentials from Secrets Manager
3. Obtain OAuth access token (or use cached token)
4. Build subscription request with webhook URL and secret
5. Send POST request to USPS subscription endpoint
6. Store subscription details in DynamoDB (even if initially DISABLED)
7. USPS will update status to ACTIVE once webhook endpoint is verified

### Configuration

Environment variables and configuration keys:

```bash
ENV=dev|prod                           # Environment (determines API base URL)
USPS_ADMIN_EMAIL=admin@example.com     # Optional admin notification email
ALLOWED_SKEW_SECONDS=300               # Timestamp validation window (default: 5 minutes)
```

## Testing

### Test Tracking Numbers

USPS provides test tracking numbers in their sandbox environment:
- `9400111899562537883943` - Delivered
- `9205596900128506542592` - In Transit
- `9361289672090175041871` - Out for Delivery

### Webhook Testing

To test webhook integration:
1. Subscribe to a test tracking number
2. USPS will send a verification webhook to your endpoint
3. Validator must successfully verify HMAC signature
4. Status will change from DISABLED to ACTIVE

## Security Considerations

1. **Webhook Secret**
   - Must be exactly 32 bytes
   - Store securely in AWS Secrets Manager
   - Rotate periodically for security

2. **OAuth Credentials**
   - Client ID and secret are sensitive
   - Never log or expose in error messages
   - Cache tokens to minimize authentication requests

3. **HTTPS Required**
   - Webhook listener URL must use HTTPS
   - USPS will not send webhooks to HTTP endpoints

4. **HMAC Validation**
   - All incoming webhooks must pass HMAC signature validation
   - Reject webhooks with invalid or missing signatures
   - Use constant-time comparison to prevent timing attacks

## Troubleshooting

### Subscription Status is DISABLED

**Cause**: USPS cannot reach your webhook endpoint.

**Solutions**:
- Verify webhook URL is publicly accessible via HTTPS
- Check firewall and security group settings
- Ensure webhook validator is correctly implemented
- Review CloudWatch logs for incoming webhook attempts

### OAuth Token Errors

**Cause**: Invalid credentials or expired token.

**Solutions**:
- Verify credentials in Secrets Manager are correct
- Check if client secret has been rotated
- Invalidate token cache and retry
- Contact USPS support if credentials are correct

### Subscription Conflict (409)

**Cause**: Subscription already exists for this tracking number.

**Solutions**:
- Check DynamoDB for existing subscription
- Delete old subscription if no longer needed
- Wait for existing subscription to expire

### Invalid Tracking Number

**Cause**: Tracking number format doesn't match USPS patterns.

**Solutions**:
- Verify tracking number is correct
- Check if tracking number is from a different carrier
- Ensure no whitespace or special characters
- Test with known valid USPS tracking numbers

## References

- [USPS Tracking API Documentation](https://developer.usps.com/)
- [OAuth 2.0 Client Credentials Flow](https://oauth.net/2/grant-types/client-credentials/)
- [HMAC-SHA256 Specification](https://tools.ietf.org/html/rfc2104)
