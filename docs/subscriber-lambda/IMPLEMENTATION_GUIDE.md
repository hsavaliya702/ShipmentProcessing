# Subscriber Lambda - Implementation Guide

## Prerequisites

- .NET 8 SDK (8.0.416 or later)
- AWS CLI configured with appropriate credentials
- AWS SAM CLI for deployment
- Visual Studio 2022 or VS Code with C# extensions

## Step-by-Step Implementation

### Step 1: Project Setup

**Create the Lambda project**:
```bash
cd src
dotnet new classlib -n ShipmentTracking.Subscriber -f net8.0
```

**Add required NuGet packages**:
```bash
cd ShipmentTracking.Subscriber
dotnet add package Amazon.Lambda.Core --version 2.2.0
dotnet add package Amazon.Lambda.SNSEvents --version 2.1.0
dotnet add package Amazon.Lambda.Serialization.SystemTextJson --version 2.2.0
dotnet add package AWSSDK.DynamoDBv2 --version 3.7.0
dotnet add package AWSSDK.SecretsManager --version 3.7.0
dotnet add package Microsoft.Extensions.DependencyInjection --version 8.0.10
dotnet add package Microsoft.Extensions.Logging.Console --version 8.0.10
dotnet add package Polly --version 8.2.1
dotnet add package Polly.Contrib.WaitAndRetry --version 1.1.1
```

**Add project reference to Common library**:
```bash
dotnet add reference ../ShipmentTracking.Common/ShipmentTracking.Common.csproj
```

### Step 2: Create Models

**SubscriptionModels.cs**:
```csharp
public class SubscriptionRecord
{
    public string PK { get; set; } = string.Empty;  // TRACK#{Carrier}#{TrackingNumber}
    public string SK { get; set; } = "META";
    public string Carrier { get; set; } = string.Empty;
    public string TrackingNumber { get; set; } = string.Empty;
    public string FulfillmentOrderId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public SubscriptionStatus SubscriptionStatus { get; set; }
    public string? CarrierSubscriptionId { get; set; }
    public string CallbackUrl { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public ErrorType? ErrorType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAt { get; set; }
    public long? TTL { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public enum SubscriptionStatus
{
    Pending = 0,
    Active = 1,
    Failed = 2,
    Expired = 3
}

public enum ErrorType
{
    Unknown = 0,
    Transient = 1,
    Permanent = 2,
    RateLimit = 3
}
```

### Step 3: Implement Carrier Client Interface

**ICarrierSubscriptionClient.cs**:
```csharp
public interface ICarrierSubscriptionClient
{
    string CarrierCode { get; }
    
    Task<CarrierSubscriptionResponse> SubscribeAsync(
        CarrierSubscriptionRequest request,
        CancellationToken cancellationToken = default);
    
    Task<bool> UnsubscribeAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default);
    
    bool IsValidTrackingNumber(string trackingNumber);
}
```

### Step 4: Implement USPS Client

**UspsSubscriptionClient.cs**:
```csharp
public class UspsSubscriptionClient : ICarrierSubscriptionClient
{
    private readonly HttpClient _httpClient;
    private readonly SecretManager _secretManager;
    private readonly ILogger<UspsSubscriptionClient> _logger;
    private readonly string _uspsApiBaseUrl;
    
    // USPS tracking number regex pattern
    private static readonly Regex UspsTrackingPattern = new(
        @"^(94|93|92|95|82|71|61|91|42|70)\d{18,20}$|^[A-Z]{2}\d{9}US$",
        RegexOptions.Compiled);

    public string CarrierCode => "USPS";

    public async Task<CarrierSubscriptionResponse> SubscribeAsync(
        CarrierSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate tracking number format
        if (!IsValidTrackingNumber(request.TrackingNumber))
        {
            return new CarrierSubscriptionResponse
            {
                Success = false,
                ErrorMessage = "Invalid USPS tracking number format",
                ErrorType = ErrorType.Permanent
            };
        }

        // 2. Retrieve credentials from Secrets Manager
        var credentials = await _secretManager.GetSecretJsonAsync<UspsCredentials>(
            "carrier/usps/credentials",
            cancellationToken);

        // 3. Build subscription request
        var uspsRequest = new UspsSubscriptionRequest
        {
            TrackingNumber = request.TrackingNumber,
            WebhookUrl = request.CallbackUrl,
            ClientId = credentials.ClientId
        };

        // 4. Generate HMAC signature for authentication
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = GenerateSignature(
            JsonSerializer.Serialize(uspsRequest),
            credentials.ClientSecret,
            timestamp);

        // 5. Send HTTP request with authentication headers
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, 
            $"{_uspsApiBaseUrl}/webhooks/tracking/v1/subscriptions")
        {
            Content = JsonContent.Create(uspsRequest)
        };
        httpRequest.Headers.Add("X-Client-Id", credentials.ClientId);
        httpRequest.Headers.Add("X-Timestamp", timestamp);
        httpRequest.Headers.Add("X-Signature", signature);

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        // 6. Process response
        if (response.IsSuccessStatusCode)
        {
            var uspsResponse = await response.Content
                .ReadFromJsonAsync<UspsSubscriptionResponse>(cancellationToken);
            
            return new CarrierSubscriptionResponse
            {
                Success = true,
                SubscriptionId = uspsResponse?.SubscriptionId,
                StatusCode = (int)response.StatusCode
            };
        }
        else
        {
            var errorType = ClassifyHttpError(response.StatusCode);
            return new CarrierSubscriptionResponse
            {
                Success = false,
                ErrorMessage = $"USPS API error: {response.StatusCode}",
                ErrorType = errorType,
                StatusCode = (int)response.StatusCode
            };
        }
    }

    private static string GenerateSignature(string payload, string secret, string timestamp)
    {
        var message = $"{timestamp}.{payload}";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(messageBytes);
        return Convert.ToBase64String(hashBytes);
    }

    private static ErrorType ClassifyHttpError(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => ErrorType.Permanent,
            HttpStatusCode.Unauthorized => ErrorType.Permanent,
            HttpStatusCode.Forbidden => ErrorType.Permanent,
            HttpStatusCode.NotFound => ErrorType.Permanent,
            HttpStatusCode.TooManyRequests => ErrorType.RateLimit,
            HttpStatusCode.InternalServerError => ErrorType.Transient,
            HttpStatusCode.BadGateway => ErrorType.Transient,
            HttpStatusCode.ServiceUnavailable => ErrorType.Transient,
            HttpStatusCode.GatewayTimeout => ErrorType.Transient,
            _ => ErrorType.Unknown
        };
    }
}
```

### Step 5: Implement DynamoDB Repository

**SubscriptionRepository.cs**:
```csharp
public class SubscriptionRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly ILogger<SubscriptionRepository> _logger;
    private readonly string _tableName;
    private const int MaxRetryAttempts = 3;

    public async Task<SubscriptionRecord?> GetSubscriptionAsync(
        string carrier,
        string trackingNumber,
        CancellationToken cancellationToken = default)
    {
        var pk = GeneratePartitionKey(carrier, trackingNumber);
        
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = pk },
                ["SK"] = new AttributeValue { S = "META" }
            }
        };

        var response = await _dynamoDb.GetItemAsync(request, cancellationToken);
        
        if (response.Item == null || !response.Item.Any())
            return null;

        return DeserializeRecord(response.Item);
    }

    public async Task<bool> CreateOrUpdateSubscriptionAsync(
        SubscriptionRecord record,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        record.PK = GeneratePartitionKey(record.Carrier, record.TrackingNumber);
        record.SK = "META";
        record.UpdatedAt = DateTime.UtcNow;
        record.TTL = CalculateTtl(90); // 90 days

        var item = SerializeRecord(record);
        
        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(PK) OR " +
                "(SubscriptionStatus <> :active AND " +
                "(SubscriptionStatus = :pending OR SubscriptionStatus = :failed))",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":active"] = new AttributeValue { S = SubscriptionStatus.Active.ToString() },
                [":pending"] = new AttributeValue { S = SubscriptionStatus.Pending.ToString() },
                [":failed"] = new AttributeValue { S = SubscriptionStatus.Failed.ToString() }
            }
        };

        try
        {
            await _dynamoDb.PutItemAsync(request, cancellationToken);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            // Record already exists with Active status
            return false;
        }
    }

    private static string GeneratePartitionKey(string carrier, string trackingNumber)
    {
        return $"TRACK#{carrier.ToUpperInvariant()}#{trackingNumber}";
    }

    private static long CalculateTtl(int days)
    {
        return DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds();
    }
}
```

### Step 6: Implement Subscription Service

**SubscriptionService.cs**:
```csharp
public class SubscriptionService
{
    private readonly CarrierSubscriptionClientFactory _clientFactory;
    private readonly SubscriptionRepository _repository;
    private readonly ILogger<SubscriptionService> _logger;

    public async Task<SubscriptionResult> ProcessSubscriptionAsync(
        string carrier,
        string trackingNumber,
        string callbackUrl,
        SubscriptionCorrelationData correlationData,
        CancellationToken cancellationToken = default)
    {
        var correlationId = $"{correlationData.FulfillmentOrderId}_{correlationData.PackageId}";

        // Step 1: Check if carrier is supported
        if (!_clientFactory.IsCarrierSupported(carrier))
        {
            return new SubscriptionResult
            {
                Success = false,
                Reason = $"Carrier '{carrier}' is not supported",
                ShouldRetry = false
            };
        }

        // Step 2: Check existing subscription (idempotency)
        var existingRecord = await _repository.GetSubscriptionAsync(
            carrier, trackingNumber, cancellationToken);

        if (existingRecord != null)
        {
            if (existingRecord.SubscriptionStatus == SubscriptionStatus.Active)
            {
                return new SubscriptionResult
                {
                    Success = true,
                    SubscriptionId = existingRecord.CarrierSubscriptionId,
                    Reason = "Subscription already active",
                    AlreadyExists = true
                };
            }

            // Check if max attempts reached
            if (await _repository.HasExceededMaxAttemptsAsync(
                carrier, trackingNumber, cancellationToken))
            {
                return new SubscriptionResult
                {
                    Success = false,
                    Reason = "Max retry attempts exceeded",
                    ShouldRetry = false
                };
            }
        }

        // Step 3: Attempt subscription with carrier
        var client = _clientFactory.CreateClient(carrier);
        var request = new CarrierSubscriptionRequest
        {
            TrackingNumber = trackingNumber,
            CallbackUrl = callbackUrl,
            CorrelationData = correlationData
        };

        await _repository.IncrementAttemptCountAsync(
            carrier, trackingNumber, cancellationToken);
        
        var response = await client.SubscribeAsync(request, cancellationToken);

        // Step 4: Handle response and update repository
        if (response.Success)
        {
            var record = new SubscriptionRecord
            {
                Carrier = carrier,
                TrackingNumber = trackingNumber,
                FulfillmentOrderId = correlationData.FulfillmentOrderId,
                OrderId = correlationData.OrderId,
                PackageId = correlationData.PackageId,
                SubscriptionStatus = SubscriptionStatus.Active,
                CarrierSubscriptionId = response.SubscriptionId,
                CallbackUrl = callbackUrl,
                AttemptCount = existingRecord?.AttemptCount + 1 ?? 1,
                ActivatedAt = DateTime.UtcNow
            };

            await _repository.CreateOrUpdateSubscriptionAsync(
                record, correlationId, cancellationToken);

            return new SubscriptionResult
            {
                Success = true,
                SubscriptionId = response.SubscriptionId,
                Reason = "Successfully subscribed"
            };
        }
        else
        {
            // Update failure record
            var shouldRetry = response.ErrorType == ErrorType.Transient || 
                            response.ErrorType == ErrorType.RateLimit;

            return new SubscriptionResult
            {
                Success = false,
                Reason = response.ErrorMessage ?? "Unknown error",
                ShouldRetry = shouldRetry,
                ErrorType = response.ErrorType
            };
        }
    }
}
```

### Step 7: Implement Lambda Handler

**Function.cs**:
```csharp
[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

public class Function
{
    private readonly ShipmentNotificationHandler _handler;
    private readonly ILogger<Function> _logger;

    public Function()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();
        ConfigureServices(services, configuration);
        
        var serviceProvider = services.BuildServiceProvider();
        _handler = serviceProvider.GetRequiredService<ShipmentNotificationHandler>();
        _logger = serviceProvider.GetRequiredService<ILogger<Function>>();
    }

    public async Task<FunctionResponse> FunctionHandler(
        SNSEvent snsEvent,
        ILambdaContext context)
    {
        var requestId = context.AwsRequestId;

        using var cts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(context.RemainingTime.TotalMilliseconds - 5000));

        var result = await _handler.HandleAsync(snsEvent, cts.Token);

        if (!result.Success)
        {
            throw new SubscriptionProcessingException(
                $"Failed to process subscriptions. Processed: {result.ProcessedCount}, Failed: {result.FailedCount}",
                result.Errors);
        }

        return new FunctionResponse
        {
            Success = true,
            Message = $"Successfully processed {result.ProcessedCount} packages",
            ProcessedCount = result.ProcessedCount,
            RequestId = requestId
        };
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
        services.AddSingleton<IAmazonSecretsManager, AmazonSecretsManagerClient>();

        // Memory cache
        services.AddMemoryCache();

        // Common utilities
        services.AddSingleton<SecretManager>();

        // Repository
        var tableName = Environment.GetEnvironmentVariable("DYNAMODB_TABLE_NAME") 
            ?? "ShipmentTrackingSubscriptions";
        services.AddSingleton(sp => new SubscriptionRepository(
            sp.GetRequiredService<IAmazonDynamoDB>(),
            sp.GetRequiredService<ILogger<SubscriptionRepository>>(),
            tableName));

        // HTTP Clients with Polly
        services.AddHttpClient<UspsSubscriptionClient>()
            .AddPolicyHandler(GetRetryPolicy())
            .AddPolicyHandler(GetCircuitBreakerPolicy());

        // Carrier clients
        var uspsApiBaseUrl = Environment.GetEnvironmentVariable("USPS_API_BASE_URL") 
            ?? "https://api.usps.com";
        services.AddTransient(sp => new UspsSubscriptionClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(UspsSubscriptionClient)),
            sp.GetRequiredService<SecretManager>(),
            sp.GetRequiredService<ILogger<UspsSubscriptionClient>>(),
            uspsApiBaseUrl));

        // Factory and services
        services.AddSingleton<CarrierSubscriptionClientFactory>();
        services.AddSingleton<SubscriptionService>();

        // Handler
        var webhookBaseUrl = Environment.GetEnvironmentVariable("WEBHOOK_BASE_URL") 
            ?? "https://tracking-webhook.example.com";
        services.AddSingleton(sp => new ShipmentNotificationHandler(
            sp.GetRequiredService<SubscriptionService>(),
            sp.GetRequiredService<ILogger<ShipmentNotificationHandler>>(),
            webhookBaseUrl));
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        var delay = Backoff.DecorrelatedJitterBackoffV2(
            medianFirstRetryDelay: TimeSpan.FromSeconds(1),
            retryCount: 3);

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(delay);
    }

    private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30));
    }
}
```

### Step 8: Add Configuration

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
  "UspsApiBaseUrl": "https://api.usps.com",
  "WebhookBaseUrl": "https://tracking-webhook.example.com"
}
```

### Step 9: Deploy Infrastructure

See [deployment documentation](../deployment/INFRASTRUCTURE.md) for SAM deployment instructions.

### Step 10: Testing

**Local Testing with Mock Data**:
```bash
# Create test event
cat > test-event.json << EOF
{
  "Records": [{
    "Sns": {
      "Message": "{\"fulfillmentOrderId\":\"FO-12345\",\"orderId\":\"ORD-67890\",\"packages\":[{\"packageId\":\"PKG-001\",\"carrier\":\"USPS\",\"trackingNumber\":\"9400116901490039382136\"}]}"
    }
  }]
}
EOF

# Test with SAM CLI
sam local invoke SubscriberFunction -e test-event.json
```

**Unit Testing**:
```csharp
[Fact]
public async Task ProcessSubscriptionAsync_WithActiveSubscription_ReturnsSuccess()
{
    // Arrange
    var mockRepository = new Mock<SubscriptionRepository>();
    mockRepository.Setup(r => r.GetSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new SubscriptionRecord
        {
            SubscriptionStatus = SubscriptionStatus.Active,
            CarrierSubscriptionId = "sub_123"
        });

    var service = new SubscriptionService(mockFactory, mockRepository.Object, mockLogger);

    // Act
    var result = await service.ProcessSubscriptionAsync("USPS", "9400...", "https://...", correlationData);

    // Assert
    Assert.True(result.Success);
    Assert.True(result.AlreadyExists);
}
```

## Best Practices

### 1. Error Handling
- Always classify errors as transient or permanent
- Log errors with correlation IDs
- Use specific exception types
- Don't swallow exceptions

### 2. Performance
- Use async/await throughout
- Minimize cold start time
- Cache AWS credentials
- Use connection pooling for HTTP clients

### 3. Security
- Never log sensitive data (tracking numbers are masked)
- Use AWS Secrets Manager for all credentials
- Implement least privilege IAM policies
- Validate all inputs

### 4. Observability
- Include correlation IDs in all logs
- Emit custom metrics for business events
- Use structured logging
- Track performance with stopwatch

### 5. Testing
- Write unit tests for all business logic
- Mock external dependencies
- Test error scenarios
- Use integration tests with LocalStack

## Common Issues and Solutions

**Issue**: Cold starts taking too long
- Solution: Use provisioned concurrency or increase invocation frequency

**Issue**: Carrier API rate limiting
- Solution: Implement exponential backoff and respect rate limit headers

**Issue**: DynamoDB throttling
- Solution: Use on-demand capacity mode or increase provisioned capacity

**Issue**: Memory errors
- Solution: Increase Lambda memory allocation (test with different values)

## Next Steps

1. Implement unit tests
2. Add UPS carrier support
3. Add FedEx carrier support
4. Implement provisioned concurrency for critical workloads
5. Set up X-Ray tracing
6. Create operational runbook

## Related Documentation

- [Architecture](./ARCHITECTURE.md)
- [API Specifications](./API_SPECIFICATIONS.md)
- [Data Model](./DATA_MODEL.md)
- [Testing Strategy](./TESTING_STRATEGY.md)
