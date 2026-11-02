using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ThinnkkFunctions;

public class Country
{
    private readonly ILogger<Country> _logger;

    public Country(ILogger<Country> logger)
    {
        _logger = logger;
    }

    [Function("CountryList")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "country/list")] HttpRequest req)
    {
        _logger.LogInformation("Country list request received.");

        var includeInactive = false;
        if (req.Query.TryGetValue("includeInactive", out var includeInactiveValue) &&
            !string.IsNullOrWhiteSpace(includeInactiveValue.FirstOrDefault()))
        {
            if (!bool.TryParse(includeInactiveValue.FirstOrDefault(), out includeInactive))
            {
                return new BadRequestObjectResult(new
                {
                    success = false,
                    message = "includeInactive must be true or false."
                });
            }
        }

        int? countryId = null;
        if (req.Query.TryGetValue("countryId", out var countryIdValue) &&
            !string.IsNullOrWhiteSpace(countryIdValue.FirstOrDefault()))
        {
            if (!int.TryParse(countryIdValue.FirstOrDefault(), out var parsedCountryId))
            {
                return new BadRequestObjectResult(new
                {
                    success = false,
                    message = "countryId must be a valid number."
                });
            }

            countryId = parsedCountryId;
        }

        var connectionString = Environment.GetEnvironmentVariable("SQLCONNSTR_SqlConnectionString");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            return new ObjectResult(new
            {
                success = false,
                message = "Database connection is not configured."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new SqlCommand("dbo.Countries_List", connection)
            {
                CommandType = CommandType.StoredProcedure
            };

            command.Parameters.Add(new SqlParameter("@IncludeInactive", SqlDbType.Bit) { Value = includeInactive });
            command.Parameters.Add(new SqlParameter("@CountryId", SqlDbType.Int) { Value = (object?)countryId ?? DBNull.Value });

            var countries = new List<object>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                countries.Add(new
                {
                    countryId = reader.GetInt32(reader.GetOrdinal("CountryId")),
                    countryName = reader.GetString(reader.GetOrdinal("CountryName")),
                    countryCode = reader.IsDBNull(reader.GetOrdinal("CountryCode")) ? null : reader.GetString(reader.GetOrdinal("CountryCode")),
                    countryCode3 = reader.IsDBNull(reader.GetOrdinal("CountryCode3")) ? null : reader.GetString(reader.GetOrdinal("CountryCode3")),
                    phoneCode = reader.IsDBNull(reader.GetOrdinal("PhoneCode")) ? null : reader.GetString(reader.GetOrdinal("PhoneCode")),
                    isActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
                    createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                    updatedAtUtc = reader.IsDBNull(reader.GetOrdinal("UpdatedAtUtc")) ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc"))
                });
            }

            return new OkObjectResult(new
            {
                success = true,
                message = "Countries fetched successfully.",
                data = countries
            });
        }
        catch (SqlException ex) when (ex.Number == 50030)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list countries.");
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to fetch countries."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }
}
