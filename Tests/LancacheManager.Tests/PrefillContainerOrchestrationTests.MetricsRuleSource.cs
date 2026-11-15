using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // The Grafana card says where the metrics sign-in rule comes from: set on the card, set in the
    // config file or an environment variable, or the built-in default. A rule the config sets must
    // not read as the default, even when it names the built-in value, so the answer the card reads
    // has to say where the rule comes from, not only what it is.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetricsRule_SetInTheConfig_IsAnsweredApartFromTheBuiltInDefault(bool configuredRule)
    {
        string AnswerWithoutTheRule(bool? configRule)
        {
            var settings = new Dictionary<string, string?>();
            if (configRule is { } rule)
                settings["Security:RequireAuthForMetrics"] = rule ? "true" : "false";
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var controller = new MetricsController(
                configuration,
                metricsService: null!,
                CreateStateService(NewTempRoot()),
                FakeInterface<ISignalRNotificationService>(),
                NullLogger<MetricsController>.Instance);

            var answer = Assert.IsType<OkObjectResult>(controller.GetSecurity().Result).Value;

            // The body the card reads, serialized as the app does (Program.cs AddJsonOptions), with the
            // rule's own values removed, so only what the answer says about the rule's origin is left.
            var body = JsonNode.Parse(JsonSerializer.Serialize(answer, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }))!.AsObject();
            body.Remove("requiresAuthentication");
            body.Remove("envVarValue");
            return body.ToJsonString();
        }

        var fromConfig = AnswerWithoutTheRule(configuredRule);
        var builtIn = AnswerWithoutTheRule(null);

        Assert.True(
            fromConfig != builtIn,
            $"a metrics rule the config sets to {(configuredRule ? "secured" : "public")} is answered {fromConfig}, " +
            "the same as a rule nobody set, so the Grafana card cannot say it comes from the config");
    }
}
