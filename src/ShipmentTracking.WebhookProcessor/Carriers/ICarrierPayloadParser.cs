using ShipmentTracking.WebhookProcessor.Models;

namespace ShipmentTracking.WebhookProcessor.Carriers;

/// <summary>
/// Interface for parsing carrier-specific webhook payloads.
/// </summary>
public interface ICarrierPayloadParser
{
    /// <summary>
    /// Gets the carrier code this parser handles.
    /// </summary>
    string CarrierCode { get; }

    /// <summary>
    /// Parses carrier-specific webhook payload into a normalized tracking event.
    /// </summary>
    /// <param name="payload">Raw webhook payload from carrier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed tracking event.</returns>
    /// <exception cref="PayloadParsingException">When payload cannot be parsed.</exception>
    Task<CarrierTrackingEvent> ParseAsync(string payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Exception thrown when a webhook payload cannot be parsed.
/// </summary>
public class PayloadParsingException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadParsingException"/> class.
    /// </summary>
    public PayloadParsingException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadParsingException"/> class with inner exception.
    /// </summary>
    public PayloadParsingException(string message, Exception innerException) : base(message, innerException) { }
}
