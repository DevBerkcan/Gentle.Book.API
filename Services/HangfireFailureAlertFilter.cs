using Hangfire.States;
using Hangfire.Storage;

namespace GentleBook.Api.Services;

// Global Hangfire filter: fires when a job's state machine actually reaches Failed — which,
// thanks to AutomaticRetryAttribute intercepting the state election first, only happens once a
// job has exhausted all of its configured retry attempts (or immediately for jobs with no retry
// attribute). Without this, a failing recurring job (dunning, invoice retry, Mollie
// reconciliation) only ever logs via ILogger, which nobody actively watches in production since
// the Hangfire dashboard itself is dev-only (see Program.cs).
public class HangfireFailureAlertFilter : IApplyStateFilter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HangfireFailureAlertFilter> _logger;

    public HangfireFailureAlertFilter(IServiceScopeFactory scopeFactory, ILogger<HangfireFailureAlertFilter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.NewState is not FailedState failedState) return;

        var jobName = context.BackgroundJob.Job?.ToString() ?? context.BackgroundJob.Id;
        var exceptionMessage = failedState.Exception?.Message ?? "Unbekannter Fehler";

        // Fire-and-forget: alerting must never block or break Hangfire's own state machine.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var email = scope.ServiceProvider.GetRequiredService<EmailService>();
                await email.SendJobFailureAlertAsync(jobName, exceptionMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Hangfire job-failure alert email for job {JobName}", jobName);
            }
        });
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
