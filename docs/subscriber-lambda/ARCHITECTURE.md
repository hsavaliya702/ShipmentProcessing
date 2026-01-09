# Subscriber Lambda - Architecture

## Overview

The Subscriber Lambda is an SNS-triggered AWS Lambda function that processes fulfillment order shipped events and subscribes to carrier tracking webhooks. It serves as the entry point for shipment tracking, initiating the webhook subscription flow with carrier APIs.

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                    SUBSCRIBER LAMBDA                             │
│                                                                   │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │                    Function Handler                         │ │
│  │  - Receives SNS events                                      │ │
│  │  - Parses fulfillment-order-shipped events                 │ │
│  │  - Creates cancellation tokens                              │ │
│  └────────────────┬───────────────────────────────────────────┘ │
│                   │                                               │
│                   ▼                                               │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │         ShipmentNotificationHandler                         │ │
│  │  - Validates event structure                                │ │
│  │  - Processes multi-package shipments                        │ │
│  │  - Orchestrates subscription for each package               │ │
│  └────────────────┬───────────────────────────────────────────┘ │
│                   │                                               │
│                   ▼                                               │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │              SubscriptionService                            │ │
│  │  - Idempotency check (DynamoDB)                            │ │
│  │  - Max retry attempts validation                            │ │
│  │  - Coordinates carrier client calls                         │ │
│  │  - Updates subscription records                             │ │
│  └────────┬───────────────────────┬───────────────────────────┘ │
│           │                       │                               │
│           ▼                       ▼                               │
│  ┌──────────────────┐   ┌──────────────────────────────────┐   │
│  │CarrierFactory    │   │ SubscriptionRepository           │   │
│  │  - Creates       │   │  - DynamoDB CRUD operations      │   │
│  │    carrier       │   │  - Conditional writes            │   │
│  │    clients       │   │  - TTL management                │   │
│  └────────┬─────────┘   └──────────────────────────────────┘   │
│           │                                                       │
│           ▼                                                       │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │     ICarrierSubscriptionClient                            │   │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐   │   │
│  │  │ USPS Client  │  │  UPS Client  │  │ FedEx Client │   │   │
│  │  │  - Auth      │  │  (Future)    │  │  (Future)    │   │   │
│  │  │  - Subscribe │  │              │  │              │   │   │
│  │  │  - Retry     │  │              │  │              │   │   │
│  │  └──────────────┘  └──────────────┘  └──────────────┘   │   │
│  └──────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────┘
```

## Component Responsibilities

### Function.cs (Lambda Handler)

**Purpose**: AWS Lambda entry point and dependency injection setup

**Key Responsibilities**:
- Initialize DI container with all services
- Configure AWS SDK clients (DynamoDB, Secrets Manager)
- Setup HTTP clients with Polly retry policies
- Handle Lambda lifecycle (cold starts)
- Create cancellation tokens from Lambda context

**Configuration**:
- Environment variables: `DYNAMODB_TABLE_NAME`, `WEBHOOK_BASE_URL`, `USPS_API_BASE_URL`
- Memory: 512 MB (configurable)
- Timeout: 60 seconds (configurable)

### ShipmentNotificationHandler

**Purpose**: Process SNS events containing fulfillment order shipped notifications

**Key Responsibilities**:
- Parse SNS message body to `FulfillmentOrderShippedEvent`
- Validate event structure (required fields, package data)
- Iterate through packages and process each independently
- Build callback URLs for carrier webhooks
- Aggregate results and error handling

**Error Handling**:
- Parse errors: Return failure but don't retry (permanent error)
- Validation errors: Log warnings and skip invalid packages
- Processing errors: Propagate for Lambda retry if transient

### SubscriptionService

**Purpose**: Core business logic for subscription management

**Key Responsibilities**:
- **Idempotency Check**: Query DynamoDB for existing subscription
  - If Active: Return success immediately (skip duplicate)
  - If at max attempts: Return failure (don't retry)
- **Carrier Validation**: Check if carrier is supported
- **Subscription Attempt**: Call carrier API via client
- **Record Management**: Create or update DynamoDB record
- **Error Classification**: Determine if error is transient or permanent

**Idempotency Algorithm**:
```csharp
1. Check DynamoDB for existing subscription record
2. IF record exists AND status == Active:
     RETURN Success (idempotent - already subscribed)
3. IF record exists AND attemptCount >= MAX_ATTEMPTS:
     RETURN Failure (max retries exceeded)
4. Increment attempt count
5. Call carrier API to subscribe
6. IF success:
     Save Active record with conditional write
     RETURN Success
7. ELSE:
     Save Failed record with error details
     RETURN Failure (with retry flag based on error type)
```

### CarrierSubscriptionClientFactory

**Purpose**: Factory pattern for creating carrier-specific clients

**Key Responsibilities**:
- Map carrier code (USPS, UPS, FedEx) to implementation
- Resolve client from DI container
- Validate carrier support
- Provide list of supported carriers

**Extensibility**: Adding new carriers requires:
1. Implement `ICarrierSubscriptionClient`
2. Register in DI container (Function.cs)
3. Add to factory mapping

### ICarrierSubscriptionClient (Interface)

**Contract**:
```csharp
string CarrierCode { get; }
Task<CarrierSubscriptionResponse> SubscribeAsync(request, cancellationToken)
Task<bool> UnsubscribeAsync(subscriptionId, cancellationToken)
bool IsValidTrackingNumber(trackingNumber)
```

**Implementations**:
- **UspsSubscriptionClient**: USPS Tracking API integration
  - HMAC signature authentication
  - Exponential backoff with Polly
  - Tracking number validation (regex)
  - Error classification (transient vs permanent)

### SubscriptionRepository

**Purpose**: DynamoDB operations with concurrency control

**Key Responsibilities**:
- **GetSubscription**: Retrieve existing subscription record
- **CreateOrUpdate**: Conditional write to prevent race conditions
- **UpdateStatus**: Update subscription status (Active, Failed, Expired)
- **IncrementAttemptCount**: Track retry attempts
- **TTL Management**: Set expiration (90 days default)

**DynamoDB Schema**:
```
PK: TRACK#{Carrier}#{TrackingNumber}
SK: META

Attributes:
- Carrier, TrackingNumber
- FulfillmentOrderId, OrderId, PackageId (correlation)
- SubscriptionStatus (Active, Pending, Failed, Expired)
- CarrierSubscriptionId (from carrier API)
- CallbackUrl
- AttemptCount, LastError, ErrorType
- CreatedAt, UpdatedAt, ActivatedAt
- TTL (Unix epoch seconds)
- Metadata (Map)
```

**Conditional Write Expression**:
```
attribute_not_exists(PK) OR 
(SubscriptionStatus <> :active AND 
 (SubscriptionStatus = :pending OR SubscriptionStatus = :failed))
```

This prevents:
- Overwriting active subscriptions
- Concurrent updates corrupting data
- Race conditions in distributed processing

## Design Patterns

### 1. Dependency Injection
All components use constructor injection for testability:
```csharp
public SubscriptionService(
    CarrierSubscriptionClientFactory clientFactory,
    SubscriptionRepository repository,
    ILogger<SubscriptionService> logger)
```

### 2. Factory Pattern
`CarrierSubscriptionClientFactory` creates carrier-specific clients:
```csharp
var client = _clientFactory.CreateClient("USPS");
```

### 3. Strategy Pattern
Different carrier implementations behind common interface:
```csharp
ICarrierSubscriptionClient
├── UspsSubscriptionClient
├── UpsSubscriptionClient (future)
└── FedExSubscriptionClient (future)
```

### 4. Repository Pattern
Data access abstracted behind repository:
```csharp
var record = await _repository.GetSubscriptionAsync(carrier, trackingNumber);
```

### 5. Circuit Breaker (Polly)
Resilient HTTP calls to carrier APIs:
```csharp
.AddPolicyHandler(GetRetryPolicy())          // 3 retries with exponential backoff
.AddPolicyHandler(GetCircuitBreakerPolicy()) // Break after 5 failures for 30s
```

## Error Handling Strategy

### Error Classification

**Transient Errors** (Lambda will retry):
- Network timeouts
- HTTP 5xx errors
- Rate limiting (429)
- Circuit breaker open

**Permanent Errors** (No retry, mark as failed):
- Invalid tracking number (400)
- Authentication failure (401, 403)
- Not found (404)
- Validation errors (422)

### Retry Logic

**Lambda Automatic Retry**:
- Maximum attempts: 2 (configurable)
- Retry on unhandled exceptions
- Dead letter queue after max retries

**Application Retry** (Polly):
- HTTP client: 3 attempts with decorrelated jitter
- Backoff: 1s median, max 60s
- Circuit breaker: 5 failures → 30s break

**DynamoDB Tracking**:
- `AttemptCount` field tracks retry attempts
- Max 3 attempts before marking as permanently failed
- Each attempt logged with timestamp

## Performance Characteristics

### Cold Start
- **Duration**: 800ms - 1.5s
- **Optimization**: 
  - Lazy initialization of AWS clients
  - Minimal dependencies in constructor
  - AOT compilation with `PublishReadyToRun`

### Warm Execution
- **Single package**: 100-300ms
- **Multi-package (5)**: 300-800ms
- **Bottleneck**: Carrier API latency (50-200ms)

### Concurrency
- **Reserved concurrent executions**: 100 (configurable)
- **Typical usage**: 10-50 concurrent executions
- **Burst capacity**: Can scale to 1000+ (account limit)

### Memory Usage
- **Allocated**: 512 MB
- **Typical usage**: 100-150 MB
- **Recommendation**: 512 MB (balance cost vs performance)

## Security Considerations

### IAM Policy (Least Privilege)

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "dynamodb:GetItem",
        "dynamodb:PutItem",
        "dynamodb:UpdateItem"
      ],
      "Resource": "arn:aws:dynamodb:*:*:table/ShipmentTrackingSubscriptions"
    },
    {
      "Effect": "Allow",
      "Action": ["secretsmanager:GetSecretValue"],
      "Resource": "arn:aws:secretsmanager:*:*:secret:carrier/*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "logs:CreateLogGroup",
        "logs:CreateLogStream",
        "logs:PutLogEvents"
      ],
      "Resource": "*"
    }
  ]
}
```

### Secrets Management

**Carrier API Credentials**:
- Stored in AWS Secrets Manager
- Secret name: `carrier/usps/credentials`
- JSON format: `{"apiKey":"...", "clientId":"...", "clientSecret":"..."}`
- Cached in memory for 5 minutes
- Automatic rotation support

### Data Protection

**In Transit**:
- HTTPS for all carrier API calls
- TLS 1.2+ required

**At Rest**:
- DynamoDB server-side encryption (KMS)
- CloudWatch Logs encryption

## Observability

### Structured Logging

**Log Levels**:
- `DEBUG`: Detailed flow information
- `INFO`: Key events (subscription created, already exists)
- `WARNING`: Validation failures, carrier errors
- `ERROR`: Unexpected exceptions

**Correlation IDs**:
- Format: `{FulfillmentOrderId}_{PackageId}`
- Propagated to all log entries
- Used for distributed tracing

**Example Log Entry**:
```json
{
  "timestamp": "2026-01-09T12:00:00Z",
  "level": "Information",
  "message": "Successfully processed package subscription",
  "CorrelationId": "FO-12345_PKG-001",
  "Carrier": "USPS",
  "TrackingNumber": "9400****0000",
  "SubscriptionId": "sub_abc123",
  "DurationMs": 245
}
```

### Custom Metrics

**Business Metrics**:
- `SubscriptionAttempts`: Count by carrier, status (success/failure)
- `SubscriptionDuration`: Processing time per package
- `IdempotentSkips`: Already active subscriptions
- `MaxAttemptsReached`: Failed after retries

**Technical Metrics**:
- `CarrierAPIErrors`: Count by carrier, error type
- `DynamoDBOperations`: Count by operation type
- `SecretsManagerCalls`: Cache hit/miss ratio

### CloudWatch Alarms

**Critical**:
- DLQ message count > 0
- Error rate > 10 per 5 minutes
- Duration > 50 seconds (approaching timeout)

**Warning**:
- Error rate > 5 per 5 minutes
- Duration > 30 seconds
- Throttled requests > 0

## Deployment

### Infrastructure (SAM Template)

**Resource Requirements**:
- Lambda function (512 MB, 60s timeout)
- DynamoDB table (on-demand)
- SNS topic (input)
- SQS queue (DLQ)
- CloudWatch log group
- IAM role with policies

**Environment Variables**:
- `DYNAMODB_TABLE_NAME`: Subscription records table
- `WEBHOOK_BASE_URL`: Base URL for carrier callbacks
- `USPS_API_BASE_URL`: USPS API endpoint

**Tags**:
- `Environment`: dev/uat/prod
- `Application`: ShipmentTracking
- `Component`: Subscriber

### CI/CD Pipeline

**Build Stage**:
1. Restore NuGet packages
2. Build with Release configuration
3. Run unit tests
4. Code coverage report

**Security Stage**:
1. CodeQL analysis
2. Dependency scanning
3. Secret scanning

**Deploy Stage** (per environment):
1. SAM build with container
2. SAM deploy with parameters
3. Smoke tests
4. Rollback on failure

## Testing Strategy

See [TESTING_STRATEGY.md](./TESTING_STRATEGY.md) for comprehensive testing approach.

## Troubleshooting

### Common Issues

**Issue**: Subscriptions failing with "Max attempts reached"
- **Cause**: Carrier API repeatedly failing
- **Solution**: Check carrier API status, verify credentials, review error logs

**Issue**: Duplicate subscriptions despite idempotency
- **Cause**: Race condition in conditional write
- **Solution**: This should not happen with current implementation; investigate DynamoDB timestamps

**Issue**: High cold start times
- **Cause**: Lambda initialization overhead
- **Solution**: Use provisioned concurrency or increase invocation frequency

**Issue**: DLQ messages accumulating
- **Cause**: Permanent errors not being caught
- **Solution**: Review error classification logic, add more specific error handling

## Related Documentation

- [Implementation Guide](./IMPLEMENTATION_GUIDE.md)
- [API Specifications](./API_SPECIFICATIONS.md)
- [Data Model](./DATA_MODEL.md)
- [Testing Strategy](./TESTING_STRATEGY.md)
