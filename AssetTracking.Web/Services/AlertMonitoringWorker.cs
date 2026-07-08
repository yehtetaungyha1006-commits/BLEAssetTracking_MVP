using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AssetTracking.Web.Services
{
    public class AlertMonitoringWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AlertMonitoringWorker> _logger;

        public AlertMonitoringWorker(IServiceProvider serviceProvider, ILogger<AlertMonitoringWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Alert Monitoring Background Worker started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var alertEngine = scope.ServiceProvider.GetRequiredService<AlertEngine>();
                        await alertEngine.ProcessOfflineAlertsAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred in Alert Monitoring Worker");
                }

                // Poll every 5 seconds
                await Task.Delay(5000, stoppingToken);
            }

            _logger.LogInformation("Alert Monitoring Background Worker stopped.");
        }
    }
}
