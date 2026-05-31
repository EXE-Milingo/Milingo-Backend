# Milingo — Unified Study Feature: Flashcard + MCQ with Spaced Repetition

## Table of Contents
1. [Architecture overview](#1-architecture-overview)
2. [Firestore changes](#2-firestore-changes)
3. [The SM-2 algorithm](#3-the-sm-2-algorithm)
4. [New models](#4-new-models)
5. [Updated IOpenAiService — distractor generation](#5-updated-iopenaiservice)
6. [Updated OpenAiService — distractor generation implementation](#6-updated-openaiservice)
7. [Updated IFirestoreService](#7-updated-ifirestoreservice)
8. [Updated FirestoreService implementation](#8-updated-firestoreservice-implementation)
9. [StudyController](#9-studycontroller)
10. [Flutter integration summary](#10-flutter-integration-summary)
11. [Files to create / modify](#11-files-to-create--modify)

---

## 1. Architecture overview

The core insight: **SRS is the engine. Flashcard and MCQ are just two skins on the same card.**

Every card has one SRS schedule. The `srs_state` field on the card determines which skin Flutter shows:

```
NEW ──────► LEARNING ──────► REVIEW ──────► MASTERED
 (Flashcard)  (Flashcard)     (MCQ)          (MCQ)
```

Both modes feed answers into the same SM-2 calculation. The only difference is how quality is determined:

| Mode       | Quality source                                              |
|------------|-------------------------------------------------------------|
| Flashcard  | User self-rates: Again=0 / Hard=2 / Good=4 / Easy=5        |
| MCQ        | App decides: wrong=1 / correct=4                            |

This also **solves the "only 1–3 words" problem** naturally. New and learning cards show as flashcards — no distractors needed. MCQ only activates once a card reaches `review` state, by which time the user has almost certainly saved more cards and real distractors are available.

---

## 2. Firestore changes

### Short answer: NO new collections. Two changes only.

#### Change A — Add SRS fields to existing card documents

Add these fields to `users/{uid}/flashcard_decks/{deckId}/cards/{cardId}`.

**Do not set them at card-creation time.** Leave them absent from new cards. The study endpoints initialise them lazily on first answer, which means zero migration work on existing cards.

```
srs_state            : string    "new" | "learning" | "review" | "mastered"
srs_easiness_factor  : double    default 2.5
srs_interval         : int       default 0  (days until next review)
srs_repetitions      : int       default 0  (consecutive correct answers)
srs_next_review_at   : Timestamp null = due immediately
srs_last_reviewed_at : Timestamp null initially
srs_distractors      : string[]  up to 3 cached AI-generated wrong-answer terms
```

The `srs_distractors` field caches the AI-generated wrong-answer options so OpenAI is only called **once per card ever**, not once per session.

#### Change B — Add one Firestore composite index

In the Firebase Console → Firestore → Indexes, add:

```
Collection group : cards
Fields           : srs_next_review_at ASC, created_at ASC
Query scope      : Collection
```

This makes the "fetch due cards" query efficient. Without it Firestore will warn you and the query will do a full scan.

---

## 3. The SM-2 algorithm

Create `Services/Sm2Algorithm.cs`:

```csharp
namespace Milingo.Backend.Services;

/// <summary>
/// Result of one SM-2 calculation. Passed directly to Firestore update.
/// </summary>
public record SrsResult(
    int Repetitions,
    double EasinessFactor,
    int IntervalDays,
    string State,
    DateTime NextReviewAt
);

/// <summary>
/// SM-2 spaced repetition algorithm.
///
/// Quality scale (matches Anki's grading buttons):
///   0 = Again  — complete blackout / wrong MCQ answer
///   1 = Again  — wrong (used internally for MCQ incorrect)
///   2 = Hard   — correct with significant difficulty
///   3 = Good   — correct with some hesitation (not used in UI, here for completeness)
///   4 = Good   — correct (used for MCQ correct, default flashcard "Good")
///   5 = Easy   — perfect recall, effortless
/// </summary>
public static class Sm2Algorithm
{
    public static SrsResult Calculate(
        int repetitions,
        double easinessFactor,
        int intervalDays,
        int quality)  // 0–5
    {
        // Clamp input
        quality = Math.Clamp(quality, 0, 5);

        int newRepetitions;
        int newInterval;

        if (quality < 3)
        {
            // Wrong / very hard: reset to beginning
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

        // Update easiness factor — formula from original SM-2 paper
        double newEF = easinessFactor
            + (0.1 - (5 - quality) * (0.08 + (5 - quality) * 0.02));
        newEF = Math.Max(1.3, newEF); // hard floor at 1.3

        string state = (newRepetitions, newInterval) switch
        {
            (0, _)       => quality < 3 ? "learning" : "new",
            (1 or 2, _)  => "learning",
            (_, >= 21)   => "mastered",
            _            => "review"
        };

        return new SrsResult(
            Repetitions: newRepetitions,
            EasinessFactor: newEF,
            IntervalDays: newInterval,
            State: state,
            NextReviewAt: DateTime.UtcNow.AddDays(newInterval)
        );
    }

    /// <summary>
    /// Convenience wrapper for MCQ mode. Converts bool to quality int.
    /// </summary>
    public static SrsResult CalculateForMcq(
        int repetitions,
        double easinessFactor,
        int intervalDays,
        bool isCorrect)
        => Calculate(repetitions, easinessFactor, intervalDays, isCorrect ? 4 : 1);
}
```

### Interval progression example

| Correct streak | Interval    | State    |
|---------------|-------------|----------|
| 0 (wrong)     | 1 day       | learning |
| 1             | 1 day       | learning |
| 2             | 6 days      | review   |
| 3             | ~15 days    | review   |
| 4             | ~38 days    | mastered |
| Wrong at any point | 1 day  | learning |

---

## 4. New models

Create `Models/StudyModels.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Milingo.Backend.Models;

// =================================================================
//  SESSION — what the server sends to Flutter
// =================================================================

/// <summary>
/// One card in a study session. Flutter reads suggested_mode and
/// renders either a flashcard widget or an MCQ widget.
/// </summary>
public class StudyCard
{
    [JsonPropertyName("card_id")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("deck_id")]
    public string DeckId { get; set; } = string.Empty;

    // ── Card content ──────────────────────────────────────────────
    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;

    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    [JsonPropertyName("example_sentence")]
    public string ExampleSentence { get; set; } = string.Empty;

    /// <summary>
    /// Image from the user's snap. Null for manually-added cards.
    /// Flutter shows the image for MCQ ("what is this?") and as
    /// the front of the flashcard.
    /// </summary>
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    // ── SRS state ─────────────────────────────────────────────────
    [JsonPropertyName("srs_state")]
    public string SrsState { get; set; } = "new";

    [JsonPropertyName("srs_repetitions")]
    public int SrsRepetitions { get; set; }

    [JsonPropertyName("srs_interval_days")]
    public int SrsIntervalDays { get; set; }

    // ── Mode hint ─────────────────────────────────────────────────
    /// <summary>
    /// "flashcard" or "mcq". Flutter should respect this but may
    /// let the user override (e.g. a "Show all as flashcard" toggle).
    /// </summary>
    [JsonPropertyName("suggested_mode")]
    public string SuggestedMode { get; set; } = "flashcard";

    // ── MCQ options (null when suggested_mode == "flashcard") ─────
    /// <summary>
    /// Four shuffled options. Only populated when suggested_mode == "mcq".
    /// Null means Flutter should render a flashcard instead.
    /// </summary>
    [JsonPropertyName("options")]
    public List<StudyOption>? Options { get; set; }
}

public class StudyOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;  // card_id or "ai_{n}"

    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;

    [JsonPropertyName("is_correct")]
    public bool IsCorrect { get; set; }
}

public class StudySessionResponse
{
    [JsonPropertyName("deck_id")]
    public string DeckId { get; set; } = string.Empty;

    [JsonPropertyName("deck_name")]
    public string DeckName { get; set; } = string.Empty;

    [JsonPropertyName("cards")]
    public List<StudyCard> Cards { get; set; } = new();

    /// <summary>Total cards due right now (may be more than the returned batch).</summary>
    [JsonPropertyName("total_due")]
    public int TotalDue { get; set; }

    /// <summary>How many of the returned cards are flashcard mode.</summary>
    [JsonPropertyName("flashcard_count")]
    public int FlashcardCount { get; set; }

    /// <summary>How many of the returned cards are MCQ mode.</summary>
    [JsonPropertyName("mcq_count")]
    public int McqCount { get; set; }
}

// =================================================================
//  ANSWER — what Flutter sends back after the user responds
// =================================================================

/// <summary>
/// Unified answer request for both flashcard and MCQ modes.
///
/// For FLASHCARD mode:
///   Set quality to one of: 0 (Again), 2 (Hard), 4 (Good), 5 (Easy)
///   Leave is_correct null.
///
/// For MCQ mode:
///   Set is_correct to true or false.
///   Leave quality null — the server maps correct→4, wrong→1 automatically.
///
/// You must set mode to either "flashcard" or "mcq".
/// </summary>
public class SubmitStudyAnswerRequest
{
    [JsonPropertyName("card_id")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("deck_id")]
    public string DeckId { get; set; } = string.Empty;

    /// <summary>"flashcard" or "mcq"</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    /// <summary>
    /// For flashcard mode: 0=Again, 2=Hard, 4=Good, 5=Easy.
    /// Null when mode == "mcq".
    /// </summary>
    [JsonPropertyName("quality")]
    public int? Quality { get; set; }

    /// <summary>
    /// For MCQ mode: did the user pick the right answer?
    /// Null when mode == "flashcard".
    /// </summary>
    [JsonPropertyName("is_correct")]
    public bool? IsCorrect { get; set; }
}

public class SubmitStudyAnswerResponse
{
    [JsonPropertyName("card_id")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("quality_applied")]
    public int QualityApplied { get; set; }  // the 0–5 value SM-2 actually received

    [JsonPropertyName("new_srs_state")]
    public string NewSrsState { get; set; } = string.Empty;

    [JsonPropertyName("new_interval_days")]
    public int NewIntervalDays { get; set; }

    [JsonPropertyName("next_review_at")]
    public string NextReviewAt { get; set; } = string.Empty;  // ISO 8601 UTC

    [JsonPropertyName("coins_awarded")]
    public int CoinsAwarded { get; set; }
}

// =================================================================
//  STATS — deck-level SRS overview
// =================================================================

public class DeckStudyStats
{
    [JsonPropertyName("deck_id")]
    public string DeckId { get; set; } = string.Empty;

    [JsonPropertyName("new_count")]
    public int NewCount { get; set; }

    [JsonPropertyName("learning_count")]
    public int LearningCount { get; set; }

    [JsonPropertyName("review_count")]
    public int ReviewCount { get; set; }

    [JsonPropertyName("mastered_count")]
    public int MasteredCount { get; set; }

    [JsonPropertyName("due_today")]
    public int DueToday { get; set; }

    [JsonPropertyName("total_cards")]
    public int TotalCards { get; set; }
}
```

---

## 5. Updated IOpenAiService

Add one new method to `Services/IOpenAiService.cs`:

```csharp
/// <summary>
/// Generates 3 plausible but incorrect distractor terms for a given vocabulary word.
/// Used to build MCQ options when the user doesn't have enough saved cards.
/// Results should be cached on the card document (srs_distractors field).
/// </summary>
/// <param name="term">The correct vocabulary term (e.g. "laptop").</param>
/// <param name="translation">Its translation, for context.</param>
/// <param name="targetLanguage">Full language name, e.g. "Vietnamese".</param>
Task<List<string>> GenerateDistractorsAsync(
    string term,
    string translation,
    string targetLanguage,
    CancellationToken cancellationToken = default);
```

---

## 6. Updated OpenAiService

Add this implementation to `Services/OpenAiService.cs`.
It follows the same `CallOpenAiAsync` pattern already used for image analysis, but uses a text-only prompt:

```csharp
/// <inheritdoc />
public async Task<List<string>> GenerateDistractorsAsync(
    string term,
    string translation,
    string targetLanguage,
    CancellationToken cancellationToken = default)
{
    var prompt = $"""
        You are a language learning assistant.
        The student is learning {targetLanguage}.
        The correct vocabulary word is: "{term}" (meaning: "{translation}").

        Generate exactly 3 plausible but INCORRECT alternative terms in {targetLanguage}
        that a student might confuse with "{term}".
        Choose words from a similar category or everyday context.

        Respond ONLY with a JSON array of 3 strings. No explanation. No markdown. Example:
        ["tablet", "keyboard", "monitor"]
        """;

    var requestBody = new
    {
        model = _model,
        input = prompt
    };

    var json = JsonSerializer.Serialize(requestBody);
    using var content = new StringContent(json, Encoding.UTF8, "application/json");

    using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesApiUrl);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    request.Content = content;

    HttpResponseMessage response;
    try
    {
        response = await _httpClient.SendAsync(request, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        _logger.LogWarning("GenerateDistractors cancelled for term '{Term}'.", term);
        return new List<string>();
    }
    catch (TaskCanceledException)
    {
        _logger.LogWarning("GenerateDistractors timed out for term '{Term}'.", term);
        return new List<string>();
    }

    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("OpenAI distractor generation failed for '{Term}': {Body}", term, body);
        return new List<string>();
    }

    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

    try
    {
        using var doc = JsonDocument.Parse(responseBody);
        // Extract text from the Responses API output array
        var outputText = doc.RootElement
            .GetProperty("output")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "[]";

        var distractors = JsonSerializer.Deserialize<List<string>>(outputText) ?? new();
        return distractors.Take(3).ToList();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to parse distractor response for term '{Term}': {Body}", term, responseBody);
        return new List<string>();
    }
}
```

---

## 7. Updated IFirestoreService

Add these methods to `Services/IFirestoreService.cs`:

```csharp
// =================================================================
//  STUDY (FLASHCARD + MCQ + SRS)
// =================================================================

/// <summary>
/// Returns up to <paramref name="limit"/> cards due for study in a deck.
/// "Due" means: srs_next_review_at is in the past, OR the card has no SRS
/// data yet (new card). Results are ordered: new cards first, then by
/// srs_next_review_at ascending (most overdue first).
/// </summary>
Task<List<CardResponse>> GetDueCardsAsync(
    string userId,
    string deckId,
    int limit = 20,
    CancellationToken cancellationToken = default);

/// <summary>
/// Returns up to <paramref name="count"/> cards NOT in excludeCardIds.
/// Used to build MCQ distractors from the user's own vocabulary.
/// Falls back to other decks if the current deck doesn't have enough.
/// </summary>
Task<List<CardResponse>> GetDistractorCardsAsync(
    string userId,
    string deckId,
    IEnumerable<string> excludeCardIds,
    int count = 3,
    CancellationToken cancellationToken = default);

/// <summary>
/// Applies SM-2 to a card and updates its SRS fields. Awards coins.
/// quality: 0=Again, 2=Hard, 4=Good/Correct, 5=Easy  (1 used internally for MCQ wrong)
/// </summary>
Task<SubmitStudyAnswerResponse> UpdateCardSrsAsync(
    string userId,
    string deckId,
    string cardId,
    int quality,
    string mode,
    CancellationToken cancellationToken = default);

/// <summary>
/// Saves AI-generated distractor terms onto the card document so
/// they can be reused without calling OpenAI again.
/// </summary>
Task CacheDistractorsAsync(
    string userId,
    string deckId,
    string cardId,
    List<string> distractors,
    CancellationToken cancellationToken = default);

/// <summary>
/// Returns SRS state counts for all cards in a deck (for the deck overview UI).
/// </summary>
Task<DeckStudyStats> GetDeckStudyStatsAsync(
    string userId,
    string deckId,
    CancellationToken cancellationToken = default);
```

---

## 8. Updated FirestoreService implementation

Add these methods to `Services/FirestoreService.cs`:

```csharp
// =================================================================
//  STUDY (FLASHCARD + MCQ + SRS)
// =================================================================

public async Task<List<CardResponse>> GetDueCardsAsync(
    string userId,
    string deckId,
    int limit = 20,
    CancellationToken cancellationToken = default)
{
    var cardsRef = _db.Collection("users").Document(userId)
        .Collection("flashcard_decks").Document(deckId)
        .Collection("cards");

    var now = Timestamp.FromDateTime(DateTime.UtcNow);
    var result = new List<CardResponse>();
    var seenIds = new HashSet<string>();

    // 1. Cards with no SRS fields at all (never studied — legacy or freshly added)
    var brandNewSnapshot = await cardsRef
        .WhereLessThanOrEqualTo("created_at", now)   // all cards
        .Limit(limit * 2)
        .GetSnapshotAsync(cancellationToken);

    foreach (var doc in brandNewSnapshot.Documents)
    {
        // Only include cards that have never been assigned an SRS state
        if (!doc.ContainsField("srs_state") && seenIds.Add(doc.Id))
        {
            var card = MapCardDocument(doc);
            if (card != null) result.Add(card);
        }
    }

    // 2. Cards explicitly marked "new" (have srs_state but haven't been answered yet)
    var newStateSnapshot = await cardsRef
        .WhereEqualTo("srs_state", "new")
        .Limit(limit)
        .GetSnapshotAsync(cancellationToken);

    foreach (var doc in newStateSnapshot.Documents)
    {
        if (seenIds.Add(doc.Id))
        {
            var card = MapCardDocument(doc);
            if (card != null) result.Add(card);
        }
    }

    // 3. Cards whose review date has passed (learning/review/mastered)
    var dueSnapshot = await cardsRef
        .WhereLessThanOrEqualTo("srs_next_review_at", now)
        .OrderBy("srs_next_review_at")
        .Limit(limit)
        .GetSnapshotAsync(cancellationToken);

    foreach (var doc in dueSnapshot.Documents)
    {
        if (seenIds.Add(doc.Id))
        {
            var card = MapCardDocument(doc);
            if (card != null) result.Add(card);
        }
    }

    // Sort: new cards first (null next_review), then by overdue-ness
    return result.Take(limit).ToList();
}

public async Task<List<CardResponse>> GetDistractorCardsAsync(
    string userId,
    string deckId,
    IEnumerable<string> excludeCardIds,
    int count = 3,
    CancellationToken cancellationToken = default)
{
    var exclude = new HashSet<string>(excludeCardIds);
    var result = new List<CardResponse>();

    // Try the current deck first
    var deckCardsSnap = await _db.Collection("users").Document(userId)
        .Collection("flashcard_decks").Document(deckId)
        .Collection("cards")
        .Limit(50)
        .GetSnapshotAsync(cancellationToken);

    var sameDeckCandidates = deckCardsSnap.Documents
        .Select(MapCardDocument)
        .Where(c => c != null && !exclude.Contains(c.Id))
        .Cast<CardResponse>()
        .OrderBy(_ => Guid.NewGuid())
        .Take(count)
        .ToList();

    result.AddRange(sameDeckCandidates);

    // If still short, pull from other decks
    if (result.Count < count)
    {
        var allDecksSnap = await _db.Collection("users").Document(userId)
            .Collection("flashcard_decks")
            .GetSnapshotAsync(cancellationToken);

        foreach (var deck in allDecksSnap.Documents.OrderBy(_ => Guid.NewGuid()))
        {
            if (deck.Id == deckId || result.Count >= count) continue;

            var otherSnap = await _db.Collection("users").Document(userId)
                .Collection("flashcard_decks").Document(deck.Id)
                .Collection("cards")
                .Limit(20)
                .GetSnapshotAsync(cancellationToken);

            var others = otherSnap.Documents
                .Select(MapCardDocument)
                .Where(c => c != null && !exclude.Contains(c.Id))
                .Cast<CardResponse>()
                .OrderBy(_ => Guid.NewGuid())
                .Take(count - result.Count);

            result.AddRange(others);
        }
    }

    return result.Take(count).ToList();
}

public async Task<SubmitStudyAnswerResponse> UpdateCardSrsAsync(
    string userId,
    string deckId,
    string cardId,
    int quality,
    string mode,
    CancellationToken cancellationToken = default)
{
    var cardRef = _db.Collection("users").Document(userId)
        .Collection("flashcard_decks").Document(deckId)
        .Collection("cards").Document(cardId);

    var userRef = _db.Collection("users").Document(userId);

    SubmitStudyAnswerResponse? response = null;

    await _db.RunTransactionAsync(async transaction =>
    {
        var cardSnap = await transaction.GetSnapshotAsync(cardRef, cancellationToken);
        if (!cardSnap.Exists)
        {
            _logger.LogWarning("UpdateCardSrs: card {CardId} not found.", cardId);
            return 0;
        }

        // Read existing SRS state (with defaults for brand-new cards)
        int repetitions = cardSnap.ContainsField("srs_repetitions")
            ? cardSnap.GetValue<int>("srs_repetitions") : 0;
        double ef = cardSnap.ContainsField("srs_easiness_factor")
            ? cardSnap.GetValue<double>("srs_easiness_factor") : 2.5;
        int interval = cardSnap.ContainsField("srs_interval")
            ? cardSnap.GetValue<int>("srs_interval") : 0;

        // Run SM-2
        var srs = Sm2Algorithm.Calculate(repetitions, ef, interval, quality);

        // Award coins: 5 for quality >= 3 (correct/good/easy), 0 otherwise
        int coins = quality >= 3 ? 5 : 0;

        // Update the card
        transaction.Update(cardRef, new Dictionary<string, object>
        {
            { "srs_state",            srs.State },
            { "srs_repetitions",      srs.Repetitions },
            { "srs_easiness_factor",  srs.EasinessFactor },
            { "srs_interval",         srs.IntervalDays },
            { "srs_next_review_at",   Timestamp.FromDateTime(srs.NextReviewAt) },
            { "srs_last_reviewed_at", FieldValue.ServerTimestamp },
            { "updated_at",           FieldValue.ServerTimestamp }
        });

        // Award coins on the user profile
        if (coins > 0)
        {
            transaction.Update(userRef, new Dictionary<string, object>
            {
                { "coins",        FieldValue.Increment(coins) },
                { "total_points", FieldValue.Increment(coins) }
            });
        }

        response = new SubmitStudyAnswerResponse
        {
            CardId = cardId,
            Mode = mode,
            QualityApplied = quality,
            NewSrsState = srs.State,
            NewIntervalDays = srs.IntervalDays,
            NextReviewAt = srs.NextReviewAt.ToString("o"),
            CoinsAwarded = coins
        };

        return 0;
    }, cancellationToken: cancellationToken);

    return response ?? new SubmitStudyAnswerResponse { CardId = cardId, Mode = mode };
}

public async Task CacheDistractorsAsync(
    string userId,
    string deckId,
    string cardId,
    List<string> distractors,
    CancellationToken cancellationToken = default)
{
    var cardRef = _db.Collection("users").Document(userId)
        .Collection("flashcard_decks").Document(deckId)
        .Collection("cards").Document(cardId);

    await cardRef.UpdateAsync(new Dictionary<string, object>
    {
        { "srs_distractors", distractors },
        { "updated_at", FieldValue.ServerTimestamp }
    }, cancellationToken);
}

public async Task<DeckStudyStats> GetDeckStudyStatsAsync(
    string userId,
    string deckId,
    CancellationToken cancellationToken = default)
{
    var cardsRef = _db.Collection("users").Document(userId)
        .Collection("flashcard_decks").Document(deckId)
        .Collection("cards");

    var snapshot = await cardsRef.GetSnapshotAsync(cancellationToken);
    var now = DateTime.UtcNow;

    var stats = new DeckStudyStats { DeckId = deckId, TotalCards = snapshot.Count };

    foreach (var doc in snapshot.Documents)
    {
        var state = doc.ContainsField("srs_state")
            ? doc.GetValue<string>("srs_state")
            : "new";

        switch (state)
        {
            case "learning":  stats.LearningCount++; break;
            case "review":    stats.ReviewCount++;   break;
            case "mastered":  stats.MasteredCount++; break;
            default:          stats.NewCount++;      break;
        }

        // Due today: no SRS data, OR next_review_at is in the past
        bool isDue = !doc.ContainsField("srs_next_review_at");
        if (!isDue && doc.ContainsField("srs_next_review_at"))
        {
            var nextReview = doc.GetValue<Timestamp>("srs_next_review_at")
                .ToDateTimeOffset().UtcDateTime;
            isDue = nextReview <= now;
        }
        if (isDue) stats.DueToday++;
    }

    return stats;
}

// ── Private helper ────────────────────────────────────────────────

/// <summary>
/// Maps a Firestore card document to a CardResponse.
/// Centralised here so all study + deck methods use the same mapping.
/// </summary>
private static CardResponse? MapCardDocument(DocumentSnapshot doc)
{
    if (!doc.Exists) return null;

    return new CardResponse
    {
        Id           = doc.Id,
        Term         = doc.ContainsField("term")             ? doc.GetValue<string>("term")             : string.Empty,
        NormalizedTerm = doc.ContainsField("normalized_term") ? doc.GetValue<string>("normalized_term") : string.Empty,
        Translation  = doc.ContainsField("translation")      ? doc.GetValue<string>("translation")      : string.Empty,
        Pronunciation = doc.ContainsField("pronunciation")   ? doc.GetValue<string>("pronunciation")    : string.Empty,
        PartOfSpeech = doc.ContainsField("part_of_speech")   ? doc.GetValue<string>("part_of_speech")   : string.Empty,
        SourceLangCode = doc.ContainsField("source_lang_code") ? doc.GetValue<string>("source_lang_code") : string.Empty,
        TargetLangCode = doc.ContainsField("target_lang_code") ? doc.GetValue<string>("target_lang_code") : string.Empty,
        ImageUrl     = doc.ContainsField("image_url")        ? doc.GetValue<string>("image_url")        : null,
        IsFavorite   = doc.ContainsField("is_favorite")      && doc.GetValue<bool>("is_favorite"),
        SourceVocabId = doc.ContainsField("source_vocab_id") ? doc.GetValue<string>("source_vocab_id") : null,
    };
}
```

> **Note:** Your existing `GetCardsAsync` and `AddCardAsync` each build `CardResponse` inline. Replace those inline builds with calls to `MapCardDocument(doc)` to avoid duplication.

---

## 9. StudyController

Create `Controller/StudyController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milingo.Backend.Extensions;
using Milingo.Backend.Models;
using Milingo.Backend.Services;

namespace Milingo.Backend.Controllers;

[ApiController]
[Route("api/v1/study")]
[Authorize]
public class StudyController : ControllerBase
{
    private readonly IFirestoreService _firestoreService;
    private readonly IOpenAiService _openAiService;
    private readonly ILogger<StudyController> _logger;

    public StudyController(
        IFirestoreService firestoreService,
        IOpenAiService openAiService,
        ILogger<StudyController> logger)
    {
        _firestoreService = firestoreService;
        _openAiService = openAiService;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────
    //  GET /api/v1/study/session?deckId=xxx&limit=20
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a mixed study session for a deck.
    /// Cards in "new" or "learning" state → suggested_mode = "flashcard".
    /// Cards in "review" or "mastered" state → suggested_mode = "mcq" (with options).
    ///
    /// MCQ options = up to 3 real user cards (from same/other decks) +
    ///               AI-generated distractors if not enough real cards.
    /// AI distractors are cached on the card so OpenAI is called once per card ever.
    /// </summary>
    [HttpGet("session")]
    public async Task<IActionResult> GetStudySession(
        [FromQuery] string deckId,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(Err("Invalid token."));

        if (string.IsNullOrWhiteSpace(deckId))
            return BadRequest(Err("deckId is required."));

        limit = Math.Clamp(limit, 1, 50);

        try
        {
            // 1. Fetch due cards
            var dueCards = await _firestoreService.GetDueCardsAsync(
                userId, deckId, limit, cancellationToken);

            if (dueCards.Count == 0)
            {
                return Ok(new ApiResponse<StudySessionResponse>
                {
                    Status = "success",
                    Message = "No cards due right now. Great work!",
                    Data = new StudySessionResponse { DeckId = deckId, TotalDue = 0 }
                });
            }

            // 2. Get all deck card IDs to exclude from distractors
            var dueCardIds = dueCards.Select(c => c.Id).ToHashSet();

            // 3. Build StudyCard for each due card
            var studyCards = new List<StudyCard>();

            foreach (var card in dueCards)
            {
                var suggestedMode = GetSuggestedMode(card);
                List<StudyOption>? options = null;

                if (suggestedMode == "mcq")
                {
                    options = await BuildMcqOptionsAsync(
                        userId, deckId, card, dueCardIds, cancellationToken);
                }

                studyCards.Add(new StudyCard
                {
                    CardId        = card.Id,
                    DeckId        = deckId,
                    Term          = card.Term,
                    Translation   = card.Translation,
                    Pronunciation = card.Pronunciation,
                    ImageUrl      = card.ImageUrl,
                    SrsState      = "new",   // will come from card fields once added to CardResponse
                    SuggestedMode = suggestedMode,
                    Options       = options
                });
            }

            // Shuffle so flashcards and MCQ aren't always grouped
            studyCards = studyCards.OrderBy(_ => Guid.NewGuid()).ToList();

            int flashcardCount = studyCards.Count(c => c.SuggestedMode == "flashcard");
            int mcqCount       = studyCards.Count(c => c.SuggestedMode == "mcq");

            return Ok(new ApiResponse<StudySessionResponse>
            {
                Status  = "success",
                Message = $"{studyCards.Count} card(s) ready — {flashcardCount} flashcard(s), {mcqCount} MCQ(s).",
                Data    = new StudySessionResponse
                {
                    DeckId         = deckId,
                    Cards          = studyCards,
                    TotalDue       = dueCards.Count,
                    FlashcardCount = flashcardCount,
                    McqCount       = mcqCount
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStudySession failed for user {UserId}, deck {DeckId}.", userId, deckId);
            return StatusCode(500, Err("An unexpected error occurred."));
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  POST /api/v1/study/answer
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a study answer and updates the card's SRS schedule.
    ///
    /// Flashcard: send mode="flashcard" and quality=0|2|4|5
    /// MCQ:       send mode="mcq" and is_correct=true|false
    /// </summary>
    [HttpPost("answer")]
    public async Task<IActionResult> SubmitAnswer(
        [FromBody] SubmitStudyAnswerRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(Err("Invalid token."));

        if (string.IsNullOrWhiteSpace(request.CardId) || string.IsNullOrWhiteSpace(request.DeckId))
            return BadRequest(Err("card_id and deck_id are required."));

        if (request.Mode != "flashcard" && request.Mode != "mcq")
            return BadRequest(Err("mode must be 'flashcard' or 'mcq'."));

        // Resolve quality
        int quality;
        if (request.Mode == "mcq")
        {
            if (request.IsCorrect is null)
                return BadRequest(Err("is_correct is required for MCQ mode."));
            quality = request.IsCorrect.Value ? 4 : 1;
        }
        else
        {
            if (request.Quality is null)
                return BadRequest(Err("quality is required for flashcard mode."));
            if (request.Quality is not (0 or 2 or 4 or 5))
                return BadRequest(Err("quality must be 0 (Again), 2 (Hard), 4 (Good), or 5 (Easy)."));
            quality = request.Quality.Value;
        }

        try
        {
            var result = await _firestoreService.UpdateCardSrsAsync(
                userId, request.DeckId, request.CardId, quality, request.Mode, cancellationToken);

            string message = request.Mode == "mcq"
                ? (quality >= 3 ? "Correct! Scheduled for later." : "Not quite — this one will come back soon.")
                : quality switch
                {
                    0 => "Got it — we'll show this one again shortly.",
                    2 => "Hard one — see you in a day.",
                    4 => "Good — see you in a few days.",
                    5 => "Easy — see you much later!",
                    _ => "Answer recorded."
                };

            return Ok(new ApiResponse<SubmitStudyAnswerResponse>
            {
                Status  = "success",
                Message = message,
                Data    = result
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubmitAnswer failed for user {UserId}, card {CardId}.", userId, request.CardId);
            return StatusCode(500, Err("An unexpected error occurred."));
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  GET /api/v1/study/stats?deckId=xxx
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns SRS state counts for a deck. Used by the deck overview screen
    /// to show a progress bar (New / Learning / Review / Mastered).
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStudyStats(
        [FromQuery] string deckId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(Err("Invalid token."));

        if (string.IsNullOrWhiteSpace(deckId))
            return BadRequest(Err("deckId is required."));

        try
        {
            var stats = await _firestoreService.GetDeckStudyStatsAsync(userId, deckId, cancellationToken);

            return Ok(new ApiResponse<DeckStudyStats>
            {
                Status  = "success",
                Message = "Deck study stats retrieved.",
                Data    = stats
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStudyStats failed for user {UserId}, deck {DeckId}.", userId, deckId);
            return StatusCode(500, Err("An unexpected error occurred."));
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines the study mode based on the card's current SRS state.
    /// New and learning cards → flashcard (build familiarity first).
    /// Review and mastered cards → MCQ (test real recall).
    /// </summary>
    private static string GetSuggestedMode(CardResponse card)
    {
        // CardResponse doesn't have SRS fields yet — you'll add them once
        // you extend the model. For now, default all to flashcard.
        // Once srs_state is on CardResponse, replace this with:
        //   return card.SrsState is "review" or "mastered" ? "mcq" : "flashcard";
        return "flashcard";
    }

    /// <summary>
    /// Builds 4 shuffled MCQ options (1 correct + 3 distractors).
    /// Distractors come from real user cards first, then AI-generated as fallback.
    /// AI results are cached on the card to avoid repeated OpenAI calls.
    /// </summary>
    private async Task<List<StudyOption>> BuildMcqOptionsAsync(
        string userId,
        string deckId,
        CardResponse correctCard,
        HashSet<string> excludeIds,
        CancellationToken cancellationToken)
    {
        const int needed = 3;

        // Try real user cards from this deck (and others if needed)
        var realDistractors = await _firestoreService.GetDistractorCardsAsync(
            userId, deckId, excludeIds, needed, cancellationToken);

        var options = realDistractors
            .Select(d => new StudyOption { Id = d.Id, Term = d.Term, IsCorrect = false })
            .ToList();

        // If still short, fill with AI-generated distractors (cached on the card)
        if (options.Count < needed)
        {
            var aiTerms = await GetOrGenerateAiDistractorsAsync(
                userId, deckId, correctCard, needed - options.Count, cancellationToken);

            options.AddRange(aiTerms.Select((term, i) => new StudyOption
            {
                Id       = $"ai_{i}",
                Term     = term,
                IsCorrect = false
            }));
        }

        // Add correct answer and shuffle
        options.Add(new StudyOption
        {
            Id        = correctCard.Id,
            Term      = correctCard.Term,
            IsCorrect = true
        });

        return options.OrderBy(_ => Guid.NewGuid()).ToList();
    }

    /// <summary>
    /// Returns cached AI distractors for a card, or generates and caches new ones.
    /// This ensures OpenAI is called at most once per card, ever.
    /// </summary>
    private async Task<List<string>> GetOrGenerateAiDistractorsAsync(
        string userId,
        string deckId,
        CardResponse card,
        int countNeeded,
        CancellationToken cancellationToken)
    {
        // TODO: add SrsDistractors to CardResponse and populate it in MapCardDocument
        // For now, always generate (will be called once and then cached)
        var targetLanguage = card.TargetLangCode switch
        {
            "vi" => "Vietnamese",
            "en" => "English",
            "ja" => "Japanese",
            "ko" => "Korean",
            "zh" => "Chinese",
            "fr" => "French",
            "de" => "German",
            "es" => "Spanish",
            _    => card.TargetLangCode
        };

        var distractors = await _openAiService.GenerateDistractorsAsync(
            card.Term, card.Translation, targetLanguage, cancellationToken);

        if (distractors.Count > 0)
        {
            // Cache on the card — fire and forget (don't block the session response)
            _ = _firestoreService.CacheDistractorsAsync(
                userId, deckId, card.Id, distractors, CancellationToken.None);
        }

        return distractors.Take(countNeeded).ToList();
    }

    private static ApiResponse<object> Err(string message) =>
        new() { Status = "error", Message = message };
}
```

---

## 10. Flutter integration summary

### Endpoints

| Method | URL | Description |
|--------|-----|-------------|
| `GET` | `/api/v1/study/session?deckId=&limit=` | Get mixed study session |
| `POST` | `/api/v1/study/answer` | Submit flashcard or MCQ answer |
| `GET` | `/api/v1/study/stats?deckId=` | Deck SRS breakdown for overview screen |

### Request bodies

**Flashcard answer** (user taps Again / Hard / Good / Easy):
```json
{
  "card_id":  "abc123",
  "deck_id":  "deck456",
  "mode":     "flashcard",
  "quality":  4
}
```

**MCQ answer** (user taps one of the four options):
```json
{
  "card_id":    "abc123",
  "deck_id":    "deck456",
  "mode":       "mcq",
  "is_correct": true
}
```

### Flutter rendering logic

```dart
for (final card in session.cards) {
  if (card.suggestedMode == 'flashcard') {
    // Show: image or term on front, translation + pronunciation on back
    // Four buttons: Again / Hard / Good / Easy → quality 0/2/4/5
  } else {
    // Show: image (or translation as prompt if no image)
    // Four option buttons from card.options (shuffled by server)
    // On tap: record isCorrect = tappedOption.isCorrect
  }
}
```

### Quality button mapping for Flutter

| Button | Quality | When |
|--------|---------|------|
| Again  | 0       | Completely forgot / wrong MCQ |
| Hard   | 2       | Remembered but with difficulty |
| Good   | 4       | Remembered correctly (also used for correct MCQ) |
| Easy   | 5       | Remembered instantly, effortlessly |

---

## 11. Files to create / modify

| Action | File | What changes |
|--------|------|-------------|
| **Create** | `Services/Sm2Algorithm.cs` | SM-2 logic, 0–5 quality scale |
| **Create** | `Models/StudyModels.cs` | All study/exam DTOs |
| **Create** | `Controller/StudyController.cs` | 3 endpoints |
| **Modify** | `Services/IOpenAiService.cs` | Add `GenerateDistractorsAsync` signature |
| **Modify** | `Services/OpenAiService.cs` | Implement `GenerateDistractorsAsync` |
| **Modify** | `Services/IFirestoreService.cs` | Add 5 method signatures |
| **Modify** | `Services/FirestoreService.cs` | Implement 5 methods + extract `MapCardDocument` |
| **Modify** | `Models/CardResponse.cs` | Add SRS fields: `SrsState`, `SrsRepetitions`, `SrsIntervalDays`, `SrsDistractors` |
| **Firestore** | Composite index on `cards` | `srs_next_review_at ASC, created_at ASC` |

### The one model change needed

Add these to `CardResponse` in `Models/CardModels.cs`:

```csharp
[JsonPropertyName("srs_state")]
public string SrsState { get; set; } = "new";

[JsonPropertyName("srs_repetitions")]
public int SrsRepetitions { get; set; }

[JsonPropertyName("srs_interval_days")]
public int SrsIntervalDays { get; set; }

// Cached AI distractors — internal use, not sent to Flutter in card lists
[JsonIgnore]
public List<string> SrsDistractors { get; set; } = new();
```

And populate them in `MapCardDocument`:

```csharp
SrsState        = doc.ContainsField("srs_state")        ? doc.GetValue<string>("srs_state") : "new",
SrsRepetitions  = doc.ContainsField("srs_repetitions")  ? doc.GetValue<int>("srs_repetitions") : 0,
SrsIntervalDays = doc.ContainsField("srs_interval")     ? doc.GetValue<int>("srs_interval") : 0,
SrsDistractors  = doc.ContainsField("srs_distractors")  ? doc.GetValue<List<string>>("srs_distractors") : new(),
```

Once these are in place, replace the placeholder in `GetSuggestedMode` with:

```csharp
private static string GetSuggestedMode(CardResponse card) =>
    card.SrsState is "review" or "mastered" ? "mcq" : "flashcard";
```

And in `GetOrGenerateAiDistractorsAsync`, skip the OpenAI call if cached distractors already exist:

```csharp
if (card.SrsDistractors.Count >= countNeeded)
    return card.SrsDistractors.Take(countNeeded).ToList();
```
