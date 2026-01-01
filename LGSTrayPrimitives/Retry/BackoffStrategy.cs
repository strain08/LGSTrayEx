namespace LGSTrayPrimitives.Retry;

/// <summary>
/// Represents a single attempt in a backoff retry sequence.
/// </summary>
/// <param name="AttemptNumber">The 1-based attempt number (1 = first attempt)</param>
/// <param name="Delay">The delay to wait before this attempt (0 for first attempt)</param>
/// <param name="Timeout">The timeout to use for this attempt's operation</param>
public record BackoffAttempt(int AttemptNumber, TimeSpan Delay, TimeSpan Timeout);

/// <summary>
/// Configurable exponential backoff strategy for retry operations.
/// Calculates progressive delays and timeouts for retry attempts.
/// </summary>
public class BackoffStrategy
{
    public required string ProfileName{ get; init; }
    /// <summary>
    /// Initial delay before the second attempt (first retry).
    /// First attempt has zero delay.
    /// </summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>
    /// Maximum delay cap. Delays will not exceed this value.
    /// </summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// Initial timeout for the first attempt.
    /// </summary>
    public TimeSpan InitialTimeout { get; }

    /// <summary>
    /// Maximum timeout cap. Timeouts will not exceed this value.
    /// </summary>
    public TimeSpan MaxTimeout { get; }

    /// <summary>
    /// Exponential multiplier for calculating backoff progression.
    /// Default is 2.0 (doubling).
    /// </summary>
    public double Multiplier { get; }

    /// <summary>
    /// Maximum number of attempts before giving up.
    /// </summary>
    public int MaxAttempts { get; }

    /// <summary>
    /// Creates a new backoff strategy with the specified parameters.
    /// </summary>
    /// <param name="initialDelay">Initial delay before second attempt</param>
    /// <param name="maxDelay">Maximum delay cap</param>
    /// <param name="initialTimeout">Initial timeout for first attempt</param>
    /// <param name="maxTimeout">Maximum timeout cap</param>
    /// <param name="multiplier">Exponential multiplier (default: 2.0)</param>
    /// <param name="maxAttempts">Maximum number of attempts</param>
    public BackoffStrategy(
        TimeSpan initialDelay,
        TimeSpan maxDelay,
        TimeSpan initialTimeout,
        TimeSpan maxTimeout,
        double multiplier = 2.0,
        int maxAttempts = 10)
    {
        if (initialDelay < TimeSpan.Zero)
            throw new ArgumentException("Initial delay cannot be negative", nameof(initialDelay));
        if (maxDelay < initialDelay)
            throw new ArgumentException("Max delay must be >= initial delay", nameof(maxDelay));
        if (initialTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Initial timeout must be positive", nameof(initialTimeout));
        if (maxTimeout < initialTimeout)
            throw new ArgumentException("Max timeout must be >= initial timeout", nameof(maxTimeout));
        if (multiplier <= 1.0)
            throw new ArgumentException("Multiplier must be > 1.0", nameof(multiplier));
        if (maxAttempts < 1)
            throw new ArgumentException("Max attempts must be >= 1", nameof(maxAttempts));

        InitialDelay = initialDelay;
        MaxDelay = maxDelay;
        InitialTimeout = initialTimeout;
        MaxTimeout = maxTimeout;
        Multiplier = multiplier;
        MaxAttempts = maxAttempts;
    }

    /// <summary>
    /// Calculates the delay before the specified attempt.
    /// </summary>
    /// <param name="attempt">The 1-based attempt number</param>
    /// <returns>The delay to wait before this attempt (0 for first attempt)</returns>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt < 1)
            throw new ArgumentException("Attempt must be >= 1", nameof(attempt));

        if (attempt == 1)
            return TimeSpan.Zero;

        // Calculate exponential delay: InitialDelay * Multiplier^(attempt-2)
        // attempt-2 because: attempt 1 has 0 delay, attempt 2 has InitialDelay, attempt 3 has InitialDelay*Multiplier, etc.
        double delayMs = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, attempt - 2);

        // Apply cap
        return TimeSpan.FromMilliseconds(Math.Min(delayMs, MaxDelay.TotalMilliseconds));
    }

    /// <summary>
    /// Calculates the timeout for the specified attempt.
    /// </summary>
    /// <param name="attempt">The 1-based attempt number</param>
    /// <returns>The timeout to use for this attempt's operation</returns>
    public TimeSpan GetTimeout(int attempt)
    {
        if (attempt < 1)
            throw new ArgumentException("Attempt must be >= 1", nameof(attempt));

        if (attempt == 1)
            return InitialTimeout;

        // Calculate exponential timeout: InitialTimeout * Multiplier^(attempt-1)
        double timeoutMs = InitialTimeout.TotalMilliseconds * Math.Pow(Multiplier, attempt - 1);

        // Apply cap
        return TimeSpan.FromMilliseconds(Math.Min(timeoutMs, MaxTimeout.TotalMilliseconds));
    }

    /// <summary>
    /// Provides an async iterator for retry attempts with backoff.
    /// Use this for manual control over retry logic.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop iteration</param>
    /// <returns>Async enumerable of backoff attempts</returns>
    /// <example>
    /// <code>
    /// await foreach (var attempt in backoff.GetAttemptsAsync(ct))
    /// {
    ///     if (attempt.AttemptNumber > 1)
    ///         await Task.Delay(attempt.Delay, ct);
    ///
    ///     var result = await OperationWithTimeout(attempt.Timeout);
    ///     if (result.Success) break;
    /// }
    /// </code>
    /// </example>
    public async IAsyncEnumerable<BackoffAttempt> GetAttemptsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var delay = GetDelay(attempt);
            var timeout = GetTimeout(attempt);

            yield return new BackoffAttempt(attempt, delay, timeout);
        }

        await Task.CompletedTask; // Suppress async warning
    }

    /// <summary>
    /// Executes an operation with automatic retry and exponential backoff.
    /// Retries until the operation returns a non-null result or max attempts is reached.
    /// </summary>
    /// <typeparam name="T">The return type of the operation</typeparam>
    /// <param name="operation">The operation to execute. Receives timeout and cancellation token. Should return null to indicate failure/retry.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the operation, or null if all attempts failed</returns>
    /// <example>
    /// <code>
    /// var result = await backoff.ExecuteAsync(async (timeout, ct) =>
    /// {
    ///     var response = await QueryDevice(timeout);
    ///     return response.IsValid ? response : null;
    /// }, cancellationToken);
    /// </code>
    /// </example>
    public async Task<T?> ExecuteAsync<T>(
        Func<TimeSpan, CancellationToken, Task<T?>> operation,
        CancellationToken cancellationToken = default)
    {
        await foreach (var attempt in GetAttemptsAsync(cancellationToken))
        {
            // Apply delay before retry attempts (not on first attempt)
            if (attempt.AttemptNumber > 1)
            {
                await Task.Delay(attempt.Delay, cancellationToken);
            }

            // Execute operation with progressive timeout
            var result = await operation(attempt.Timeout, cancellationToken);

            // Success (non-null result)
            if (result != null)
            {
                return result;
            }

            // Failure - continue to next attempt (unless this was the last one)
        }

        // All attempts exhausted
        return default;
    }
}
