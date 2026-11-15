namespace LancacheManager.Infrastructure.Filters;

/// <summary>
/// Marks a route whose handler itself decides what a user-role account may change through it, such
/// as its own account or its own session, so <see cref="UserRoleRefusalFilter"/> leaves it to that
/// handler.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HandlerChecksCallerAttribute : Attribute
{
}
