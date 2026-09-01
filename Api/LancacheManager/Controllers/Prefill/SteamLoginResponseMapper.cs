using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Maps SteamKit2 authentication outcomes to the shared Steam login API response shape.
/// </summary>
public static class SteamLoginResponseMapper
{
    /// <summary>
    /// Builds the standard success response after credentials were persisted by the caller.
    /// </summary>
    public static SteamLoginResponse CreateSuccessResponse(string username, Guid? operationId) =>
        new()
        {
            Success = true,
            Message = "Authentication successful",
            AuthMode = "authenticated",
            Username = username,
            OperationId = operationId
        };

    /// <summary>
    /// Maps challenge or failure outcomes to the appropriate <see cref="IActionResult"/>.
    /// Returns <c>null</c> when <paramref name="result"/> indicates success so the caller can
    /// persist credentials and return <see cref="CreateSuccessResponse"/>.
    /// </summary>
    public static IActionResult? MapChallengeOrFailure(SteamKit2Service.AuthenticationResult result)
    {
        if (result.Success)
        {
            return null;
        }

        // Prefer the service's own wording, the way the SessionExpired branch below already does: it
        // knows whether this is the first prompt or a code Steam just rejected, and those two need to
        // read differently or the user retypes the same failing code.
        if (result.RequiresTwoFactor)
        {
            return new OkObjectResult(new SteamLoginResponse
            {
                RequiresTwoFactor = true,
                Message = result.Message ?? "Two-factor authentication required",
                OperationId = result.OperationId
            });
        }

        if (result.RequiresEmailCode)
        {
            return new OkObjectResult(new SteamLoginResponse
            {
                RequiresEmailCode = true,
                Message = result.Message ?? "Email verification code required",
                OperationId = result.OperationId
            });
        }

        if (result.SessionExpired)
        {
            return new OkObjectResult(new SteamLoginResponse
            {
                SessionExpired = true,
                RequiresTwoFactor = true,
                Message = result.Message ?? "Session expired. Please enter your 2FA code instead.",
                OperationId = result.OperationId
            });
        }

        var failure = ApiResponse.Error(result.Message ?? "Authentication failed");
        failure.StageKey = result.StageKey;
        return new BadRequestObjectResult(failure);
    }
}
