# Testing Strategy

## Overview

This document outlines the comprehensive testing strategy for the Webhook Processor Lambda, covering unit tests, integration tests, end-to-end tests, and production monitoring.

## Testing Pyramid

```
           ┌─────────────┐
           │   Manual    │  Production validation
           │   Testing   │  Smoke tests
           └─────────────┘
          ┌───────────────┐
          │  End-to-End   │  Full workflow tests
          │     Tests     │  Multi-service tests
          └───────────────┘
        ┌─────────────────────┐
        │  Integration Tests  │  Component interaction
        │   (LocalStack)      │  AWS service mocks
        └─────────────────────┘
      ┌───────────────────────────┐
      │      Unit Tests           │  Individual component tests
      │   (Mocked Dependencies)   │  Fast, isolated
      └───────────────────────────┘
```

## Unit Testing

### Scope

Unit tests validate individual components in isolation with mocked dependencies.

**Coverage Target**: >80% code coverage

**Components to Test**:
- `WebhookHandler` - Orchestration logic
- `UspsWebhookValidator` - Signature validation
- `UspsPayloadParser` - Payload parsing
- `StatusTranslationService` - Status mapping
- `CorrelationService` - DynamoDB interactions
- `EventPublisher` - SNS publishing

### Test Framework

**Tools**:
- xUnit - Test framework
- Moq - Mocking framework
- FluentAssertions - Assertion library
- AutoFixture - Test data generation

**Project Setup**:
```bash
cd src
dotnet new xunit -n ShipmentTracking.WebhookProcessor.Tests
cd ShipmentTracking.WebhookProcessor.Tests

dotnet add reference ../ShipmentTracking.WebhookProcessor/ShipmentTracking.WebhookProcessor.csproj
dotnet add package Moq --version 4.20.0
dotnet add package FluentAssertions --version 6.12.0
dotnet add package AutoFixture --version 4.18.0
```

### Example Unit Tests

#### UspsWebhookValidator Tests

```csharp
public class UspsWebhookValidatorTests
{
    private readonly Mock<SecretManager> _mockSecretManager;
    private readonly Mock<ILogger<UspsWebhookValidator>> _mockLogger;
    private readonly UspsWebhookValidator _validator;
    private const string TestSecret = "test-webhook-secret";

    public UspsWebhookValidatorTests()
    {
        _mockSecretManager = new Mock<SecretManager>();
        _mockSecretManager
            .Setup(m => m.GetSecretStringAsync(
                It.IsAny<string>(), 
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestSecret);

        _mockLogger = new Mock<ILogger<UspsWebhookValidator>>();
        _validator = new UspsWebhookValidator(_mockSecretManager.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task ValidateAsync_WithValidSignature_ReturnsSuccess()
    {
        // Arrange
        var payload = "{\"trackingNumber\":\"9400116901490039382136\"}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeTestSignature(payload, timestamp, TestSecret);

        var headers = new Dictionary<string, string>
        {
            ["X-USPS-Signature"] = signature,
            ["X-USPS-Timestamp"] = timestamp
        };

        // Act
        var result = await _validator.ValidateAsync(payload, headers);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WithInvalidSignature_ReturnsFailure()
    {
        // Arrange
        var payload = "{\"trackingNumber\":\"9400116901490039382136\"}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var invalidSignature = "invalid-signature";

        var headers = new Dictionary<string, string>
        {
            ["X-USPS-Signature"] = invalidSignature,
            ["X-USPS-Timestamp"] = timestamp
        };

        // Act
        var result = await _validator.ValidateAsync(payload, headers);

        // Assert
        result.IsValid.Should().BeFalse();
        result.StatusCode.Should().Be(401);
        result.ErrorMessage.Should().Contain("Invalid signature");
    }

    [Fact]
    public async Task ValidateAsync_WithExpiredTimestamp_ReturnsFailure()
    {
        // Arrange
        var payload = "{\"trackingNumber\":\"9400116901490039382136\"}";
        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        var signature = ComputeTestSignature(payload, expiredTimestamp, TestSecret);

        var headers = new Dictionary<string, string>
        {
            ["X-USPS-Signature"] = signature,
            ["X-USPS-Timestamp"] = expiredTimestamp
        };

        // Act
        var result = await _validator.ValidateAsync(payload, headers);

        // Assert
        result.IsValid.Should().BeFalse();
        result.StatusCode.Should().Be(401);
        result.ErrorMessage.Should().Contain("Timestamp outside allowed window");
    }

    [Theory]
    [InlineData("X-USPS-Signature", "Missing signature header")]
    [InlineData("X-USPS-Timestamp", "Missing timestamp header")]
    public async Task ValidateAsync_WithMissingHeader_ReturnsFailure(
        string missingHeader, 
        string expectedMessage)
    {
        // Arrange
        var payload = "{\"trackingNumber\":\"9400116901490039382136\"}";
        var headers = new Dictionary<string, string>
        {
            ["X-USPS-Signature"] = "signature",
            ["X-USPS-Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
        };
        headers.Remove(missingHeader);

        // Act
        var result = await _validator.ValidateAsync(payload, headers);

        // Assert
        result.IsValid.Should().BeFalse();
        result.StatusCode.Should().Be(401);
        result.ErrorMessage.Should().Contain(expectedMessage);
    }

    private static string ComputeTestSignature(string payload, string timestamp, string secret)
    {
        var message = $"{timestamp}.{payload}";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToBase64String(hmac.ComputeHash(messageBytes));
    }
}
```

#### StatusTranslationService Tests

```csharp
public class StatusTranslationServiceTests
{
    private readonly StatusTranslationService _service;

    public StatusTranslationServiceTests()
    {
        _service = new StatusTranslationService(Mock.Of<ILogger<StatusTranslationService>>());
    }

    [Theory]
    [InlineData("USPS", "01", null, TrackingStatus.PreTransit)]
    [InlineData("USPS", "04", null, TrackingStatus.OutForDelivery)]
    [InlineData("USPS", "05", null, TrackingStatus.Delivered)]
    [InlineData("USPS", "06", null, TrackingStatus.DeliveryAttemptFailed)]
    [InlineData("USPS", "08", null, TrackingStatus.AvailableForPickup)]
    [InlineData("USPS", "DELIVERED", null, TrackingStatus.Delivered)]
    public void TranslateStatus_WithKnownStatus_ReturnsCorrectMapping(
        string carrier,
        string statusCode,
        string? subCode,
        TrackingStatus expected)
    {
        // Act
        var result = _service.TranslateStatus(carrier, statusCode, subCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USPS", "06", "01", TrackingStatus.DeliveryAttemptFailed)]
    [InlineData("USPS", "11", "01", TrackingStatus.OnHold)]
    public void TranslateStatus_WithCompositeKey_UsesSubCode(
        string carrier,
        string statusCode,
        string subCode,
        TrackingStatus expected)
    {
        // Act
        var result = _service.TranslateStatus(carrier, statusCode, subCode);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void TranslateStatus_WithUnknownStatus_ReturnsUnknown()
    {
        // Act
        var result = _service.TranslateStatus("USPS", "99", null);

        // Assert
        result.Should().Be(TrackingStatus.Unknown);
    }

    [Fact]
    public void TranslateStatus_WithUnsupportedCarrier_ReturnsUnknown()
    {
        // Act
        var result = _service.TranslateStatus("UNKNOWN_CARRIER", "01", null);

        // Assert
        result.Should().Be(TrackingStatus.Unknown);
    }
}
```

#### WebhookHandler Tests

```csharp
public class WebhookHandlerTests
{
    private readonly Mock<IWebhookValidator> _mockValidator;
    private readonly Mock<ICarrierPayloadParser> _mockParser;
    private readonly Mock<StatusTranslationService> _mockTranslationService;
    private readonly Mock<CorrelationService> _mockCorrelationService;
    private readonly Mock<EventPublisher> _mockEventPublisher;
    private readonly WebhookHandler _handler;

    public WebhookHandlerTests()
    {
        _mockValidator = new Mock<IWebhookValidator>();
        _mockValidator.Setup(v => v.CarrierCode).Returns("USPS");

        _mockParser = new Mock<ICarrierPayloadParser>();
        _mockParser.Setup(p => p.CarrierCode).Returns("USPS");

        _mockTranslationService = new Mock<StatusTranslationService>(Mock.Of<ILogger<StatusTranslationService>>());
        _mockCorrelationService = new Mock<CorrelationService>();
        _mockEventPublisher = new Mock<EventPublisher>();

        _handler = new WebhookHandler(
            new[] { _mockValidator.Object },
            new[] { _mockParser.Object },
            _mockTranslationService.Object,
            _mockCorrelationService.Object,
            _mockEventPublisher.Object,
            Mock.Of<ILogger<WebhookHandler>>());
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithValidRequest_PublishesEvent()
    {
        // Arrange
        var request = new WebhookRequest
        {
            Carrier = "USPS",
            Payload = "{\"trackingNumber\":\"9400116901490039382136\"}",
            Headers = new Dictionary<string, string>(),
            RequestId = "test-123"
        };

        _mockValidator
            .Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ValidationResult.Success());

        var carrierEvent = new CarrierTrackingEvent
        {
            TrackingNumber = "9400116901490039382136",
            StatusCode = "05",
            EventTimestamp = DateTime.UtcNow
        };
        _mockParser
            .Setup(p => p.ParseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(carrierEvent);

        var correlation = new CorrelationData
        {
            FulfillmentOrderId = "FO-123",
            OrderId = "ORD-456",
            PackageId = "PKG-789"
        };
        _mockCorrelationService
            .Setup(c => c.GetCorrelationDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(correlation);

        _mockTranslationService
            .Setup(t => t.TranslateStatus(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(TrackingStatus.Delivered);

        _mockEventPublisher
            .Setup(e => e.PublishAsync(It.IsAny<ShipmentTrackingStatusEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("msg-123");

        // Act
        var result = await _handler.ProcessWebhookAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.MessageId.Should().Be("msg-123");

        _mockEventPublisher.Verify(e => e.PublishAsync(
            It.Is<ShipmentTrackingStatusEvent>(evt =>
                evt.TrackingNumber == "9400116901490039382136" &&
                evt.Status == TrackingStatus.Delivered),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithInvalidSignature_ReturnsUnauthorized()
    {
        // Arrange
        var request = new WebhookRequest
        {
            Carrier = "USPS",
            Payload = "{\"trackingNumber\":\"9400116901490039382136\"}",
            Headers = new Dictionary<string, string>(),
            RequestId = "test-123"
        };

        _mockValidator
            .Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ValidationResult.Failure("Invalid signature", 401));

        // Act
        var result = await _handler.ProcessWebhookAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(401);
        result.ErrorMessage.Should().Contain("Invalid signature");

        _mockEventPublisher.Verify(e => e.PublishAsync(
            It.IsAny<ShipmentTrackingStatusEvent>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithNoCorrelation_ReturnsSuccessWithoutPublishing()
    {
        // Arrange
        var request = new WebhookRequest
        {
            Carrier = "USPS",
            Payload = "{\"trackingNumber\":\"9400116901490039382136\"}",
            Headers = new Dictionary<string, string>(),
            RequestId = "test-123"
        };

        _mockValidator
            .Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ValidationResult.Success());

        var carrierEvent = new CarrierTrackingEvent
        {
            TrackingNumber = "9400116901490039382136",
            StatusCode = "05"
        };
        _mockParser
            .Setup(p => p.ParseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(carrierEvent);

        _mockCorrelationService
            .Setup(c => c.GetCorrelationDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CorrelationData?)null);

        // Act
        var result = await _handler.ProcessWebhookAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Message.Should().Contain("No matching subscription");

        _mockEventPublisher.Verify(e => e.PublishAsync(
            It.IsAny<ShipmentTrackingStatusEvent>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
```

### Running Unit Tests

```bash
# Run all tests
dotnet test ShipmentTracking.WebhookProcessor.Tests

# Run with coverage
dotnet test ShipmentTracking.WebhookProcessor.Tests /p:CollectCoverage=true /p:CoverletOutputFormat=opencover

# Run specific test class
dotnet test --filter "FullyQualifiedName~UspsWebhookValidatorTests"

# Run tests matching pattern
dotnet test --filter "Name~ValidSignature"
```

## Integration Testing

### Scope

Integration tests validate component interactions using actual AWS services (via LocalStack) or service mocks.

**Tools**:
- LocalStack - Local AWS cloud stack
- Docker Compose - Container orchestration
- WireMock - HTTP mocking server
- Testcontainers - Docker containers for tests

### LocalStack Setup

**docker-compose.yml**:
```yaml
version: '3.8'

services:
  localstack:
    image: localstack/localstack:latest
    ports:
      - "4566:4566"
    environment:
      - SERVICES=dynamodb,sns,secretsmanager,lambda
      - DEBUG=1
      - DATA_DIR=/tmp/localstack/data
    volumes:
      - "./localstack-data:/tmp/localstack"
```

**Start LocalStack**:
```bash
docker-compose up -d localstack
```

### Integration Test Examples

```csharp
public class WebhookProcessorIntegrationTests : IAsyncLifetime
{
    private IAmazonDynamoDB _dynamoDb;
    private IAmazonSimpleNotificationService _sns;
    private IAmazonSecretsManager _secretsManager;
    private string _tableName;
    private string _topicArn;

    public async Task InitializeAsync()
    {
        // Configure LocalStack clients
        var config = new AmazonDynamoDBConfig
        {
            ServiceURL = "http://localhost:4566"
        };

        _dynamoDb = new AmazonDynamoDBClient(config);
        _sns = new AmazonSimpleNotificationServiceClient(config);
        _secretsManager = new AmazonSecretsManagerClient(config);

        // Setup test infrastructure
        _tableName = "test-subscriptions";
        await CreateDynamoDBTableAsync();

        _topicArn = await CreateSNSTopicAsync();
        await CreateSecretsAsync();
    }

    [Fact]
    public async Task EndToEnd_ValidWebhook_PublishesToSNS()
    {
        // Arrange - Create subscription record
        await CreateSubscriptionRecordAsync("USPS", "9400116901490039382136", "FO-123");

        // Build webhook request
        var payload = @"{
            ""trackingNumber"": ""9400116901490039382136"",
            ""statusCode"": ""05"",
            ""eventTimestamp"": ""2026-01-10T14:30:00Z""
        }";

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeSignature(payload, timestamp, "test-secret");

        var request = new WebhookRequest
        {
            Carrier = "USPS",
            Payload = payload,
            Headers = new Dictionary<string, string>
            {
                ["X-USPS-Signature"] = signature,
                ["X-USPS-Timestamp"] = timestamp
            },
            RequestId = "test-123"
        };

        // Create handler with real services
        var handler = CreateWebhookHandler();

        // Act
        var result = await handler.ProcessWebhookAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.MessageId.Should().NotBeNullOrEmpty();

        // Verify SNS message was published
        var messages = await GetSNSMessagesAsync();
        messages.Should().ContainSingle();
        var message = JsonSerializer.Deserialize<ShipmentTrackingStatusEvent>(messages.First());
        message.TrackingNumber.Should().Be("9400116901490039382136");
        message.Status.Should().Be(TrackingStatus.Delivered);
    }

    private async Task CreateDynamoDBTableAsync()
    {
        var request = new CreateTableRequest
        {
            TableName = _tableName,
            KeySchema = new List<KeySchemaElement>
            {
                new KeySchemaElement("PK", KeyType.HASH),
                new KeySchemaElement("SK", KeyType.RANGE)
            },
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new AttributeDefinition("PK", ScalarAttributeType.S),
                new AttributeDefinition("SK", ScalarAttributeType.S)
            },
            BillingMode = BillingMode.PAY_PER_REQUEST
        };

        await _dynamoDb.CreateTableAsync(request);
    }

    // Additional helper methods...
}
```

## End-to-End Testing

### Test Scenarios

1. **Happy Path - Delivered Package**
   - Subscribe to tracking
   - Receive delivery webhook
   - Verify canonical event published

2. **Delivery Attempt Failed**
   - Subscribe to tracking
   - Receive failed delivery webhook
   - Verify correct status mapping

3. **Package Exception**
   - Subscribe to tracking
   - Receive exception webhook
   - Verify exception status

4. **No Subscription Found**
   - Send webhook without subscription
   - Verify 200 OK response
   - Verify no event published

5. **Invalid Signature**
   - Send webhook with bad signature
   - Verify 401 Unauthorized
   - Verify no processing occurred

### Test Automation

**E2E Test Script**:
```bash
#!/bin/bash
# e2e-test.sh

set -e

echo "Starting E2E tests..."

# Deploy to test environment
sam deploy --stack-name webhook-processor-test --parameter-overrides Environment=test

# Get API Gateway URL
API_URL=$(aws cloudformation describe-stacks \
  --stack-name webhook-processor-test \
  --query 'Stacks[0].Outputs[?OutputKey==`WebhookApiUrl`].OutputValue' \
  --output text)

echo "API URL: $API_URL"

# Test 1: Valid webhook
echo "Test 1: Valid webhook"
RESPONSE=$(curl -s -w "%{http_code}" -X POST "$API_URL/webhook/usps" \
  -H "Content-Type: application/json" \
  -H "X-USPS-Signature: $(generate_signature)" \
  -H "X-USPS-Timestamp: $(date +%s)" \
  -d @test-data/usps-delivered.json)

if [[ "$RESPONSE" == *"200"* ]]; then
  echo "✓ Test 1 passed"
else
  echo "✗ Test 1 failed"
  exit 1
fi

# Test 2: Invalid signature
echo "Test 2: Invalid signature"
RESPONSE=$(curl -s -w "%{http_code}" -X POST "$API_URL/webhook/usps" \
  -H "Content-Type: application/json" \
  -H "X-USPS-Signature: invalid" \
  -H "X-USPS-Timestamp: $(date +%s)" \
  -d @test-data/usps-delivered.json)

if [[ "$RESPONSE" == *"401"* ]]; then
  echo "✓ Test 2 passed"
else
  echo "✗ Test 2 failed"
  exit 1
fi

echo "All E2E tests passed!"
```

## Performance Testing

### Load Testing

**Artillery Configuration** (`load-test.yml`):
```yaml
config:
  target: "https://webhook-test.example.com"
  phases:
    - duration: 60
      arrivalRate: 10  # 10 requests/sec
      name: "Warm up"
    - duration: 300
      arrivalRate: 100  # 100 requests/sec
      name: "Sustained load"
    - duration: 60
      arrivalRate: 500  # 500 requests/sec
      name: "Peak load"

scenarios:
  - name: "USPS webhook"
    flow:
      - post:
          url: "/webhook/usps"
          headers:
            Content-Type: "application/json"
            X-USPS-Signature: "{{ signature }}"
            X-USPS-Timestamp: "{{ timestamp }}"
          json:
            trackingNumber: "9400116901490039382136"
            statusCode: "05"
            eventTimestamp: "2026-01-10T14:30:00Z"
```

**Run Load Test**:
```bash
artillery run load-test.yml --output report.json
artillery report report.json
```

**Performance Targets**:
- P50 latency: < 100ms
- P95 latency: < 300ms
- P99 latency: < 500ms
- Error rate: < 0.1%
- Throughput: > 1000 req/sec

## Security Testing

### Vulnerability Scanning

**OWASP Dependency Check**:
```bash
dotnet tool install --global dependency-check
dependency-check --project ShipmentTracking --scan . --format HTML
```

### Penetration Testing

**Test Cases**:
1. Signature bypass attempts
2. Timestamp manipulation
3. Replay attack simulation
4. SQL injection in payload (should be prevented by JSON parsing)
5. XXE attacks (should be prevented by JSON parsing)
6. Payload size limits
7. Rate limiting bypass attempts

## Production Monitoring

### Synthetic Monitoring

**CloudWatch Synthetics Canary**:
```python
# webhook-canary.py
import json
import hmac
import hashlib
import base64
import time
from aws_synthetics.common import synthetics_logger as logger

def handler(event, context):
    # Generate valid webhook request
    payload = json.dumps({
        "trackingNumber": "9999999999999999999999",
        "statusCode": "05",
        "eventTimestamp": "2026-01-10T14:30:00Z"
    })
    
    timestamp = str(int(time.time()))
    message = f"{timestamp}.{payload}"
    
    # Compute signature
    secret = os.environ['WEBHOOK_SECRET']
    signature = base64.b64encode(
        hmac.new(secret.encode(), message.encode(), hashlib.sha256).digest()
    ).decode()
    
    # Send request
    response = requests.post(
        os.environ['WEBHOOK_URL'],
        data=payload,
        headers={
            'Content-Type': 'application/json',
            'X-USPS-Signature': signature,
            'X-USPS-Timestamp': timestamp
        }
    )
    
    # Validate response
    assert response.status_code == 200, f"Expected 200, got {response.status_code}"
    logger.info("Canary test passed")
```

### Real User Monitoring

**Custom Metrics**:
- Webhook validation success rate
- Processing latency by carrier
- Unknown status code frequency
- Correlation miss rate

**Alarms**:
- Validation failure rate > 5%
- Processing latency P95 > 500ms
- Unknown status codes > 10/hour
- Correlation miss rate > 1%

## Test Data Management

### Sample Payloads

Store in `/tests/test-data/`:

**usps-delivered.json**:
```json
{
  "trackingNumber": "9400116901490039382136",
  "status": "Delivered",
  "statusCode": "05",
  "eventTimestamp": "2026-01-10T14:30:00Z",
  "eventCity": "Springfield",
  "eventState": "IL"
}
```

**usps-out-for-delivery.json**:
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

### Test Data Builders

```csharp
public class WebhookRequestBuilder
{
    private string _carrier = "USPS";
    private string _trackingNumber = "9400116901490039382136";
    private string _statusCode = "05";
    
    public WebhookRequestBuilder WithCarrier(string carrier)
    {
        _carrier = carrier;
        return this;
    }
    
    public WebhookRequestBuilder WithTrackingNumber(string trackingNumber)
    {
        _trackingNumber = trackingNumber;
        return this;
    }
    
    public WebhookRequestBuilder WithStatusCode(string statusCode)
    {
        _statusCode = statusCode;
        return this;
    }
    
    public WebhookRequest Build()
    {
        var payload = JsonSerializer.Serialize(new
        {
            trackingNumber = _trackingNumber,
            statusCode = _statusCode,
            eventTimestamp = DateTime.UtcNow.ToString("O")
        });
        
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeSignature(payload, timestamp);
        
        return new WebhookRequest
        {
            Carrier = _carrier,
            Payload = payload,
            Headers = new Dictionary<string, string>
            {
                ["X-USPS-Signature"] = signature,
                ["X-USPS-Timestamp"] = timestamp
            },
            RequestId = Guid.NewGuid().ToString()
        };
    }
}

// Usage
var request = new WebhookRequestBuilder()
    .WithCarrier("USPS")
    .WithTrackingNumber("9400116901490039382136")
    .WithStatusCode("05")
    .Build();
```

## Continuous Testing

### CI/CD Integration

**GitHub Actions Workflow**:
```yaml
name: Test

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '8.0.x'
      
      - name: Restore dependencies
        run: dotnet restore
      
      - name: Build
        run: dotnet build --no-restore
      
      - name: Run unit tests
        run: dotnet test --no-build --verbosity normal --collect:"XPlat Code Coverage"
      
      - name: Start LocalStack
        run: docker-compose up -d localstack
      
      - name: Run integration tests
        run: dotnet test --filter "Category=Integration"
      
      - name: Upload coverage
        uses: codecov/codecov-action@v3
        with:
          files: ./coverage.opencover.xml
```

## Related Documentation

- [Architecture](./ARCHITECTURE.md)
- [Implementation Guide](./IMPLEMENTATION_GUIDE.md)
- [Webhook Specifications](./WEBHOOK_SPECIFICATIONS.md)
- [Status Mapping](./STATUS_MAPPING.md)
