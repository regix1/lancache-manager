using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // The Users page says where the guest session length comes from: set on that page, set in the
    // config file, or the built-in default. A length set in the config file must not read "Uses the
    // built-in default", even when the file names the same number of hours, so the answer the page
    // reads has to say where the length comes from, not only how long it is.
    // The length the server uses when nobody set one (SessionService.GetGuestDurationHours).
    private const int BuiltInGuestHours = 6;

    [Theory]
    [InlineData(BuiltInGuestHours)]
    [InlineData(12)]
    public async Task GuestLength_SetInTheConfigFile_IsAnsweredApartFromTheBuiltInDefault(int configuredHours)
    {
        async Task<string> AnswerWithoutNumbersAsync(int? configFileHours)
        {
            var (_, contexts) = NewDatabase();
            var root = NewTempRoot();
            var settings = new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(root, "api_key.txt"),
                ["Security:EnableAuthentication"] = "true"
            };
            if (configFileHours is { } hours)
                settings["Security:GuestSessionDurationHours"] = hours.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IDbContextFactory<AppDbContext>>(contexts);
            services.AddSingleton(FakeInterface<ISignalRNotificationService>());
            services.AddSingleton(CreateStateService(root));
            services.AddSingleton(new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!));
            services.AddSingleton<SessionService>();
            services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
            services.AddSingleton<IdentityAuditService>();
            services.AddSingleton<AccountLockout>();
            await using var app = services.BuildServiceProvider();

            var controller = ActivatorUtilities.CreateInstance<AuthController>(app);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = app } };
            var answer = Assert.IsType<OkObjectResult>(controller.GetGuestDuration().Result).Value;

            // The body the page reads, serialized as the app does (Program.cs AddJsonOptions), with the
            // hour counts set to 0, so only what the answer says about the length's origin is left.
            var body = JsonSerializer.Serialize(answer, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            return Regex.Replace(body, $@"(?<=:)({configuredHours}|{BuiltInGuestHours})(?=[,}}])", "0");
        }

        var fromConfigFile = await AnswerWithoutNumbersAsync(configuredHours);
        var builtIn = await AnswerWithoutNumbersAsync(null);

        Assert.True(
            fromConfigFile != builtIn,
            $"a guest session length of {configuredHours} hours set in the config file is answered {fromConfigFile}, " +
            "the same as a length nobody set, so the Users page reads \"Uses the built-in default\"");
    }
}
