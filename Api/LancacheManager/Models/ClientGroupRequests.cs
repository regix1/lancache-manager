namespace LancacheManager.Models;

public class CreateClientGroupRequest
{
    public string Nickname { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string>? InitialIps { get; set; }
}

public class UpdateClientGroupRequest
{
    public string Nickname { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class AddMemberRequest
{
    public string ClientIp { get; set; } = string.Empty;
}
