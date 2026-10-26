namespace EnglishApp.Shared.Common;

/// <summary>Tüm API yanıtlarının standart zarfı.</summary>
public class ApiResponse
{
    public bool Success { get; set; }
    public ApiError? Error { get; set; }
    public string? CorrelationId { get; set; }

    public static ApiResponse Ok() => new() { Success = true };
    public static ApiResponse Fail(string code, string message, IEnumerable<string>? details = null) =>
        new() { Success = false, Error = new ApiError(code, message, details?.ToList()) };
}

public class ApiResponse<T> : ApiResponse
{
    public T? Data { get; set; }

    public static ApiResponse<T> Ok(T data) => new() { Success = true, Data = data };
}

public record ApiError(string Code, string Message, List<string>? Details = null);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
