using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Extensions;
using ShipmentTracking.Common.Models.Carriers;
using ShipmentTracking.Common.Utilities;
using ShipmentTracking.Subscriber.Models;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShipmentTracking.Subscriber.Carriers;

/// <summary>
/// USPS-specific implementation of carrier subscription client.
/// Implements USPS Tracking API v3 webhook subscriptions with OAuth 2.0 authentication.
/// </summary>
public class UspsSubscriptionClient : ICarrierSubscriptionClient
{
    private readonly HttpClient _httpClient;
    private readonly SecretManager _secretManager;
    private readonly ILogger<UspsSubscriptionClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _uspsApiBaseUrl;
    private readonly string _uspsOAuthUrl;
    private readonly string _environment;
    
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
        IConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _secretManager = secretManager ?? throw new ArgumentNullException(nameof(secretManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        
        _environment = _configuration["ENV"] ?? "dev";
        
        // Determine base URL based on environment
        var isProd = _environment.Equals("prod", StringComparison.OrdinalIgnoreCase);
        _uspsApiBaseUrl = isProd ? "https://api.usps.com" : "https://api-cat.usps.com";
        _uspsOAuthUrl = _uspsApiBaseUrl;
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

            // Get credentials and webhook secret
            var secretName = $"{_environment}/carrier/usps/credentials";
            var credentials = await _secretManager.GetSecretJsonAsync<UspsCredentials>(secretName, cancellationToken);

            if (string.IsNullOrEmpty(credentials.ClientId) || string.IsNullOrEmpty(credentials.ClientSecret))
            {
                _logger.LogError("USPS credentials missing ClientId or ClientSecret");
                return new CarrierSubscriptionResponse
                {
                    Success = false,
                    ErrorMessage = "USPS credentials not configured",
                    ErrorType = ErrorType.Permanent
                };
            }

            if (string.IsNullOrEmpty(credentials.WebhookSecret) || credentials.WebhookSecret.Length != 32)
            {
                _logger.LogError("USPS webhook secret must be exactly 32 bytes");
                return new CarrierSubscriptionResponse
                {
                    Success = false,
                    ErrorMessage = "USPS webhook secret not properly configured",
                    ErrorType = ErrorType.Permanent
                };
            }

            // Get OAuth access token
            var accessToken = await GetAccessTokenAsync(credentials, cancellationToken);

            // Build subscription request per USPS API v3 spec
            var uspsRequest = new UspsSubscriptionRequest
            {
                ListenerUrl = request.CallbackUrl,
                Secret = credentials.WebhookSecret,
                FilterProperties = new FilterProperties
                {
                    TrackingNumber = request.TrackingNumber,
                    TrackingEventTypes = new List<string> { "ALL_UPDATES" }
                }
            };

            // Optional: Add admin notification email if configured
            var adminEmail = _configuration["USPS_ADMIN_EMAIL"];
            if (!string.IsNullOrEmpty(adminEmail))
            {
                uspsRequest.AdminNotification = new List<AdminNotification>
                {
                    new AdminNotification { Email = adminEmail }
                };
            }

            // Create HTTP request
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_uspsApiBaseUrl}/subscriptions/v3/subscriptions")
            {
                Content = JsonContent.Create(uspsRequest)
            };

            // Add OAuth Bearer token
            httpRequest.Headers.Add("Authorization", $"Bearer {accessToken}");

            // Send request
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var uspsResponse = JsonSerializer.Deserialize<UspsSubscriptionResponse>(
                    responseContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                // USPS may return DISABLED status initially if they can't verify the endpoint
                // This is normal - store subscription and it will become ACTIVE once verified
                var status = uspsResponse?.Status ?? "UNKNOWN";
                var isActive = status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
                var isDisabled = status.Equals("DISABLED", StringComparison.OrdinalIgnoreCase);
                
                if (isActive || isDisabled)
                {
                    _logger.LogInformationWithCorrelation(
                        $"Successfully created USPS subscription with status {status} for {request.TrackingNumber}",
                        correlationId,
                        new Dictionary<string, object>
                        {
                            ["SubscriptionId"] = uspsResponse?.SubscriptionId ?? "unknown",
                            ["Status"] = status,
                            ["StatusReason"] = uspsResponse?.StatusReason ?? "",
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
                    _logger.LogWarningWithCorrelation(
                        $"USPS subscription created but has unexpected status: {status}",
                        correlationId,
                        new Dictionary<string, object>
                        {
                            ["SubscriptionId"] = uspsResponse?.SubscriptionId ?? "unknown",
                            ["Status"] = status,
                            ["StatusReason"] = uspsResponse?.StatusReason ?? ""
                        });

                    // Still treat as success but log the unexpected status
                    return new CarrierSubscriptionResponse
                    {
                        Success = true,
                        SubscriptionId = uspsResponse?.SubscriptionId,
                        StatusCode = (int)response.StatusCode
                    };
                }
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
            var secretName = $"{_environment}/carrier/usps/credentials";
            var credentials = await _secretManager.GetSecretJsonAsync<UspsCredentials>(secretName, cancellationToken);

            // Get OAuth access token
            var accessToken = await GetAccessTokenAsync(credentials, cancellationToken);

            var httpRequest = new HttpRequestMessage(HttpMethod.Delete, $"{_uspsApiBaseUrl}/subscriptions/v3/subscriptions/{subscriptionId}");
            httpRequest.Headers.Add("Authorization", $"Bearer {accessToken}");

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

    /// <summary>
    /// Gets an OAuth access token for USPS API v3.
    /// Token is cached in Secrets Manager with expiration.
    /// </summary>
    private async Task<string> GetAccessTokenAsync(UspsCredentials credentials, CancellationToken cancellationToken)
    {
        // Check if we have a cached token in Secrets Manager
        var tokenCacheKey = $"{_environment}/carrier/usps/access-token";
        
        try
        {
            var cachedTokenJson = await _secretManager.GetSecretStringAsync(tokenCacheKey, cancellationToken);
            var cached = JsonSerializer.Deserialize<CachedAccessToken>(cachedTokenJson);
            
            if (cached != null && cached.ExpiresAt > DateTime.UtcNow.AddMinutes(5))
            {
                _logger.LogDebug("Using cached USPS access token");
                return cached.AccessToken;
            }
        }
        catch (SecretNotFoundException)
        {
            // Token not cached or expired, continue to request new one
            _logger.LogDebug("No cached USPS access token found, requesting new one");
        }

        // Request new OAuth token
        _logger.LogInformation("Requesting new USPS OAuth access token");
        
        var tokenRequest = new UspsOAuthTokenRequest
        {
            GrantType = "client_credentials",
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            Scope = "subscriptions"
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_uspsOAuthUrl}/oauth2/v3/token")
        {
            Content = JsonContent.Create(tokenRequest)
        };

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get USPS OAuth token: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new InvalidOperationException($"Failed to obtain USPS OAuth token: {response.StatusCode}");
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonSerializer.Deserialize<UspsOAuthTokenResponse>(
            responseContent,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
        {
            throw new InvalidOperationException("USPS OAuth token response is invalid");
        }

        // Cache the token with expiration (expires_in is in seconds)
        var expiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
        
        // Note: In production, you might want to store this in a more appropriate cache
        // For now, we'll just return the token without caching to Secrets Manager
        // since Secrets Manager is not ideal for frequent updates
        
        _logger.LogInformation("Successfully obtained USPS OAuth access token, expires at {ExpiresAt}", expiresAt);
        
        return tokenResponse.AccessToken;
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
/// Cached access token with expiration.
/// </summary>
internal class CachedAccessToken
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
