using Microsoft.Extensions.Options;
using Perfcho.Performance.Configuration;

namespace Perfcho.Performance.Services;

public sealed class CalculationConcurrencyLimiter
{
    private readonly SemaphoreSlim semaphore;
    private readonly TimeSpan queueTimeout;

    public CalculationConcurrencyLimiter(IOptions<CalculatorOptions> options)
    {
        semaphore = new SemaphoreSlim(options.Value.EffectiveMaximumConcurrentCalculations);
        queueTimeout = TimeSpan.FromMilliseconds(options.Value.CalculationQueueTimeoutMilliseconds);
    }

    public async Task<T> RunAsync<T>(Func<T> calculation)
    {
        if (!await semaphore.WaitAsync(queueTimeout).ConfigureAwait(false))
            throw new CalculatorException(StatusCodes.Status429TooManyRequests, "calculator_overloaded", "Calculator is at capacity.");

        try
        {
            return await Task.Run(calculation).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
