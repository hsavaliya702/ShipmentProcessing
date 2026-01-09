using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Models;

namespace ShipmentTracking.Subscriber.Carriers;

/// <summary>
/// Factory for creating carrier-specific subscription clients.
/// Implements the Factory pattern to provide appropriate carrier clients based on carrier code.
/// </summary>
public class CarrierSubscriptionClientFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CarrierSubscriptionClientFactory> _logger;
    private readonly Dictionary<string, Type> _carrierClientTypes;

    /// <summary>
    /// Initializes a new instance of the <see cref="CarrierSubscriptionClientFactory"/> class.
    /// </summary>
    public CarrierSubscriptionClientFactory(
        IServiceProvider serviceProvider,
        ILogger<CarrierSubscriptionClientFactory> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Register carrier client types
        _carrierClientTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            [CarrierCodes.Usps] = typeof(UspsSubscriptionClient),
            // Future implementations:
            // [CarrierCodes.Ups] = typeof(UpsSubscriptionClient),
            // [CarrierCodes.FedEx] = typeof(FedExSubscriptionClient)
        };
    }

    /// <summary>
    /// Creates a carrier subscription client for the specified carrier.
    /// </summary>
    /// <param name="carrierCode">The carrier code (e.g., "USPS", "UPS", "FEDEX").</param>
    /// <returns>The carrier-specific subscription client.</returns>
    /// <exception cref="ArgumentException">When carrier code is null or empty.</exception>
    /// <exception cref="NotSupportedException">When the carrier is not supported.</exception>
    public ICarrierSubscriptionClient CreateClient(string carrierCode)
    {
        if (string.IsNullOrWhiteSpace(carrierCode))
        {
            throw new ArgumentException("Carrier code cannot be null or empty.", nameof(carrierCode));
        }

        if (!_carrierClientTypes.TryGetValue(carrierCode, out var clientType))
        {
            _logger.LogWarning("Unsupported carrier requested: {CarrierCode}", carrierCode);
            throw new NotSupportedException($"Carrier '{carrierCode}' is not supported. Supported carriers: {string.Join(", ", _carrierClientTypes.Keys)}");
        }

        try
        {
            var client = _serviceProvider.GetService(clientType) as ICarrierSubscriptionClient;
            
            if (client == null)
            {
                throw new InvalidOperationException($"Failed to resolve carrier client for {carrierCode}. Ensure it is registered in DI container.");
            }

            _logger.LogDebug("Created carrier client for {CarrierCode}", carrierCode);
            return client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating carrier client for {CarrierCode}", carrierCode);
            throw;
        }
    }

    /// <summary>
    /// Checks if a carrier is supported by the factory.
    /// </summary>
    /// <param name="carrierCode">The carrier code to check.</param>
    /// <returns>True if the carrier is supported, false otherwise.</returns>
    public bool IsCarrierSupported(string carrierCode)
    {
        if (string.IsNullOrWhiteSpace(carrierCode))
        {
            return false;
        }

        return _carrierClientTypes.ContainsKey(carrierCode);
    }

    /// <summary>
    /// Gets the list of supported carrier codes.
    /// </summary>
    /// <returns>Collection of supported carrier codes.</returns>
    public IEnumerable<string> GetSupportedCarriers()
    {
        return _carrierClientTypes.Keys;
    }
}
