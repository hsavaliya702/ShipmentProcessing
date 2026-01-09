using ShipmentTracking.Subscriber.Models;

namespace ShipmentTracking.Subscriber.Carriers;

/// <summary>
/// Interface for carrier-specific subscription client implementations.
/// Each carrier (USPS, UPS, FedEx) implements this interface to provide
/// carrier-specific webhook subscription logic.
/// </summary>
public interface ICarrierSubscriptionClient
{
    /// <summary>
    /// Gets the carrier code this client handles (e.g., "USPS", "UPS", "FEDEX").
    /// </summary>
    string CarrierCode { get; }

    /// <summary>
    /// Subscribes to tracking updates for the specified tracking number.
    /// The carrier will send webhook notifications to the provided callback URL
    /// when tracking status changes occur.
    /// </summary>
    /// <param name="request">Subscription request containing tracking number and callback URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Subscription response indicating success or failure.</returns>
    /// <exception cref="ArgumentNullException">When request is null.</exception>
    /// <exception cref="ArgumentException">When request contains invalid data.</exception>
    Task<CarrierSubscriptionResponse> SubscribeAsync(
        CarrierSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsubscribes from tracking updates for the specified subscription.
    /// Optional operation - not all carriers support explicit unsubscribe.
    /// </summary>
    /// <param name="subscriptionId">The carrier's subscription ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if unsubscribe was successful or not needed, false otherwise.</returns>
    Task<bool> UnsubscribeAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a tracking number is in the correct format for this carrier.
    /// </summary>
    /// <param name="trackingNumber">The tracking number to validate.</param>
    /// <returns>True if the tracking number format is valid for this carrier.</returns>
    bool IsValidTrackingNumber(string trackingNumber);
}
