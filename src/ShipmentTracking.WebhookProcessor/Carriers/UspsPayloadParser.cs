using Microsoft.Extensions.Logging;
using ShipmentTracking.WebhookProcessor.Models;
using System.Globalization;
using System.Text.Json;

namespace ShipmentTracking.WebhookProcessor.Carriers;

/// <summary>
/// USPS-specific webhook payload parser.
/// Parses USPS tracking webhook notifications into normalized format.
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

            // Deserialize USPS-specific payload
            var uspsPayload = JsonSerializer.Deserialize<UspsWebhookPayload>(payload);
            
            if (uspsPayload == null)
            {
                throw new PayloadParsingException("Failed to deserialize USPS payload");
            }

            // Validate required fields
            if (string.IsNullOrWhiteSpace(uspsPayload.TrackingNumber))
            {
                throw new PayloadParsingException("Tracking number is required");
            }

            if (string.IsNullOrWhiteSpace(uspsPayload.StatusCode))
            {
                throw new PayloadParsingException("Status code is required");
            }

            // Parse timestamp
            DateTime eventTimestamp;
            try
            {
                eventTimestamp = DateTime.Parse(uspsPayload.EventTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse event timestamp: {Timestamp}", uspsPayload.EventTimestamp);
                eventTimestamp = DateTime.UtcNow;
            }

            // Parse estimated delivery date if present
            DateTime? estimatedDeliveryDate = null;
            if (!string.IsNullOrWhiteSpace(uspsPayload.EstimatedDeliveryDate))
            {
                try
                {
                    estimatedDeliveryDate = DateTime.Parse(uspsPayload.EstimatedDeliveryDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse estimated delivery date: {Date}", uspsPayload.EstimatedDeliveryDate);
                }
            }

            // Build location information
            EventLocation? location = null;
            if (!string.IsNullOrWhiteSpace(uspsPayload.EventCity) ||
                !string.IsNullOrWhiteSpace(uspsPayload.EventState) ||
                !string.IsNullOrWhiteSpace(uspsPayload.EventZip))
            {
                location = new EventLocation
                {
                    City = uspsPayload.EventCity,
                    State = uspsPayload.EventState,
                    PostalCode = uspsPayload.EventZip,
                    Country = uspsPayload.EventCountry ?? "US",
                    FacilityName = uspsPayload.FacilityName
                };
            }

            // Build normalized tracking event
            var trackingEvent = new CarrierTrackingEvent
            {
                Carrier = "USPS",
                TrackingNumber = uspsPayload.TrackingNumber,
                StatusCode = uspsPayload.StatusCode,
                SubStatusCode = uspsPayload.StatusCategory,
                StatusDescription = uspsPayload.StatusSummary ?? uspsPayload.Status,
                EventTimestamp = eventTimestamp,
                Location = location,
                EstimatedDeliveryDate = estimatedDeliveryDate,
                AdditionalData = new Dictionary<string, string>
                {
                    ["RawStatus"] = uspsPayload.Status
                }
            };

            _logger.LogDebug(
                "Successfully parsed USPS webhook: TrackingNumber={TrackingNumber}, StatusCode={StatusCode}",
                trackingEvent.TrackingNumber,
                trackingEvent.StatusCode);

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
}
