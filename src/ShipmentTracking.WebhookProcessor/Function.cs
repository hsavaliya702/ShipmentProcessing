using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SecretsManager;
using Amazon.SimpleNotificationService;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Utilities;
using ShipmentTracking.WebhookProcessor.Carriers;
using ShipmentTracking.WebhookProcessor.Handlers;
using ShipmentTracking.WebhookProcessor.Models;
using ShipmentTracking.WebhookProcessor.Services;
using ShipmentTracking.WebhookProcessor.Translation;
using ShipmentTracking.WebhookProcessor.Validation;
using System.Text.Json;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ShipmentTracking.WebhookProcessor;

/// <summary>
/// AWS Lambda function handler for processing carrier webhook callbacks.
/// This function receives webhook notifications from carriers and publishes canonical tracking events.
/// </summary>
public class Function
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<Function> _logger;
    private readonly WebhookHandler _handler;
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
        _handler = _serviceProvider.GetRequiredService<WebhookHandler>();

        if (_coldStart)
        {
            _logger.LogInformation("Lambda cold start - Webhook Processor initialized");
            _coldStart = false;
        }
    }

    /// <summary>
    /// Constructor for testing with dependency injection.
    /// </summary>
    public Function(WebhookHandler handler, ILogger<Function> logger)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = null!;
    }

    /// <summary>
    /// Lambda function handler method for API Gateway proxy requests.
    /// Routes webhook requests by carrier identifier from the path.
    /// </summary>
    /// <param name="request">API Gateway proxy request.</param>
    /// <param name="context">Lambda context.</param>
    /// <returns>API Gateway proxy response.</returns>
    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var requestId = context.AwsRequestId;

        try
        {
            _logger.LogInformation(
                "Processing webhook request. Path: {Path}, RequestId: {RequestId}, RemainingTime: {RemainingTime}ms",
                request.Path,
                requestId,
                context.RemainingTime.TotalMilliseconds);

            // Validate request
            if (request == null || string.IsNullOrWhiteSpace(request.Body))
            {
                _logger.LogWarning("Received empty webhook request");
                return CreateResponse(400, "Empty request body");
            }

            // Extract carrier from path (e.g., /webhook/usps -> "usps")
            var carrier = ExtractCarrierFromPath(request.Path);
            if (string.IsNullOrWhiteSpace(carrier))
            {
                _logger.LogWarning("Could not extract carrier from path: {Path}", request.Path);
                return CreateResponse(400, "Invalid webhook path - carrier not specified");
            }

            // Build webhook request
            var webhookRequest = new WebhookRequest
            {
                Carrier = carrier,
                Payload = request.Body,
                Headers = request.Headers != null 
                    ? new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(),
                ReceivedAt = DateTime.UtcNow
            };

            // Create cancellation token based on remaining time
            using var cts = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(context.RemainingTime.TotalMilliseconds - 2000)); // Reserve 2s

            // Process webhook
            var result = await _handler.ProcessWebhookAsync(webhookRequest, cts.Token);

            _logger.LogInformation(
                "Webhook processing completed. Success: {Success}, StatusCode: {StatusCode}, Message: {Message}",
                result.Success,
                result.StatusCode,
                result.Message);

            // Return appropriate response
            return CreateResponse(result.StatusCode, result.Message, new
            {
                success = result.Success,
                message = result.Message,
                messageId = result.MessageId,
                requestId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in webhook processor Lambda function");
            return CreateResponse(500, "Internal server error");
        }
    }

    private static string? ExtractCarrierFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Expected format: /webhook/{carrier} or /webhook/{carrier}/
        var segments = path.Trim('/').Split('/');
        
        if (segments.Length >= 2 && segments[0].Equals("webhook", StringComparison.OrdinalIgnoreCase))
        {
            return segments[1].ToUpperInvariant();
        }

        return null;
    }

    private static APIGatewayProxyResponse CreateResponse(int statusCode, string message, object? body = null)
    {
        var responseBody = body ?? new { message };

        return new APIGatewayProxyResponse
        {
            StatusCode = statusCode,
            Body = JsonSerializer.Serialize(responseBody),
            Headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Processing-Timestamp"] = DateTime.UtcNow.ToString("O")
            }
        };
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
        services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();

        // Common utilities
        services.AddSingleton<SecretManager>();

        // Configuration values
        var tableName = configuration["DynamoDbTableName"] ?? 
                       Environment.GetEnvironmentVariable("DYNAMODB_TABLE_NAME") ?? 
                       "ShipmentTrackingSubscriptions";
        
        var snsTopicArn = configuration["SnsTopicArn"] ?? 
                         Environment.GetEnvironmentVariable("SNS_TOPIC_ARN") ?? 
                         throw new InvalidOperationException("SNS_TOPIC_ARN environment variable is required");

        // Services
        services.AddSingleton(sp => new CorrelationService(
            sp.GetRequiredService<IAmazonDynamoDB>(),
            sp.GetRequiredService<ILogger<CorrelationService>>(),
            tableName));

        services.AddSingleton(sp => new EventPublisher(
            sp.GetRequiredService<IAmazonSimpleNotificationService>(),
            sp.GetRequiredService<ILogger<EventPublisher>>(),
            snsTopicArn));

        services.AddSingleton<StatusTranslationService>();

        // Validators
        services.AddSingleton<IWebhookValidator, UspsWebhookValidator>();
        // Future: services.AddSingleton<IWebhookValidator, UpsWebhookValidator>();
        // Future: services.AddSingleton<IWebhookValidator, FedExWebhookValidator>();

        // Parsers
        services.AddSingleton<ICarrierPayloadParser, UspsPayloadParser>();
        // Future: services.AddSingleton<ICarrierPayloadParser, UpsPayloadParser>();
        // Future: services.AddSingleton<ICarrierPayloadParser, FedExPayloadParser>();

        // Handler
        services.AddSingleton<WebhookHandler>();
    }
}
