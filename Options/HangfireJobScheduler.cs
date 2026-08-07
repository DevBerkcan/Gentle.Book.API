using GentleBook.Api.Services;
using Hangfire;

namespace GentleBook.Api.Options
{
    // Add this class to your project
    public class HangfireJobScheduler : IHostedService
    {
        private readonly IRecurringJobManager _recurringJobManager;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<HangfireJobScheduler> _logger;

        public HangfireJobScheduler(
            IRecurringJobManager recurringJobManager,
            IServiceProvider serviceProvider,
            ILogger<HangfireJobScheduler> logger)
        {
            _recurringJobManager = recurringJobManager;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Hangfire job scheduler");

            // Schedule daily reminders at 8:00 AM UTC
            _recurringJobManager.AddOrUpdate<ReminderService>(
                "daily-reminders",
                service => service.SendDailyRemindersAsync(),
                Cron.Daily(8, 0));

            // Daily 01:00 UTC — set expired trials to Status=Expired + send expiration email
            _recurringJobManager.AddOrUpdate<SubscriptionService>(
                "process-expired-trials",
                s => s.ProcessExpiredTrialsAsync(),
                Cron.Daily(1, 0));

            // Daily 09:00 UTC — send warning emails at 7 and 3 days before expiry
            _recurringJobManager.AddOrUpdate<SubscriptionService>(
                "trial-warning-emails",
                s => s.SendTrialWarningEmailsAsync(),
                Cron.Daily(9, 0));

            // Hourly — mark bookings whose end time + grace period has passed as NoShow
            _recurringJobManager.AddOrUpdate<NoShowService>(
                "auto-no-show",
                s => s.MarkNoShowsAsync(),
                Cron.Hourly());

            // Hourly — Agency only: mark bookings whose end time + grace period has passed as
            // Completed, then award loyalty points for each.
            _recurringJobManager.AddOrUpdate<BookingCompletionService>(
                "auto-complete-bookings",
                s => s.AutoCompleteBookingsAsync(),
                Cron.Hourly());

            // Every 2 hours — Agency only: send "how was your appointment" emails for bookings
            // completed since the last run.
            _recurringJobManager.AddOrUpdate<ReviewRequestService>(
                "send-review-requests",
                s => s.SendReviewRequestsAsync(),
                "0 */2 * * *");

            // Daily 07:00 UTC — Agency only: team-report digest per TenantSettings.DigestFrequency
            // (Daily tenants every run, Weekly tenants only on Mondays).
            _recurringJobManager.AddOrUpdate<AdminDigestService>(
                "send-admin-digests",
                s => s.SendDigestsAsync(),
                Cron.Daily(7, 0));

            // Hourly — safety net for missed Mollie webhooks (re-polls Mollie directly)
            _recurringJobManager.AddOrUpdate<MollieReconciliationJob>(
                "mollie-reconciliation",
                j => j.ReconcileAsync(),
                Cron.Hourly());

            // Daily 02:00 UTC — finalize subscriptions whose CancelRequestedAt period has ended
            _recurringJobManager.AddOrUpdate<SubscriptionService>(
                "process-pending-cancellations",
                s => s.ProcessPendingCancellationsAsync(),
                Cron.Daily(2, 0));

            // Daily 10:00 UTC — dunning: warn on first PastDue, auto-cancel after grace period
            _recurringJobManager.AddOrUpdate<SubscriptionService>(
                "process-dunning",
                s => s.ProcessDunningAsync(),
                Cron.Daily(10, 0));

            // Daily 03:00 UTC — warn before and execute the 30-day operational-data purge.
            _recurringJobManager.AddOrUpdate<SubscriptionService>(
                "process-retention-expirations",
                s => s.ProcessRetentionExpirationsAsync(),
                Cron.Daily(3, 0));

            _logger.LogInformation("Hangfire jobs scheduled successfully");

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping Hangfire job scheduler");
            return Task.CompletedTask;
        }
    }
}
