using System;
using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    [Function("CustomerEditProfile")]
    public async Task<IActionResult> EditProfile(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "customer/profile/edit")] HttpRequest req)
    {
        _logger.LogInformation("Customer edit profile request received.");

        if (!TryGetCustomerIdFromToken(req, out var customerId))
        {
            return new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Invalid or missing bearer token."
            });
        }

        string? phoneNumber = null;
        string? email = null;
        string? fullName = null;

        try
        {
            using var document = await JsonDocument.ParseAsync(req.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new BadRequestObjectResult(new
                {
                    success = false,
                    message = "Request body must be a JSON object."
                });
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("phoneNumber"))
                {
                    phoneNumber = ReadJsonStringValue(property.Value);
                    continue;
                }

                if (property.NameEquals("email"))
                {
                    email = ReadJsonStringValue(property.Value);
                    continue;
                }

                if (property.NameEquals("fullName"))
                {
                    fullName = ReadJsonStringValue(property.Value);
                    continue;
                }

                return new BadRequestObjectResult(new
                {
                    success = false,
                    message = "Only phoneNumber, email, and fullName are allowed for profile edit."
                });
            }
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "Invalid JSON payload."
            });
        }

        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "phoneNumber is required."
            });
        }

        try
        {
            phoneNumber = NormalizePhoneNumber(phoneNumber);
        }
        catch (InvalidOperationException)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "phoneNumber is invalid."
            });
        }

        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        fullName = string.IsNullOrWhiteSpace(fullName) ? null : fullName.Trim();

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

            await using var command = new SqlCommand("dbo.CustomerProfile_Update", connection)
            {
                CommandType = CommandType.StoredProcedure
            };

            command.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.BigInt) { Value = customerId });
            command.Parameters.Add(new SqlParameter("@PhoneNumber", SqlDbType.NVarChar, 255) { Value = (object?)phoneNumber ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@Email", SqlDbType.NVarChar, 255) { Value = (object?)email ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@FullName", SqlDbType.NVarChar, 255) { Value = (object?)fullName ?? DBNull.Value });

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                _logger.LogError("dbo.CustomerProfile_Update did not return a result for CustomerId {CustomerId}.", customerId);
                return new ObjectResult(new
                {
                    success = false,
                    message = "Failed to update customer profile."
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }

            var isSuccess = !reader.IsDBNull(reader.GetOrdinal("Success")) && Convert.ToBoolean(reader["Success"]);
            var resultMessage = reader.IsDBNull(reader.GetOrdinal("Message"))
                ? (isSuccess ? "Customer profile updated successfully." : "Failed to update customer profile.")
                : reader.GetString(reader.GetOrdinal("Message"));

            if (isSuccess)
            {
                return new OkObjectResult(new
                {
                    success = true,
                    message = resultMessage
                });
            }

            if (string.Equals(resultMessage, "Customer not found.", StringComparison.OrdinalIgnoreCase))
            {
                return new NotFoundObjectResult(new
                {
                    success = false,
                    message = resultMessage
                });
            }

            if (string.Equals(resultMessage, "Phone number is already registered.", StringComparison.OrdinalIgnoreCase))
            {
                return new ConflictObjectResult(new
                {
                    success = false,
                    message = resultMessage
                });
            }

            return new BadRequestObjectResult(new
            {
                success = false,
                message = resultMessage
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit customer profile for CustomerId {CustomerId}.", customerId);
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to update customer profile."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    [Function("CustomerCurrentApplication")]
    public async Task<IActionResult> GetCurrentApplication(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "customer/application/current")] HttpRequest req)
    {
        _logger.LogInformation("Customer current application request received.");

        if (!TryGetCustomerAndApplicationIdFromToken(req, out var customerId, out var applicationId))
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

            await using var command = new SqlCommand("dbo.CustomerApplication_GetCurrentDetails", connection)
            {
                CommandType = CommandType.StoredProcedure
            };
            command.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.BigInt) { Value = customerId });
            command.Parameters.Add(new SqlParameter("@ApplicationId", SqlDbType.Int) { Value = applicationId });

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new NotFoundObjectResult(new
                {
                    success = false,
                    message = "Customer application details not found."
                });
            }

            var details = new
            {
                customerApplicationId = reader.GetInt64(reader.GetOrdinal("CustomerApplicationId")),
                customerId = reader.GetInt64(reader.GetOrdinal("CustomerId")),
                applicationId = reader.GetInt32(reader.GetOrdinal("ApplicationId")),
                accessStatus = reader.GetString(reader.GetOrdinal("AccessStatus")),
                accessType = reader.GetString(reader.GetOrdinal("AccessType")),
                subscriptionReference = reader.IsDBNull(reader.GetOrdinal("SubscriptionReference"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("SubscriptionReference")),
                validFromUtc = reader.IsDBNull(reader.GetOrdinal("ValidFromUtc"))
                    ? (DateTime?)null
                    : reader.GetDateTime(reader.GetOrdinal("ValidFromUtc")),
                validToUtc = reader.IsDBNull(reader.GetOrdinal("ValidToUtc"))
                    ? (DateTime?)null
                    : reader.GetDateTime(reader.GetOrdinal("ValidToUtc")),
                grantedAtUtc = reader.GetDateTime(reader.GetOrdinal("GrantedAtUtc")),
                createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))
            };

            return new OkObjectResult(new
            {
                success = true,
                message = "Customer application details fetched successfully.",
                data = details
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch customer application details for CustomerId {CustomerId}, ApplicationId {ApplicationId}.", customerId, applicationId);
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to fetch customer application details."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private static bool TryGetCustomerIdFromToken(HttpRequest req, out long customerId)
    {
        customerId = 0;

        if (!TryGetJwtToken(req, out var jwt))
        {
            return false;
        }

        var customerIdClaim = jwt.Claims.FirstOrDefault(c =>
            string.Equals(c.Type, "customerId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Type, JwtRegisteredClaimNames.Sub, StringComparison.OrdinalIgnoreCase));

        return customerIdClaim is not null && long.TryParse(customerIdClaim.Value, out customerId);
    }

    private static bool TryGetCustomerAndApplicationIdFromToken(HttpRequest req, out long customerId, out int applicationId)
    {
        customerId = 0;
        applicationId = 0;

        if (!TryGetJwtToken(req, out var jwt))
        {
            return false;
        }

        var customerIdClaim = jwt.Claims.FirstOrDefault(c =>
            string.Equals(c.Type, "customerId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Type, JwtRegisteredClaimNames.Sub, StringComparison.OrdinalIgnoreCase));
        var applicationIdClaim = jwt.Claims.FirstOrDefault(c =>
            string.Equals(c.Type, "applicationId", StringComparison.OrdinalIgnoreCase));

        return customerIdClaim is not null &&
               long.TryParse(customerIdClaim.Value, out customerId) &&
               applicationIdClaim is not null &&
               int.TryParse(applicationIdClaim.Value, out applicationId) &&
               applicationId > 0;
    }

    private static bool TryGetJwtToken(HttpRequest req, out JwtSecurityToken jwt)
    {
        jwt = null!;

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

        try
        {
            jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadJsonStringValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString()
        };
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var trimmed = phoneNumber.Trim();
        var normalizedBuilder = new StringBuilder(trimmed.Length);

        for (var i = 0; i < trimmed.Length; i++)
        {
            var character = trimmed[i];
            if (char.IsDigit(character))
            {
                normalizedBuilder.Append(character);
            }
        }

        var normalized = normalizedBuilder.ToString();
        if (normalized.StartsWith("00", StringComparison.Ordinal) && normalized.Length > 2)
        {
            normalized = normalized[2..];
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Phone number is required.");
        }

        return normalized;
    }
}
