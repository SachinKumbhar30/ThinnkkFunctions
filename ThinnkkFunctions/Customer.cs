using System;
using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ThinnkkFunctions;

public class Customer
{
    private readonly ILogger<Customer> _logger;

    public Customer(ILogger<Customer> logger)
    {
        _logger = logger;
    }

    [Function("CustomerProfile")]
    public async Task<IActionResult> GetProfile(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "customer/profile")] HttpRequest req)
    {
        _logger.LogInformation("Customer profile request received.");

        if (!TryGetCustomerIdFromToken(req, out var customerId))
        {
            return new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Invalid or missing bearer token."
            });
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

            await using var command = new SqlCommand("dbo.CustomerProfile_GetById", connection)
            {
                CommandType = CommandType.StoredProcedure
            };
            command.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.BigInt) { Value = customerId });

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new NotFoundObjectResult(new
                {
                    success = false,
                    message = "Customer not found."
                });
            }

            var profile = new
            {
                customerId = reader.GetInt64(reader.GetOrdinal("CustomerId")),
                phoneNumber = reader.IsDBNull(reader.GetOrdinal("PhoneNumber")) ? null : reader.GetString(reader.GetOrdinal("PhoneNumber")),
                isActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
                createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                updatedAtUtc = reader.IsDBNull(reader.GetOrdinal("UpdatedAtUtc")) ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")),
                customerGuid = reader.GetGuid(reader.GetOrdinal("CustomerGuid")),
                fullName = reader.IsDBNull(reader.GetOrdinal("FullName")) ? null : reader.GetString(reader.GetOrdinal("FullName")),
                email = reader.IsDBNull(reader.GetOrdinal("Email")) ? null : reader.GetString(reader.GetOrdinal("Email")),
                isPhoneVerified = reader.GetBoolean(reader.GetOrdinal("IsPhoneVerified")),
                isEmailVerified = reader.GetBoolean(reader.GetOrdinal("IsEmailVerified")),
                profileImageUrl = reader.IsDBNull(reader.GetOrdinal("ProfileImageUrl")) ? null : reader.GetString(reader.GetOrdinal("ProfileImageUrl")),
                lastLoginAtUtc = reader.IsDBNull(reader.GetOrdinal("LastLoginAtUtc")) ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("LastLoginAtUtc")),
                deactivatedAtUtc = reader.IsDBNull(reader.GetOrdinal("DeactivatedAtUtc")) ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("DeactivatedAtUtc"))
            };

            return new OkObjectResult(new
            {
                success = true,
                message = "Customer profile fetched successfully.",
                data = profile
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch customer profile for CustomerId {CustomerId}.", customerId);
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to fetch customer profile."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private static bool TryGetCustomerIdFromToken(HttpRequest req, out long customerId)
    {
        customerId = 0;

        if (!req.Headers.TryGetValue("Authorization", out var authorizationHeader))
        {
            return false;
        }

        var headerValue = authorizationHeader.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(headerValue) ||
            !headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = headerValue["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        JwtSecurityToken jwt;
        try
        {
            jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        }
        catch
        {
            return false;
        }

        var customerIdClaim = jwt.Claims.FirstOrDefault(c =>
            string.Equals(c.Type, "customerId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Type, JwtRegisteredClaimNames.Sub, StringComparison.OrdinalIgnoreCase));

        return customerIdClaim is not null && long.TryParse(customerIdClaim.Value, out customerId);
    }
}
