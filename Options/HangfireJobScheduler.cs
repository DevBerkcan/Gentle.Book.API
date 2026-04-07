using GentleBook.Api.Services;
using Hangfire;

namespace Skinbloom.Api.Options
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
