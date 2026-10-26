using EnglishApp.Shared.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnglishApp.Api.Controllers;

[ApiController]
[Authorize]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected ActionResult<ApiResponse<T>> Success<T>(T data) =>
        Ok(new ApiResponse<T> { Success = true, Data = data, CorrelationId = HttpContext.TraceIdentifier });

    protected ActionResult<ApiResponse> Success() =>
        Ok(new ApiResponse { Success = true, CorrelationId = HttpContext.TraceIdentifier });
}
