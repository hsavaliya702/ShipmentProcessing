using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using ShipmentTracking.WebhookProcessor.Models;
using System.Text.Json;

namespace ShipmentTracking.WebhookProcessor.Services;

/// <summary>
/// Service for retrieving subscription correlation data from DynamoDB.
/// </summary>
public class CorrelationService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly ILogger<CorrelationService> _logger;
    private readonly string _tableName;

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationService"/> class.
    /// </summary>
    public CorrelationService(
        IAmazonDynamoDB dynamoDb,
        ILogger<CorrelationService> logger,
        string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <summary>
    /// Retrieves correlation data for a tracking number.
    /// </summary>
    /// <param name="carrier">Carrier identifier.</param>
    /// <param name="trackingNumber">Tracking number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Subscription correlation data if found, null otherwise.</returns>
    public async Task<SubscriptionCorrelation?> GetCorrelationDataAsync(
        string carrier,
        string trackingNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(carrier))
        {
            throw new ArgumentException("Carrier cannot be null or empty", nameof(carrier));
        }

        if (string.IsNullOrWhiteSpace(trackingNumber))
        {
            throw new ArgumentException("Tracking number cannot be null or empty", nameof(trackingNumber));
        }

        try
        {
            var pk = GeneratePartitionKey(carrier, trackingNumber);
            
            var request = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = pk },
                    ["SK"] = new AttributeValue { S = "META" }
                },
                ProjectionExpression = "FulfillmentOrderId, OrderId, PackageId, Metadata"
            };

            var response = await _dynamoDb.GetItemAsync(request, cancellationToken);

            if (response.Item == null || !response.Item.Any())
            {
                _logger.LogWarning(
                    "No correlation data found for {Carrier}/{TrackingNumber}",
                    carrier, trackingNumber);
                return null;
            }

            var correlation = new SubscriptionCorrelation
            {
                FulfillmentOrderId = response.Item.TryGetValue("FulfillmentOrderId", out var foId) 
                    ? foId.S 
                    : string.Empty,
                OrderId = response.Item.TryGetValue("OrderId", out var oId) 
                    ? oId.S 
                    : string.Empty,
                PackageId = response.Item.TryGetValue("PackageId", out var pId) 
                    ? pId.S 
                    : string.Empty
            };

            // Parse metadata if present
            if (response.Item.TryGetValue("Metadata", out var metadataAttr) && metadataAttr.M != null)
            {
                correlation.Metadata = new Dictionary<string, string>();
                foreach (var kvp in metadataAttr.M)
                {
                    if (kvp.Value.S != null)
                    {
                        correlation.Metadata[kvp.Key] = kvp.Value.S;
                    }
                }
            }

            _logger.LogDebug(
                "Retrieved correlation data for {Carrier}/{TrackingNumber}: FulfillmentOrderId={FulfillmentOrderId}",
                carrier, trackingNumber, correlation.FulfillmentOrderId);

            return correlation;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error retrieving correlation data for {Carrier}/{TrackingNumber}",
                carrier, trackingNumber);
            throw;
        }
    }

    private static string GeneratePartitionKey(string carrier, string trackingNumber)
    {
        return $"TRACK#{carrier.ToUpperInvariant()}#{trackingNumber}";
    }
}
