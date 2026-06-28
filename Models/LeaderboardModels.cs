using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

public class LeaderboardUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("photoUrl")]
    public string? PhotoUrl { get; set; }

    [JsonPropertyName("totalPoints")]
    public int TotalPoints { get; set; }

    [JsonPropertyName("rank")]
    public int Rank { get; set; }
}

public class LeaderboardResponse
{
    [JsonPropertyName("users")]
    public List<LeaderboardUser> Users { get; set; } = new();

    [JsonPropertyName("currentUser")]
    public LeaderboardUser? CurrentUser { get; set; }
}
