namespace LancacheManager.Security;

/// <summary>
/// A sign-in failure whose cause the callback already knows, carrying the failure code the sign-in page
/// shows instead of leaving the redirect to guess from the exception.
/// </summary>
public sealed class SignInFailureException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
