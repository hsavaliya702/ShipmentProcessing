using Microsoft.Extensions.Logging;
using ShipmentTracking.WebhookProcessor.Models;
using System.Globalization;
using System.Text.Json;

namespace ShipmentTracking.WebhookProcessor.Carriers;

/// <summary>
/// USPS-specific webhook payload parser per API v3 specification.
/// Parses USPS tracking webhook notifications with nested JSON structure into normalized format.
/// </summary>
public class UspsPayloadParser : ICarrierPayloadParser
{
    private readonly ILogger<UspsPayloadParser> _logger;

    /// <inheritdoc/>
    public string CarrierCode => "USPS";

    /// <summary>
    /// Initializes a new instance of the <see cref="UspsPayloadParser"/> class.
    /// </summary>
    public UspsPayloadParser(ILogger<UspsPayloadParser> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<CarrierTrackingEvent> ParseAsync(string payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new PayloadParsingException("Payload cannot be null or empty");
        }

        try
        {
            _logger.LogDebug("Parsing USPS webhook payload");

            // Step 1: Parse outer notification envelope
            var notification = JsonSerializer.Deserialize<UspsWebhookNotification>(
                payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (notification == null)
            {
                throw new PayloadParsingException("Failed to deserialize USPS notification envelope");
            }

            // Step 2: The payload field contains escaped JSON - need to deserialize it a second time
            var trackingData = JsonSerializer.Deserialize<UspsTrackingData>(
                notification.Payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (trackingData == null || trackingData.TrackingEvents == null || !trackingData.TrackingEvents.Any())
            {
                throw new PayloadParsingException("No tracking events in USPS payload");
            }

            // Step 3: Get the most recent tracking event
            var latestEvent = trackingData.TrackingEvents
                .OrderByDescending(e => ParseUspsTimestamp(e.EventTimestamp))
                .First();

            // Validate required fields
            if (string.IsNullOrWhiteSpace(trackingData.TrackingNumber))
            {
                throw new PayloadParsingException("Tracking number is required");
            }

            if (string.IsNullOrWhiteSpace(latestEvent.EventCode))
            {
                throw new PayloadParsingException("Event code is required");
            }

            // Parse event timestamp
            var eventTimestamp = ParseUspsTimestamp(latestEvent.EventTimestamp);

            // Build location information
            EventLocation? location = null;
            if (!string.IsNullOrWhiteSpace(latestEvent.EventCity) ||
                !string.IsNullOrWhiteSpace(latestEvent.EventState) ||
                !string.IsNullOrWhiteSpace(latestEvent.EventZIPCode))
            {
                location = new EventLocation
                {
                    City = latestEvent.EventCity,
                    State = latestEvent.EventState,
                    PostalCode = latestEvent.EventZIPCode,
                    Country = latestEvent.EventCountry ?? "US",
                    FacilityName = latestEvent.FacilityName
                };
            }

            // Build normalized tracking event
            var trackingEvent = new CarrierTrackingEvent
            {
                Carrier = "USPS",
                TrackingNumber = trackingData.TrackingNumber,
                StatusCode = latestEvent.EventCode,
                SubStatusCode = trackingData.StatusCategory,
                StatusDescription = latestEvent.EventType,
                EventTimestamp = eventTimestamp,
                Location = location,
                AdditionalData = new Dictionary<string, string>
                {
                    ["subscriptionId"] = notification.SubscriptionId,
                    ["statusCategory"] = trackingData.StatusCategory ?? "",
                    ["statusSummary"] = trackingData.StatusSummary ?? "",
                    ["mailClass"] = trackingData.MailClass ?? "",
                    ["serviceTypeCode"] = trackingData.ServiceTypeCode ?? "",
                    ["destinationZip"] = trackingData.DestinationZIPCode ?? "",
                    ["actionCode"] = latestEvent.ActionCode ?? "",
                    ["reasonCode"] = latestEvent.ReasonCode ?? "",
                    ["recipientName"] = latestEvent.RecipientName ?? "",
                    ["firm"] = latestEvent.Firm ?? ""
                }
            };

            _logger.LogDebug(
                "Successfully parsed USPS webhook: TrackingNumber={TrackingNumber}, EventCode={EventCode}, SubscriptionId={SubscriptionId}",
                trackingEvent.TrackingNumber,
                trackingEvent.StatusCode,
                notification.SubscriptionId);

            return Task.FromResult(trackingEvent);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse USPS JSON payload");
            throw new PayloadParsingException("Invalid JSON format", ex);
        }
        catch (PayloadParsingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error parsing USPS payload");
            throw new PayloadParsingException($"Unexpected error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses USPS timestamp formats.
    /// USPS format examples:
    /// - "2025-02-07T12:55:12-05:00" (with timezone)
    /// - "2025-02-07T17:55:12Z" (GMT)
    /// </summary>
    private DateTime ParseUspsTimestamp(string timestamp)
    {
        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result))
        {
            return result.ToUniversalTime();
        }

        _logger.LogWarning("Failed to parse USPS timestamp: {Timestamp}, using current time", timestamp);
        return DateTime.UtcNow;
    }
}
