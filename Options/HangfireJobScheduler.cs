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
