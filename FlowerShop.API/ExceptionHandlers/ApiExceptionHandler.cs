
using System.ComponentModel.DataAnnotations;
using FlowerShop.Application.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FlowerShop.API.ExceptionHandlers;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            DbUpdateException
            {
                InnerException: SqlException { Number: 2601 or 2627 }
            } => StatusCodes.Status409Conflict,

            BusinessException { ErrorType: BusinessErrorType.NotFound }
                => StatusCodes.Status404NotFound,

            BusinessException { ErrorType: BusinessErrorType.Conflict }
                => StatusCodes.Status409Conflict,
           
            BusinessException { ErrorType: BusinessErrorType.Unauthorized }
                => StatusCodes.Status401Unauthorized,
            
            ValidationException
                => StatusCodes.Status400BadRequest,

            _ => 0
        };

        if (status == 0)
            return false;

        context.Response.StatusCode = status;

        return await problemDetails.TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails
                {
                    Status = status,
                    Title = ReasonPhrases.GetReasonPhrase(status),
                    Detail = exception is DbUpdateException
                    ? "UNIQUE_CONSTRAINT_VIOLATION"
                    : exception.Message
                }
            });
    }
}
