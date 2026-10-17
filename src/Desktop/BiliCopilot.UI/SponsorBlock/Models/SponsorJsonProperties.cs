using System.Text.Json;

namespace BiliCopilot.UI.SponsorBlock.Models;

internal static class SponsorJsonProperties
{
    internal static bool AreUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !AreUnique(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!AreUnique(item)) return false;
        return true;
    }
}
