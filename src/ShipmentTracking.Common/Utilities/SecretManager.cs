using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ShipmentTracking.Common.Utilities;

/// <summary>
/// Wrapper for AWS Secrets Manager with caching support.
/// Provides secure retrieval of secrets with automatic caching and error handling.
/// </summary>
public class SecretManager : IDisposable
{
    private readonly IAmazonSecretsManager _secretsManager;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SecretManager> _logger;
    private readonly TimeSpan _cacheDuration;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretManager"/> class.
    /// </summary>
    /// <param name="secretsManager">AWS Secrets Manager client.</param>
    /// <param name="cache">Memory cache for storing retrieved secrets.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="cacheDuration">Duration to cache secrets (default: 5 minutes).</param>
    public SecretManager(
        IAmazonSecretsManager secretsManager,
        IMemoryCache cache,
        ILogger<SecretManager> logger,
        TimeSpan? cacheDuration = null)
    {
        _secretsManager = secretsManager ?? throw new ArgumentNullException(nameof(secretsManager));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheDuration = cacheDuration ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Retrieves a secret string from AWS Secrets Manager with caching.
    /// </summary>
    /// <param name="secretName">The name or ARN of the secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The secret string value.</returns>
    /// <exception cref="SecretNotFoundException">When the secret is not found.</exception>
    /// <exception cref="SecretRetrievalException">When an error occurs retrieving the secret.</exception>
    public async Task<string> GetSecretStringAsync(
        string secretName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            throw new ArgumentException("Secret name cannot be null or whitespace.", nameof(secretName));
        }

        var cacheKey = $"secret:{secretName}";

        // Try to get from cache
        if (_cache.TryGetValue(cacheKey, out string? cachedValue) && cachedValue != null)
        {
            _logger.LogDebug("Retrieved secret {SecretName} from cache", secretName);
            return cachedValue;
        }

        try
        {
            _logger.LogDebug("Retrieving secret {SecretName} from AWS Secrets Manager", secretName);

            var request = new GetSecretValueRequest
            {
                SecretId = secretName
            };

            var response = await _secretsManager.GetSecretValueAsync(request, cancellationToken);

            var secretValue = response.SecretString
                ?? throw new SecretRetrievalException($"Secret {secretName} does not contain a string value.");

            // Cache the secret
            _cache.Set(cacheKey, secretValue, _cacheDuration);

            _logger.LogInformation("Successfully retrieved and cached secret {SecretName}", secretName);

            return secretValue;
        }
        catch (ResourceNotFoundException ex)
        {
            _logger.LogError(ex, "Secret {SecretName} not found", secretName);
            throw new SecretNotFoundException($"Secret '{secretName}' was not found.", ex);
        }
        catch (InvalidRequestException ex)
        {
            _logger.LogError(ex, "Invalid request for secret {SecretName}", secretName);
            throw new SecretRetrievalException($"Invalid request for secret '{secretName}'.", ex);
        }
        catch (InvalidParameterException ex)
        {
            _logger.LogError(ex, "Invalid parameter for secret {SecretName}", secretName);
            throw new SecretRetrievalException($"Invalid parameter for secret '{secretName}'.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving secret {SecretName}", secretName);
            throw new SecretRetrievalException($"Error retrieving secret '{secretName}'.", ex);
        }
    }

    /// <summary>
    /// Retrieves a secret and deserializes it as JSON to a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the secret to.</typeparam>
    /// <param name="secretName">The name or ARN of the secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized secret object.</returns>
    /// <exception cref="SecretDeserializationException">When the secret cannot be deserialized.</exception>
    public async Task<T> GetSecretJsonAsync<T>(
        string secretName,
        CancellationToken cancellationToken = default) where T : class
    {
        var secretString = await GetSecretStringAsync(secretName, cancellationToken);

        try
        {
            var result = JsonSerializer.Deserialize<T>(secretString)
                ?? throw new SecretDeserializationException($"Failed to deserialize secret {secretName} to type {typeof(T).Name}.");

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize secret {SecretName} as JSON", secretName);
            throw new SecretDeserializationException($"Secret '{secretName}' is not valid JSON.", ex);
        }
    }

    /// <summary>
    /// Invalidates the cache for a specific secret.
    /// Useful when a secret has been rotated and needs to be refreshed.
    /// </summary>
    /// <param name="secretName">The name or ARN of the secret to invalidate.</param>
    public void InvalidateCache(string secretName)
    {
        var cacheKey = $"secret:{secretName}";
        _cache.Remove(cacheKey);
        _logger.LogInformation("Invalidated cache for secret {SecretName}", secretName);
    }

    /// <summary>
    /// Clears all cached secrets.
    /// </summary>
    public void ClearCache()
    {
        // Note: MemoryCache doesn't have a built-in clear all method
        // In production, consider using a custom cache key tracking mechanism
        _logger.LogWarning("Cache clear requested - consider implementing custom cache key tracking");
    }

    /// <summary>
    /// Disposes of resources used by the SecretManager.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _secretsManager?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Exception thrown when a secret is not found in AWS Secrets Manager.
/// </summary>
public class SecretNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SecretNotFoundException"/> class.
    /// </summary>
    public SecretNotFoundException() : base() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretNotFoundException"/> class with a message.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public SecretNotFoundException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretNotFoundException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SecretNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when an error occurs retrieving a secret.
/// </summary>
public class SecretRetrievalException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SecretRetrievalException"/> class.
    /// </summary>
    public SecretRetrievalException() : base() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretRetrievalException"/> class with a message.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public SecretRetrievalException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretRetrievalException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SecretRetrievalException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when a secret cannot be deserialized.
/// </summary>
public class SecretDeserializationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SecretDeserializationException"/> class.
    /// </summary>
    public SecretDeserializationException() : base() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretDeserializationException"/> class with a message.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public SecretDeserializationException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretDeserializationException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SecretDeserializationException(string message, Exception innerException) : base(message, innerException) { }
}
