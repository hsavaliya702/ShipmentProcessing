using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Extensions;
using ShipmentTracking.Subscriber.Models;
using System.Text.Json;

namespace ShipmentTracking.Subscriber.Data;

/// <summary>
/// Repository for managing subscription records in DynamoDB.
/// Implements the Repository pattern with conditional writes for concurrency control.
/// </summary>
public class SubscriptionRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly ILogger<SubscriptionRepository> _logger;
    private readonly string _tableName;
    private const int DefaultTtlDays = 90; // Keep records for 90 days
    private const int MaxRetryAttempts = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscriptionRepository"/> class.
    /// </summary>
    public SubscriptionRepository(
        IAmazonDynamoDB dynamoDb,
        ILogger<SubscriptionRepository> logger,
        string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <summary>
    /// Gets an existing subscription record.
    /// </summary>
    public async Task<SubscriptionRecord?> GetSubscriptionAsync(
        string carrier,
        string trackingNumber,
        CancellationToken cancellationToken = default)
    {
        var pk = GeneratePartitionKey(carrier, trackingNumber);
        
        try
        {
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
            {
                return null;
            }

            return DeserializeRecord(response.Item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving subscription for {Carrier}/{TrackingNumber}", carrier, trackingNumber);
            throw;
        }
    }

    /// <summary>
    /// Creates or updates a subscription record with conditional write for idempotency.
    /// Prevents duplicate active subscriptions using condition expression.
    /// </summary>
    public async Task<bool> CreateOrUpdateSubscriptionAsync(
        SubscriptionRecord record,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        record.PK = GeneratePartitionKey(record.Carrier, record.TrackingNumber);
        record.SK = "META";
        record.UpdatedAt = DateTime.UtcNow;
        record.TTL = CalculateTtl(DefaultTtlDays);

        try
        {
            var item = SerializeRecord(record);
            
            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                // Only allow write if:
                // 1. Record doesn't exist (attribute_not_exists), OR
                // 2. Existing record is not Active AND status is Pending or Failed
                ConditionExpression = "attribute_not_exists(PK) OR (SubscriptionStatus <> :active AND (SubscriptionStatus = :pending OR SubscriptionStatus = :failed))",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":active"] = new AttributeValue { S = SubscriptionStatus.Active.ToString() },
                    [":pending"] = new AttributeValue { S = SubscriptionStatus.Pending.ToString() },
                    [":failed"] = new AttributeValue { S = SubscriptionStatus.Failed.ToString() }
                }
            };

            await _dynamoDb.PutItemAsync(request, cancellationToken);
            
            _logger.LogInformationWithCorrelation(
                $"Successfully saved subscription record for {record.Carrier}/{record.TrackingNumber}",
                correlationId,
                new Dictionary<string, object>
                {
                    ["Status"] = record.SubscriptionStatus.ToString(),
                    ["AttemptCount"] = record.AttemptCount
                });

            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            // Record already exists with Active status - this is expected for idempotency
            _logger.LogInformationWithCorrelation(
                $"Subscription already active for {record.Carrier}/{record.TrackingNumber}",
                correlationId);
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(
                ex,
                $"Error saving subscription record for {record.Carrier}/{record.TrackingNumber}",
                correlationId);
            throw;
        }
    }

    /// <summary>
    /// Updates an existing subscription record without condition checks.
    /// Use this when you need to update fields regardless of current state.
    /// </summary>
    public async Task UpdateSubscriptionStatusAsync(
        string carrier,
        string trackingNumber,
        SubscriptionStatus status,
        string? errorMessage = null,
        ErrorType? errorType = null,
        CancellationToken cancellationToken = default)
    {
        var pk = GeneratePartitionKey(carrier, trackingNumber);

        try
        {
            var updateExpression = "SET SubscriptionStatus = :status, UpdatedAt = :updatedAt";
            var attributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new AttributeValue { S = status.ToString() },
                [":updatedAt"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") }
            };

            if (status == SubscriptionStatus.Active)
            {
                updateExpression += ", ActivatedAt = :activatedAt";
                attributeValues[":activatedAt"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") };
            }

            if (!string.IsNullOrEmpty(errorMessage))
            {
                updateExpression += ", LastError = :error";
                attributeValues[":error"] = new AttributeValue { S = errorMessage };
            }

            if (errorType.HasValue)
            {
                updateExpression += ", ErrorType = :errorType";
                attributeValues[":errorType"] = new AttributeValue { S = errorType.Value.ToString() };
            }

            var request = new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = pk },
                    ["SK"] = new AttributeValue { S = "META" }
                },
                UpdateExpression = updateExpression,
                ExpressionAttributeValues = attributeValues
            };

            await _dynamoDb.UpdateItemAsync(request, cancellationToken);
            
            _logger.LogInformation(
                "Updated subscription status for {Carrier}/{TrackingNumber} to {Status}",
                carrier, trackingNumber, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error updating subscription status for {Carrier}/{TrackingNumber}",
                carrier, trackingNumber);
            throw;
        }
    }

    /// <summary>
    /// Increments the attempt count for a subscription record.
    /// </summary>
    public async Task IncrementAttemptCountAsync(
        string carrier,
        string trackingNumber,
        CancellationToken cancellationToken = default)
    {
        var pk = GeneratePartitionKey(carrier, trackingNumber);

        try
        {
            var request = new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = pk },
                    ["SK"] = new AttributeValue { S = "META" }
                },
                UpdateExpression = "ADD AttemptCount :inc SET UpdatedAt = :updatedAt",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":inc"] = new AttributeValue { N = "1" },
                    [":updatedAt"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") }
                }
            };

            await _dynamoDb.UpdateItemAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error incrementing attempt count for {Carrier}/{TrackingNumber}",
                carrier, trackingNumber);
            throw;
        }
    }

    /// <summary>
    /// Checks if max retry attempts have been reached for a subscription.
    /// </summary>
    public async Task<bool> HasExceededMaxAttemptsAsync(
        string carrier,
        string trackingNumber,
        CancellationToken cancellationToken = default)
    {
        var record = await GetSubscriptionAsync(carrier, trackingNumber, cancellationToken);
        return record != null && record.AttemptCount >= MaxRetryAttempts;
    }

    private static string GeneratePartitionKey(string carrier, string trackingNumber)
    {
        return $"TRACK#{carrier.ToUpperInvariant()}#{trackingNumber}";
    }

    private static long CalculateTtl(int days)
    {
        return DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds();
    }

    private static Dictionary<string, AttributeValue> SerializeRecord(SubscriptionRecord record)
    {
        var json = JsonSerializer.Serialize(record);
        var doc = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
        
        var item = new Dictionary<string, AttributeValue>();
        
        foreach (var kvp in doc)
        {
            item[kvp.Key] = ConvertToAttributeValue(kvp.Value);
        }

        return item;
    }

    private static SubscriptionRecord? DeserializeRecord(Dictionary<string, AttributeValue> item)
    {
        var dict = new Dictionary<string, object>();
        
        foreach (var kvp in item)
        {
            dict[kvp.Key] = ConvertFromAttributeValue(kvp.Value);
        }

        var json = JsonSerializer.Serialize(dict);
        return JsonSerializer.Deserialize<SubscriptionRecord>(json);
    }

    private static AttributeValue ConvertToAttributeValue(object value)
    {
        if (value == null)
        {
            return new AttributeValue { NULL = true };
        }

        var jsonElement = (JsonElement)value;
        
        return jsonElement.ValueKind switch
        {
            JsonValueKind.String => new AttributeValue { S = jsonElement.GetString() },
            JsonValueKind.Number => new AttributeValue { N = jsonElement.GetInt64().ToString() },
            JsonValueKind.True => new AttributeValue { BOOL = true },
            JsonValueKind.False => new AttributeValue { BOOL = false },
            JsonValueKind.Null => new AttributeValue { NULL = true },
            JsonValueKind.Object => new AttributeValue { M = ConvertObjectToMap(jsonElement) },
            _ => new AttributeValue { S = jsonElement.ToString() }
        };
    }

    private static Dictionary<string, AttributeValue> ConvertObjectToMap(JsonElement jsonElement)
    {
        var map = new Dictionary<string, AttributeValue>();
        
        foreach (var property in jsonElement.EnumerateObject())
        {
            map[property.Name] = ConvertToAttributeValue(property.Value);
        }

        return map;
    }

    private static object ConvertFromAttributeValue(AttributeValue attr)
    {
        if (attr.NULL)
        {
            return null!;
        }
        if (attr.S != null)
        {
            return attr.S;
        }
        if (attr.N != null)
        {
            return long.Parse(attr.N);
        }
        if (attr.BOOL)
        {
            return true;
        }
        if (attr.M != null && attr.M.Any())
        {
            var dict = new Dictionary<string, object>();
            foreach (var kvp in attr.M)
            {
                dict[kvp.Key] = ConvertFromAttributeValue(kvp.Value);
            }
            return dict;
        }

        return attr.S ?? string.Empty;
    }
}
