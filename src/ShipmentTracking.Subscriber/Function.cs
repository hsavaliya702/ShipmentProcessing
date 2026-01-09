using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SNSEvents;
using Amazon.SecretsManager;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Extensions.Http;
using ShipmentTracking.Common.Utilities;
using ShipmentTracking.Subscriber.Carriers;
using ShipmentTracking.Subscriber.Data;
using ShipmentTracking.Subscriber.Handlers;
using ShipmentTracking.Subscriber.Services;
using System.Text.Json;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ShipmentTracking.Subscriber;

/// <summary>
/// AWS Lambda function handler for processing fulfillment order shipped events.
/// This function subscribes to carrier tracking webhooks for each shipped package.
/// </summary>
public class Function
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<Function> _logger;
    private readonly ShipmentNotificationHandler _handler;
    private static bool _coldStart = true;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Initializes dependency injection container and configures services.
    /// </summary>
    public Function()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();
        
        ConfigureServices(services, configuration);
        
        _serviceProvider = services.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<ILogger<Function>>();
        _handler = _serviceProvider.GetRequiredService<ShipmentNotificationHandler>();

        if (_coldStart)
        {
            _logger.LogInformation("Lambda cold start - Function initialized");
            _coldStart = false;
        }
    }

    /// <summary>
    /// Constructor for testing with dependency injection.
    /// </summary>
    /// <param name="handler">The notification handler.</param>
    /// <param name="logger">The logger instance.</param>
    public Function(ShipmentNotificationHandler handler, ILogger<Function> logger)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = null!;
    }

    /// <summary>
    /// Lambda function handler method.
    /// Processes SNS events containing fulfillment order shipped notifications.
    /// </summary>
    /// <param name="snsEvent">The SNS event containing shipped order information.</param>
    /// <param name="context">The Lambda context.</param>
    /// <returns>A response object with processing results.</returns>
    public async Task<FunctionResponse> FunctionHandler(SNSEvent snsEvent, ILambdaContext context)
    {
        var requestId = context.AwsRequestId;
        
        try
        {
            _logger.LogInformation(
                "Processing SNS event with {RecordCount} records. RequestId: {RequestId}, RemainingTime: {RemainingTime}ms",
                snsEvent?.Records?.Count ?? 0,
                requestId,
                context.RemainingTime.TotalMilliseconds);

            // Validate input
            if (snsEvent == null || snsEvent.Records == null || !snsEvent.Records.Any())
            {
                _logger.LogWarning("Received empty SNS event");
                return new FunctionResponse
                {
                    Success = false,
                    Message = "Empty SNS event received",
                    RequestId = requestId
                };
            }

            // Create cancellation token based on remaining time
            using var cts = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(context.RemainingTime.TotalMilliseconds - 5000)); // Reserve 5s for cleanup

            // Process the event
            var result = await _handler.HandleAsync(snsEvent, cts.Token);

            // Log results
            _logger.LogInformation(
                "Completed processing. Processed: {ProcessedCount}, Failed: {FailedCount}, Success: {Success}",
                result.ProcessedCount,
                result.FailedCount,
                result.Success);

            if (result.Errors.Any())
            {
                _logger.LogWarning("Errors encountered: {Errors}", JsonSerializer.Serialize(result.Errors));
            }

            // If not all succeeded and there are retryable errors, throw exception to trigger Lambda retry
            if (!result.Success)
            {
                throw new SubscriptionProcessingException(
                    $"Failed to process all subscriptions. Processed: {result.ProcessedCount}, Failed: {result.FailedCount}",
                    result.Errors);
            }

            return new FunctionResponse
            {
                Success = true,
                Message = $"Successfully processed {result.ProcessedCount} packages",
                ProcessedCount = result.ProcessedCount,
                FailedCount = result.FailedCount,
                RequestId = requestId
            };
        }
        catch (SubscriptionProcessingException ex)
        {
            _logger.LogError(ex, "Subscription processing failed - Lambda will retry");
            throw; // Let Lambda retry mechanism handle it
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in Lambda function handler");
            throw; // Let Lambda retry mechanism handle it
        }
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Configuration
        services.AddSingleton(configuration);

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddConfiguration(configuration.GetSection("Logging"));
        });

        // Memory cache for secrets
        services.AddMemoryCache();

        // AWS Services
        services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
        services.AddSingleton<IAmazonSecretsManager, AmazonSecretsManagerClient>();

        // Common utilities
        services.AddSingleton<SecretManager>();

        // Repository
        var tableName = configuration["DynamoDbTableName"] ?? 
                       Environment.GetEnvironmentVariable("DYNAMODB_TABLE_NAME") ?? 
                       "ShipmentTrackingSubscriptions";
        services.AddSingleton(sp => new SubscriptionRepository(
            sp.GetRequiredService<IAmazonDynamoDB>(),
            sp.GetRequiredService<ILogger<SubscriptionRepository>>(),
            tableName));

        // HTTP Clients with Polly retry policies
        var uspsApiBaseUrl = configuration["UspsApiBaseUrl"] ?? 
                            Environment.GetEnvironmentVariable("USPS_API_BASE_URL") ?? 
                            "https://api.usps.com";

        services.AddHttpClient<UspsSubscriptionClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "ShipmentTracking-Subscriber/1.0");
        })
        .AddPolicyHandler(GetRetryPolicy())
        .AddPolicyHandler(GetCircuitBreakerPolicy());

        // Carrier clients
        services.AddTransient(sp => new UspsSubscriptionClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(UspsSubscriptionClient)),
            sp.GetRequiredService<SecretManager>(),
            sp.GetRequiredService<ILogger<UspsSubscriptionClient>>(),
            uspsApiBaseUrl));

        // Carrier factory
        services.AddSingleton<CarrierSubscriptionClientFactory>();

        // Services
        services.AddSingleton<SubscriptionService>();

        // Handlers
        var webhookBaseUrl = configuration["WebhookBaseUrl"] ?? 
                            Environment.GetEnvironmentVariable("WEBHOOK_BASE_URL") ?? 
                            "https://tracking-webhook.example.com";
        services.AddSingleton(sp => new ShipmentNotificationHandler(
            sp.GetRequiredService<SubscriptionService>(),
            sp.GetRequiredService<ILogger<ShipmentNotificationHandler>>(),
            webhookBaseUrl));
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        // Exponential backoff with jitter
        var delay = Backoff.DecorrelatedJitterBackoffV2(
            medianFirstRetryDelay: TimeSpan.FromSeconds(1),
            retryCount: 3);

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
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

/// <summary>
/// Response object returned by the Lambda function.
/// </summary>
public class FunctionResponse
{
    /// <summary>
    /// Indicates if the function executed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Message describing the result.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Number of packages successfully processed.
    /// </summary>
    public int ProcessedCount { get; set; }

    /// <summary>
    /// Number of packages that failed processing.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// AWS Request ID for correlation.
    /// </summary>
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>
/// Exception thrown when subscription processing fails with retryable errors.
/// </summary>
public class SubscriptionProcessingException : Exception
{
    /// <summary>
    /// List of error messages.
    /// </summary>
    public List<string> Errors { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscriptionProcessingException"/> class.
    /// </summary>
    public SubscriptionProcessingException(string message, List<string> errors) : base(message)
    {
        Errors = errors;
    }
}
