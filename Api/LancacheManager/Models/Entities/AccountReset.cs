namespace LancacheManager.Models;

public sealed class AccountReset
{
    public int Id { get; set; }
    public Guid? PrimaryAccountId { get; set; }
    public Guid[] AccountIds { get; set; } = [];
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
