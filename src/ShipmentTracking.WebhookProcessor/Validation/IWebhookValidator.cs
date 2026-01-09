namespace ShipmentTracking.WebhookProcessor.Validation;

/// <summary>
/// Interface for validating webhook authenticity.
/// Each carrier implements signature validation according to their specifications.
/// </summary>
public interface IWebhookValidator
{
    /// <summary>
    /// Gets the carrier code this validator handles.
    /// </summary>
    string CarrierCode { get; }

    /// <summary>
    /// Validates webhook signature and timestamp.
    /// </summary>
    /// <param name="payload">Raw webhook payload body.</param>
    /// <param name="headers">Request headers containing signature and timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result indicating if webhook is authentic.</returns>
    Task<ValidationResult> ValidateAsync(
        string payload,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of webhook validation.
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// Indicates if the webhook is valid.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Error message if validation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// HTTP status code to return if invalid.
    /// </summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static ValidationResult Success() => new() { IsValid = true };

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static ValidationResult Failure(string errorMessage, int statusCode = 401) =>
        new() { IsValid = false, ErrorMessage = errorMessage, StatusCode = statusCode };
}
