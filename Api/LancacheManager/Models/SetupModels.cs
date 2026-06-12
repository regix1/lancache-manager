namespace LancacheManager.Models;

public class SetupCredentialsRequest
{
    public string? Username { get; set; }
    public string Password { get; set; } = string.Empty;
}

public class SetExternalDbCredentialsRequest
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "lancache";
    public string Username { get; set; } = "lancache";
    public string Password { get; set; } = string.Empty;
}

public class SetExternalDbCredentialsResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool RestartRequired { get; set; } = true;
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
