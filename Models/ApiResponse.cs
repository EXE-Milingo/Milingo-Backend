namespace Milingo.Backend.Models;

/// <summary>
/// Standard API response envelope for consistent client-side parsing.
/// </summary>
public class ApiResponse<T>
{
    public string Status { get; set; } = "success";
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }
}
