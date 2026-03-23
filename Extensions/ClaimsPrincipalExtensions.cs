using System.Security.Claims;

namespace Milingo.Backend.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static string? GetFirebaseUid(this ClaimsPrincipal user)
    {
        return user.FindFirstValue("user_id")
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("uid");
    }

    public static string GetFirebaseEmailOrEmpty(this ClaimsPrincipal user)
    {
        return user.FindFirstValue("email")
            ?? user.FindFirstValue(ClaimTypes.Email)
            ?? string.Empty;
    }
}
