namespace LancacheManager.Models;

public class SetupCredentialsRequest
{
    public string? Username { get; set; }
    public string Password { get; set; } = string.Empty;
}

public class SetupInitStatusResponse
{
    public bool NeedsSetup { get; set; }
    public bool Configured { get; set; }
}

public class SetupCredentialsResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class SetupErrorResponse
{
    public string Error { get; set; } = string.Empty;
}
