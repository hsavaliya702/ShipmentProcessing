using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ShipmentTracking.Common.Extensions;
using ShipmentTracking.Common.Models.Carriers;
using ShipmentTracking.Common.Utilities;
using ShipmentTracking.WebhookProcessor.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShipmentTracking.WebhookProcessor.Validation;

/// <summary>
/// USPS-specific webhook validator implementing HMAC-SHA256 signature verification per API v3 specification.
/// Validates X-HMAC header using: HMAC-SHA256(timestamp + payload, secret) encoded in Base64.
/// </summary>
public class UspsWebhookValidator : IWebhookValidator
{
    private readonly SecretManager _secretManager;
    private readonly ILogger<UspsWebhookValidator> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _environment;
    private readonly int _allowedTimestampSkewSeconds;

    /// <inheritdoc/>
    public string CarrierCode => "USPS";

    /// <summary>
    /// Initializes a new instance of the <see cref="UspsWebhookValidator"/> class.
    /// </summary>
    public UspsWebhookValidator(
        SecretManager secretManager,
        ILogger<UspsWebhookValidator> logger,
        IConfiguration configuration)
    {
        _secretManager = secretManager ?? throw new ArgumentNullException(nameof(secretManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        
        _environment = _configuration["ENV"] ?? "dev";
        _allowedTimestampSkewSeconds = int.Parse(_configuration["ALLOWED_SKEW_SECONDS"] ?? "300");
    }

    /// <inheritdoc/>
    public async Task<ValidationResult> ValidateAsync(
        string payload,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Step 1: Extract HMAC signature from X-HMAC header
            if (!headers.TryGetValue("X-HMAC", out var providedHmac) || string.IsNullOrWhiteSpace(providedHmac))
            {
                _logger.LogWarning("USPS webhook missing X-HMAC header");
                return ValidationResult.Failure("Missing X-HMAC header", 401);
            }

            // Step 2: Parse webhook payload to get timestamp
            UspsWebhookNotification? webhookPayload;
            try
            {
                webhookPayload = JsonSerializer.Deserialize<UspsWebhookNotification>(
                    payload,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse USPS webhook payload");
                return ValidationResult.Failure("Invalid JSON payload", 400);
            }

            if (webhookPayload == null)
            {
                _logger.LogWarning("USPS webhook payload is null");
                return ValidationResult.Failure("Invalid webhook payload", 400);
            }

            // Step 3: Validate timestamp (replay attack protection)
            if (!ValidateTimestamp(webhookPayload.Timestamp))
            {
                _logger.LogWarning(
                    "USPS webhook timestamp outside allowed window: {Timestamp}",
                    webhookPayload.Timestamp);
                return ValidationResult.Failure("Timestamp outside allowed window", 401);
            }

            // Step 4: Retrieve webhook secret from Secrets Manager
            var secretName = $"{_environment}/carrier/usps/credentials";
            string webhookSecret;
            try
            {
                var credentialsJson = await _secretManager.GetSecretStringAsync(secretName, cancellationToken);
                var credentials = JsonSerializer.Deserialize<UspsCredentials>(
                    credentialsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (credentials == null || string.IsNullOrEmpty(credentials.WebhookSecret))
                {
                    _logger.LogError("USPS webhook secret not found in credentials");
                    return ValidationResult.Failure("Configuration error", 500);
                }

                webhookSecret = credentials.WebhookSecret;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve USPS webhook secret");
                return ValidationResult.Failure("Internal server error", 500);
            }

            // Step 5: Compute expected HMAC
            // Per USPS spec: HMAC-SHA256(timestamp + payload, secret) encoded in Base64
            var expectedHmac = ComputeUspsHmac(
                webhookPayload.Timestamp,
                webhookPayload.Payload,
                webhookSecret);

            // Step 6: Constant-time comparison to prevent timing attacks
            if (!ConstantTimeEquals(providedHmac, expectedHmac))
            {
                _logger.LogWarning(
                    "USPS webhook HMAC mismatch. SubscriptionId: {SubscriptionId}",
                    webhookPayload.SubscriptionId);
                return ValidationResult.Failure("HMAC signature mismatch", 401);
            }

            _logger.LogInformation(
                "USPS webhook validated successfully. SubscriptionId: {SubscriptionId}",
                webhookPayload.SubscriptionId);

            return ValidationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during USPS webhook validation");
            return ValidationResult.Failure("Internal server error", 500);
        }
    }

    /// <summary>
    /// Computes HMAC-SHA256 signature per USPS specification.
    /// Message = timestamp + payload (string concatenation).
    /// </summary>
    private string ComputeUspsHmac(string timestamp, string payload, string secret)
    {
        // USPS spec: Concatenate timestamp and payload, then compute HMAC-SHA256
        var message = timestamp + payload;
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var keyBytes = Encoding.UTF8.GetBytes(secret);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(messageBytes);

        // Encode in Base64 per USPS spec
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Validates that the timestamp is within the allowed skew window.
    /// </summary>
    private bool ValidateTimestamp(string timestamp)
    {
        if (!DateTime.TryParse(timestamp, out var webhookTime))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var difference = Math.Abs((now - webhookTime.ToUniversalTime()).TotalSeconds);

        return difference <= _allowedTimestampSkewSeconds;
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

    /// <summary>
    /// Constant-time string comparison to prevent timing attacks.
    /// </summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length)
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
