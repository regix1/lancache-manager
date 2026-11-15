using System.Reflection;
using LancacheManager.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;

namespace LancacheManager.Tests;

/// <summary>
/// The import form's connection test sends a source database's connection string, its password
/// included. A string in the address is written into every reverse-proxy access log on the way, and a
/// read skips the antiforgery check (<c>AntiforgeryFilter.cs</c>), so a page on another site could make
/// a signed-in administrator's browser open a connection to any host. So the route that runs the test
/// is a write that takes the string in its body.
/// </summary>
public sealed class MigrationConnectionCheckKeepsPasswordOutOfUrlTests
{
    [Fact]
    public void TheConnectionTest_TakesTheConnectionStringInTheBodyOfAWrite()
    {
        var actions = typeof(DataMigrationController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(action => action.GetCustomAttributes().OfType<IRouteTemplateProvider>()
                .Any(route => route.Template == "validate-connection"))
            .ToList();
        var action = Assert.Single(actions);

        var wrong = new List<string>();
        var methods = action.GetCustomAttributes().OfType<IActionHttpMethodProvider>().SelectMany(verb => verb.HttpMethods).ToList();
        if (methods.Count == 0 || methods.Contains("GET"))
            wrong.Add($"it answers {(methods.Count == 0 ? "every verb" : string.Join(", ", methods))}, so the check runs as a read with no antiforgery token");
        foreach (var parameter in action.GetParameters())
        {
            var source = parameter.GetCustomAttributes().OfType<IBindingSourceMetadata>().FirstOrDefault()?.BindingSource;
            if (source == BindingSource.Query || (source is null && parameter.ParameterType == typeof(string)))
                wrong.Add($"its {parameter.Name} is read from the address (?{parameter.Name}=...), so the password lands in access logs");
        }
        if (action.GetCustomAttributes().OfType<IgnoreAntiforgeryTokenAttribute>().Any())
            wrong.Add("it opts out of the antiforgery check");
        Assert.True(wrong.Count == 0, "api/migration/validate-connection: " + string.Join("; ", wrong));
    }
}
