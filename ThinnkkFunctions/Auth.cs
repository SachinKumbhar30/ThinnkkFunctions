using System;
using System.Collections.Generic;
using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ThinnkkFunctions;

public class Auth
{
    private static readonly HttpClient HttpClient = new();
    private static readonly object JwtSigningKeyLock = new();
    private static string? _cachedJwtSigningKey;
    private readonly ILogger<Auth> _logger;

    public Auth(ILogger<Auth> logger)
    {
        _logger = logger;
    }

    [Function("AuthRequestOtp")]
    public Task<IActionResult> RequestOtp(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "auth/request-otp")] HttpRequest req)
    {
        return HandleOtpRequestAsync(req, "OTP sent successfully.");
    }

    [Function("AuthResendOtp")]
    public Task<IActionResult> ResendOtp(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "auth/resend-otp")] HttpRequest req)
    {
        return HandleResendOtpAsync(req);
    }

    private async Task<IActionResult> HandleResendOtpAsync(HttpRequest req)
    {
        var (requestId, countryCode, mobileNo, channel) = await ReadResendOtpRequestAsync(req);

        if (requestId is null || requestId <= 0)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "requestId is required."
            });
        }

        var existingRequest = await GetOtpRequestAsync(requestId.Value);
        if (existingRequest is null)
        {
            return new NotFoundObjectResult(new
            {
                success = false,
                message = "OTP request not found."
            });
        }

        var resolvedChannel = string.IsNullOrWhiteSpace(channel) ? existingRequest.Channel : channel;
        if (!IsSupportedChannel(resolvedChannel))
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "channel must be one of sms, whatsapp, or voice."
            });
        }

        var otp = GetOtpCode();
        var otpSalt = RandomNumberGenerator.GetBytes(32);
        var otpCodeHash = SHA256.HashData(Combine(otpSalt, otp));
        var normalizedCountryCode = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim().TrimStart('+');
        var mobile = NormalizePhoneNumber(existingRequest.Target);

        if (!IsDemoOtpEnabled())
        {
            var authKey = Environment.GetEnvironmentVariable("APITXT_AUTHKEY");
            if (string.IsNullOrWhiteSpace(authKey))
            {
                _logger.LogError("APITXT_AUTHKEY environment variable is not configured.");
                return new ObjectResult(new
                {
                    success = false,
                    message = "APITXT_AUTHKEY environment variable is not configured."
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }

            if (!await SendOtpAsync(authKey, otp, mobile, normalizedCountryCode, resolvedChannel))
            {
                return new ObjectResult(new
                {
                    success = false,
                    message = "Failed to send OTP via provider."
                })
                {
                    StatusCode = StatusCodes.Status502BadGateway
                };
            }
        }
        else
        {
            _logger.LogInformation("Demo OTP mode enabled. Skipping APITXT resend for {Mobile} via {Channel}.", mobile, resolvedChannel);
        }

        try
        {
            var (resendId, resendCount) = await StoreResendRequestAsync(
                requestId: requestId.Value,
                channel: resolvedChannel,
                requestedBy: "system",
                result: 1,
                attemptCount: 1,
                requestIp: GetRequestIp(req),
                userAgent: GetUserAgent(req),
                reason: "OTP resent via API",
                newExpiresAt: DateTime.UtcNow.AddMinutes(5),
                otpCodeHash: otpCodeHash,
                otpSalt: otpSalt);

            _logger.LogInformation("OTP resent successfully to {Mobile} via {Channel}. RequestId: {RequestId}. ResendId: {ResendId}.", mobile, resolvedChannel, requestId.Value, resendId);

            return new OkObjectResult(new
            {
                success = true,
                message = "OTP resent successfully.",
                channel = resolvedChannel,
                requestId = requestId.Value,
                resendId,
                resendCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OTP was resent to {Mobile} via {Channel}, but storing the resend request failed.", mobile, resolvedChannel);
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to store OTP resend request."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private async Task<IActionResult> HandleOtpRequestAsync(HttpRequest req, string successMessage)
    {
        if (!TryReadApplicationId(req, out var applicationId))
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "X-Application-Id header is required and must be a positive integer."
            });
        }

        var application = await GetActiveApplicationAsync(applicationId);
        if (application is null)
        {
            return new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Invalid or inactive application."
            });
        }

        var (countryCode, mobileNo, channel) = await ReadOtpRequestAsync(req);
        var normalizedCountryCode = NormalizeCountryCode(countryCode);

        if (string.IsNullOrWhiteSpace(normalizedCountryCode) || string.IsNullOrWhiteSpace(mobileNo))
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "countryCode and mobileNo are required."
            });
        }

        if (!IsSupportedChannel(channel))
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "channel must be one of sms, whatsapp, or voice."
            });
        }

        var otp = GetOtpCode();
        var otpSalt = RandomNumberGenerator.GetBytes(32);
        var otpCodeHash = SHA256.HashData(Combine(otpSalt, otp));
        var mobile = NormalizePhoneNumber($"{normalizedCountryCode}{mobileNo}");

        if (!IsDemoOtpEnabled())
        {
            var authKey = Environment.GetEnvironmentVariable("APITXT_AUTHKEY");
            if (string.IsNullOrWhiteSpace(authKey))
            {
                _logger.LogError("APITXT_AUTHKEY environment variable is not configured.");
                return new ObjectResult(new
                {
                    success = false,
                    message = "APITXT_AUTHKEY environment variable is not configured."
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }

            if (!await SendOtpAsync(authKey, otp, mobile, normalizedCountryCode, channel))
            {
                return new ObjectResult(new
                {
                    success = false,
                    message = "Failed to send OTP via provider."
                })
                {
                    StatusCode = StatusCodes.Status502BadGateway
                };
            }
        }
        else
        {
            _logger.LogInformation("Demo OTP mode enabled. Skipping APITXT send for {Mobile} via {Channel}.", mobile, channel);
        }

        try
        {
            var (requestId, correlationId) = await StoreOtpRequestAsync(
                targetType: "phone",
                target: mobile,
                otpCodeHash: otpCodeHash,
                otpSalt: otpSalt,
                otpLength: 6,
                channel: channel,
                expiresAt: DateTime.UtcNow.AddMinutes(5),
                requestIp: GetRequestIp(req),
                userAgent: GetUserAgent(req),
                notes: null,
                applicationId: application.ApplicationId);

            _logger.LogInformation("OTP sent successfully to {Mobile} via {Channel}. RequestId: {RequestId}. ApplicationId: {ApplicationId}.", mobile, channel, requestId, application.ApplicationId);

            return new OkObjectResult(new
            {
                success = true,
                message = successMessage,
                channel,
                requestId,
                correlationId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OTP was sent to {Mobile} via {Channel}, but storing the request failed.", mobile, channel);
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to store OTP request."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    [Function("AuthVerifyOtp")]
    public async Task<IActionResult> VerifyOtp(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "auth/verify-otp")] HttpRequest req)
    {
        var (requestId, otp) = await ReadVerifyOtpRequestAsync(req);

        if (requestId is null || requestId <= 0)
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "requestId is required."
            });
        }

        if (string.IsNullOrWhiteSpace(otp))
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "otp is required."
            });
        }

        var existingRequest = await GetOtpRequestDetailsAsync(requestId.Value);
        if (existingRequest is null)
        {
            return new NotFoundObjectResult(new
            {
                success = false,
                message = "OTP request not found."
            });
        }

        if (!existingRequest.ApplicationIsActive)
        {
            return new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Application is inactive."
            });
        }

        var nowUtc = DateTime.UtcNow;
        var isTerminalStatus = existingRequest.CurrentStatus is 2 or 3 or 4;
        var isExpired = nowUtc > existingRequest.ExpiresAt || existingRequest.CurrentStatus == 3;
        byte[]? attemptedHash = null;
        var isHashMatch = false;

        if (!isTerminalStatus && !isExpired)
        {
            attemptedHash = SHA256.HashData(Combine(existingRequest.OtpSalt, otp.Trim()));
            isHashMatch = CryptographicOperations.FixedTimeEquals(attemptedHash, existingRequest.OtpCodeHash);
        }

        var isSuccessful = !isTerminalStatus && !isExpired && isHashMatch;
        var failureReason = isSuccessful
            ? null
            : existingRequest.CurrentStatus == 2
                ? "OTP already verified"
                : existingRequest.CurrentStatus == 4
                    ? "OTP request revoked"
                    : isExpired
                        ? "OTP expired"
                        : "Invalid OTP";

        try
        {
            var requestIp = GetRequestIp(req);
            var userAgent = GetUserAgent(req);

            var (verificationId, attemptNumber, currentStatus, accepted) = await StoreVerifyRequestAsync(
                requestId: requestId.Value,
                attemptedHash: attemptedHash,
                successful: isSuccessful,
                requestIp: requestIp,
                userAgent: userAgent,
                failureReason: failureReason);

            var isVerified = accepted && currentStatus == 2;

            AuthTokenResult? authToken = null;
            if (isVerified)
            {
                authToken = await GenerateAuthTokensAsync(
                    requestId.Value,
                    existingRequest.Target,
                    existingRequest.ApplicationId,
                    existingRequest.AppCode,
                    requestIp,
                    userAgent);
            }

            return new OkObjectResult(new
            {
                success = isVerified ? true : false,
                verified = isVerified,
                verificationId,
                attemptNumber,
                currentStatus,
                accepted,
                accessToken = authToken?.AccessToken,
                tokenType = authToken?.TokenType,
                expiresIn = authToken?.ExpiresIn,
                refreshToken = authToken?.RefreshToken
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store OTP verification attempt for RequestId {RequestId}.", requestId.Value);
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to verify OTP request."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    [Function("AuthRefreshToken")]
    public async Task<IActionResult> RefreshToken(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "auth/refresh-token")] HttpRequest req)
    {
        var refreshToken = await ReadRefreshTokenRequestAsync(req);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                message = "refreshToken is required."
            });
        }

        var requestIp = GetRequestIp(req);
        var userAgent = GetUserAgent(req);
        var oldRefreshTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken.Trim()));
        var newAccessTokenJti = Guid.NewGuid();
        var newRefreshToken = GenerateRefreshToken();
        var newRefreshTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(newRefreshToken));
        var newRefreshTokenExpiresAt = DateTime.UtcNow.AddDays(GetRefreshTokenLifetimeDays());

        try
        {
            var rotationResult = await RotateRefreshTokenAsync(
                oldRefreshTokenHash: oldRefreshTokenHash,
                newAccessTokenJti: newAccessTokenJti,
                newRefreshTokenHash: newRefreshTokenHash,
                newRefreshTokenExpiresAt: newRefreshTokenExpiresAt,
                requestIp: requestIp,
                userAgent: userAgent);

            if (rotationResult.ResultCode != 1 || rotationResult.CustomerId is null || rotationResult.ApplicationId is null)
            {
                return rotationResult.ResultCode switch
                {
                    0 => new UnauthorizedObjectResult(new
                    {
                        success = false,
                        message = "Invalid refresh token."
                    }),
                    3 => new UnauthorizedObjectResult(new
                    {
                        success = false,
                        message = "Refresh token expired or revoked."
                    }),
                    4 => new UnauthorizedObjectResult(new
                    {
                        success = false,
                        message = "Refresh token reuse detected. Please sign in again."
                    }),
                    _ => new UnauthorizedObjectResult(new
                    {
                        success = false,
                        message = "Refresh token rotation failed."
                    })
                };
            }

            var accessToken = await GenerateAccessTokenForRefreshAsync(
                rotationResult.CustomerId.Value,
                rotationResult.ApplicationId.Value,
                newAccessTokenJti);

            return new OkObjectResult(new
            {
                success = true,
                refreshed = true,
                accessToken = accessToken.Token,
                tokenType = "Bearer",
                expiresIn = accessToken.ExpiresIn,
                refreshToken = newRefreshToken
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rotate refresh token.");
            return new ObjectResult(new
            {
                success = false,
                message = "Failed to refresh token."
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private async Task<(long? RequestId, string? Otp)> ReadVerifyOtpRequestAsync(HttpRequest req)
    {
        long? requestId = null;
        string? otp = req.Query["otp"];

        if (long.TryParse(req.Query["requestId"], out var queryRequestId))
        {
            requestId = queryRequestId;
        }

        if ((requestId is null || string.IsNullOrWhiteSpace(otp)) && req.ContentLength > 0)
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();

            if (!string.IsNullOrWhiteSpace(body))
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;

                if (requestId is null && root.TryGetProperty("requestId", out var requestIdElement))
                {
                    if (requestIdElement.ValueKind == JsonValueKind.Number && requestIdElement.TryGetInt64(out var bodyRequestId))
                    {
                        requestId = bodyRequestId;
                    }
                    else if (requestIdElement.ValueKind == JsonValueKind.String && long.TryParse(requestIdElement.GetString(), out var stringRequestId))
                    {
                        requestId = stringRequestId;
                    }
                }

                if (string.IsNullOrWhiteSpace(otp) && root.TryGetProperty("otp", out var otpElement))
                {
                    otp = ReadJsonStringValue(otpElement);
                }
                else if (string.IsNullOrWhiteSpace(otp) && root.TryGetProperty("otpCode", out var otpCodeElement))
                {
                    otp = ReadJsonStringValue(otpCodeElement);
                }
            }
        }

        return (requestId, otp);
    }

    private async Task<string?> ReadRefreshTokenRequestAsync(HttpRequest req)
    {
        string? refreshToken = req.Query["refreshToken"];

        if (string.IsNullOrWhiteSpace(refreshToken) && req.ContentLength > 0)
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();

            if (!string.IsNullOrWhiteSpace(body))
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;

                if (root.TryGetProperty("refreshToken", out var refreshTokenElement))
                {
                    refreshToken = refreshTokenElement.GetString();
                }
                else if (root.TryGetProperty("token", out var tokenElement))
                {
                    refreshToken = tokenElement.GetString();
                }
            }
        }

        return refreshToken;
    }

    private sealed record OtpRequestDetails(
        long RequestId,
        string Target,
        byte[] OtpCodeHash,
        byte[] OtpSalt,
        DateTime ExpiresAt,
        byte CurrentStatus,
        int ApplicationId,
        string AppCode,
        bool ApplicationIsActive);

    private sealed record RefreshRotationResult(
        byte ResultCode,
        long? RefreshSessionId,
        long? CustomerId,
        int? ApplicationId,
        Guid? TokenFamilyId);

    private sealed record ApplicationInfo(int ApplicationId);

    private async Task<OtpRequestDetails?> GetOtpRequestDetailsAsync(long requestId)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            throw new InvalidOperationException("SqlConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("dbo.AuthVerifyOtp_GetRequestDetails", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.BigInt) { Value = requestId });

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new OtpRequestDetails(
            reader.GetInt64(0),
            reader.GetString(1),
            (byte[])reader[2],
            (byte[])reader[3],
            reader.GetDateTime(4),
            reader.GetByte(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetBoolean(8));
    }

    private async Task<(long? VerificationId, int? AttemptNumber, byte CurrentStatus, bool Accepted)> StoreVerifyRequestAsync(
        long requestId,
        byte[]? attemptedHash,
        bool successful,
        string? requestIp,
        string? userAgent,
        string? failureReason)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            throw new InvalidOperationException("SqlConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("dbo.AuthVerifyOtp_StoreRequest", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.BigInt) { Value = requestId });
        command.Parameters.Add(new SqlParameter("@AttemptedHash", SqlDbType.VarBinary, 32) { Value = (object?)attemptedHash ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Successful", SqlDbType.Bit) { Value = successful });
        command.Parameters.Add(new SqlParameter("@RequestIp", SqlDbType.NVarChar, 45) { Value = (object?)requestIp ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@UserAgent", SqlDbType.NVarChar, 512) { Value = (object?)userAgent ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@FailureReason", SqlDbType.NVarChar, 250) { Value = (object?)failureReason ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("dbo.AuthVerifyOtp_StoreRequest did not return a result row.");
        }

        var verificationId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
        var attemptNumber = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        var currentStatus = reader.GetByte(3);
        var accepted = reader.GetBoolean(4);

        return (verificationId, attemptNumber, currentStatus, accepted);
    }

    private async Task<RefreshRotationResult> RotateRefreshTokenAsync(
        byte[] oldRefreshTokenHash,
        Guid newAccessTokenJti,
        byte[] newRefreshTokenHash,
        DateTime newRefreshTokenExpiresAt,
        string? requestIp,
        string? userAgent)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            throw new InvalidOperationException("SqlConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("dbo.AuthRefreshToken_Rotate", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.Add(new SqlParameter("@OldRefreshTokenHash", SqlDbType.VarBinary, 32) { Value = oldRefreshTokenHash });
        command.Parameters.Add(new SqlParameter("@NewAccessTokenJti", SqlDbType.UniqueIdentifier) { Value = newAccessTokenJti });
        command.Parameters.Add(new SqlParameter("@NewRefreshTokenHash", SqlDbType.VarBinary, 32) { Value = newRefreshTokenHash });
        command.Parameters.Add(new SqlParameter("@NewRefreshTokenExpiresAt", SqlDbType.DateTime2) { Value = newRefreshTokenExpiresAt });
        command.Parameters.Add(new SqlParameter("@RequestIp", SqlDbType.NVarChar, 45) { Value = (object?)requestIp ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@UserAgent", SqlDbType.NVarChar, 512) { Value = (object?)userAgent ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("dbo.AuthRefreshToken_Rotate did not return a result row.");
        }

        var resultCode = reader.GetByte(0);
        var refreshSessionId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        var customerId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
        var applicationId = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
        var tokenFamilyId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);

        return new RefreshRotationResult(resultCode, refreshSessionId, customerId, applicationId, tokenFamilyId);
    }

    private sealed record AccessTokenResult(string Token, int ExpiresIn, Guid JwtId, DateTime ExpiresAtUtc);
    private sealed record AuthTokenResult(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken);

    private async Task<AuthTokenResult> GenerateAuthTokensAsync(
        long requestId,
        string target,
        int applicationId,
        string appCode,
        string? requestIp,
        string? userAgent)
    {
        var normalizedTarget = NormalizePhoneNumber(target);
        var customerId = await GetOrCreateCustomerIdAsync(normalizedTarget);
        var tokenFamilyId = Guid.NewGuid();
        var accessToken = await GenerateAccessTokenAsync(requestId, normalizedTarget, customerId, applicationId, appCode);
        var refreshToken = GenerateRefreshToken();
        var refreshTokenExpiresAt = DateTime.UtcNow.AddDays(GetRefreshTokenLifetimeDays());
        var refreshTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));

        var storedApplicationId = await StoreRefreshTokenSessionAsync(
            requestId: requestId,
            target: normalizedTarget,
            customerId: customerId,
            tokenFamilyId: tokenFamilyId,
            accessTokenJti: accessToken.JwtId,
            refreshTokenHash: refreshTokenHash,
            refreshTokenExpiresAt: refreshTokenExpiresAt,
            requestIp: requestIp,
            userAgent: userAgent);

        if (storedApplicationId != applicationId)
        {
            throw new InvalidOperationException("Stored session application does not match OTP request application.");
        }

        return new AuthTokenResult(
            accessToken.Token,
            "Bearer",
            accessToken.ExpiresIn,
            refreshToken);
    }

    private async Task<long> GetOrCreateCustomerIdAsync(string normalizedPhoneNumber)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            throw new InvalidOperationException("SqlConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("dbo.AuthCustomer_GetOrCreate", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.Add(new SqlParameter("@PhoneNumber", SqlDbType.NVarChar, 255) { Value = normalizedPhoneNumber });
        command.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.BigInt)
        {
            Direction = ParameterDirection.Output
        });

        await command.ExecuteNonQueryAsync();

        if (command.Parameters["@CustomerId"].Value is DBNull or null)
        {
            throw new InvalidOperationException("dbo.AuthCustomer_GetOrCreate did not return CustomerId.");
        }

        return Convert.ToInt64(command.Parameters["@CustomerId"].Value);
    }

    private async Task<AccessTokenResult> GenerateAccessTokenAsync(
        long requestId,
        string target,
        long customerId,
        int applicationId,
        string appCode)
    {
        var algorithm = ResolveJwtAlgorithm(GetRequiredEnvironmentVariable("JwtAlgorithm"));
        var issuer = GetRequiredEnvironmentVariable("JwtIssuer");
        var audience = GetRequiredEnvironmentVariable("JwtAudience");
        var signingKeySecret = GetJwtSigningKey();
        var signingKey = new SymmetricSecurityKey(GetSigningKeyBytes(signingKeySecret));
        var credentials = new SigningCredentials(signingKey, algorithm);
        var nowUtc = DateTime.UtcNow;
        var expiresAtUtc = nowUtc.AddMinutes(GetAccessTokenExpirationMinutes());
        var jwtId = Guid.NewGuid();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, customerId.ToString()),
            new(JwtRegisteredClaimNames.Jti, jwtId.ToString()),
            new(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(nowUtc).ToString(), ClaimValueTypes.Integer64),
            new("requestId", requestId.ToString()),
            new("customerId", customerId.ToString()),
            new("applicationId", applicationId.ToString()),
            new("appCode", appCode),
            new("phone", target)
        };

        var tokenDescriptor = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: nowUtc,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        var token = new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);

        return new AccessTokenResult(
            token,
            Convert.ToInt32((expiresAtUtc - nowUtc).TotalSeconds),
            jwtId,
            expiresAtUtc);
    }

    private Task<AccessTokenResult> GenerateAccessTokenForRefreshAsync(long customerId, int applicationId, Guid jwtId)
    {
        var algorithm = ResolveJwtAlgorithm(GetRequiredEnvironmentVariable("JwtAlgorithm"));
        var issuer = GetRequiredEnvironmentVariable("JwtIssuer");
        var audience = GetRequiredEnvironmentVariable("JwtAudience");
        var signingKeySecret = GetJwtSigningKey();
        var signingKey = new SymmetricSecurityKey(GetSigningKeyBytes(signingKeySecret));
        var credentials = new SigningCredentials(signingKey, algorithm);
        var nowUtc = DateTime.UtcNow;
        var expiresAtUtc = nowUtc.AddMinutes(GetAccessTokenExpirationMinutes());

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, customerId.ToString()),
            new(JwtRegisteredClaimNames.Jti, jwtId.ToString()),
            new(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(nowUtc).ToString(), ClaimValueTypes.Integer64),
            new("customerId", customerId.ToString()),
            new("applicationId", applicationId.ToString())
        };

        var tokenDescriptor = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: nowUtc,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        var token = new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);

        return Task.FromResult(new AccessTokenResult(
            token,
            Convert.ToInt32((expiresAtUtc - nowUtc).TotalSeconds),
            jwtId,
            expiresAtUtc));
    }

    private static string GetJwtSigningKey()
    {
        if (!string.IsNullOrWhiteSpace(_cachedJwtSigningKey))
        {
            return _cachedJwtSigningKey;
        }

        try
        {
            var configuredValue = GetRequiredEnvironmentVariable("JwtSigningKey");
            var resolvedValue = ResolveJwtSigningKeyValue(configuredValue);

            lock (JwtSigningKeyLock)
            {
                _cachedJwtSigningKey ??= resolvedValue;
                return _cachedJwtSigningKey;
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("JwtSigningKey environment variable is not configured.", ex);
        }


    }

    private static string ResolveJwtSigningKeyValue(string configuredValue)
    {
        var trimmedValue = configuredValue.Trim();
        if (!Uri.TryCreate(trimmedValue, UriKind.Absolute, out var secretUri) ||
            !string.Equals(secretUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !secretUri.Host.Contains(".vault.azure.net", StringComparison.OrdinalIgnoreCase))
        {
            return trimmedValue;
        }

        return GetSecretValueFromKeyVault(secretUri);
    }

    private static string GetSecretValueFromKeyVault(Uri secretUri)
    {
        var pathSegments = secretUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length < 2 || !string.Equals(pathSegments[0], "secrets", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("JwtSigningKey must be a valid Key Vault secret URL when configured as a URL.");
        }

        var secretName = Uri.UnescapeDataString(pathSegments[1]);
        var secretVersion = pathSegments.Length >= 3 ? Uri.UnescapeDataString(pathSegments[2]) : null;
        var vaultUri = new Uri($"{secretUri.Scheme}://{secretUri.Host}");
        var secretClient = new SecretClient(vaultUri, new DefaultAzureCredential());

        var keyVaultSecret = string.IsNullOrWhiteSpace(secretVersion)
            ? secretClient.GetSecret(secretName).Value
            : secretClient.GetSecret(secretName, secretVersion).Value;

        if (string.IsNullOrWhiteSpace(keyVaultSecret.Value))
        {
            throw new InvalidOperationException("JwtSigningKey secret value in Key Vault is empty.");
        }

        return keyVaultSecret.Value.Trim();
    }

    private static string ResolveJwtAlgorithm(string algorithm)
    {
        return algorithm.Trim().ToUpperInvariant() switch
        {
            "HS256" => SecurityAlgorithms.HmacSha256,
            "HS384" => SecurityAlgorithms.HmacSha384,
            "HS512" => SecurityAlgorithms.HmacSha512,
            _ => algorithm
        };
    }

    private async Task<int> StoreRefreshTokenSessionAsync(
        long requestId,
        string target,
        long customerId,
        Guid tokenFamilyId,
        Guid accessTokenJti,
        byte[] refreshTokenHash,
        DateTime refreshTokenExpiresAt,
        string? requestIp,
        string? userAgent)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            throw new InvalidOperationException("SqlConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("dbo.AuthVerifyOtp_StoreRefreshSession", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.BigInt) { Value = requestId });
        command.Parameters.Add(new SqlParameter("@Target", SqlDbType.NVarChar, 255) { Value = target });
        command.Parameters.Add(new SqlParameter("@CustomerId", SqlDbType.BigInt) { Value = customerId });
        command.Parameters.Add(new SqlParameter("@TokenFamilyId", SqlDbType.UniqueIdentifier) { Value = tokenFamilyId });
        command.Parameters.Add(new SqlParameter("@AccessTokenJti", SqlDbType.UniqueIdentifier) { Value = accessTokenJti });
        command.Parameters.Add(new SqlParameter("@RefreshTokenHash", SqlDbType.VarBinary, 32) { Value = refreshTokenHash });
        command.Parameters.Add(new SqlParameter("@RefreshTokenExpiresAt", SqlDbType.DateTime2) { Value = refreshTokenExpiresAt });
        command.Parameters.Add(new SqlParameter("@RequestIp", SqlDbType.NVarChar, 45) { Value = (object?)requestIp ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@UserAgent", SqlDbType.NVarChar, 512) { Value = (object?)userAgent ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("dbo.AuthVerifyOtp_StoreRefreshSession did not return a result row.");
        }

        return reader.GetInt32(2);
    }

    private static string GenerateRefreshToken()
    {
        return Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));
    }

    private static byte[] GetSigningKeyBytes(string signingKeySecret)
    {
        if (Convert.TryFromBase64String(signingKeySecret, new Span<byte>(new byte[signingKeySecret.Length]), out var bytesWritten))
        {
            var buffer = new byte[bytesWritten];
            Convert.TryFromBase64String(signingKeySecret, buffer, out _);
            return buffer;
        }

        return Encoding.UTF8.GetBytes(signingKeySecret);
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} environment variable is not configured.");
        }

        return value;
    }

    private static bool IsDemoOtpEnabled()
    {
        return bool.TryParse(Environment.GetEnvironmentVariable("UseDemoOtp"), out var enabled) && enabled;
    }

    private static string GetOtpCode()
    {
        if (IsDemoOtpEnabled())
        {
            return GetDemoOtpValue();
        }

        return Random.Shared.Next(100000, 1000000).ToString();
    }

    private static string GetDemoOtpValue()
    {
        var value = GetRequiredEnvironmentVariable("UseDemoOtpValue");
        if (value.Length != 6)
        {
            throw new InvalidOperationException("UseDemoOtpValue must be a 6-digit number.");
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsDigit(value[index]))
            {
                throw new InvalidOperationException("UseDemoOtpValue must be a 6-digit number.");
            }
        }

        return value;
    }

    private static int GetAccessTokenExpirationMinutes()
    {
        var value = Environment.GetEnvironmentVariable("AccessTokenExpirationMinutes");
        return int.TryParse(value, out var minutes) && minutes > 0 ? minutes : 15;
    }

    private static int GetRefreshTokenLifetimeDays()
    {
        var value = Environment.GetEnvironmentVariable("RefreshTokenExpirationDays");
        return int.TryParse(value, out var days) && days > 0 ? days : 30;
    }

    private async Task<(string? CountryCode, string? MobileNo, string Channel)> ReadOtpRequestAsync(HttpRequest req)
    {
        string? countryCode = req.Query["countryCode"];
        string? mobileNo = req.Query["mobileNo"];
        string? channel = req.Query["channel"];

        if ((string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(mobileNo) || string.IsNullOrWhiteSpace(channel)) && req.ContentLength > 0)
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();

            if (!string.IsNullOrWhiteSpace(body))
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;

                if (string.IsNullOrWhiteSpace(countryCode) && root.TryGetProperty("countryCode", out var countryCodeElement))
                {
                    countryCode = ReadJsonStringValue(countryCodeElement);
                }

                if (string.IsNullOrWhiteSpace(mobileNo) && root.TryGetProperty("mobileNo", out var mobileNoElement))
                {
                    mobileNo = ReadJsonStringValue(mobileNoElement);
                }

                if (string.IsNullOrWhiteSpace(channel) && root.TryGetProperty("channel", out var channelElement))
                {
                    channel = ReadJsonStringValue(channelElement);
                }
            }
        }

        countryCode = NormalizeCountryCode(countryCode);
        channel = string.IsNullOrWhiteSpace(channel) ? "sms" : channel.Trim().ToLowerInvariant();
        return (countryCode, mobileNo, channel);
    }

    private static string? NormalizeCountryCode(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        var normalized = countryCode.Trim().TrimStart('+').Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            throw new InvalidOperationException("Phone number is required.");
        }

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

    private static string? ReadJsonStringValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString()
        };
    }

    private sealed record OtpRequestInfo(long RequestId, string Target, string Channel);

    private async Task<(long? RequestId, string? CountryCode, string? MobileNo, string Channel)> ReadResendOtpRequestAsync(HttpRequest req)
    {
        long? requestId = null;
        string? countryCode = req.Query["countryCode"];
        string? mobileNo = req.Query["mobileNo"];
        string? channel = req.Query["channel"];

        if (long.TryParse(req.Query["requestId"], out var queryRequestId))
        {
            requestId = queryRequestId;
        }

        if ((requestId is null || string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(mobileNo) || string.IsNullOrWhiteSpace(channel)) && req.ContentLength > 0)
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();

            if (!string.IsNullOrWhiteSpace(body))
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;

                if (requestId is null && root.TryGetProperty("requestId", out var requestIdElement))
                {
                    if (requestIdElement.ValueKind == JsonValueKind.Number && requestIdElement.TryGetInt64(out var bodyRequestId))
                    {
                        requestId = bodyRequestId;
                    }
                    else if (requestIdElement.ValueKind == JsonValueKind.String && long.TryParse(requestIdElement.GetString(), out var stringRequestId))
                    {
                        requestId = stringRequestId;
                    }
                }

                if (string.IsNullOrWhiteSpace(countryCode) && root.TryGetProperty("countryCode", out var countryCodeElement))
                {
                    countryCode = ReadJsonStringValue(countryCodeElement);
                }

                if (string.IsNullOrWhiteSpace(mobileNo) && root.TryGetProperty("mobileNo", out var mobileNoElement))
                {
                    mobileNo = ReadJsonStringValue(mobileNoElement);
                }

                if (string.IsNullOrWhiteSpace(channel) && root.TryGetProperty("channel", out var channelElement))
                {
                    channel = ReadJsonStringValue(channelElement);
                }
            }
        }

        countryCode = NormalizeCountryCode(countryCode);
        channel = string.IsNullOrWhiteSpace(channel) ? string.Empty : channel.Trim().ToLowerInvariant();
        return (requestId, countryCode, mobileNo, channel);
    }

    private async Task<OtpRequestInfo?> GetOtpRequestAsync(long requestId)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            throw new InvalidOperationException("SqlConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT TOP (1) RequestId, Target, Channel FROM dbo.OtpRequests WHERE RequestId = @RequestId;",
            connection);
        command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.BigInt) { Value = requestId });

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new OtpRequestInfo(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? "sms" : reader.GetString(2));
    }

    private static bool IsSupportedChannel(string channel)
    {
        return channel == "sms" || channel == "whatsapp" || channel == "voice";
    }

    private static string GetRequestIp(HttpRequest req)
    {
        return req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    private static string? GetUserAgent(HttpRequest req)
    {
        return req.Headers.TryGetValue("User-Agent", out var userAgentValues) ? userAgentValues.ToString() : null;
    }

    private static byte[] Combine(byte[] salt, string otp)
    {
        var otpBytes = System.Text.Encoding.UTF8.GetBytes(otp);
        var buffer = new byte[salt.Length + otpBytes.Length];
        Buffer.BlockCopy(salt, 0, buffer, 0, salt.Length);
        Buffer.BlockCopy(otpBytes, 0, buffer, salt.Length, otpBytes.Length);
        return buffer;
    }

    private static string? GetConnectionString()
    {
        return Environment.GetEnvironmentVariable("SQLCONNSTR_SqlConnectionString");
    }

    private static bool TryReadApplicationId(HttpRequest req, out int applicationId)
    {
        applicationId = 0;

        return req.Headers.TryGetValue("X-Application-Id", out var applicationHeader) &&
            int.TryParse(applicationHeader.ToString(), out applicationId) &&
            applicationId > 0;
    }

    private async Task<ApplicationInfo?> GetActiveApplicationAsync(int applicationId)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            throw new InvalidOperationException("SqlConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("dbo.AuthApplication_GetActiveById", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@ApplicationId", SqlDbType.Int) { Value = applicationId });

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ApplicationInfo(reader.GetInt32(0));
    }

    private async Task<(long RequestId, Guid CorrelationId)> StoreOtpRequestAsync(
        string targetType,
        string target,
        byte[] otpCodeHash,
        byte[] otpSalt,
        byte otpLength,
        string channel,
        DateTime expiresAt,
        string? requestIp,
        string? userAgent,
        string? notes,
        int applicationId)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            throw new InvalidOperationException("SqlConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("dbo.AuthRequestOtp_StoreRequest", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.Add(new SqlParameter("@TargetType", SqlDbType.NVarChar, 20) { Value = targetType });
        command.Parameters.Add(new SqlParameter("@Target", SqlDbType.NVarChar, 255) { Value = target });
        command.Parameters.Add(new SqlParameter("@OtpCodeHash", SqlDbType.VarBinary, 32) { Value = otpCodeHash });
        command.Parameters.Add(new SqlParameter("@OtpSalt", SqlDbType.VarBinary, 32) { Value = otpSalt });
        command.Parameters.Add(new SqlParameter("@OtpLength", SqlDbType.TinyInt) { Value = otpLength });
        command.Parameters.Add(new SqlParameter("@Channel", SqlDbType.NVarChar, 50) { Value = channel });
        command.Parameters.Add(new SqlParameter("@ExpiresAt", SqlDbType.DateTime2) { Value = expiresAt });
        command.Parameters.Add(new SqlParameter("@RequestIp", SqlDbType.NVarChar, 45) { Value = (object?)requestIp ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@UserAgent", SqlDbType.NVarChar, 512) { Value = (object?)userAgent ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Notes", SqlDbType.NVarChar, 1000) { Value = (object?)notes ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@ApplicationId", SqlDbType.Int) { Value = applicationId });
        command.Parameters.Add(new SqlParameter("@CorrelationId", SqlDbType.UniqueIdentifier)
        {
            Direction = ParameterDirection.InputOutput,
            Value = DBNull.Value
        });
        command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.BigInt)
        {
            Direction = ParameterDirection.InputOutput,
            Value = DBNull.Value
        });

        await command.ExecuteNonQueryAsync();

        var requestId = Convert.ToInt64(command.Parameters["@RequestId"].Value);
        var correlationId = (Guid)command.Parameters["@CorrelationId"].Value;
        return (requestId, correlationId);
    }

    private async Task<(long ResendId, int ResendCount)> StoreResendRequestAsync(
        long requestId,
        string channel,
        string? requestedBy,
        byte result,
        byte? attemptCount,
        string? requestIp,
        string? userAgent,
        string? reason,
        DateTime? newExpiresAt,
        byte[]? otpCodeHash,
        byte[]? otpSalt)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("SQLCONNSTR_SqlConnectionString environment variable is not configured.");
            throw new InvalidOperationException("SqlConnectionString is not configured.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("dbo.AuthResendOtp_StoreRequest", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.BigInt) { Value = requestId });
        command.Parameters.Add(new SqlParameter("@Channel", SqlDbType.NVarChar, 50) { Value = (object?)channel ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@RequestedBy", SqlDbType.NVarChar, 100) { Value = (object?)requestedBy ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Result", SqlDbType.TinyInt) { Value = result });
        command.Parameters.Add(new SqlParameter("@AttemptCount", SqlDbType.TinyInt) { Value = (object?)attemptCount ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@RequestIp", SqlDbType.NVarChar, 45) { Value = (object?)requestIp ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@UserAgent", SqlDbType.NVarChar, 512) { Value = (object?)userAgent ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Reason", SqlDbType.NVarChar, 250) { Value = (object?)reason ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@NewExpiresAt", SqlDbType.DateTime2) { Value = (object?)newExpiresAt ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@OtpCodeHash", SqlDbType.VarBinary, 32) { Value = (object?)otpCodeHash ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@OtpSalt", SqlDbType.VarBinary, 32) { Value = (object?)otpSalt ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@ResendId", SqlDbType.BigInt)
        {
            Direction = ParameterDirection.InputOutput,
            Value = DBNull.Value
        });
        command.Parameters.Add(new SqlParameter("@ResendCount", SqlDbType.Int)
        {
            Direction = ParameterDirection.InputOutput,
            Value = DBNull.Value
        });

        await command.ExecuteNonQueryAsync();

        var resendId = Convert.ToInt64(command.Parameters["@ResendId"].Value);
        var resendCount = Convert.ToInt32(command.Parameters["@ResendCount"].Value);
        return (resendId, resendCount);
    }

    private async Task<bool> SendOtpAsync(string authKey, string otp, string mobile, string? normalizedCountryCode, string channel)
    {
        var requestUrl = $"https://apitxt.com/api/sendOTP?authkey={Uri.EscapeDataString(authKey)}&mobile={Uri.EscapeDataString(mobile)}&otp={Uri.EscapeDataString(otp)}";

        if (channel != "sms")
        {
            requestUrl = $"{requestUrl}&channel={Uri.EscapeDataString(channel)}";
        }

        if (channel == "whatsapp" && !string.IsNullOrWhiteSpace(normalizedCountryCode))
        {
            requestUrl = $"{requestUrl}&country={Uri.EscapeDataString(normalizedCountryCode)}";
        }

        try
        {
            var response = await HttpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OTP provider returned status code {StatusCode} for mobile {Mobile} and channel {Channel}.", response.StatusCode, mobile, channel);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send OTP to {Mobile} via {Channel}.", mobile, channel);
            return false;
        }
    }
}
