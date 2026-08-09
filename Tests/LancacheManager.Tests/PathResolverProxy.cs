using System.Reflection;
using LancacheManager.Core.Interfaces;

namespace LancacheManager.Tests;

internal class PathResolverProxy : DispatchProxy
{
    public string Root { get; set; } = string.Empty;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.Name == nameof(IPathResolver.ResolvePath))
        {
            var path = Assert.IsType<string>(args![0]);
            return Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
        }

        if (targetMethod.Name == nameof(IPathResolver.NormalizePath))
        {
            return Assert.IsType<string>(args![0]);
        }

        if (targetMethod.ReturnType == typeof(string))
        {
            return Path.Combine(Root, targetMethod.Name);
        }

        if (targetMethod.ReturnType == typeof(bool))
        {
            return true;
        }

        if (targetMethod.ReturnType == typeof(int))
        {
            return 0;
        }

        return null;
    }
}
