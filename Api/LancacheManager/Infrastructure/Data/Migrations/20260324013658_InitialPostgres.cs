using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BannedSteamUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BanReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BannedBySessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BannedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BannedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsLifted = table.Column<bool>(type: "boolean", nullable: false),
                    LiftedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LiftedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BannedSteamUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CachedCorruptionDetections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServiceName = table.Column<string>(type: "text", nullable: false),
                    CorruptedChunkCount = table.Column<long>(type: "bigint", nullable: false),
                    LastDetectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedCorruptionDetections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CachedGameDetections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GameAppId = table.Column<long>(type: "bigint", nullable: false),
                    GameName = table.Column<string>(type: "text", nullable: false),
                    CacheFilesFound = table.Column<int>(type: "integer", nullable: false),
                    TotalSizeBytes = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    DepotIdsJson = table.Column<string>(type: "text", nullable: false),
                    SampleUrlsJson = table.Column<string>(type: "text", nullable: false),
                    CacheFilePathsJson = table.Column<string>(type: "text", nullable: false),
                    DatasourcesJson = table.Column<string>(type: "text", nullable: false),
                    LastDetectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedGameDetections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CachedServiceDetections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServiceName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CacheFilesFound = table.Column<int>(type: "integer", nullable: false),
                    TotalSizeBytes = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    SampleUrlsJson = table.Column<string>(type: "text", nullable: false),
                    CacheFilePathsJson = table.Column<string>(type: "text", nullable: false),
                    DatasourcesJson = table.Column<string>(type: "text", nullable: false),
                    LastDetectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedServiceDetections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CacheSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedCacheSize = table.Column<long>(type: "bigint", nullable: false),
                    TotalCacheSize = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Nickname = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientStats",
                columns: table => new
                {
                    ClientIp = table.Column<string>(type: "text", nullable: false),
                    TotalCacheHitBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalCacheMissBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalDownloads = table.Column<int>(type: "integer", nullable: false),
                    TotalDurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActivityLocal = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientStats", x => x.ClientIp);
                });

            migrationBuilder.CreateTable(
                name: "Downloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Service = table.Column<string>(type: "text", nullable: false),
                    ClientIp = table.Column<string>(type: "text", nullable: false),
                    StartTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartTimeLocal = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTimeLocal = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CacheHitBytes = table.Column<long>(type: "bigint", nullable: false),
                    CacheMissBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    GameAppId = table.Column<long>(type: "bigint", nullable: true),
                    GameName = table.Column<string>(type: "text", nullable: true),
                    GameImageUrl = table.Column<string>(type: "text", nullable: true),
                    LastUrl = table.Column<string>(type: "text", nullable: true),
                    DepotId = table.Column<long>(type: "bigint", nullable: true),
                    EpicAppId = table.Column<string>(type: "text", nullable: true),
                    Datasource = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EpicCdnPatterns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CdnHost = table.Column<string>(type: "text", nullable: false),
                    ChunkBaseUrl = table.Column<string>(type: "text", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicCdnPatterns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EpicGameMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiscoveredByHash = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicGameMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    StartTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartTimeLocal = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTimeLocal = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ColorIndex = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrefillCachedDepots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    DepotId = table.Column<long>(type: "bigint", nullable: false),
                    ManifestId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    AppName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CachedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CachedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefillCachedDepots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrefillSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedBySessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ContainerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ContainerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SteamUsername = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Steam"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsAuthenticated = table.Column<bool>(type: "boolean", nullable: false),
                    IsPrefilling = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TerminationReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TerminatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefillSessions", x => x.Id);
                    table.UniqueConstraint("AK_PrefillSessions_SessionId", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "ServiceStats",
                columns: table => new
                {
                    Service = table.Column<string>(type: "text", nullable: false),
                    TotalCacheHitBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalCacheMissBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalDownloads = table.Column<int>(type: "integer", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActivityLocal = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceStats", x => x.Service);
                });

            migrationBuilder.CreateTable(
                name: "SteamDepotMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DepotId = table.Column<long>(type: "bigint", nullable: false),
                    DepotName = table.Column<string>(type: "text", nullable: true),
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    AppName = table.Column<string>(type: "text", nullable: true),
                    IsOwner = table.Column<bool>(type: "boolean", nullable: false),
                    DiscoveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteamDepotMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionTokenHash = table.Column<string>(type: "text", nullable: false),
                    SessionType = table.Column<string>(type: "text", nullable: false),
                    IpAddress = table.Column<string>(type: "text", nullable: false),
                    UserAgent = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SteamPrefillExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EpicPrefillExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PreviousSessionTokenHash = table.Column<string>(type: "text", nullable: true),
                    PreviousTokenValidUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientGroupMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientGroupId = table.Column<int>(type: "integer", nullable: false),
                    ClientIp = table.Column<string>(type: "text", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientGroupMembers_ClientGroups_ClientGroupId",
                        column: x => x.ClientGroupId,
                        principalTable: "ClientGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LogEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientIp = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Service = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    BytesServed = table.Column<long>(type: "bigint", nullable: false),
                    CacheStatus = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DepotId = table.Column<long>(type: "bigint", nullable: true),
                    Datasource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DownloadId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogEntries_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EventDownloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    DownloadId = table.Column<int>(type: "integer", nullable: false),
                    TaggedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AutoTagged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventDownloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventDownloads_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EventDownloads_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrefillHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AppId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AppName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BytesDownloaded = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefillHistoryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrefillHistoryEntries_PrefillSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "PrefillSessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedTheme = table.Column<string>(type: "text", nullable: true),
                    SharpCorners = table.Column<bool>(type: "boolean", nullable: false),
                    DisableFocusOutlines = table.Column<bool>(type: "boolean", nullable: false),
                    DisableTooltips = table.Column<bool>(type: "boolean", nullable: false),
                    PicsAlwaysVisible = table.Column<bool>(type: "boolean", nullable: false),
                    DisableStickyNotifications = table.Column<bool>(type: "boolean", nullable: false),
                    UseLocalTimezone = table.Column<bool>(type: "boolean", nullable: false),
                    Use24HourFormat = table.Column<bool>(type: "boolean", nullable: false),
                    ShowDatasourceLabels = table.Column<bool>(type: "boolean", nullable: false),
                    ShowYearInDates = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedTimeFormats = table.Column<string>(type: "text", nullable: true),
                    RefreshRate = table.Column<string>(type: "text", nullable: true),
                    RefreshRateLocked = table.Column<bool>(type: "boolean", nullable: true),
                    SteamMaxThreadCount = table.Column<int>(type: "integer", nullable: true),
                    EpicMaxThreadCount = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPreferences_UserSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "UserSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BannedSteamUsers_BannedAtUtc",
                table: "BannedSteamUsers",
                column: "BannedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BannedSteamUsers_IsLifted",
                table: "BannedSteamUsers",
                column: "IsLifted");

            migrationBuilder.CreateIndex(
                name: "IX_BannedSteamUsers_Username",
                table: "BannedSteamUsers",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_CachedGameDetection_GameAppId",
                table: "CachedGameDetections",
                column: "GameAppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CachedGameDetection_LastDetectedUtc",
                table: "CachedGameDetections",
                column: "LastDetectedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CachedServiceDetection_LastDetectedUtc",
                table: "CachedServiceDetections",
                column: "LastDetectedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CachedServiceDetection_ServiceName",
                table: "CachedServiceDetections",
                column: "ServiceName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheSnapshots_TimestampUtc",
                table: "CacheSnapshots",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClientGroupMembers_ClientGroupId",
                table: "ClientGroupMembers",
                column: "ClientGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientGroupMembers_ClientIp",
                table: "ClientGroupMembers",
                column: "ClientIp",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientGroups_Nickname",
                table: "ClientGroups",
                column: "Nickname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientStats_LastActivityUtc",
                table: "ClientStats",
                column: "LastActivityUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_Client_Service_Active",
                table: "Downloads",
                columns: new[] { "ClientIp", "Service", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_Datasource",
                table: "Downloads",
                column: "Datasource");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_DepotId",
                table: "Downloads",
                column: "DepotId");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_EndTime",
                table: "Downloads",
                column: "EndTimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_EpicAppId",
                table: "Downloads",
                column: "EpicAppId");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_IsActive",
                table: "Downloads",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_StartTime",
                table: "Downloads",
                column: "StartTimeUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_EpicCdnPatterns_AppId",
                table: "EpicCdnPatterns",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_EpicCdnPatterns_ChunkBaseUrl",
                table: "EpicCdnPatterns",
                column: "ChunkBaseUrl",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpicGameMappings_AppId",
                table: "EpicGameMappings",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpicGameMappings_DiscoveredAtUtc",
                table: "EpicGameMappings",
                column: "DiscoveredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EpicGameMappings_Name",
                table: "EpicGameMappings",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_EventDownloads_DownloadId",
                table: "EventDownloads",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_EventDownloads_EventId_DownloadId",
                table: "EventDownloads",
                columns: new[] { "EventId", "DownloadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventDownloads_TaggedAtUtc",
                table: "EventDownloads",
                column: "TaggedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Events_EndTimeUtc",
                table: "Events",
                column: "EndTimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Events_StartTimeUtc",
                table: "Events",
                column: "StartTimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Client_Service",
                table: "LogEntries",
                columns: new[] { "ClientIp", "Service" });

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Datasource",
                table: "LogEntries",
                column: "Datasource");

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_DownloadId",
                table: "LogEntries",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_DuplicateCheck",
                table: "LogEntries",
                columns: new[] { "ClientIp", "Service", "Timestamp", "Url", "BytesServed" });

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Timestamp",
                table: "LogEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillCachedDepots_AppId",
                table: "PrefillCachedDepots",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillCachedDepots_DepotId_ManifestId",
                table: "PrefillCachedDepots",
                columns: new[] { "DepotId", "ManifestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrefillHistoryEntries_AppId",
                table: "PrefillHistoryEntries",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillHistoryEntries_SessionId",
                table: "PrefillHistoryEntries",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillHistoryEntries_StartedAtUtc",
                table: "PrefillHistoryEntries",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_ContainerId",
                table: "PrefillSessions",
                column: "ContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_CreatedAtUtc",
                table: "PrefillSessions",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_CreatedBySessionId",
                table: "PrefillSessions",
                column: "CreatedBySessionId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_Platform",
                table: "PrefillSessions",
                column: "Platform");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_SessionId",
                table: "PrefillSessions",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_Status",
                table: "PrefillSessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_SteamUsername",
                table: "PrefillSessions",
                column: "SteamUsername");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceStats_LastActivityUtc",
                table: "ServiceStats",
                column: "LastActivityUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_AppId",
                table: "SteamDepotMappings",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_DepotId",
                table: "SteamDepotMappings",
                column: "DepotId");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_DepotId_AppId",
                table: "SteamDepotMappings",
                columns: new[] { "DepotId", "AppId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_SessionId",
                table: "UserPreferences",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ExpiresAtUtc",
                table: "UserSessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_IsRevoked",
                table: "UserSessions",
                column: "IsRevoked");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_SessionTokenHash",
                table: "UserSessions",
                column: "SessionTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_SessionType",
                table: "UserSessions",
                column: "SessionType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BannedSteamUsers");

            migrationBuilder.DropTable(
                name: "CachedCorruptionDetections");

            migrationBuilder.DropTable(
                name: "CachedGameDetections");

            migrationBuilder.DropTable(
                name: "CachedServiceDetections");

            migrationBuilder.DropTable(
                name: "CacheSnapshots");

            migrationBuilder.DropTable(
                name: "ClientGroupMembers");

            migrationBuilder.DropTable(
                name: "ClientStats");

            migrationBuilder.DropTable(
                name: "EpicCdnPatterns");

            migrationBuilder.DropTable(
                name: "EpicGameMappings");

            migrationBuilder.DropTable(
                name: "EventDownloads");

            migrationBuilder.DropTable(
                name: "LogEntries");

            migrationBuilder.DropTable(
                name: "PrefillCachedDepots");

            migrationBuilder.DropTable(
                name: "PrefillHistoryEntries");

            migrationBuilder.DropTable(
                name: "ServiceStats");

            migrationBuilder.DropTable(
                name: "SteamDepotMappings");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropTable(
                name: "ClientGroups");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Downloads");

            migrationBuilder.DropTable(
                name: "PrefillSessions");

            migrationBuilder.DropTable(
                name: "UserSessions");
        }
    }
}
