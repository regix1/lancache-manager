namespace LancacheManager.Models;

/// <summary>
/// The client group one IP belongs to, plus how that group reports its members in the client
/// stats surfaces. Carried together so the stats fold needs no second lookup per IP.
/// </summary>
public readonly record struct ClientGroupAssignment(
    long GroupId,
    string Nickname,
    bool SeparateMemberRows);
