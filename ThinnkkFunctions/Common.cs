using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ThinnkkFunctions;

public class Common
{
    private readonly ILogger<Common> _logger;

    public Common(ILogger<Common> logger)
    {
        _logger = logger;
    }

    [Function("HealthCheck")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "com/health-check")] HttpRequest req)
    {
        _logger.LogInformation("Health-check request received.");

        var connectionString = Environment.GetEnvironmentVariable("SQLCONNSTR_SqlConnectionString");
        var dbStatus = "NotConfigured";
        var dbMessage = "SqlConnectionString is not set.";

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                dbStatus = connection.State == ConnectionState.Open ? "Healthy" : "Unhealthy";
                dbMessage = connection.State == ConnectionState.Open
                    ? "Database connection successful."
                    : "Database connection did not open.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database health-check failed.");
                dbStatus = "Unhealthy";
                dbMessage = "Database connection failed.";
            }
        }

        var overallStatus = dbStatus == "Healthy" ? "Healthy" : "Unhealthy";

        return new JsonResult(new
        {
            success = overallStatus == "Healthy" ? true : false,
            status = overallStatus,
            timestamp = DateTime.UtcNow,
            message = "Health check completed.",
            database = new
            {
                status = dbStatus,
                message = dbMessage
            }
        });
    }
}