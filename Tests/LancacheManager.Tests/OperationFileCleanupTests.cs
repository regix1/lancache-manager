using LancacheManager.Infrastructure.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class OperationFileCleanupTests
{
    [Fact]
    public async Task CleanupOperationFilesPreservesResumeStateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"operation-cleanup-{Guid.NewGuid():N}");
        var paths = new CleanupPathResolver(root);
        var operations = paths.GetOperationsDirectory();
        var resumePath = Path.Combine(operations, "rust_resume_alpha.json");
        var oldOperationPath = Path.Combine(operations, "rust_progress_alpha.json");
        var nearMatchPath = Path.Combine(operations, "old_rust_resume_alpha.json");
        var freshOperationPath = Path.Combine(operations, "operation_fresh.json");
        var resumeBytes = new byte[] { 0x7b, 0x22, 0x70, 0x22, 0x3a, 0x31, 0x7d };

        try
        {
            await File.WriteAllBytesAsync(resumePath, resumeBytes);
            await File.WriteAllTextAsync(oldOperationPath, "{}");
            await File.WriteAllTextAsync(nearMatchPath, "{}");
            await File.WriteAllTextAsync(freshOperationPath, "{}");

            var oldWriteTime = DateTime.UtcNow.AddDays(-7);
            File.SetLastWriteTimeUtc(resumePath, oldWriteTime);
            File.SetLastWriteTimeUtc(oldOperationPath, oldWriteTime);
            File.SetLastWriteTimeUtc(nearMatchPath, oldWriteTime);

            var deletedCount = paths.CleanupOperationFiles();

            Assert.Equal(2, deletedCount);
            Assert.Equal(resumeBytes, await File.ReadAllBytesAsync(resumePath));
            Assert.False(File.Exists(oldOperationPath));
            Assert.False(File.Exists(nearMatchPath));
            Assert.True(File.Exists(freshOperationPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CleanupPathResolver : PathResolverBase
    {
        public CleanupPathResolver(string root)
            : base(NullLogger.Instance)
        {
            BasePath = root;
        }

        protected override string BasePath { get; }
        protected override string RustExecutableExtension => OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        public override string ResolvePath(string relativePath) =>
            Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(BasePath, relativePath);

        public override string NormalizePath(string path) => path;

        public override bool IsDockerSocketAvailable() => false;
    }
}
