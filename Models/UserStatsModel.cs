namespace Milingo.Backend.Models;

/// <summary>
/// Response trả về stats tổng hợp của user: coins, streak, totalPoints.
/// </summary>
public class UserStatsResponse
{
    public int Coins { get; set; }
    public int CurrentStreak { get; set; }
    public int TotalPoints { get; set; }
    public string? LastStudyDate { get; set; } // ISO 8601, nullable
}

/// <summary>
/// Request body để ghi nhận user học flashcard hôm nay.
/// </summary>
public class RecordStudyRequest
{
    // Không cần body — server tự lấy ngày từ server time.
    // Giữ class để dễ mở rộng sau.
}