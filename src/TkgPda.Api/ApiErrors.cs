using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using TkgPda.Application;
using TkgPda.Domain;

namespace TkgPda.Api;

public sealed class ApiException(int statusCode, string code, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException error)
        {
            await ApiErrorWriter.WriteAsync(context, error.StatusCode, error.Code, error.Message);
        }
        catch (WorkflowRuleException error)
        {
            var status = WorkflowHttpStatus(error.Code);
            await ApiErrorWriter.WriteAsync(context, status, error.Code, error.Message);
        }
        catch (AdminRuleException error)
        {
            var status = AdminHttpStatus(error.Code);
            await ApiErrorWriter.WriteAsync(context, status, error.Code, error.Message);
        }
        catch (RdsIntegrationRuleException error)
        {
            var status = error.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal)
                ? StatusCodes.Status404NotFound
                : error.Code is "NAVIGATION_PROFILE_REQUIRED" or "ID_REQUIRED"
                    ? StatusCodes.Status400BadRequest
                    : error.Code is "MACHINE_REGION_MAPPING_FORBIDDEN" or "RDS_WORKSITE_NOT_PHYSICAL"
                        or "RDS_WORKSITE_PLAN_NAME_MISMATCH" or "SLOT_NOT_TRANSPORT_ELIGIBLE"
                        ? StatusCodes.Status422UnprocessableEntity
                        : StatusCodes.Status409Conflict;
            await ApiErrorWriter.WriteAsync(context, status, error.Code, error.Message);
        }
        catch (RdsReadException error)
        {
            logger.LogWarning("RDS read failed with {Code} at {Endpoint} for correlation {CorrelationId}.", error.Code, error.Endpoint, error.CorrelationId);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                error.Code,
                "The read-only RDS service is temporarily unavailable.");
        }
        catch (RdsCommissioningDiscoveryException error)
        {
            logger.LogWarning("RDSCore discovery failed with {Code} for request {TraceIdentifier}.", error.Code, context.TraceIdentifier);
            var status = error.Code switch
            {
                "RDSCORE_DISCOVERY_DISABLED" => StatusCodes.Status503ServiceUnavailable,
                "RDSCORE_PROBE_INVALID" or "RDSCORE_PROBE_LIMIT_EXCEEDED" => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status502BadGateway
            };
            await ApiErrorWriter.WriteAsync(context, status, error.Code, error.Message);
        }
        catch (RdsExecutionException error)
        {
            logger.LogWarning("RDS execution operation failed with {Code} for request {TraceIdentifier}.", error.Code, context.TraceIdentifier);
            var status = error.Code is "RDS_DISPATCH_DISABLED" or "RDS_UNAVAILABLE" or "RDS_TIMEOUT"
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status409Conflict;
            await ApiErrorWriter.WriteAsync(context, status, error.Code, error.Message);
        }
        catch (BadHttpRequestException error)
        {
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status400BadRequest, "INVALID_HTTP_REQUEST", error.Message);
        }
        catch (JsonException)
        {
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status400BadRequest, "INVALID_JSON", "The request body is not valid JSON.");
        }
        // The single cancellation path. A client disconnect is not a server fault:
        // it is logged at Information and the request ends without a response body,
        // because nothing can be written to a connection the client has abandoned.
        // An OperationCanceledException whose RequestAborted is NOT cancelled is a
        // genuine internal fault and deliberately falls through to the 500 below.
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request {TraceIdentifier} was cancelled by the client.", context.TraceIdentifier);
        }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation)
        {
            logger.LogInformation("Configuration constraint rejected request {TraceIdentifier}: {ConstraintName}.", context.TraceIdentifier, error.ConstraintName);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status409Conflict,
                "CONFIGURATION_CONFLICT",
                "The requested configuration conflicts with an existing record or protected reference.");
        }
        catch (NpgsqlException error)
        {
            logger.LogWarning(error, "PostgreSQL was unavailable for request {TraceIdentifier}.", context.TraceIdentifier);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "DATABASE_UNAVAILABLE",
                "Veritabanı bağlantısı geçici olarak kullanılamıyor. Sunucu hazır olduğunda Yenile ile tekrar deneyin.");
        }
        catch (Exception error)
        {
            logger.LogError(error, "Unhandled API error for request {TraceIdentifier}.", context.TraceIdentifier);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "INTERNAL_ERROR",
                "An unexpected server error occurred.");
        }
    }

    private static int WorkflowHttpStatus(string code) => code switch
    {
        "PRODUCTION_ACCESS_DENIED" => StatusCodes.Status403Forbidden,
        "PRODUCTION_SESSION_NOT_FOUND" => StatusCodes.Status404NotFound,
        "PRODUCTION_SESSION_ALREADY_ACTIVE" or "PRODUCTION_CONTEXT_MISMATCH" or "PRODUCTION_SESSION_ACTIVE" or "PRODUCTION_SESSION_CHANGED"
            or "PRODUCTION_ENDING" or "PRODUCTION_END_IN_PROGRESS"
            => StatusCodes.Status409Conflict,
        "REQUEST_NOT_FOUND" or "SLOT_NOT_FOUND" or "PART_NOT_FOUND" or "CONTAINER_NOT_FOUND" or "TASK_TYPE_NOT_FOUND"
            => StatusCodes.Status404NotFound,
        "IDEMPOTENCY_KEY_REQUIRED" or "IDEMPOTENCY_KEY_TOO_LONG" or "SOURCE_REQUIRED" or "DESTINATION_REQUIRED"
            or "SAME_SOURCE_DESTINATION" or "INVALID_PRIORITY" or "REQUEST_ID_REQUIRED" or "ID_REQUIRED"
            or "LOAD_STATE_NOT_SELECTABLE" or "BATCH_IDEMPOTENCY_KEY_REQUIRED" or "BATCH_IDEMPOTENCY_KEY_TOO_LONG"
            or "BATCH_ITEMS_REQUIRED" or "BATCH_MACHINE_MISMATCH" or "BATCH_ITEM_IDEMPOTENCY_KEY_DUPLICATED"
            or "CONTAINER_ACTION_INVALID" or "CONTAINER_POSITION_INVALID" or "CONTAINER_BATCH_ITEM_INVALID"
            or "QUEUE_PAGE_INVALID"
            => StatusCodes.Status400BadRequest,
        "IDEMPOTENCY_KEY_REUSED" or "SLOT_ALREADY_RESERVED" or "ACTIVE_TRANSPORT_EXISTS" or "PART_REQUEST_ALREADY_ACTIVE"
            or "RUNTIME_CONTEXT_MISMATCH" or "NAVIGATION_CONTEXT_MISMATCH" or "VARIANT_LAYOUT_CONFLICT"
            or "NAVIGATION_PROFILE_CONFLICT" or "NAVIGATION_PROFILE_AMBIGUOUS" or "PRODUCT_GROUP_CONFLICT" or "PART_CONFLICT"
            or "CONTAINER_CONFLICT" or "INVALID_SLOT_STATE" or "ILLEGAL_STATUS_TRANSITION"
            or "CANCELLATION_AFTER_EXECUTION_DEFERRED" or "REQUEST_LOCK_SCOPE_CHANGED" or "CANCELLATION_NOT_ALLOWED"
            or "SLOT_NOT_IN_ACTIVE_LAYOUT" or "OCCUPIED_SLOT_REQUIRED" or "SLOT_BUSY"
            => StatusCodes.Status409Conflict,
        "MACHINE_REGION_FORBIDDEN" or "CROSS_MACHINE_SLOT" or "CROSS_LAYOUT_SLOT" or "SLOT_DISABLED"
            => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status422UnprocessableEntity
    };

    private static int AdminHttpStatus(string code)
    {
        if (code.EndsWith("_NOT_FOUND", StringComparison.Ordinal))
        {
            return StatusCodes.Status404NotFound;
        }

        return code switch
        {
            "WEAK_PASSWORD" or "INVALID_FIELD" or "INVALID_ENUM_VALUE" or "INVALID_JSON_OBJECT"
                or "ID_REQUIRED" or "ROLE_REQUIRED" or "UNKNOWN_ROLE" or "CONCURRENCY_TOKEN_REQUIRED"
                or "MACHINE_VIEW_PERMISSION_REQUIRED"
                => StatusCodes.Status400BadRequest,
            "INVALID_SLOT_GEOMETRY" or "INVALID_CONTAINER_DIMENSIONS"
                => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status409Conflict
        };
    }
}

public static class ApiErrorWriter
{
    public static async Task WriteAsync(HttpContext context, int statusCode, string code, string detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = ReasonPhrases.GetReasonPhrase(statusCode),
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = context.Items.TryGetValue(ApiContextKeys.CorrelationId, out var value)
            ? value
            : context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(problem, cancellationToken: context.RequestAborted);
    }
}
