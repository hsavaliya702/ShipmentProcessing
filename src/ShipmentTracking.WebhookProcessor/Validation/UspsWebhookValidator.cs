using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Extensions;
using ShipmentTracking.Common.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace ShipmentTracking.WebhookProcessor.Validation;

/// <summary>
/// USPS-specific webhook validator implementing HMAC-SHA256 signature verification.
/// </summary>
public class UspsWebhookValidator : IWebhookValidator
{
    private readonly SecretManager _secretManager;
    private readonly ILogger<UspsWebhookValidator> _logger;
    private readonly int _allowedTimestampSkewSeconds;

    /// <inheritdoc/>
    public string CarrierCode => "USPS";

    // Default timestamp skew: 5 minutes (configurable)
    private const int DefaultTimestampSkewSeconds = 300;

    /// <summary>
    /// Initializes a new instance of the <see cref="UspsWebhookValidator"/> class.
    /// </summary>
    public UspsWebhookValidator(
        SecretManager secretManager,
        ILogger<UspsWebhookValidator> logger,
        int? allowedTimestampSkewSeconds = null)
    {
        _secretManager = secretManager ?? throw new ArgumentNullException(nameof(secretManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _allowedTimestampSkewSeconds = allowedTimestampSkewSeconds ?? DefaultTimestampSkewSeconds;
    }

    /// <inheritdoc/>
    public async Task<ValidationResult> ValidateAsync(
        string payload,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Step 1: Extract signature and timestamp from headers
            if (!headers.TryGetValue("X-USPS-Signature", out var signature) || string.IsNullOrWhiteSpace(signature))
            {
                _logger.LogWarning("Missing X-USPS-Signature header");
                return ValidationResult.Failure("Missing signature header", 401);
            }

            if (!headers.TryGetValue("X-USPS-Timestamp", out var timestampStr) || string.IsNullOrWhiteSpace(timestampStr))
            {
                _logger.LogWarning("Missing X-USPS-Timestamp header");
                return ValidationResult.Failure("Missing timestamp header", 401);
            }

            // Step 2: Validate timestamp freshness
            if (!long.TryParse(timestampStr, out var timestamp))
            {
                _logger.LogWarning("Invalid timestamp format: {Timestamp}", timestampStr);
                return ValidationResult.Failure("Invalid timestamp format", 400);
            }

            var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var now = DateTimeOffset.UtcNow;
            var timeDifference = Math.Abs((now - requestTime).TotalSeconds);

            if (timeDifference > _allowedTimestampSkewSeconds)
            {
                _logger.LogWarning(
                    "Timestamp outside allowed window. Request: {RequestTime}, Now: {Now}, Difference: {Difference}s",
                    requestTime,
                    now,
                    timeDifference);
                
                return ValidationResult.Failure("Timestamp outside allowed window", 401);
            }

            // Step 3: Retrieve webhook signing secret
            string webhookSecret;
            try
            {
                webhookSecret = await _secretManager.GetSecretStringAsync("carrier/usps/webhook-secret", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve USPS webhook secret");
                return ValidationResult.Failure("Internal server error", 500);
            }

            // Step 4: Compute expected signature
            var expectedSignature = ComputeSignature(payload, webhookSecret, timestampStr);

            // Step 5: Constant-time comparison to prevent timing attacks
            if (!ConstantTimeEquals(signature, expectedSignature))
            {
                _logger.LogWarning("Signature mismatch for USPS webhook");
                return ValidationResult.Failure("Invalid signature", 401);
            }

            _logger.LogDebug("USPS webhook signature validated successfully");
            return ValidationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during webhook validation");
            return ValidationResult.Failure("Internal server error", 500);
        }
    }

    private static string ComputeSignature(string payload, string secret, string timestamp)
    {
        var message = $"{timestamp}.{payload}";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(messageBytes);
        return Convert.ToBase64String(hashBytes);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var result = 0;
        for (var i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }

        return result == 0;
    }
}
