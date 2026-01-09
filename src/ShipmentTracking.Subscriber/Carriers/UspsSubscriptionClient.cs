using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Extensions;
using ShipmentTracking.Common.Utilities;
using ShipmentTracking.Subscriber.Models;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShipmentTracking.Subscriber.Carriers;

/// <summary>
/// USPS-specific implementation of carrier subscription client.
/// Handles USPS Tracking API webhook subscriptions with HMAC signature authentication.
/// </summary>
public class UspsSubscriptionClient : ICarrierSubscriptionClient
{
    private readonly HttpClient _httpClient;
    private readonly SecretManager _secretManager;
    private readonly ILogger<UspsSubscriptionClient> _logger;
    private readonly string _uspsApiBaseUrl;
    
    // USPS tracking number format: 20-22 digits or specific patterns
    private static readonly Regex UspsTrackingPattern = new(
        @"^(94|93|92|94|95|82|71|61|91|42|70|14|03|04|12|10|20|21|22|23|30|31|32|34|35|36|37|38|39|40|41|43|44|45|46|47|48|49|50|51|52|53|54|55|56|57|58|59|60|62|63|64|65|66|67|68|69|72|73|74|75|76|77|78|79|80|81|83|84|85|86|87|88|89|90)\d{18,20}$|^[A-Z]{2}\d{9}US$",
        RegexOptions.Compiled);

    /// <inheritdoc/>
    public string CarrierCode => "USPS";

    /// <summary>
    /// Initializes a new instance of the <see cref="UspsSubscriptionClient"/> class.
    /// </summary>
    public UspsSubscriptionClient(
        HttpClient httpClient,
        SecretManager secretManager,
        ILogger<UspsSubscriptionClient> logger,
        string uspsApiBaseUrl)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _secretManager = secretManager ?? throw new ArgumentNullException(nameof(secretManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _uspsApiBaseUrl = uspsApiBaseUrl ?? throw new ArgumentNullException(nameof(uspsApiBaseUrl));
    }

    /// <inheritdoc/>
    public async Task<CarrierSubscriptionResponse> SubscribeAsync(
        CarrierSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.TrackingNumber))
        {
            throw new ArgumentException("Tracking number is required.", nameof(request));
        }

        if (!IsValidTrackingNumber(request.TrackingNumber))
        {
            return new CarrierSubscriptionResponse
            {
                Success = false,
                ErrorMessage = $"Invalid USPS tracking number format: {request.TrackingNumber}",
                ErrorType = ErrorType.Permanent,
                StatusCode = 400
            };
        }

        var correlationId = $"{request.CorrelationData.FulfillmentOrderId}_{request.CorrelationData.PackageId}";

        try
        {
            _logger.LogOperationStart("UspsSubscribe", correlationId, new Dictionary<string, object>
            {
                ["TrackingNumber"] = request.TrackingNumber,
                ["CallbackUrl"] = request.CallbackUrl
            });

            // Retrieve USPS API credentials from Secrets Manager
            var credentials = await GetUspsCredentialsAsync(cancellationToken);

            // Build subscription request
            var uspsRequest = new UspsSubscriptionRequest
            {
                TrackingNumber = request.TrackingNumber,
                WebhookUrl = request.CallbackUrl,
                ClientId = credentials.ClientId
            };

            // Create HTTP request with authentication
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_uspsApiBaseUrl}/webhooks/tracking/v1/subscriptions")
            {
                Content = JsonContent.Create(uspsRequest)
            };

            // Add authentication headers
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = GenerateSignature(JsonSerializer.Serialize(uspsRequest), credentials.ClientSecret, timestamp);
            
            httpRequest.Headers.Add("Authorization", $"Bearer {credentials.ApiKey}");
            httpRequest.Headers.Add("X-Client-Id", credentials.ClientId);
            httpRequest.Headers.Add("X-Timestamp", timestamp);
            httpRequest.Headers.Add("X-Signature", signature);

            // Send request
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var uspsResponse = JsonSerializer.Deserialize<UspsSubscriptionResponse>(responseContent);
                
                _logger.LogInformationWithCorrelation(
                    $"Successfully subscribed to USPS tracking for {request.TrackingNumber}",
                    correlationId,
                    new Dictionary<string, object>
                    {
                        ["SubscriptionId"] = uspsResponse?.SubscriptionId ?? "unknown",
                        ["StatusCode"] = (int)response.StatusCode
                    });

                return new CarrierSubscriptionResponse
                {
                    Success = true,
                    SubscriptionId = uspsResponse?.SubscriptionId,
                    StatusCode = (int)response.StatusCode
                };
            }
            else
            {
                var errorType = ClassifyHttpError(response.StatusCode);
                
                _logger.LogWarningWithCorrelation(
                    $"USPS subscription failed: {response.StatusCode}",
                    correlationId,
                    new Dictionary<string, object>
                    {
                        ["StatusCode"] = (int)response.StatusCode,
                        ["ErrorType"] = errorType.ToString(),
                        ["Response"] = responseContent
                    });

                return new CarrierSubscriptionResponse
                {
                    Success = false,
                    ErrorMessage = $"USPS API error: {response.StatusCode} - {responseContent}",
                    ErrorType = errorType,
                    StatusCode = (int)response.StatusCode
                };
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogErrorWithCorrelation(ex, "HTTP error during USPS subscription", correlationId);
            
            return new CarrierSubscriptionResponse
            {
                Success = false,
                ErrorMessage = $"HTTP error: {ex.Message}",
                ErrorType = ErrorType.Transient
            };
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogErrorWithCorrelation(ex, "Request timeout during USPS subscription", correlationId);
            
            return new CarrierSubscriptionResponse
            {
                Success = false,
                ErrorMessage = "Request timeout",
                ErrorType = ErrorType.Transient
            };
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCorrelation(ex, "Unexpected error during USPS subscription", correlationId);
            
            return new CarrierSubscriptionResponse
            {
                Success = false,
                ErrorMessage = $"Unexpected error: {ex.Message}",
                ErrorType = ErrorType.Unknown
            };
        }
    }

    /// <inheritdoc/>
    public async Task<bool> UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("Subscription ID is required.", nameof(subscriptionId));
        }

        try
        {
            var credentials = await GetUspsCredentialsAsync(cancellationToken);

            var httpRequest = new HttpRequestMessage(HttpMethod.Delete, $"{_uspsApiBaseUrl}/webhooks/tracking/v1/subscriptions/{subscriptionId}");
            httpRequest.Headers.Add("Authorization", $"Bearer {credentials.ApiKey}");
            httpRequest.Headers.Add("X-Client-Id", credentials.ClientId);

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully unsubscribed from USPS tracking: {SubscriptionId}", subscriptionId);
                return true;
            }
            
            _logger.LogWarning("Failed to unsubscribe from USPS tracking: {SubscriptionId}, Status: {StatusCode}", 
                subscriptionId, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsubscribing from USPS tracking: {SubscriptionId}", subscriptionId);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool IsValidTrackingNumber(string trackingNumber)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber))
        {
            return false;
        }

        return UspsTrackingPattern.IsMatch(trackingNumber.Trim());
    }

    private async Task<UspsCredentials> GetUspsCredentialsAsync(CancellationToken cancellationToken)
    {
        return await _secretManager.GetSecretJsonAsync<UspsCredentials>(
            "carrier/usps/credentials",
            cancellationToken);
    }

    private static string GenerateSignature(string payload, string secret, string timestamp)
    {
        var message = $"{timestamp}.{payload}";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(messageBytes);
        return Convert.ToBase64String(hashBytes);
    }

    private static ErrorType ClassifyHttpError(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => ErrorType.Permanent,
            HttpStatusCode.Unauthorized => ErrorType.Permanent,
            HttpStatusCode.Forbidden => ErrorType.Permanent,
            HttpStatusCode.NotFound => ErrorType.Permanent,
            HttpStatusCode.Conflict => ErrorType.Permanent,
            HttpStatusCode.UnprocessableEntity => ErrorType.Permanent,
            HttpStatusCode.TooManyRequests => ErrorType.RateLimit,
            HttpStatusCode.InternalServerError => ErrorType.Transient,
            HttpStatusCode.BadGateway => ErrorType.Transient,
            HttpStatusCode.ServiceUnavailable => ErrorType.Transient,
            HttpStatusCode.GatewayTimeout => ErrorType.Transient,
            _ => ErrorType.Unknown
        };
    }
}

/// <summary>
/// USPS API credentials model.
/// </summary>
internal class UspsCredentials
{
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientSecret")]
    public string ClientSecret { get; set; } = string.Empty;
}

/// <summary>
/// USPS subscription request payload.
/// </summary>
internal class UspsSubscriptionRequest
{
    [JsonPropertyName("trackingNumber")]
    public string TrackingNumber { get; set; } = string.Empty;

    [JsonPropertyName("webhookUrl")]
    public string WebhookUrl { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>
/// USPS subscription response payload.
/// </summary>
internal class UspsSubscriptionResponse
{
    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}
