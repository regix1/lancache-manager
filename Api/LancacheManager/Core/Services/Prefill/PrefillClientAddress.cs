namespace LancacheManager.Core.Services;

/// <summary>
/// One address a live prefill container shows up as in the cache log, with the cache service
/// that container downloads from. The live speed hides entries that match both.
/// </summary>
public sealed record PrefillClientAddress(string Service, string ClientIp);
