# Milingo — SRS Migration & Daily Review Session

## Why this is needed

Firestore queries **only return documents where the queried field exists**.
When `GetDueCardsAsync` runs:

```
WHERE srs_state == "new"
```

Cards that have **no** `srs_state` field at all are completely invisible.
Your existing cards will never appear in any study session until this is fixed.

This guide covers exactly four changes:

| # | Change | File |
|---|--------|------|
| 1 | Add SRS fields to `CardResponse` | `Models/CardModels.cs` |
| 2 | Write SRS defaults into every new card | `Services/FirestoreService.cs` — `AddCardAsync` |
| 3 | One-time migration for existing cards | `Services/IFirestoreService.cs` + `FirestoreService.cs` |
| 4 | Cross-deck daily review session | `Services/IFirestoreService.cs` + `FirestoreService.cs` + `StudyController.cs` |

---

## Change 1 — Add SRS fields to `CardResponse`

**File:** `Models/CardModels.cs`

Find the `CardResponse` class. It currently ends at `UpdatedAt`. Add the SRS fields
after `UpdatedAt` and before the closing `}`:

```csharp
public class CardResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;

    [JsonPropertyName("normalized_term")]
    public string NormalizedTerm { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;

    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = string.Empty;

    [JsonPropertyName("part_of_speech")]
    public string PartOfSpeech { get; set; } = string.Empty;

    [JsonPropertyName("source_lang_code")]
    public string SourceLangCode { get; set; } = string.Empty;

    [JsonPropertyName("target_lang_code")]
    public string TargetLangCode { get; set; } = string.Empty;

    [JsonPropertyName("source_vocab_id")]
    public string? SourceVocabId { get; set; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("is_favorite")]
    public bool IsFavorite { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    // ── SRS fields (NEW) ─────────────────────────────────────────

    [JsonPropertyName("srs_state")]
    public string SrsState { get; set; } = "new";

    [JsonPropertyName("srs_repetitions")]
    public int SrsRepetitions { get; set; }

    [JsonPropertyName("srs_interval_days")]
    public int SrsIntervalDays { get; set; }

    [JsonPropertyName("srs_easiness_factor")]
    public double SrsEasinessFactor { get; set; } = 2.5;

    [JsonPropertyName("srs_next_review_at")]
    public string? SrsNextReviewAt { get; set; }  // ISO 8601 UTC, null = due immediately

    // Cached AI distractors — used internally, not sent to Flutter in card list responses
    [JsonIgnore]
    public List<string> SrsDistractors { get; set; } = new();
}
```

---

## Change 2 — Write SRS defaults into every new card

**File:** `Services/FirestoreService.cs` — inside `AddCardAsync`

Find the `cardData` dictionary (around line 619). It currently looks like this:

```csharp
var cardData = new Dictionary<string, object>
{
    { "term", request.Term.Trim() },
    { "normalized_term", normalizedTerm },
    { "translation", request.Translation.Trim() },
    { "pronunciation", request.Pronunciation?.Trim() ?? string.Empty },
    { "part_of_speech", request.PartOfSpeech?.Trim() ?? string.Empty },
    { "source_lang_code", sourceLang },
    { "target_lang_code", targetLang },
    { "is_favorite", false },
    { "created_at", FieldValue.ServerTimestamp },
    { "updated_at", FieldValue.ServerTimestamp }
};
```

Add the four SRS lines so it becomes:

```csharp
var cardData = new Dictionary<string, object>
{
    { "term", request.Term.Trim() },
    { "normalized_term", normalizedTerm },
    { "translation", request.Translation.Trim() },
    { "pronunciation", request.Pronunciation?.Trim() ?? string.Empty },
    { "part_of_speech", request.PartOfSpeech?.Trim() ?? string.Empty },
    { "source_lang_code", sourceLang },
    { "target_lang_code", targetLang },
    { "is_favorite", false },
    { "created_at", FieldValue.ServerTimestamp },
    { "updated_at", FieldValue.ServerTimestamp },

    // SRS defaults — written at creation so study queries can find this card immediately
    { "srs_state",           "new" },
    { "srs_easiness_factor", 2.5   },
    { "srs_interval",        0     },
    { "srs_repetitions",     0     },
};
```

That is the only change to `AddCardAsync`. Every card created after deploying this
will be visible in study sessions immediately — no migration needed for future cards.

---

## Change 3 — One-time migration for existing cards

This endpoint scans every existing card across all decks for the current user,
finds ones missing `srs_state`, and batch-writes the defaults. Run it once from
Postman after deploying, then remove it.

### 3a — Add to `IFirestoreService.cs`

```csharp
/// <summary>
/// One-time migration: writes default SRS fields onto every card that is
/// missing them. Safe to run multiple times — skips cards that already
/// have srs_state set. Returns the number of cards updated.
/// </summary>
Task<int> MigrateSrsFieldsAsync(
    string userId,
    CancellationToken cancellationToken = default);
```

### 3b — Implement in `FirestoreService.cs`

```csharp
public async Task<int> MigrateSrsFieldsAsync(
    string userId,
    CancellationToken cancellationToken = default)
{
    var decksSnap = await _db.Collection("users").Document(userId)
        .Collection("flashcard_decks")
        .GetSnapshotAsync(cancellationToken);

    int totalUpdated = 0;
    const int batchLimit = 500; // Firestore hard limit per batch

    foreach (var deckDoc in decksSnap.Documents)
    {
        var cardsSnap = await _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckDoc.Id)
            .Collection("cards")
            .GetSnapshotAsync(cancellationToken);

        // Only touch cards that have no srs_state field at all
        var needsMigration = cardsSnap.Documents
            .Where(doc => !doc.ContainsField("srs_state"))
            .ToList();

        if (needsMigration.Count == 0)
        {
            _logger.LogInformation(
                "Deck {DeckId}: all {Total} card(s) already have SRS fields. Skipping.",
                deckDoc.Id, cardsSnap.Count);
            continue;
        }

        _logger.LogInformation(
            "Deck {DeckId}: migrating {Count}/{Total} card(s).",
            deckDoc.Id, needsMigration.Count, cardsSnap.Count);

        // Process in chunks of 500 (Firestore batch write limit)
        foreach (var chunk in needsMigration.Chunk(batchLimit))
        {
            var batch = _db.StartBatch();

            foreach (var cardDoc in chunk)
            {
                batch.Update(cardDoc.Reference, new Dictionary<string, object>
                {
                    { "srs_state",           "new" },
                    { "srs_easiness_factor", 2.5   },
                    { "srs_interval",        0     },
                    { "srs_repetitions",     0     },
                });
            }

            await batch.CommitAsync(cancellationToken);
            totalUpdated += chunk.Length;
        }
    }

    _logger.LogInformation("SRS migration complete. Total cards updated: {Total}.", totalUpdated);
    return totalUpdated;
}
```

### 3c — Add endpoint to `StudyController.cs`

Add this endpoint inside `StudyController`. Delete or comment it out after you have
run it once.

```csharp
// ─────────────────────────────────────────────────────────────────────────
//  POST /api/v1/study/migrate-srs
//
//  Run ONCE from Postman after deploying Change 2.
//  Deletes itself from the codebase once migration is confirmed.
// ─────────────────────────────────────────────────────────────────────────
[HttpPost("migrate-srs")]
public async Task<IActionResult> MigrateSrsFields(CancellationToken cancellationToken)
{
    var userId = User.GetFirebaseUid();
    if (string.IsNullOrEmpty(userId))
        return Unauthorized(Err("Invalid token."));

    try
    {
        var updated = await _firestoreService.MigrateSrsFieldsAsync(userId, cancellationToken);

        return Ok(new ApiResponse<object>
        {
            Status  = "success",
            Message = $"Migration complete. {updated} card(s) updated with default SRS fields.",
            Data    = new { cards_migrated = updated }
        });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "SRS migration failed for user {UserId}.", userId);
        return StatusCode(500, Err("Migration failed. Check server logs."));
    }
}
```

---

## Change 4 — Cross-deck daily review session

Two things: a new Firestore method that pulls due cards from all decks, and a new
`/daily-session` endpoint in `StudyController`.

### 4a — Add to `IFirestoreService.cs`

```csharp
/// <summary>
/// Returns up to <paramref name="limit"/> cards due for study across ALL
/// of the user's decks. Results are ordered: new cards first (no review date),
/// then by srs_next_review_at ascending (most overdue shown first).
/// Each CardResponse includes a DeckId so Flutter can label the card's origin.
/// </summary>
Task<List<CardResponse>> GetAllDueCardsAsync(
    string userId,
    int limit = 30,
    CancellationToken cancellationToken = default);
```

### 4b — Implement in `FirestoreService.cs`

Also update `MapCardDocument` to read the new SRS fields and accept a `deckId`
parameter so each card knows which deck it came from.

```csharp
public async Task<List<CardResponse>> GetAllDueCardsAsync(
    string userId,
    int limit = 30,
    CancellationToken cancellationToken = default)
{
    var decksSnap = await _db.Collection("users").Document(userId)
        .Collection("flashcard_decks")
        .GetSnapshotAsync(cancellationToken);

    if (decksSnap.Count == 0) return new List<CardResponse>();

    var now = Timestamp.FromDateTime(DateTime.UtcNow);

    // Collect due cards from every deck in parallel
    var tasks = decksSnap.Documents.Select(async deckDoc =>
    {
        var cardsRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckDoc.Id)
            .Collection("cards");

        // Cards with srs_state == "new" (just migrated or just added)
        var newSnap = await cardsRef
            .WhereEqualTo("srs_state", "new")
            .Limit(limit)
            .GetSnapshotAsync(cancellationToken);

        // Cards whose review date has arrived
        var dueSnap = await cardsRef
            .WhereLessThanOrEqualTo("srs_next_review_at", now)
            .OrderBy("srs_next_review_at")
            .Limit(limit)
            .GetSnapshotAsync(cancellationToken);

        var seen = new HashSet<string>();
        var results = new List<(CardResponse Card, DateTime? NextReview)>();

        foreach (var doc in newSnap.Documents.Concat(dueSnap.Documents))
        {
            if (!seen.Add(doc.Id)) continue;
            var card = MapCardDocument(doc, deckDoc.Id);
            if (card == null) continue;

            DateTime? nextReview = null;
            if (doc.ContainsField("srs_next_review_at"))
            {
                nextReview = doc.GetValue<Timestamp>("srs_next_review_at")
                    .ToDateTimeOffset().UtcDateTime;
            }

            results.Add((card, nextReview));
        }

        return results;
    });

    var allResults = (await Task.WhenAll(tasks))
        .SelectMany(r => r)
        .ToList();

    // Sort: new cards (no review date) first, then most overdue
    return allResults
        .OrderBy(x => x.NextReview.HasValue ? 1 : 0)
        .ThenBy(x => x.NextReview)
        .Select(x => x.Card)
        .Take(limit)
        .ToList();
}
```

### 4c — Update `MapCardDocument` in `FirestoreService.cs`

Your existing `MapCardDocument` helper currently doesn't read SRS fields or know
which deck the card belongs to. Replace it with this updated version:

```csharp
/// <summary>
/// Maps a Firestore card document to a CardResponse.
/// Pass deckId when you already know it (avoids an extra read).
/// </summary>
private static CardResponse? MapCardDocument(DocumentSnapshot doc, string deckId = "")
{
    if (!doc.Exists) return null;

    DateTime? nextReviewAt = null;
    if (doc.ContainsField("srs_next_review_at"))
    {
        nextReviewAt = doc.GetValue<Timestamp>("srs_next_review_at")
            .ToDateTimeOffset().UtcDateTime;
    }

    return new CardResponse
    {
        Id               = doc.Id,
        Term             = doc.ContainsField("term")              ? doc.GetValue<string>("term")             : string.Empty,
        NormalizedTerm   = doc.ContainsField("normalized_term")   ? doc.GetValue<string>("normalized_term")  : string.Empty,
        Translation      = doc.ContainsField("translation")       ? doc.GetValue<string>("translation")      : string.Empty,
        Pronunciation    = doc.ContainsField("pronunciation")     ? doc.GetValue<string>("pronunciation")    : string.Empty,
        PartOfSpeech     = doc.ContainsField("part_of_speech")    ? doc.GetValue<string>("part_of_speech")   : string.Empty,
        SourceLangCode   = doc.ContainsField("source_lang_code")  ? doc.GetValue<string>("source_lang_code") : string.Empty,
        TargetLangCode   = doc.ContainsField("target_lang_code")  ? doc.GetValue<string>("target_lang_code") : string.Empty,
        ImageUrl         = doc.ContainsField("image_url")         ? doc.GetValue<string>("image_url")        : null,
        IsFavorite       = doc.ContainsField("is_favorite")       && doc.GetValue<bool>("is_favorite"),
        SourceVocabId    = doc.ContainsField("source_vocab_id")   ? doc.GetValue<string>("source_vocab_id")  : null,

        // SRS fields
        SrsState           = doc.ContainsField("srs_state")           ? doc.GetValue<string>("srs_state")         : "new",
        SrsRepetitions     = doc.ContainsField("srs_repetitions")     ? doc.GetValue<int>("srs_repetitions")      : 0,
        SrsIntervalDays    = doc.ContainsField("srs_interval")        ? doc.GetValue<int>("srs_interval")         : 0,
        SrsEasinessFactor  = doc.ContainsField("srs_easiness_factor") ? doc.GetValue<double>("srs_easiness_factor"): 2.5,
        SrsNextReviewAt    = nextReviewAt?.ToString("o"),
        SrsDistractors     = doc.ContainsField("srs_distractors")     ? doc.GetValue<List<string>>("srs_distractors") : new(),
    };
}
```

> **Note:** All existing callers of `MapCardDocument(doc)` still work — the `deckId`
> parameter defaults to `""`. Only the new `GetAllDueCardsAsync` passes it explicitly.

### 4d — Add `/daily-session` endpoint to `StudyController.cs`

```csharp
// ─────────────────────────────────────────────────────────────────────────
//  GET /api/v1/study/daily-session?limit=30
//
//  Returns due cards from ALL decks in one mixed queue.
//  This is the main daily habit loop — the user doesn't pick a deck.
// ─────────────────────────────────────────────────────────────────────────
[HttpGet("daily-session")]
public async Task<IActionResult> GetDailySession(
    [FromQuery] int limit = 30,
    CancellationToken cancellationToken = default)
{
    var userId = User.GetFirebaseUid();
    if (string.IsNullOrEmpty(userId))
        return Unauthorized(Err("Invalid token."));

    limit = Math.Clamp(limit, 1, 50);

    try
    {
        var dueCards = await _firestoreService.GetAllDueCardsAsync(
            userId, limit, cancellationToken);

        if (dueCards.Count == 0)
        {
            return Ok(new ApiResponse<StudySessionResponse>
            {
                Status  = "success",
                Message = "All caught up! No cards due right now.",
                Data    = new StudySessionResponse { TotalDue = 0 }
            });
        }

        var dueCardIds = dueCards.Select(c => c.Id).ToHashSet();
        var studyCards = new List<StudyCard>();

        foreach (var card in dueCards)
        {
            var suggestedMode = card.SrsState is "review" or "mastered" ? "mcq" : "flashcard";
            List<StudyOption>? options = null;

            if (suggestedMode == "mcq")
            {
                // Use the card's own deckId for distractor search
                options = await BuildMcqOptionsAsync(
                    userId, card.Id, dueCardIds, cancellationToken);
            }

            studyCards.Add(new StudyCard
            {
                CardId        = card.Id,
                DeckId        = card.Id,  // CardResponse.DeckId added in step 4c
                Term          = card.Term,
                Translation   = card.Translation,
                Pronunciation = card.Pronunciation,
                ImageUrl      = card.ImageUrl,
                SrsState      = card.SrsState,
                SrsRepetitions = card.SrsRepetitions,
                SrsIntervalDays = card.SrsIntervalDays,
                SuggestedMode = suggestedMode,
                Options       = options
            });
        }

        int flashcardCount = studyCards.Count(c => c.SuggestedMode == "flashcard");
        int mcqCount       = studyCards.Count(c => c.SuggestedMode == "mcq");

        return Ok(new ApiResponse<StudySessionResponse>
        {
            Status  = "success",
            Message = $"{studyCards.Count} card(s) due across all decks — {flashcardCount} flashcard(s), {mcqCount} MCQ(s).",
            Data    = new StudySessionResponse
            {
                Cards          = studyCards,
                TotalDue       = dueCards.Count,
                FlashcardCount = flashcardCount,
                McqCount       = mcqCount
            }
        });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "GetDailySession failed for user {UserId}.", userId);
        return StatusCode(500, Err("An unexpected error occurred."));
    }
}
```

---

## Complete endpoint list after all changes

| Method | URL | Description |
|--------|-----|-------------|
| `GET`  | `/api/v1/study/daily-session?limit=30` | Cross-deck daily review queue |
| `GET`  | `/api/v1/study/session?deckId=&limit=` | Single-deck practice session |
| `POST` | `/api/v1/study/answer` | Submit flashcard or MCQ answer |
| `GET`  | `/api/v1/study/stats?deckId=` | SRS state breakdown for a deck |
| `POST` | `/api/v1/study/migrate-srs` | ⚠️ One-time migration — delete after use |

---

## Exact sequence to follow

```
Step 1 — Apply Change 1 (CardResponse SRS fields)
         Edit Models/CardModels.cs

Step 2 — Apply Change 2 (AddCardAsync SRS defaults)
         Edit Services/FirestoreService.cs — the cardData dictionary

Step 3 — Apply Change 3 (migration method + endpoint)
         Edit Services/IFirestoreService.cs  — add MigrateSrsFieldsAsync signature
         Edit Services/FirestoreService.cs   — add MigrateSrsFieldsAsync implementation
         Edit Controller/StudyController.cs  — add migrate-srs endpoint

Step 4 — Apply Change 4 (daily session)
         Edit Services/IFirestoreService.cs  — add GetAllDueCardsAsync signature
         Edit Services/FirestoreService.cs   — add GetAllDueCardsAsync + update MapCardDocument
         Edit Controller/StudyController.cs  — add daily-session endpoint

Step 5 — Deploy to your server

Step 6 — Open Postman
         POST /api/v1/study/migrate-srs   (with your Firebase Bearer token)
         Response will say: "Migration complete. N card(s) updated."

Step 7 — Verify in Firebase Console
         Open any existing card document → confirm it now has srs_state = "new"

Step 8 — Delete the migrate-srs endpoint from StudyController
         Redeploy

Done. All existing cards are queryable. All future cards get SRS fields at creation.
```

---

## What the Firestore Console should look like after migration

Every card document under
`users/{uid}/flashcard_decks/{deckId}/cards/{cardId}`
should have these fields:

```
term                 : "laptop"
translation          : "máy tính xách tay"
...existing fields...
srs_state            : "new"          ← written by migration
srs_easiness_factor  : 2.5            ← written by migration
srs_interval         : 0              ← written by migration
srs_repetitions      : 0              ← written by migration
```

After the user's first study answer, `UpdateCardSrsAsync` will add:

```
srs_next_review_at   : 2026-06-02T10:00:00Z
srs_last_reviewed_at : 2026-06-01T10:00:00Z
srs_state            : "learning"
srs_interval         : 1
srs_repetitions      : 1
srs_easiness_factor  : 2.5
```
