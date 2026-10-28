using System;
using System.Collections.Generic;
using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ThinnkkFunctions;

public class Company
{
    private readonly ILogger<Company> _logger;

    public Company(ILogger<Company> logger)
    {
        _logger = logger;
    }

    [Function("CompanyCreate")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "company/create")] HttpRequest req)
    {
        _logger.LogInformation("Company create request received.");

        if (!TryGetCustomerAndApplicationIdFromToken(req, out var customerId, out var applicationId))
        {
            return new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Invalid or missing bearer token."
            });
        }

        CompanyRequestModel? request;
        try
        {
            request = await ReadCompanyRequestAsync(req, requireCompanyId: false);
        }
        catch (InvalidOperationException ex)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = ex.Message
            });
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "Invalid JSON payload."
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

            await using var command = new SqlCommand("dbo.Company_Create", connection)
            {
                CommandType = CommandType.StoredProcedure
            };

            command.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.BigInt) { Value = customerId });
            command.Parameters.Add(new SqlParameter("@ApplicationId", SqlDbType.Int) { Value = applicationId });
            command.Parameters.Add(new SqlParameter("@CompanyName", SqlDbType.NVarChar, 200) { Value = request.CompanyName! });
            command.Parameters.Add(new SqlParameter("@CompanyCode", SqlDbType.NVarChar, 50) { Value = (object?)request.CompanyCode ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@GSTIN", SqlDbType.NVarChar, 15) { Value = (object?)request.Gstin ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@PAN", SqlDbType.NVarChar, 10) { Value = (object?)request.Pan ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@AddressLine1", SqlDbType.NVarChar, 200) { Value = (object?)request.AddressLine1 ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@AddressLine2", SqlDbType.NVarChar, 200) { Value = (object?)request.AddressLine2 ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@City", SqlDbType.NVarChar, 100) { Value = (object?)request.City ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@State", SqlDbType.NVarChar, 100) { Value = (object?)request.State ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@Country", SqlDbType.NVarChar, 100) { Value = (object?)request.Country ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@PinCode", SqlDbType.NVarChar, 10) { Value = (object?)request.PinCode ?? DBNull.Value });

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new ObjectResult(new
                {
                    success = false,
                    message = "Failed to create company."
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }

            var company = MapCompany(reader);
            return new OkObjectResult(new
            {
                success = true,
                message = "Company created successfully.",
                data = company
            });
        }
        catch (SqlException ex) when (ex.Number is 50001 or 50002)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create company for CustomerId {CustomerId}, ApplicationId {ApplicationId}.", customerId, applicationId);
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to create company."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    [Function("CompanyUpdate")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "company/update")] HttpRequest req)
    {
        _logger.LogInformation("Company update request received.");

        if (!TryGetCustomerAndApplicationIdFromToken(req, out var customerId, out var applicationId))
        {
            return new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Invalid or missing bearer token."
            });
        }

        CompanyRequestModel? request;
        try
        {
            request = await ReadCompanyRequestAsync(req, requireCompanyId: true);
        }
        catch (InvalidOperationException ex)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = ex.Message
            });
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "Invalid JSON payload."
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

            await using var command = new SqlCommand("dbo.Company_Update", connection)
            {
                CommandType = CommandType.StoredProcedure
            };

            command.Parameters.Add(new SqlParameter("@CompanyId", SqlDbType.BigInt) { Value = request.CompanyId!.Value });
            command.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.BigInt) { Value = customerId });
            command.Parameters.Add(new SqlParameter("@ApplicationId", SqlDbType.Int) { Value = applicationId });
            command.Parameters.Add(new SqlParameter("@CompanyName", SqlDbType.NVarChar, 200) { Value = request.CompanyName! });
            command.Parameters.Add(new SqlParameter("@CompanyCode", SqlDbType.NVarChar, 50) { Value = (object?)request.CompanyCode ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@GSTIN", SqlDbType.NVarChar, 15) { Value = (object?)request.Gstin ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@PAN", SqlDbType.NVarChar, 10) { Value = (object?)request.Pan ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@AddressLine1", SqlDbType.NVarChar, 200) { Value = (object?)request.AddressLine1 ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@AddressLine2", SqlDbType.NVarChar, 200) { Value = (object?)request.AddressLine2 ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@City", SqlDbType.NVarChar, 100) { Value = (object?)request.City ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@State", SqlDbType.NVarChar, 100) { Value = (object?)request.State ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@Country", SqlDbType.NVarChar, 100) { Value = (object?)request.Country ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@PinCode", SqlDbType.NVarChar, 10) { Value = (object?)request.PinCode ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@IsActive", SqlDbType.Bit) { Value = request.IsActive ?? true });

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new NotFoundObjectResult(new
                {
                    success = false,
                    message = "Company not found for this customer and application."
                });
            }

            var company = MapCompany(reader);
            return new OkObjectResult(new
            {
                success = true,
                message = "Company updated successfully.",
                data = company
            });
        }
        catch (SqlException ex) when (ex.Number is 50003 or 50004)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = ex.Message
            });
        }
        catch (SqlException ex) when (ex.Number == 50005)
        {
            return new NotFoundObjectResult(new
            {
                success = false,
                message = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update company {CompanyId} for CustomerId {CustomerId}, ApplicationId {ApplicationId}.", request.CompanyId, customerId, applicationId);
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to update company."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    [Function("CompanyList")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "company/list")] HttpRequest req)
    {
        _logger.LogInformation("Company list request received.");

        if (!TryGetCustomerAndApplicationIdFromToken(req, out var customerId, out var applicationId))
        {
            return new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Invalid or missing bearer token."
            });
        }

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

            await using var command = new SqlCommand("dbo.Company_List", connection)
            {
                CommandType = CommandType.StoredProcedure
            };

            command.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.BigInt) { Value = customerId });
            command.Parameters.Add(new SqlParameter("@ApplicationId", SqlDbType.Int) { Value = applicationId });
            command.Parameters.Add(new SqlParameter("@IncludeInactive", SqlDbType.Bit) { Value = includeInactive });

            var companies = new List<object>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                companies.Add(MapCompany(reader));
            }

            return new OkObjectResult(new
            {
                success = true,
                message = "Companies fetched successfully.",
                data = companies
            });
        }
        catch (SqlException ex) when (ex.Number == 50006)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list companies for CustomerId {CustomerId}, ApplicationId {ApplicationId}.", customerId, applicationId);
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to fetch companies."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private static async Task<CompanyRequestModel> ReadCompanyRequestAsync(HttpRequest req, bool requireCompanyId)
    {
        using var document = await JsonDocument.ParseAsync(req.Body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Request body must be a JSON object.");
        }

        var request = new CompanyRequestModel();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("companyId"))
            {
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    request.CompanyId = null;
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var companyId))
                {
                    request.CompanyId = companyId;
                    continue;
                }

                throw new InvalidOperationException("companyId must be a valid number.");
            }

            if (property.NameEquals("companyName"))
            {
                request.CompanyName = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("companyCode"))
            {
                request.CompanyCode = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("gstin"))
            {
                request.Gstin = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("pan"))
            {
                request.Pan = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("addressLine1"))
            {
                request.AddressLine1 = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("addressLine2"))
            {
                request.AddressLine2 = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("city"))
            {
                request.City = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("state"))
            {
                request.State = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("country"))
            {
                request.Country = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("pinCode"))
            {
                request.PinCode = NormalizeNullableString(property.Value);
                continue;
            }

            if (property.NameEquals("isActive"))
            {
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    request.IsActive = null;
                    continue;
                }

                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    request.IsActive = property.Value.GetBoolean();
                    continue;
                }

                throw new InvalidOperationException("isActive must be true or false.");
            }

            throw new InvalidOperationException("Invalid field in request body.");
        }

        if (requireCompanyId && (!request.CompanyId.HasValue || request.CompanyId.Value <= 0))
        {
            throw new InvalidOperationException("companyId is required and must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(request.CompanyName))
        {
            throw new InvalidOperationException("companyName is required.");
        }

        request.CompanyName = request.CompanyName.Trim();
        return request;
    }

    private static object MapCompany(SqlDataReader reader)
    {
        return new
        {
            companyId = reader.GetInt64(reader.GetOrdinal("CompanyId")),
            customerId = reader.GetInt64(reader.GetOrdinal("CustomerId")),
            applicationId = reader.GetInt32(reader.GetOrdinal("ApplicationId")),
            companyName = reader.GetString(reader.GetOrdinal("CompanyName")),
            companyCode = ReadNullableString(reader, "CompanyCode"),
            gstin = ReadNullableString(reader, "GSTIN"),
            pan = ReadNullableString(reader, "PAN"),
            addressLine1 = ReadNullableString(reader, "AddressLine1"),
            addressLine2 = ReadNullableString(reader, "AddressLine2"),
            city = ReadNullableString(reader, "City"),
            state = ReadNullableString(reader, "State"),
            country = ReadNullableString(reader, "Country"),
            pinCode = ReadNullableString(reader, "PinCode"),
            isActive = ReadNullableBoolean(reader, "IsActive") ?? true,
            createdAtUtc = ReadNullableDateTime(reader, "CreatedAtUtc"),
            updatedAtUtc = ReadNullableDateTime(reader, "UpdatedAtUtc")
        };
    }

    private static string? ReadNullableString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool? ReadNullableBoolean(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? (bool?)null : reader.GetBoolean(ordinal);
    }

    private static DateTime? ReadNullableDateTime(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? (DateTime?)null : reader.GetDateTime(ordinal);
    }

    private static string? NormalizeNullableString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => string.IsNullOrWhiteSpace(value.ToString()) ? null : value.ToString().Trim()
        };
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

    private sealed class CompanyRequestModel
    {
        public long? CompanyId { get; set; }

        public string? CompanyName { get; set; }

        public string? CompanyCode { get; set; }

        public string? Gstin { get; set; }

        public string? Pan { get; set; }

        public string? AddressLine1 { get; set; }

        public string? AddressLine2 { get; set; }

        public string? City { get; set; }

        public string? State { get; set; }

        public string? Country { get; set; }

        public string? PinCode { get; set; }

        public bool? IsActive { get; set; }
    }
}
