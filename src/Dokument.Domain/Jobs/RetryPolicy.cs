namespace Dokument.Domain.Jobs;

/// <summary>
/// When a failed job runs again (docs/design.md §10.1): exponential backoff from <see cref="FirstDelay"/>, capped at
/// <see cref="MaxDelay"/>, and no more tries after <see cref="MaxAttempts"/>.
/// </summary>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan FirstDelay, TimeSpan MaxDelay)
{
    public static readonly RetryPolicy Default = new(
        MaxAttempts: 5,
        FirstDelay: TimeSpan.FromSeconds(30),
        MaxDelay: TimeSpan.FromHours(1)
    );

    /// <summary>When to try again after attempt number <paramref name="attempts"/> failed, or null to give up.</summary>
    public DateTimeOffset? NextAttempt(int attempts, DateTimeOffset now)
    {
        if (attempts >= MaxAttempts)
        {
            return null;
        }

        var delay = FirstDelay * Math.Pow(2, Math.Max(0, attempts - 1));
        return now + (delay < MaxDelay ? delay : MaxDelay);
    }
}
