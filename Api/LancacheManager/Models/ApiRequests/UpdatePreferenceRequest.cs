using System.Text.Json;

namespace LancacheManager.Models.ApiRequests;

public class UpdatePreferenceRequest
{
    public PreferenceKey Key { get; set; }
    public JsonElement Value { get; set; }
}
