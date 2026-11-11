using LancacheManager.Models;

namespace LancacheManager.Security;

public sealed record AccessModeState(AccountMode Mode, bool SetupRequired, bool ModeFromEnvironment);
