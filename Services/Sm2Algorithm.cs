namespace Milingo.Backend.Services;

public record SrsResult(
    int Repetitions,
    double EasinessFactor,
    int IntervalDays,
    string State,
    DateTime NextReviewAt);

public static class Sm2Algorithm
{
    public static SrsResult Calculate(
        int repetitions,
        double easinessFactor,
        int intervalDays,
        int quality)
    {
        quality = Math.Clamp(quality, 0, 5);

        int newRepetitions;
        int newInterval;

        if (quality < 3)
        {
            newRepetitions = 0;
            newInterval = 1;
        }
        else
        {
            newRepetitions = repetitions + 1;
            newInterval = newRepetitions switch
            {
                1 => 1,
                2 => 6,
                _ => (int)Math.Round(intervalDays * easinessFactor)
            };
        }

        var newEasinessFactor = easinessFactor
            + (0.1 - (5 - quality) * (0.08 + (5 - quality) * 0.02));
        newEasinessFactor = Math.Max(1.3, newEasinessFactor);

        var state = (newRepetitions, newInterval) switch
        {
            (0, _) => "learning",
            (1 or 2, _) => "learning",
            (_, >= 21) => "mastered",
            _ => "review"
        };

        return new SrsResult(
            newRepetitions,
            newEasinessFactor,
            newInterval,
            state,
            DateTime.UtcNow.AddDays(newInterval));
    }

    public static SrsResult CalculateForMcq(
        int repetitions,
        double easinessFactor,
        int intervalDays,
        bool isCorrect)
        => Calculate(repetitions, easinessFactor, intervalDays, isCorrect ? 4 : 1);
}
