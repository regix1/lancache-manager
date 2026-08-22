using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Validators;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the membership contract for client groups: one save carries the whole address list, an
/// address a different nickname already holds comes back named instead of failing the save or
/// disappearing, a create reports the addresses it could not take, and the save reaches historical
/// dashboard ranges rather than only the live one.
///
/// These run against a real PostgreSQL database rather than the InMemory provider because the
/// guarantee under test is the UNIQUE index on ClientGroupMembers.ClientIp - one address belongs to
/// exactly one group - and the InMemory provider does not enforce unique indexes.
/// </summary>
public sealed class ClientGroupMembershipTests
{
    [Fact]
    public async Task CreatingAGroupNamesTheAddressesItCouldNotTakeAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.ClientGroups.Add(new ClientGroup
            {
                Nickname = "Living Room",
                Members = { new ClientGroupMember { ClientIp = "1.1.1.2" } }
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new AppDbContext(options);
        var (controller, _) = CreateController(context);

        var result = await controller.CreateAsync(new CreateClientGroupRequest
        {
            Nickname = "Lab",
            InitialIps = new List<string> { "1.1.1.1", "1.1.1.2" }
        });

        var created = Assert.IsType<CreatedResult>(result);
        var response = Assert.IsType<CreateClientGroupResponse>(created.Value);

        // The address the other nickname holds is named back, so the caller cannot report a clean
        // success over a group that is short an address the user picked.
        Assert.Equal(new[] { "1.1.1.2" }, response.RejectedIps);
        Assert.Equal(new[] { "1.1.1.1" }, response.MemberIps);
        Assert.Equal("Lab", response.Nickname);
    }

    [Fact]
    public async Task SettingMembersAddsAndRemovesInOneCallAndLeavesTheRestAloneAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;
        var untouchedSince = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup
            {
                Nickname = "Lab",
                Members =
                {
                    new ClientGroupMember { ClientIp = "10.0.0.1", AddedAtUtc = untouchedSince },
                    new ClientGroupMember { ClientIp = "10.0.0.2", AddedAtUtc = untouchedSince },
                    new ClientGroupMember { ClientIp = "10.0.0.3", AddedAtUtc = untouchedSince }
                }
            };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var rejected = await CreateService(run).SetMembersAsync(
                groupId,
                new[] { "10.0.0.1", "10.0.0.3", "10.0.0.4" });

            Assert.Empty(rejected);
        }

        await using (var assert = new AppDbContext(options))
        {
            var members = await assert.ClientGroupMembers
                .Where(m => m.ClientGroupId == groupId)
                .OrderBy(m => m.ClientIp)
                .ToListAsync();

            Assert.Equal(new[] { "10.0.0.1", "10.0.0.3", "10.0.0.4" }, members.Select(m => m.ClientIp));

            // A member that stayed in the list keeps its original row rather than being deleted and
            // reinserted, which is what makes the save a diff instead of a replacement.
            Assert.Equal(untouchedSince, members.Single(m => m.ClientIp == "10.0.0.1").AddedAtUtc);
            Assert.Equal(untouchedSince, members.Single(m => m.ClientIp == "10.0.0.3").AddedAtUtc);
        }
    }

    [Fact]
    public async Task SettingMembersReportsAnAddressAnotherGroupOwnsInsteadOfThrowingAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long labId;
        await using (var seed = new AppDbContext(options))
        {
            seed.ClientGroups.Add(new ClientGroup
            {
                Nickname = "Living Room",
                Members = { new ClientGroupMember { ClientIp = "10.0.0.9" } }
            });
            var lab = new ClientGroup { Nickname = "Lab" };
            seed.ClientGroups.Add(lab);
            await seed.SaveChangesAsync();
            labId = lab.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var rejected = await CreateService(run).SetMembersAsync(
                labId,
                new[] { "10.0.0.5", "10.0.0.9" });

            Assert.Equal(new[] { "10.0.0.9" }, rejected);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.Equal(
                new[] { "10.0.0.5" },
                await assert.ClientGroupMembers.Where(m => m.ClientGroupId == labId)
                    .Select(m => m.ClientIp).ToListAsync());

            // The address stays where it was: taking it would need a decision about the nickname
            // losing it, which this endpoint does not make.
            var owner = await assert.ClientGroupMembers.SingleAsync(m => m.ClientIp == "10.0.0.9");
            Assert.NotEqual(labId, owner.ClientGroupId);
        }
    }

    [Fact]
    public void AnUpdateWithoutARowModeFailsValidation()
    {
        // ApplyUpdate reads request.SeparateMemberRows!.Value, so validation is the only thing
        // between an omitted field and a NullReferenceException.
        var result = new UpdateClientGroupRequestValidator().Validate(new UpdateClientGroupRequest
        {
            Nickname = "Lab",
            SeparateMemberRows = null
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateClientGroupRequest.SeparateMemberRows));
    }

    [Fact]
    public async Task SettingMembersToleratesDuplicatesAndAddressesTheGroupAlreadyHasAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup
            {
                Nickname = "Lab",
                Members = { new ClientGroupMember { ClientIp = "10.0.0.1" } }
            };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var rejected = await CreateService(run).SetMembersAsync(
                groupId,
                new[] { "10.0.0.1", "10.0.0.1", "10.0.0.2" });

            Assert.Empty(rejected);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.Equal(
                new[] { "10.0.0.1", "10.0.0.2" },
                await assert.ClientGroupMembers.Where(m => m.ClientGroupId == groupId)
                    .OrderBy(m => m.ClientIp).Select(m => m.ClientIp).ToListAsync());
        }
    }

    [Fact]
    public async Task SettingMembersToAnEmptyListEmptiesTheGroupAsync()
    {
        // The server treats the list as the whole truth, matching the per-address delete that has
        // always allowed a group to reach zero. The modal is where a nickname is kept reachable.
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup
            {
                Nickname = "Lab",
                Members = { new ClientGroupMember { ClientIp = "10.0.0.1" } }
            };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            Assert.Empty(await CreateService(run).SetMembersAsync(groupId, Array.Empty<string>()));
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.Empty(await assert.ClientGroupMembers.Where(m => m.ClientGroupId == groupId).ToListAsync());
        }
    }

    [Fact]
    public async Task SettingMembersOnAGroupThatIsNotThereIsNotFoundAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using var context = new AppDbContext(options);
        var (controller, _) = CreateController(context);

        await Assert.ThrowsAsync<NotFoundException>(() => controller.SetMembersAsync(
            404,
            new SetMembersRequest { ClientIps = new List<string> { "10.0.0.1" } }));
    }

    [Fact]
    public async Task TheMembersEndpointNamesEntriesThatAreNotAddressesAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup { Nickname = "Lab" };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using var context = new AppDbContext(options);
        var (controller, _) = CreateController(context);

        var result = await controller.SetMembersAsync(groupId, new SetMembersRequest
        {
            ClientIps = new List<string> { "10.0.0.1", "not-an-address" }
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var body = Assert.IsType<InvalidClientIpsResponse>(badRequest.Value);
        Assert.Equal(new[] { "not-an-address" }, body.InvalidIps);
        Assert.NotEmpty(body.Error);

        // Nothing is applied when an entry is turned down, so the caller is never left guessing which
        // half of the list landed.
        await using var assert = new AppDbContext(options);
        Assert.Empty(await assert.ClientGroupMembers.Where(m => m.ClientGroupId == groupId).ToListAsync());
    }

    [Fact]
    public async Task TheMembersEndpointReturnsTheGroupAndTheRejectedAddressesAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long labId;
        await using (var seed = new AppDbContext(options))
        {
            seed.ClientGroups.Add(new ClientGroup
            {
                Nickname = "Living Room",
                Members = { new ClientGroupMember { ClientIp = "10.0.0.9" } }
            });
            var lab = new ClientGroup
            {
                Nickname = "Lab",
                Members = { new ClientGroupMember { ClientIp = "10.0.0.1" } }
            };
            seed.ClientGroups.Add(lab);
            await seed.SaveChangesAsync();
            labId = lab.Id;
        }

        await using var context = new AppDbContext(options);
        var (controller, notifications) = CreateController(context);

        var result = await controller.SetMembersAsync(labId, new SetMembersRequest
        {
            ClientIps = new List<string> { "10.0.0.2", "10.0.0.9" }
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<SetMembersResponse>(ok.Value);

        Assert.Equal(new[] { "10.0.0.2" }, response.Group.MemberIps);
        Assert.Equal(new[] { "10.0.0.9" }, response.RejectedIps);
        Assert.Equal(labId, response.Group.Id);

        // One event for the whole save, not one per address, so the frontend refetches once.
        var broadcasts = notifications.Invocations
            .Where(i => i.Method == nameof(ISignalRNotificationService.NotifyAllAsync))
            .ToList();
        Assert.Equal(SignalREvents.ClientGroupUpdated, (string?)Assert.Single(broadcasts).Args[0]);
    }

    [Fact]
    public async Task AMembershipSaveBuiltOnAStampTheGroupHasMovedPastIsTurnedDownAsync()
    {
        // The list replaces the whole membership, so a save built on a copy taken before someone else
        // added an address deletes that address, and neither editor is ever told.
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;
        var stampTheEditorRead = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup
            {
                Nickname = "Lab",
                UpdatedAtUtc = stampTheEditorRead.AddMinutes(5),
                Members = { new ClientGroupMember { ClientIp = "10.0.0.1" } }
            };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using var context = new AppDbContext(options);
        var (controller, notifications) = CreateController(context);

        var result = await controller.SetMembersAsync(groupId, new SetMembersRequest
        {
            ClientIps = new List<string> { "10.0.0.2" },
            ExpectedUpdatedAtUtc = stampTheEditorRead
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);

        var body = Assert.IsType<ClientGroupChangedResponse>(conflict.Value);
        Assert.Equal(ClientGroupChangedResponse.ErrorCode, body.Error);

        // The group comes back as it now stands, so the caller can start again from the current
        // addresses without a second request.
        Assert.Equal(groupId, body.CurrentGroup.Id);
        Assert.Equal(new[] { "10.0.0.1" }, body.CurrentGroup.MemberIps);

        await using var assert = new AppDbContext(options);
        Assert.Equal(
            new[] { "10.0.0.1" },
            await assert.ClientGroupMembers.Where(m => m.ClientGroupId == groupId)
                .Select(m => m.ClientIp).ToListAsync());

        // Nothing happened, so nothing is announced.
        Assert.Empty(notifications.Invocations);
    }

    [Fact]
    public async Task AMembershipSaveCarryingTheCurrentStampGoesThroughAndMovesItAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;
        var stampTheEditorRead = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup
            {
                Nickname = "Lab",
                UpdatedAtUtc = stampTheEditorRead,
                Members = { new ClientGroupMember { ClientIp = "10.0.0.1" } }
            };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using var context = new AppDbContext(options);
        var (controller, _) = CreateController(context);

        var result = await controller.SetMembersAsync(groupId, new SetMembersRequest
        {
            ClientIps = new List<string> { "10.0.0.2" },
            ExpectedUpdatedAtUtc = stampTheEditorRead
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<SetMembersResponse>(ok.Value);
        Assert.Equal(new[] { "10.0.0.2" }, response.Group.MemberIps);

        // The stamp has to move with the save, or the editor that just lost the race still holds one
        // that reads as current and overwrites this on its next attempt.
        await using var assert = new AppDbContext(options);
        var saved = await assert.ClientGroups.SingleAsync(g => g.Id == groupId);
        Assert.NotNull(saved.UpdatedAtUtc);
        Assert.True(saved.UpdatedAtUtc > stampTheEditorRead);
    }

    [Fact]
    public async Task AMembershipSaveWithoutAStampGoesThroughAsync()
    {
        // Omitting the stamp is how a caller says it is not tracking the version, which is what keeps
        // clients written before the stamp existed working.
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup
            {
                Nickname = "Lab",
                UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Members = { new ClientGroupMember { ClientIp = "10.0.0.1" } }
            };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using var context = new AppDbContext(options);
        var (controller, _) = CreateController(context);

        var result = await controller.SetMembersAsync(groupId, new SetMembersRequest
        {
            ClientIps = new List<string> { "10.0.0.2" },
            ExpectedUpdatedAtUtc = null
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<SetMembersResponse>(ok.Value);
        Assert.Equal(new[] { "10.0.0.2" }, response.Group.MemberIps);
    }

    [Fact]
    public async Task AnEditSessionCannotEraseAChangeMadeBeforeItSavedItsFieldsAsync()
    {
        // The whole submit, in the order the dialog performs it: fields first, addresses second, and
        // the second one carries the stamp the first one handed back. Because the field write moves
        // the stamp, only a check on the FIELD write can still see what the other editor did.
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;
        var stampTheEditorRead = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup
            {
                Nickname = "Lab",
                UpdatedAtUtc = stampTheEditorRead,
                Members = { new ClientGroupMember { ClientIp = "10.0.0.1" } }
            };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        // Someone else adds an address after this editor opened the nickname and before it saves.
        await using (var elsewhere = new AppDbContext(options))
        {
            await CreateService(elsewhere).SetMembersAsync(
                groupId,
                new[] { "10.0.0.1", "10.0.0.2" });
        }

        await using var context = new AppDbContext(options);
        var (controller, _) = CreateController(context);

        var fieldsSaved = await controller.UpdateAsync(groupId, new UpdateClientGroupRequest
        {
            Nickname = "Workshop",
            SeparateMemberRows = false,
            ExpectedUpdatedAtUtc = stampTheEditorRead
        });

        // The dialog only reaches the address save once the field save is accepted, and it sends the
        // stamp that save returned. Driving that leg is what turns this into the real sequence: with
        // the field write unguarded it hands back a stamp that matches, and the address list this
        // editor built before the other change then replaces the whole membership.
        if (fieldsSaved is OkObjectResult accepted && accepted.Value is ClientGroupDto written)
        {
            await controller.SetMembersAsync(groupId, new SetMembersRequest
            {
                ClientIps = new List<string> { "10.0.0.1" },
                ExpectedUpdatedAtUtc = written.UpdatedAtUtc
            });
        }

        await using (var assert = new AppDbContext(options))
        {
            // Neither editor loses their work in silence: the other one's address is still there, and
            // this one's rename was not applied behind a response that reads as a success.
            Assert.Equal(
                new[] { "10.0.0.1", "10.0.0.2" },
                await assert.ClientGroupMembers.Where(m => m.ClientGroupId == groupId)
                    .OrderBy(m => m.ClientIp).Select(m => m.ClientIp).ToListAsync());

            Assert.Equal("Lab", (await assert.ClientGroups.SingleAsync(g => g.Id == groupId)).Nickname);
        }

        var conflict = Assert.IsType<ConflictObjectResult>(fieldsSaved);
        Assert.Equal(409, conflict.StatusCode);

        var body = Assert.IsType<ClientGroupChangedResponse>(conflict.Value);
        Assert.Equal(ClientGroupChangedResponse.ErrorCode, body.Error);

        // The group comes back as it now stands, so the editor can start again from it without a
        // second request.
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, body.CurrentGroup.MemberIps);
    }

    [Fact]
    public async Task AnEditSessionWithNoOneElseEditingSavesItsFieldsAndAddressesAsync()
    {
        // The same submit with nobody else involved has to land both writes. A guard that turns down
        // the ordinary save is worse than the race it was added for.
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;
        var stampTheEditorRead = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup
            {
                Nickname = "Lab",
                UpdatedAtUtc = stampTheEditorRead,
                Members = { new ClientGroupMember { ClientIp = "10.0.0.1" } }
            };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using var context = new AppDbContext(options);
        var (controller, _) = CreateController(context);

        var fieldsSaved = await controller.UpdateAsync(groupId, new UpdateClientGroupRequest
        {
            Nickname = "Workshop",
            SeparateMemberRows = true,
            ExpectedUpdatedAtUtc = stampTheEditorRead
        });

        var accepted = Assert.IsType<OkObjectResult>(fieldsSaved);
        var written = Assert.IsType<ClientGroupDto>(accepted.Value);

        // The stamp the field write hands back is the one the address save is checked against, so
        // this editor's own first write cannot turn down its own second one.
        var membersSaved = await controller.SetMembersAsync(groupId, new SetMembersRequest
        {
            ClientIps = new List<string> { "10.0.0.1", "10.0.0.2" },
            ExpectedUpdatedAtUtc = written.UpdatedAtUtc
        });

        var ok = Assert.IsType<OkObjectResult>(membersSaved);
        var response = Assert.IsType<SetMembersResponse>(ok.Value);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, response.Group.MemberIps);

        await using var assert = new AppDbContext(options);
        var saved = await assert.ClientGroups.SingleAsync(g => g.Id == groupId);
        Assert.Equal("Workshop", saved.Nickname);
        Assert.True(saved.SeparateMemberRows);
    }

    [Fact]
    public async Task TheStampAWriteHandsBackIsOneTheDatabaseCanReturnAsync()
    {
        // PostgreSQL keeps a timestamp to the microsecond while DateTime.UtcNow carries ticks a
        // hundred times finer, and the field write hands its stamp straight back to the caller
        // without re-reading the row. A stamp carrying those finer ticks can never match the stored
        // copy again, so the address save that follows it in the same submit would be turned down
        // and saving again would not clear it. A round trip cannot show that: the row reads back
        // truncated whether or not the write rounded, so the resolution is asserted on the stamp
        // the write handed back.
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using var context = new AppDbContext(options);
        var service = CreateService(context);

        var created = await service.CreateAsync(new ClientGroup { Nickname = "Lab" });
        Assert.Equal(0L, created.CreatedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(0L, created.UpdatedAtUtc!.Value.Ticks % TimeSpan.TicksPerMicrosecond);

        var updated = await service.UpdateAsync(new ClientGroup { Id = created.Id, Nickname = "Workshop" });
        Assert.Equal(0L, updated.UpdatedAtUtc!.Value.Ticks % TimeSpan.TicksPerMicrosecond);

        await service.SetMembersAsync(created.Id, new[] { "10.0.0.1" });
        var afterMembers = await service.GetByIdAsync(created.Id);
        Assert.Equal(0L, afterMembers!.UpdatedAtUtc!.Value.Ticks % TimeSpan.TicksPerMicrosecond);
    }

    [Fact]
    public async Task AnUpdateWithoutAStampGoesThroughAsync()
    {
        // Omitting the stamp is how a caller says it is not tracking the version, which is what keeps
        // clients written before the stamp existed working.
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long groupId;
        await using (var seed = new AppDbContext(options))
        {
            var group = new ClientGroup
            {
                Nickname = "Lab",
                UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            seed.ClientGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using var context = new AppDbContext(options);
        var (controller, _) = CreateController(context);

        var result = await controller.UpdateAsync(groupId, new UpdateClientGroupRequest
        {
            Nickname = "Workshop",
            SeparateMemberRows = false,
            ExpectedUpdatedAtUtc = null
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ClientGroupDto>(ok.Value);
        Assert.Equal("Workshop", response.Nickname);
    }

    [Fact]
    public async Task ACreateThatCouldNotTakeASingleAddressLeavesNoGroupBehindAsync()
    {
        // The group commits before the addresses are attempted. Keeping it would leave a nickname
        // holding nothing the user picked, and the modal has no way to save its way out of that.
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.ClientGroups.Add(new ClientGroup
            {
                Nickname = "Living Room",
                Members =
                {
                    new ClientGroupMember { ClientIp = "1.1.1.1" },
                    new ClientGroupMember { ClientIp = "1.1.1.2" }
                }
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new AppDbContext(options);
        var (controller, notifications) = CreateController(context);

        var result = await controller.CreateAsync(new CreateClientGroupRequest
        {
            Nickname = "Lab",
            InitialIps = new List<string> { "1.1.1.1", "1.1.1.2" }
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);

        var body = Assert.IsType<RejectedClientIpsResponse>(conflict.Value);
        Assert.Equal(RejectedClientIpsResponse.ErrorCode, body.Error);
        Assert.Equal(new[] { "1.1.1.1", "1.1.1.2" }, body.RejectedIps);

        await using var assert = new AppDbContext(options);
        Assert.Null(await assert.ClientGroups.FirstOrDefaultAsync(g => g.Nickname == "Lab"));

        // The group never reached anyone, so no client is told about one that no longer exists.
        Assert.Empty(notifications.Invocations);
    }

    [Fact]
    public async Task ACreateThatAsksForNoAddressesStillMakesTheGroupAsync()
    {
        // A nickname with no addresses yet is a legitimate starting point, and it is not the
        // all-rejected path.
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using var context = new AppDbContext(options);
        var (controller, _) = CreateController(context);

        var result = await controller.CreateAsync(new CreateClientGroupRequest { Nickname = "Lab" });

        var created = Assert.IsType<CreatedResult>(result);
        var response = Assert.IsType<CreateClientGroupResponse>(created.Value);
        Assert.Empty(response.MemberIps);
        Assert.Empty(response.RejectedIps);

        await using var assert = new AppDbContext(options);
        Assert.NotNull(await assert.ClientGroups.FirstOrDefaultAsync(g => g.Nickname == "Lab"));
    }

    [Fact]
    public void TheMembershipBroadcastExpiresEveryDashboardRange()
    {
        // A membership write restructures client rows at 24h and 7d as well as live, and only the
        // all-cache bump can evict those keys - the live generation is pinned to 0 for a fixed range.
        // If ClientGroupUpdated ever leaves this branch, historical dashboard data goes stale and
        // never self-heals.
        var source = ReadSource("Infrastructure", "Services", "SignalRNotificationService.cs");

        var branchStart = source.IndexOf("SignalREvents.ClientGroupUpdated", StringComparison.Ordinal);
        Assert.True(branchStart >= 0, "SignalRNotificationService no longer dispatches on ClientGroupUpdated");

        var branchEnd = source.IndexOf("else if", branchStart, StringComparison.Ordinal);
        Assert.True(branchEnd > branchStart, "ClientGroupUpdated is handled by the final branch; re-read the dispatch");

        Assert.Contains("InvalidateAllCache()", source[branchStart..branchEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCreateResponseCarriesEveryGroupField()
    {
        // CreateClientGroupResponse extends the group shape by hand, so a field added to the group
        // would otherwise quietly stop appearing on the create response.
        var source = ReadSource("Controllers", "Clients", "ClientGroupsController.cs");

        foreach (var property in typeof(ClientGroupDto).GetProperties())
        {
            Assert.Contains($"{property.Name} = dto.{property.Name}", source, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private static ClientGroupsService CreateService(AppDbContext context)
        => new(context, NullLogger<ClientGroupsService>.Instance);

    private static (ClientGroupsController Controller, RecordingNotificationsProxy Notifications) CreateController(
        AppDbContext context)
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        var dashboardBatchService = (IDashboardBatchService)DispatchProxy.Create<IDashboardBatchService, NullReturningProxy>();

        var controller = new ClientGroupsController(
            CreateService(context),
            notifications,
            NullLogger<ClientGroupsController>.Instance,
            dashboardBatchService);

        return (controller, (RecordingNotificationsProxy)(object)notifications);
    }

    private static string ReadSource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        var path = Path.Combine(new[] { root, "Api", "LancacheManager" }.Concat(pathSegments).ToArray());

        return File.ReadAllText(path);
    }

    private class RecordingNotificationsProxy : DispatchProxy
    {
        public List<(string Method, object?[] Args)> Invocations { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null)
            {
                Invocations.Add((targetMethod.Name, args ?? Array.Empty<object?>()));
            }

            var returnType = targetMethod?.ReturnType;

            if (returnType is null || returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }
}
