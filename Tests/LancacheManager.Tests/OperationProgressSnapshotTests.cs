using System.Text.Json;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public class OperationProgressSnapshotTests
{
    [Fact]
    public void CreateDefensivelyCopiesContextAndHostEnrichment()
    {
        var mutable = new Dictionary<string, object?> { ["count"] = 4 };
        var host = new Dictionary<string, object?> { ["datasourceName"] = "primary" };
        var snapshot = OperationProgressSnapshot.Create("stage", 150, mutable, 1, host);

        mutable["count"] = 99;
        host["datasourceName"] = "changed";

        Assert.Equal(100, snapshot.PercentComplete);
        Assert.Equal(4, snapshot.Context["count"]);
        Assert.Equal("primary", snapshot.Context["datasourceName"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, object?>)snapshot.Context).Add("new", 1));
    }

    [Fact]
    public void JsonElementsSurviveSourceDocumentDisposal()
    {
        OperationProgressSnapshot snapshot;
        using (var document = JsonDocument.Parse("{\"processed\":12}"))
        {
            snapshot = OperationProgressSnapshot.Create(
                "stage",
                10,
                new Dictionary<string, object?> { ["payload"] = document.RootElement },
                1);
        }

        var payload = Assert.IsType<JsonElement>(snapshot.Context["payload"]);
        Assert.Equal(12, payload.GetProperty("processed").GetInt32());
    }
}
