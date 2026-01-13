using System.Text.Json.Serialization;

namespace ShipmentTracking.Common.Models.Carriers;

/// <summary>
/// USPS API credentials stored in AWS Secrets Manager.
/// </summary>
public class UspsCredentials
{
    /// <summary>
    /// OAuth Client ID for USPS API.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth Client Secret for USPS API.
    /// </summary>
    [JsonPropertyName("clientSecret")]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// 32-byte webhook secret used by USPS to sign webhook callbacks with HMAC-SHA256.
    /// </summary>
    [JsonPropertyName("webhookSecret")]
    public string WebhookSecret { get; set; } = string.Empty;
}

/// <summary>
/// OAuth token request for USPS API.
/// </summary>
public class UspsOAuthTokenRequest
{
    /// <summary>
    /// OAuth grant type (should be "client_credentials").
    /// </summary>
    [JsonPropertyName("grant_type")]
    public string GrantType { get; set; } = "client_credentials";

    /// <summary>
    /// OAuth client ID.
    /// </summary>
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client secret.
    /// </summary>
    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// OAuth scope (should be "subscriptions").
    /// </summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "subscriptions";
}

/// <summary>
/// OAuth token response from USPS API.
/// </summary>
public class UspsOAuthTokenResponse
{
    /// <summary>
    /// Access token for API requests.
    /// </summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Token type (typically "Bearer").
    /// </summary>
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    /// <summary>
    /// Token expiration time in seconds.
    /// </summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    /// <summary>
    /// Token scope.
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

/// <summary>
/// USPS subscription request per API v3 specification.
/// </summary>
public class UspsSubscriptionRequest
{
    /// <summary>
    /// Webhook callback URL where USPS will send notifications.
    /// </summary>
    [JsonPropertyName("listenerURL")]
    public string ListenerUrl { get; set; } = string.Empty;

    /// <summary>
    /// 32-byte webhook secret used by USPS to sign webhook callbacks.
    /// </summary>
    [JsonPropertyName("secret")]
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Admin notification email addresses.
    /// </summary>
    [JsonPropertyName("adminNotification")]
    public List<AdminNotification>? AdminNotification { get; set; }

    /// <summary>
    /// Filter properties for the subscription.
    /// </summary>
    [JsonPropertyName("filterProperties")]
    public FilterProperties FilterProperties { get; set; } = new();
}

/// <summary>
/// Admin notification configuration.
/// </summary>
public class AdminNotification
{
    /// <summary>
    /// Email address for admin notifications.
    /// </summary>
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Filter properties for USPS subscription.
/// </summary>
public class FilterProperties
{
    /// <summary>
    /// Tracking number to subscribe to.
    /// </summary>
    [JsonPropertyName("trackingNumber")]
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>
    /// Types of tracking events to receive (e.g., ["ALL_UPDATES"]).
    /// </summary>
    [JsonPropertyName("trackingEventTypes")]
    public List<string> TrackingEventTypes { get; set; } = new() { "ALL_UPDATES" };
}

/// <summary>
/// USPS subscription response per API v3 specification.
/// </summary>
public class UspsSubscriptionResponse
{
    /// <summary>
    /// Unique subscription identifier.
    /// </summary>
    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    /// <summary>
    /// Webhook callback URL.
    /// </summary>
    [JsonPropertyName("listenerURL")]
    public string? ListenerUrl { get; set; }

    /// <summary>
    /// Webhook secret.
    /// </summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    /// <summary>
    /// Subscription status (ACTIVE, DISABLED, EXPIRED).
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Reason for current status.
    /// </summary>
    [JsonPropertyName("statusReason")]
    public string? StatusReason { get; set; }

    /// <summary>
    /// Timestamp when subscription was created.
    /// </summary>
    [JsonPropertyName("creationTimestamp")]
    public string? CreationTimestamp { get; set; }

    /// <summary>
    /// Timestamp when subscription will expire.
    /// </summary>
    [JsonPropertyName("expirationTimestamp")]
    public string? ExpirationTimestamp { get; set; }

    /// <summary>
    /// Filter properties for the subscription.
    /// </summary>
    [JsonPropertyName("filterProperties")]
    public FilterProperties? FilterProperties { get; set; }
}
