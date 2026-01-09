using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Extensions;
using ShipmentTracking.Common.Models;
using System.Text.Json;

namespace ShipmentTracking.WebhookProcessor.Services;

/// <summary>
/// Service for publishing canonical tracking status events to SNS.
/// </summary>
public class EventPublisher
{
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly ILogger<EventPublisher> _logger;
    private readonly string _topicArn;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventPublisher"/> class.
    /// </summary>
    public EventPublisher(
        IAmazonSimpleNotificationService snsClient,
        ILogger<EventPublisher> logger,
        string topicArn)
    {
        _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _topicArn = topicArn ?? throw new ArgumentNullException(nameof(topicArn));
    }

    /// <summary>
    /// Publishes a canonical tracking status event to SNS.
    /// </summary>
    /// <param name="trackingEvent">The canonical tracking status event to publish.</param>
    /// <param name="correlationId">Correlation ID for distributed tracing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Message ID from SNS.</returns>
    public async Task<string> PublishAsync(
        ShipmentTrackingStatusEvent trackingEvent,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (trackingEvent == null)
        {
            throw new ArgumentNullException(nameof(trackingEvent));
        }

        try
        {
            _logger.LogInformationWithCorrelation(
                $"Publishing tracking status event for {trackingEvent.Carrier}/{trackingEvent.TrackingNumber}",
                correlationId,
                new Dictionary<string, object>
                {
                    ["Status"] = trackingEvent.Status.ToString(),
                    ["OrderId"] = trackingEvent.OrderId,
                    ["PackageId"] = trackingEvent.PackageId
                });

            var message = JsonSerializer.Serialize(trackingEvent, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            var request = new PublishRequest
            {
                TopicArn = _topicArn,
                Message = message,
                Subject = $"Shipment Status Update - {trackingEvent.Status}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["CorrelationId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = correlationId
                    },
                    ["EventType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "ShipmentTrackingStatusEvent"
                    },
                    ["Carrier"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = trackingEvent.Carrier
                    },
                    ["Status"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = trackingEvent.Status.ToString()
                    },
                    ["OrderId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = trackingEvent.OrderId
                    },
                    ["FulfillmentOrderId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = trackingEvent.FulfillmentOrderId
                    }
                }
            };

            var response = await _snsClient.PublishAsync(request, cancellationToken);

            _logger.LogInformationWithCorrelation(
                $"Successfully published tracking event to SNS",
                correlationId,
                new Dictionary<string, object>
                {
                    ["MessageId"] = response.MessageId,
                    ["SequenceNumber"] = response.SequenceNumber ?? "N/A"
                });

            return response.MessageId;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(
                ex,
                "Failed to publish tracking event to SNS",
                correlationId,
                new Dictionary<string, object>
                {
                    ["OrderId"] = trackingEvent.OrderId,
                    ["TrackingNumber"] = trackingEvent.TrackingNumber
                });
            throw;
        }
    }

    /// <summary>
    /// Publishes multiple tracking events in batch.
    /// </summary>
    public async Task<List<string>> PublishBatchAsync(
        List<ShipmentTrackingStatusEvent> trackingEvents,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (trackingEvents == null || !trackingEvents.Any())
        {
            throw new ArgumentException("At least one event is required", nameof(trackingEvents));
        }

        var messageIds = new List<string>();

        foreach (var trackingEvent in trackingEvents)
        {
            try
            {
                var messageId = await PublishAsync(trackingEvent, correlationId, cancellationToken);
                messageIds.Add(messageId);
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCorrelation(
                    ex,
                    $"Failed to publish event in batch for {trackingEvent.TrackingNumber}",
                    correlationId);
                // Continue with remaining events
            }
        }

        return messageIds;
    }
}
