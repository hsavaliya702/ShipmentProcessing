# Webhook Processor Lambda - Implementation Guide

## Prerequisites

- .NET 8 SDK (8.0.416 or later)
- AWS CLI configured with appropriate credentials
- AWS SAM CLI for deployment
- Visual Studio 2022 or VS Code with C# extensions
- Postman or curl for webhook testing

## Step-by-Step Implementation

### Step 1: Project Setup

**Create the Lambda project**:
```bash
cd src
dotnet new classlib -n ShipmentTracking.WebhookProcessor -f net8.0
```

**Add required NuGet packages**:
```bash
cd ShipmentTracking.WebhookProcessor
dotnet add package Amazon.Lambda.Core --version 2.2.0
dotnet add package Amazon.Lambda.APIGatewayEvents --version 2.7.0
dotnet add package Amazon.Lambda.Serialization.SystemTextJson --version 2.2.0
dotnet add package AWSSDK.DynamoDBv2 --version 3.7.0
dotnet add package AWSSDK.SimpleNotificationService --version 3.7.0
dotnet add package AWSSDK.SecretsManager --version 3.7.0
dotnet add package Microsoft.Extensions.DependencyInjection --version 8.0.10
dotnet add package Microsoft.Extensions.Logging.Console --version 8.0.10
```

**Add project reference to Common library**:
```bash
dotnet add reference ../ShipmentTracking.Common/ShipmentTracking.Common.csproj
```

### Step 2: Create Models

**WebhookModels.cs**:
```csharp
namespace ShipmentTracking.WebhookProcessor.Models;

/// <summary>
/// Incoming webhook request from carrier
/// </summary>
public class WebhookRequest
{
    public string Carrier { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>
/// Result of webhook processing
/// </summary>
public class WebhookProcessingResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string? Message { get; set; }
    public string? MessageId { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Carrier-specific tracking event (parsed from webhook)
/// </summary>
public class CarrierTrackingEvent
{
    public string TrackingNumber { get; set; } = string.Empty;
    public string StatusCode { get; set; } = string.Empty;
    public string? SubStatusCode { get; set; }
    public string? StatusDescription { get; set; }
    public DateTime EventTimestamp { get; set; }
    public LocationInfo? Location { get; set; }
    public DateTime? EstimatedDeliveryDate { get; set; }
    public Dictionary<string, object>? AdditionalData { get; set; }
}

/// <summary>
/// Location information from carrier event
/// </summary>
public class LocationInfo
{
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? FacilityName { get; set; }
}

/// <summary>
/// Correlation data retrieved from DynamoDB
/// </summary>
public class CorrelationData
{
    public string FulfillmentOrderId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Webhook validation result
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public int StatusCode { get; set; } = 200;
    
    public static ValidationResult Success() => new() { IsValid = true };
    public static ValidationResult Failure(string message, int statusCode) => 
        new() { IsValid = false, ErrorMessage = message, StatusCode = statusCode };
}
```

### Step 3: Implement Webhook Validator Interface

**IWebhookValidator.cs**:
```csharp
namespace ShipmentTracking.WebhookProcessor.Validation;

/// <summary>
/// Interface for carrier-specific webhook signature validation
/// </summary>
public interface IWebhookValidator
{
    /// <summary>
    /// Gets the carrier code this validator handles
    /// </summary>
    string CarrierCode { get; }
    
    /// <summary>
    /// Validates webhook signature and timestamp
    /// </summary>
    Task<ValidationResult> ValidateAsync(
        string payload,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default);
}
```

### Step 4: Implement USPS Webhook Validator

**UspsWebhookValidator.cs**:
```csharp
namespace ShipmentTracking.WebhookProcessor.Validation;

public class UspsWebhookValidator : IWebhookValidator
{
    private readonly SecretManager _secretManager;
    private readonly ILogger<UspsWebhookValidator> _logger;
    private readonly int _allowedTimestampSkewSeconds;

    public string CarrierCode => "USPS";

    public UspsWebhookValidator(
        SecretManager secretManager,
        ILogger<UspsWebhookValidator> logger,
        int allowedTimestampSkewSeconds = 300)
    {
        _secretManager = secretManager;
        _logger = logger;
        _allowedTimestampSkewSeconds = allowedTimestampSkewSeconds;
    }

    public async Task<ValidationResult> ValidateAsync(
        string payload,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Extract required headers
        if (!headers.TryGetValue("X-USPS-Signature", out var signature))
        {
            _logger.LogWarning("Missing X-USPS-Signature header");
            return ValidationResult.Failure("Missing signature header", 401);
        }

        if (!headers.TryGetValue("X-USPS-Timestamp", out var timestampStr))
        {
            _logger.LogWarning("Missing X-USPS-Timestamp header");
            return ValidationResult.Failure("Missing timestamp header", 401);
        }

        // Step 2: Validate timestamp freshness
        if (!long.TryParse(timestampStr, out var timestamp))
        {
            _logger.LogWarning("Invalid timestamp format: {Timestamp}", timestampStr);
            return ValidationResult.Failure("Invalid timestamp format", 400);
        }

        var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        var now = DateTimeOffset.UtcNow;
        var timeDifference = Math.Abs((now - requestTime).TotalSeconds);

        if (timeDifference > _allowedTimestampSkewSeconds)
        {
            _logger.LogWarning("Timestamp outside allowed window. Difference: {Diff}s, Allowed: {Allowed}s",
                timeDifference, _allowedTimestampSkewSeconds);
            return ValidationResult.Failure("Timestamp outside allowed window", 401);
        }

        // Step 3: Retrieve webhook secret
        string webhookSecret;
        try
        {
            webhookSecret = await _secretManager.GetSecretStringAsync(
                "carrier/usps/webhook-secret",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve webhook secret");
            return ValidationResult.Failure("Internal server error", 500);
        }

        // Step 4: Compute expected signature
        var expectedSignature = ComputeSignature(payload, webhookSecret, timestampStr);

        // Step 5: Constant-time comparison to prevent timing attacks
        if (!ConstantTimeEquals(signature, expectedSignature))
        {
            _logger.LogWarning("Signature mismatch");
            return ValidationResult.Failure("Invalid signature", 401);
        }

        _logger.LogDebug("Webhook signature validated successfully");
        return ValidationResult.Success();
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
}
```

### Step 5: Implement Carrier Payload Parser Interface

**ICarrierPayloadParser.cs**:
```csharp
namespace ShipmentTracking.WebhookProcessor.Carriers;

/// <summary>
/// Interface for parsing carrier-specific webhook payloads
/// </summary>
public interface ICarrierPayloadParser
{
    /// <summary>
    /// Gets the carrier code this parser handles
    /// </summary>
    string CarrierCode { get; }
    
    /// <summary>
    /// Parses carrier webhook payload into normalized tracking event
    /// </summary>
    Task<CarrierTrackingEvent> ParseAsync(
        string payload,
        CancellationToken cancellationToken = default);
}
```

### Step 6: Implement USPS Payload Parser

**UspsPayloadParser.cs**:
```csharp
namespace ShipmentTracking.WebhookProcessor.Carriers;

public class UspsPayloadParser : ICarrierPayloadParser
{
    private readonly ILogger<UspsPayloadParser> _logger;

    public string CarrierCode => "USPS";

    public UspsPayloadParser(ILogger<UspsPayloadParser> logger)
    {
        _logger = logger;
    }

    public async Task<CarrierTrackingEvent> ParseAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var uspsPayload = JsonSerializer.Deserialize<UspsWebhookPayload>(payload);
            if (uspsPayload == null)
                throw new PayloadParsingException("Failed to deserialize USPS payload");

            // Validate required fields
            if (string.IsNullOrWhiteSpace(uspsPayload.TrackingNumber))
                throw new PayloadParsingException("Missing tracking number");

            // Parse event
            var trackingEvent = new CarrierTrackingEvent
            {
                TrackingNumber = uspsPayload.TrackingNumber,
                StatusCode = uspsPayload.StatusCode ?? uspsPayload.Status ?? "UNKNOWN",
                SubStatusCode = uspsPayload.StatusCategory,
                StatusDescription = uspsPayload.StatusSummary ?? uspsPayload.Status,
                EventTimestamp = ParseTimestamp(uspsPayload.EventTimestamp),
                EstimatedDeliveryDate = ParseTimestamp(uspsPayload.ExpectedDeliveryDate),
                Location = ParseLocation(uspsPayload),
                AdditionalData = new Dictionary<string, object>
                {
                    ["eventType"] = uspsPayload.EventType ?? "status_update",
                    ["serviceType"] = uspsPayload.ServiceType ?? "unknown"
                }
            };

            _logger.LogDebug("Successfully parsed USPS payload for tracking {TrackingNumber}",
                trackingEvent.TrackingNumber);

            return await Task.FromResult(trackingEvent);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error for USPS payload");
            throw new PayloadParsingException("Invalid JSON format", ex);
        }
    }

    private static DateTime ParseTimestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return DateTime.UtcNow;

        if (DateTime.TryParse(timestamp, out var dt))
            return dt.ToUniversalTime();

        return DateTime.UtcNow;
    }

    private static LocationInfo? ParseLocation(UspsWebhookPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.EventCity) &&
            string.IsNullOrWhiteSpace(payload.EventState))
            return null;

        return new LocationInfo
        {
            City = payload.EventCity,
            State = payload.EventState,
            PostalCode = payload.EventZip,
            Country = "US",
            FacilityName = payload.FacilityName
        };
    }

    private class UspsWebhookPayload
    {
        [JsonPropertyName("trackingNumber")]
        public string? TrackingNumber { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("statusCode")]
        public string? StatusCode { get; set; }

        [JsonPropertyName("statusCategory")]
        public string? StatusCategory { get; set; }

        [JsonPropertyName("statusSummary")]
        public string? StatusSummary { get; set; }

        [JsonPropertyName("eventTimestamp")]
        public string? EventTimestamp { get; set; }

        [JsonPropertyName("eventType")]
        public string? EventType { get; set; }

        [JsonPropertyName("eventCity")]
        public string? EventCity { get; set; }

        [JsonPropertyName("eventState")]
        public string? EventState { get; set; }

        [JsonPropertyName("eventZip")]
        public string? EventZip { get; set; }

        [JsonPropertyName("facilityName")]
        public string? FacilityName { get; set; }

        [JsonPropertyName("expectedDeliveryDate")]
        public string? ExpectedDeliveryDate { get; set; }

        [JsonPropertyName("serviceType")]
        public string? ServiceType { get; set; }
    }
}

public class PayloadParsingException : Exception
{
    public PayloadParsingException(string message) : base(message) { }
    public PayloadParsingException(string message, Exception innerException) 
        : base(message, innerException) { }
}
```

### Step 7: Implement Correlation Service

**CorrelationService.cs**:
```csharp
namespace ShipmentTracking.WebhookProcessor.Services;

public class CorrelationService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly ILogger<CorrelationService> _logger;
    private readonly string _tableName;

    public CorrelationService(
        IAmazonDynamoDB dynamoDb,
        ILogger<CorrelationService> logger,
        string tableName)
    {
        _dynamoDb = dynamoDb;
        _logger = logger;
        _tableName = tableName;
    }

    public async Task<CorrelationData?> GetCorrelationDataAsync(
        string carrier,
        string trackingNumber,
        CancellationToken cancellationToken = default)
    {
        var pk = $"TRACK#{carrier.ToUpperInvariant()}#{trackingNumber}";

        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = pk },
                ["SK"] = new AttributeValue { S = "META" }
            }
        };

        try
        {
            var response = await _dynamoDb.GetItemAsync(request, cancellationToken);

            if (response.Item == null || !response.Item.Any())
            {
                _logger.LogWarning("No subscription found for {Carrier} tracking {TrackingNumber}",
                    carrier, trackingNumber);
                return null;
            }

            return new CorrelationData
            {
                FulfillmentOrderId = response.Item.GetValueOrDefault("FulfillmentOrderId")?.S ?? "",
                OrderId = response.Item.GetValueOrDefault("OrderId")?.S ?? "",
                PackageId = response.Item.GetValueOrDefault("PackageId")?.S ?? "",
                Metadata = response.Item
                    .Where(kvp => kvp.Key.StartsWith("Meta_"))
                    .ToDictionary(
                        kvp => kvp.Key.Substring(5),
                        kvp => kvp.Value.S ?? "")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving correlation data for {Carrier} {TrackingNumber}",
                carrier, trackingNumber);
            throw;
        }
    }
}
```

### Step 8: Implement Webhook Handler

**WebhookHandler.cs**:
```csharp
namespace ShipmentTracking.WebhookProcessor.Handlers;

public class WebhookHandler
{
    private readonly Dictionary<string, IWebhookValidator> _validators;
    private readonly Dictionary<string, ICarrierPayloadParser> _parsers;
    private readonly StatusTranslationService _translationService;
    private readonly CorrelationService _correlationService;
    private readonly EventPublisher _eventPublisher;
    private readonly ILogger<WebhookHandler> _logger;

    public WebhookHandler(
        IEnumerable<IWebhookValidator> validators,
        IEnumerable<ICarrierPayloadParser> parsers,
        StatusTranslationService translationService,
        CorrelationService correlationService,
        EventPublisher eventPublisher,
        ILogger<WebhookHandler> logger)
    {
        _validators = validators.ToDictionary(v => v.CarrierCode.ToUpperInvariant());
        _parsers = parsers.ToDictionary(p => p.CarrierCode.ToUpperInvariant());
        _translationService = translationService;
        _correlationService = correlationService;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<WebhookProcessingResult> ProcessWebhookAsync(
        WebhookRequest request,
        CancellationToken cancellationToken = default)
    {
        var correlationId = request.RequestId;

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["Carrier"] = request.Carrier
        }))
        {
            try
            {
                // Step 1: Validate webhook signature
                var validationResult = await ValidateWebhookAsync(request, correlationId, cancellationToken);
                if (!validationResult.IsValid)
                {
                    return new WebhookProcessingResult
                    {
                        Success = false,
                        StatusCode = validationResult.StatusCode,
                        ErrorMessage = validationResult.ErrorMessage
                    };
                }

                // Step 2: Parse carrier payload
                var carrierEvent = await ParsePayloadAsync(request, correlationId, cancellationToken);

                // Step 3: Get correlation data
                var correlation = await _correlationService.GetCorrelationDataAsync(
                    request.Carrier,
                    carrierEvent.TrackingNumber,
                    cancellationToken);

                if (correlation == null)
                {
                    _logger.LogWarning("No subscription found for tracking {TrackingNumber}",
                        carrierEvent.TrackingNumber);
                    return new WebhookProcessingResult
                    {
                        Success = true,
                        StatusCode = 200,
                        Message = "No matching subscription found"
                    };
                }

                // Step 4: Translate status
                var canonicalStatus = _translationService.TranslateStatus(
                    request.Carrier,
                    carrierEvent.StatusCode,
                    carrierEvent.SubStatusCode);

                // Step 5: Build canonical event
                var canonicalEvent = BuildCanonicalEvent(carrierEvent, correlation, canonicalStatus, request.Carrier);

                // Step 6: Publish to SNS
                var messageId = await _eventPublisher.PublishAsync(
                    canonicalEvent,
                    correlationId,
                    cancellationToken);

                _logger.LogInformation("Successfully processed webhook and published event. MessageId: {MessageId}",
                    messageId);

                return new WebhookProcessingResult
                {
                    Success = true,
                    StatusCode = 200,
                    Message = "Webhook processed successfully",
                    MessageId = messageId
                };
            }
            catch (PayloadParsingException ex)
            {
                _logger.LogError(ex, "Failed to parse webhook payload");
                return new WebhookProcessingResult
                {
                    Success = false,
                    StatusCode = 400,
                    ErrorMessage = "Invalid payload format"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing webhook");
                return new WebhookProcessingResult
                {
                    Success = false,
                    StatusCode = 500,
                    ErrorMessage = "Internal server error"
                };
            }
        }
    }

    private async Task<ValidationResult> ValidateWebhookAsync(
        WebhookRequest request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var carrierKey = request.Carrier.ToUpperInvariant();
        if (!_validators.TryGetValue(carrierKey, out var validator))
        {
            _logger.LogWarning("No validator found for carrier: {Carrier}", request.Carrier);
            return ValidationResult.Failure($"Unsupported carrier: {request.Carrier}", 400);
        }

        return await validator.ValidateAsync(request.Payload, request.Headers, cancellationToken);
    }

    private async Task<CarrierTrackingEvent> ParsePayloadAsync(
        WebhookRequest request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var carrierKey = request.Carrier.ToUpperInvariant();
        if (!_parsers.TryGetValue(carrierKey, out var parser))
        {
            throw new PayloadParsingException($"No parser found for carrier: {request.Carrier}");
        }

        return await parser.ParseAsync(request.Payload, cancellationToken);
    }

    private static ShipmentTrackingStatusEvent BuildCanonicalEvent(
        CarrierTrackingEvent carrierEvent,
        CorrelationData correlation,
        TrackingStatus canonicalStatus,
        string carrier)
    {
        return new ShipmentTrackingStatusEvent
        {
            FulfillmentOrderId = correlation.FulfillmentOrderId,
            OrderId = correlation.OrderId,
            PackageId = correlation.PackageId,
            Carrier = carrier,
            TrackingNumber = carrierEvent.TrackingNumber,
            Status = canonicalStatus,
            StatusDescription = carrierEvent.StatusDescription ?? canonicalStatus.ToString(),
            EventTimestamp = carrierEvent.EventTimestamp,
            Location = carrierEvent.Location,
            CarrierStatusCode = carrierEvent.StatusCode,
            EstimatedDeliveryDate = carrierEvent.EstimatedDeliveryDate,
            ProcessedAt = DateTime.UtcNow
        };
    }
}
```

### Step 9: Implement Lambda Function Handler

**Function.cs**:
```csharp
[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace ShipmentTracking.WebhookProcessor;

public class Function
{
    private readonly WebhookHandler _handler;
    private readonly ILogger<Function> _logger;

    public Function()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();
        ConfigureServices(services, configuration);

        var serviceProvider = services.BuildServiceProvider();
        _handler = serviceProvider.GetRequiredService<WebhookHandler>();
        _logger = serviceProvider.GetRequiredService<ILogger<Function>>();
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var requestId = context.AwsRequestId;
        _logger.LogInformation("Processing webhook request: {RequestId}", requestId);

        try
        {
            // Extract carrier from path parameter
            var carrier = request.PathParameters?.GetValueOrDefault("carrier") ?? "unknown";

            // Build webhook request
            var webhookRequest = new WebhookRequest
            {
                Carrier = carrier,
                Payload = request.Body ?? "",
                Headers = request.Headers ?? new Dictionary<string, string>(),
                RequestId = requestId
            };

            // Process webhook
            using var cts = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(context.RemainingTime.TotalMilliseconds - 2000));

            var result = await _handler.ProcessWebhookAsync(webhookRequest, cts.Token);

            // Build response
            return new APIGatewayProxyResponse
            {
                StatusCode = result.StatusCode,
                Body = JsonSerializer.Serialize(new
                {
                    success = result.Success,
                    message = result.Message ?? result.ErrorMessage,
                    messageId = result.MessageId,
                    requestId
                }),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in webhook processor");

            return new APIGatewayProxyResponse
            {
                StatusCode = 500,
                Body = JsonSerializer.Serialize(new
                {
                    success = false,
                    message = "Internal server error",
                    requestId
                }),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json"
                }
            };
        }
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddConfiguration(configuration.GetSection("Logging"));
        });

        // AWS Services
        services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
        services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();
        services.AddSingleton<IAmazonSecretsManager, AmazonSecretsManagerClient>();

        // Memory cache
        services.AddMemoryCache();

        // Common utilities
        services.AddSingleton<SecretManager>();

        // Services
        var tableName = Environment.GetEnvironmentVariable("DYNAMODB_TABLE_NAME") 
            ?? "ShipmentTrackingSubscriptions";
        services.AddSingleton(sp => new CorrelationService(
            sp.GetRequiredService<IAmazonDynamoDB>(),
            sp.GetRequiredService<ILogger<CorrelationService>>(),
            tableName));

        var topicArn = Environment.GetEnvironmentVariable("SNS_TOPIC_ARN") 
            ?? "arn:aws:sns:us-east-1:123456789012:shipment-tracking-status-events";
        services.AddSingleton(sp => new EventPublisher(
            sp.GetRequiredService<IAmazonSimpleNotificationService>(),
            sp.GetRequiredService<ILogger<EventPublisher>>(),
            topicArn));

        services.AddSingleton<StatusTranslationService>();

        // Validators
        services.AddTransient<IWebhookValidator>(sp => new UspsWebhookValidator(
            sp.GetRequiredService<SecretManager>(),
            sp.GetRequiredService<ILogger<UspsWebhookValidator>>(),
            allowedTimestampSkewSeconds: 300));

        // Parsers
        services.AddTransient<ICarrierPayloadParser, UspsPayloadParser>();

        // Handler
        services.AddSingleton<WebhookHandler>();
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }
}
```

### Step 10: Testing

**Unit Test Example**:
```csharp
[Fact]
public async Task ValidateAsync_WithValidSignature_ReturnsSuccess()
{
    // Arrange
    var mockSecretManager = new Mock<SecretManager>();
    mockSecretManager
        .Setup(m => m.GetSecretStringAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync("test-secret");

    var validator = new UspsWebhookValidator(mockSecretManager.Object, Mock.Of<ILogger<UspsWebhookValidator>>());

    var payload = "{\"trackingNumber\":\"9400116901490039382136\"}";
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    
    // Compute valid signature
    var message = $"{timestamp}.{payload}";
    var keyBytes = Encoding.UTF8.GetBytes("test-secret");
    var messageBytes = Encoding.UTF8.GetBytes(message);
    using var hmac = new HMACSHA256(keyBytes);
    var signature = Convert.ToBase64String(hmac.ComputeHash(messageBytes));

    var headers = new Dictionary<string, string>
    {
        ["X-USPS-Signature"] = signature,
        ["X-USPS-Timestamp"] = timestamp
    };

    // Act
    var result = await validator.ValidateAsync(payload, headers);

    // Assert
    Assert.True(result.IsValid);
}
```

**Integration Test Example**:
```bash
# Test webhook endpoint
curl -X POST https://your-api-gateway-url/webhook/usps \
  -H "Content-Type: application/json" \
  -H "X-USPS-Signature: <computed-signature>" \
  -H "X-USPS-Timestamp: $(date +%s)" \
  -d @tests/test-data/usps-webhook-payload.json
```

## Configuration

**appsettings.json**:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "System": "Warning"
    }
  },
  "DynamoDbTableName": "ShipmentTrackingSubscriptions",
  "SnsTopicArn": "arn:aws:sns:us-east-1:123456789012:shipment-tracking-status-events"
}
```

**Environment Variables** (Lambda configuration):
```
DYNAMODB_TABLE_NAME=ShipmentTrackingSubscriptions
SNS_TOPIC_ARN=arn:aws:sns:us-east-1:123456789012:shipment-tracking-status-events
```

## Deployment

See [Infrastructure Documentation](../deployment/INFRASTRUCTURE.md) for complete deployment instructions.

**Quick Deploy**:
```bash
cd infrastructure/webhook-processor-lambda
sam build --use-container
sam deploy --guided
```

## Troubleshooting

### Common Issues

**401 Unauthorized**:
- Check webhook secret is correct in Secrets Manager
- Verify timestamp is within allowed window
- Ensure signature computation matches carrier's method

**400 Bad Request**:
- Validate payload JSON structure
- Check required fields are present
- Review carrier webhook documentation

**500 Internal Server Error**:
- Check Lambda logs in CloudWatch
- Verify DynamoDB table exists and Lambda has permissions
- Ensure SNS topic exists and Lambda can publish

### Monitoring

**View Logs**:
```bash
aws logs tail /aws/lambda/webhook-processor-function --follow
```

**Check Metrics**:
```bash
aws cloudwatch get-metric-statistics \
  --namespace AWS/Lambda \
  --metric-name Invocations \
  --dimensions Name=FunctionName,Value=webhook-processor-function \
  --start-time 2026-01-09T00:00:00Z \
  --end-time 2026-01-09T23:59:59Z \
  --period 3600 \
  --statistics Sum
```

## Best Practices

1. **Always validate signatures** before processing webhooks
2. **Use constant-time comparison** for signature validation
3. **Check timestamp freshness** to prevent replay attacks
4. **Log correlation IDs** for request tracing
5. **Return 200 OK** even for no-subscription scenarios (acknowledge receipt)
6. **Don't leak sensitive information** in error messages
7. **Monitor validation failure rates** to detect issues
8. **Test with actual carrier payloads** before production

## Related Documentation

- [Architecture](./ARCHITECTURE.md)
- [Webhook Specifications](./WEBHOOK_SPECIFICATIONS.md)
- [Status Mapping](./STATUS_MAPPING.md)
- [Testing Strategy](./TESTING_STRATEGY.md)
