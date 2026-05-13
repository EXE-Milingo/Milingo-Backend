using System.Text.Json;
using Google.Cloud.Firestore;
using Grpc.Core;
using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Handles all Firestore database operations for user profiles, vocabularies,
/// flashcard decks, and cards.
/// </summary>
public class FirestoreService : IFirestoreService
{
    private readonly FirestoreDb _db;
    private readonly ILogger<FirestoreService> _logger;

    public FirestoreService(FirestoreDb db, ILogger<FirestoreService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // =================================================================
    //  SNAP & LEARN (unchanged)
    // =================================================================

    /// <inheritdoc />
    public async Task<SnapAnalysisResponse?> GetCachedSnapResultAsync(
        string userId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var eventRef = _db.Collection("users").Document(userId)
            .Collection("snap_events").Document(idempotencyKey);

        var snapshot = await eventRef.GetSnapshotAsync(cancellationToken);

        if (!snapshot.Exists)
            return null;

        _logger.LogInformation(
            "Idempotency key '{Key}' already processed for user '{UserId}'. Returning cached result.",
            idempotencyKey, userId);

        var cachedJson = snapshot.ContainsField("cached_response")
            ? snapshot.GetValue<string>("cached_response")
            : null;

        if (!string.IsNullOrEmpty(cachedJson))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<SnapAnalysisResponse>(cachedJson);
                if (cached is not null)
                {
                    cached.CoinsAwarded = 0;
                    return cached;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deserialize cached response for key '{Key}'. Rebuilding from stored fields.",
                    idempotencyKey);
            }
        }

        var keywords = snapshot.ContainsField("keywords")
            ? snapshot.GetValue<List<string>>("keywords")
            : new List<string>();

        var objectCount = snapshot.ContainsField("object_count")
            ? snapshot.GetValue<int>("object_count")
            : keywords.Count;

        var usedFallback = snapshot.ContainsField("used_fallback")
            && snapshot.GetValue<bool>("used_fallback");

        var vocabItems = keywords.Select(k => new SnapVocabItem { Keyword = k }).ToList();

        var response = new SnapAnalysisResponse
        {
            SnapGroupId = idempotencyKey,
            ObjectCount = objectCount,
            UsedFallback = usedFallback,
            VocabItems = vocabItems,
            CoinsAwarded = 0
        };

        if (vocabItems.Count > 0)
            response.Keyword = vocabItems[0].Keyword;

        return response;
    }

    /// <inheritdoc />
    public async Task<bool> SaveVocabAndAddCoinsAsync(
        string userId,
        VocabResponse vocab,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(userId);
        var vocabCollection = userRef.Collection("vocabularies");
        var eventRef = userRef.Collection("snap_events").Document(idempotencyKey);

        var isNewRequest = await _db.RunTransactionAsync(async transaction =>
        {
            DocumentSnapshot eventSnapshot = await transaction.GetSnapshotAsync(
                eventRef, cancellationToken);

            if (eventSnapshot.Exists)
            {
                _logger.LogInformation(
                    "Duplicate snap event detected for user '{UserId}', key '{Key}'. Skipping.",
                    userId, idempotencyKey);
                return false;
            }

            transaction.Set(eventRef, new Dictionary<string, object>
            {
                { "keyword", vocab.Keyword },
                { "processed_at", FieldValue.ServerTimestamp }
            });

            transaction.Set(userRef,
                new Dictionary<string, object> { { "coins", FieldValue.Increment(10) } },
                SetOptions.MergeAll);

            var newVocabDoc = vocabCollection.Document();
            transaction.Set(newVocabDoc, new Dictionary<string, object>
            {
                { "keyword", vocab.Keyword },
                { "translation", vocab.Translation },
                { "pronunciation", vocab.Pronunciation },
                { "example_sentence", vocab.ExampleSentence },
                { "mastery_level", 0.0 },
                { "created_at", FieldValue.ServerTimestamp }
            });

            return true;

        }, cancellationToken: cancellationToken);

        if (isNewRequest)
        {
            _logger.LogInformation(
                "Saved vocabulary '{Keyword}' and awarded 10 coins to user '{UserId}' (key: {Key}).",
                vocab.Keyword, userId, idempotencyKey);
        }

        return isNewRequest;
    }

    /// <inheritdoc />
    public async Task<bool> SaveMultiVocabAndAddCoinsAsync(
        string userId,
        List<SnapVocabItem> vocabItems,
        string idempotencyKey,
        bool usedFallback,
        List<SnapDetectionDetail> detectionDetails,
        CancellationToken cancellationToken = default)
    {
        if (vocabItems.Count == 0)
        {
            _logger.LogWarning("SaveMultiVocabAndAddCoinsAsync called with 0 items for user '{UserId}'.", userId);
            return false;
        }

        var userRef = _db.Collection("users").Document(userId);
        var vocabCollection = userRef.Collection("vocabularies");
        var eventRef = userRef.Collection("snap_events").Document(idempotencyKey);
        var coinsToAward = vocabItems.Count * 10;

        var fullResponse = new SnapAnalysisResponse
        {
            SnapGroupId = idempotencyKey,
            ObjectCount = vocabItems.Count,
            UsedFallback = usedFallback,
            VocabItems = vocabItems,
            CoinsAwarded = coinsToAward,
            Keyword = vocabItems[0].Keyword,
            Translation = vocabItems[0].Translation,
            Pronunciation = vocabItems[0].Pronunciation,
            ExampleSentence = vocabItems[0].ExampleSentence
        };
        var cachedResponseJson = JsonSerializer.Serialize(fullResponse);

        var isNewRequest = await _db.RunTransactionAsync(async transaction =>
        {
            DocumentSnapshot eventSnapshot = await transaction.GetSnapshotAsync(
                eventRef, cancellationToken);

            if (eventSnapshot.Exists)
            {
                _logger.LogInformation(
                    "Duplicate snap event detected for user '{UserId}', key '{Key}'. Skipping.",
                    userId, idempotencyKey);
                return false;
            }

            var eventData = new Dictionary<string, object>
            {
                { "keywords", vocabItems.Select(v => v.Keyword).ToList() },
                { "object_count", vocabItems.Count },
                { "coins_awarded", coinsToAward },
                { "used_fallback", usedFallback },
                { "cached_response", cachedResponseJson },
                { "processed_at", FieldValue.ServerTimestamp }
            };

            if (detectionDetails.Count > 0)
            {
                eventData["detections"] = detectionDetails.Select(d => new Dictionary<string, object>
                {
                    { "label", d.Label },
                    { "confidence", d.Confidence },
                    { "boundingBox", new Dictionary<string, object>
                        {
                            { "x", d.X },
                            { "y", d.Y },
                            { "width", d.Width },
                            { "height", d.Height }
                        }
                    }
                }).ToList();
            }

            transaction.Set(eventRef, eventData);

            transaction.Set(userRef,
                new Dictionary<string, object> { { "coins", FieldValue.Increment(coinsToAward) } },
                SetOptions.MergeAll);

            foreach (var item in vocabItems)
            {
                var newVocabDoc = vocabCollection.Document();
                var vocabData = new Dictionary<string, object>
                {
                    { "keyword", item.Keyword },
                    { "translation", item.Translation },
                    { "pronunciation", item.Pronunciation },
                    { "example_sentence", item.ExampleSentence },
                    { "snap_group_id", idempotencyKey },
                    { "mastery_level", 0.0 },
                    { "created_at", FieldValue.ServerTimestamp }
                };

                if (item.DetectionLabel is not null)
                    vocabData["detection_label"] = item.DetectionLabel;
                if (item.DetectionConfidence is not null)
                    vocabData["detection_confidence"] = item.DetectionConfidence.Value;

                transaction.Set(newVocabDoc, vocabData);
            }

            return true;

        }, cancellationToken: cancellationToken);

        if (isNewRequest)
        {
            _logger.LogInformation(
                "Saved {Count} vocabularies and awarded {Coins} coins to user '{UserId}' (key: {Key}).",
                vocabItems.Count, coinsToAward, userId, idempotencyKey);
        }

        return isNewRequest;
    }

    // =================================================================
    //  USER PROFILE (unchanged)
    // =================================================================

    /// <inheritdoc />
    public async Task<bool> InitUserProfileAsync(
        string uid,
        string email,
        string displayName,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(uid);

        var profileData = new Dictionary<string, object>
        {
            { "email", email },
            { "display_name", displayName },
            { "target_language", targetLanguage },
            { "coins", 50 },
            { "current_streak", 0 },
            { "created_at", FieldValue.ServerTimestamp }
        };

        try
        {
            await userRef.CreateAsync(profileData, cancellationToken: cancellationToken);
            _logger.LogInformation("Created profile for user '{Uid}' with 50 welcome coins.", uid);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            _logger.LogInformation("Profile already exists for user '{Uid}'. Skipping creation.", uid);
            return false;
        }
    }

    // =================================================================
    //  DECKS
    // =================================================================

    /// <inheritdoc />
    public async Task<List<DeckResponse>> GetDecksAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var decksRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks");

        var query = decksRef.OrderBy("created_at");
        var snapshots = await query.GetSnapshotAsync(cancellationToken);

        return snapshots.Documents.Select(MapToDeckResponse).ToList();
    }

    /// <inheritdoc />
    public async Task<DeckResponse> CreateDeckAsync(
        string userId,
        CreateDeckRequest request,
        CancellationToken cancellationToken = default)
    {
        var decksRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks");

        var newDeckRef = decksRef.Document(); // Auto-generate ID

        var deckData = new Dictionary<string, object>
        {
            { "name", request.Name.Trim() },
            { "description", request.Description?.Trim() ?? string.Empty },
            { "emoji", request.Emoji?.Trim() ?? string.Empty },
            { "is_default", false },
            { "vocab_count", 0 },
            { "created_at", FieldValue.ServerTimestamp },
            { "updated_at", FieldValue.ServerTimestamp }
        };

        await newDeckRef.SetAsync(deckData, cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Created deck '{DeckName}' (id: {DeckId}) for user '{UserId}'.",
            request.Name, newDeckRef.Id, userId);

        // Re-read to get server-generated timestamps
        var snapshot = await newDeckRef.GetSnapshotAsync(cancellationToken);
        return MapToDeckResponse(snapshot);
    }

    /// <inheritdoc />
    public async Task<DeckResponse?> UpdateDeckAsync(
        string userId,
        string deckId,
        UpdateDeckRequest request,
        CancellationToken cancellationToken = default)
    {
        var deckRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId);

        var snapshot = await deckRef.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Exists)
            return null;

        var updates = new Dictionary<string, object>
        {
            { "updated_at", FieldValue.ServerTimestamp }
        };

        if (request.Name is not null)
            updates["name"] = request.Name.Trim();
        if (request.Description is not null)
            updates["description"] = request.Description.Trim();
        if (request.Emoji is not null)
            updates["emoji"] = request.Emoji.Trim();

        await deckRef.UpdateAsync(updates, cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Updated deck '{DeckId}' for user '{UserId}'.", deckId, userId);

        // Re-read to return the updated state
        snapshot = await deckRef.GetSnapshotAsync(cancellationToken);
        return MapToDeckResponse(snapshot);
    }

    /// <inheritdoc />
    public async Task<(bool Success, string? ErrorReason)> DeleteDeckAsync(
        string userId,
        string deckId,
        CancellationToken cancellationToken = default)
    {
        var deckRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId);

        var snapshot = await deckRef.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Exists)
            return (false, "Deck not found.");

        var isDefault = snapshot.ContainsField("is_default")
            && snapshot.GetValue<bool>("is_default");

        if (isDefault)
            return (false, "Cannot delete a default deck.");

        // Delete all cards in the subcollection first
        var cardsRef = deckRef.Collection("cards");
        var cardSnapshots = await cardsRef.GetSnapshotAsync(cancellationToken);

        // Firestore batch delete (max 500 per batch, sufficient for flashcards)
        var batch = _db.StartBatch();
        foreach (var cardDoc in cardSnapshots.Documents)
        {
            batch.Delete(cardDoc.Reference);
        }
        batch.Delete(deckRef);
        await batch.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Deleted deck '{DeckId}' and {CardCount} cards for user '{UserId}'.",
            deckId, cardSnapshots.Count, userId);

        return (true, null);
    }

    // =================================================================
    //  CARDS
    // =================================================================

    /// <inheritdoc />
    public async Task<List<CardResponse>?> GetCardsAsync(
        string userId,
        string deckId,
        CancellationToken cancellationToken = default)
    {
        var deckRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId);

        var deckSnapshot = await deckRef.GetSnapshotAsync(cancellationToken);
        if (!deckSnapshot.Exists)
            return null;

        var cardsRef = deckRef.Collection("cards");
        var query = cardsRef.OrderByDescending("created_at");
        var snapshots = await query.GetSnapshotAsync(cancellationToken);

        return snapshots.Documents.Select(MapToCardResponse).ToList();
    }

    /// <inheritdoc />
    public async Task<(CardResponse? Card, string? ConflictMessage, bool DeckNotFound)> AddCardAsync(
        string userId,
        string deckId,
        AddCardRequest request,
        CancellationToken cancellationToken = default)
    {
        var deckRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId);

        var normalizedTerm = request.Term.Trim().ToLowerInvariant();
        var sourceLang = request.SourceLangCode.Trim().ToLowerInvariant();
        var targetLang = request.TargetLangCode.Trim().ToLowerInvariant();

        // Use a transaction to ensure atomicity:
        //  1. Verify deck exists
        //  2. Check for duplicate card
        //  3. Create card + increment vocab_count
        var result = await _db.RunTransactionAsync(async transaction =>
        {
            // -- Check deck exists --
            var deckSnapshot = await transaction.GetSnapshotAsync(deckRef, cancellationToken);
            if (!deckSnapshot.Exists)
                return ((CardResponse?)null, (string?)null, true);

            // -- Check for duplicate --
            // Firestore transactions require all reads before writes.
            // Query cards with matching normalized_term + source + target lang.
            var cardsRef = deckRef.Collection("cards");
            var duplicateQuery = cardsRef
                .WhereEqualTo("normalized_term", normalizedTerm)
                .WhereEqualTo("source_lang_code", sourceLang)
                .WhereEqualTo("target_lang_code", targetLang)
                .Limit(1);

            var duplicates = await duplicateQuery.GetSnapshotAsync(cancellationToken);
            if (duplicates.Count > 0)
            {
                var existing = duplicates.Documents[0];
                var existingTerm = existing.ContainsField("term")
                    ? existing.GetValue<string>("term")
                    : normalizedTerm;

                return (null, $"Card '{existingTerm}' already exists in this deck.", false);
            }

            // -- Create the card --
            var newCardRef = cardsRef.Document();
            var cardData = new Dictionary<string, object>
            {
                { "term", request.Term.Trim() },
                { "normalized_term", normalizedTerm },
                { "translation", request.Translation.Trim() },
                { "pronunciation", request.Pronunciation?.Trim() ?? string.Empty },
                { "part_of_speech", request.PartOfSpeech?.Trim() ?? string.Empty },
                { "source_lang_code", sourceLang },
                { "target_lang_code", targetLang },
                { "created_at", FieldValue.ServerTimestamp },
                { "updated_at", FieldValue.ServerTimestamp }
            };

            if (request.SourceVocabId is not null)
                cardData["source_vocab_id"] = request.SourceVocabId;
            // When SourceVocabId is null, simply omit the field from the document

            transaction.Set(newCardRef, cardData);

            // -- Increment vocab_count and update timestamp --
            transaction.Update(deckRef, new Dictionary<string, object>
            {
                { "vocab_count", FieldValue.Increment(1) },
                { "updated_at", FieldValue.ServerTimestamp }
            });

            // Return a preliminary card response (timestamps will be null in transaction)
            var card = new CardResponse
            {
                Id = newCardRef.Id,
                Term = request.Term.Trim(),
                NormalizedTerm = normalizedTerm,
                Translation = request.Translation.Trim(),
                Pronunciation = request.Pronunciation?.Trim() ?? string.Empty,
                PartOfSpeech = request.PartOfSpeech?.Trim() ?? string.Empty,
                SourceLangCode = sourceLang,
                TargetLangCode = targetLang,
                SourceVocabId = request.SourceVocabId
            };

            return (card, (string?)null, false);

        }, cancellationToken: cancellationToken);

        var (card, conflictMessage, deckNotFound) = result;

        if (card is not null)
        {
            _logger.LogInformation(
                "Added card '{Term}' to deck '{DeckId}' for user '{UserId}'.",
                request.Term, deckId, userId);

            // Re-read to get server timestamps
            var cardSnapshot = await deckRef.Collection("cards")
                .Document(card.Id)
                .GetSnapshotAsync(cancellationToken);

            if (cardSnapshot.Exists)
                return (MapToCardResponse(cardSnapshot), null, false);
        }

        return (card, conflictMessage, deckNotFound);
    }

    /// <inheritdoc />
    public async Task<(bool Success, string? ErrorReason)> DeleteCardAsync(
        string userId,
        string deckId,
        string cardId,
        CancellationToken cancellationToken = default)
    {
        var deckRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId);
        var cardRef = deckRef.Collection("cards").Document(cardId);

        var result = await _db.RunTransactionAsync(async transaction =>
        {
            var deckSnapshot = await transaction.GetSnapshotAsync(deckRef, cancellationToken);
            if (!deckSnapshot.Exists)
                return (false, (string?)"Deck not found.");

            var cardSnapshot = await transaction.GetSnapshotAsync(cardRef, cancellationToken);
            if (!cardSnapshot.Exists)
                return (false, (string?)"Card not found.");

            // Delete the card
            transaction.Delete(cardRef);

            // Decrement vocab_count (floor at 0)
            var currentCount = deckSnapshot.ContainsField("vocab_count")
                ? deckSnapshot.GetValue<int>("vocab_count")
                : 0;

            var newCount = Math.Max(0, currentCount - 1);

            transaction.Update(deckRef, new Dictionary<string, object>
            {
                { "vocab_count", newCount },
                { "updated_at", FieldValue.ServerTimestamp }
            });

            return (true, (string?)null);

        }, cancellationToken: cancellationToken);

        if (result.Item1)
        {
            _logger.LogInformation(
                "Deleted card '{CardId}' from deck '{DeckId}' for user '{UserId}'.",
                cardId, deckId, userId);
        }

        return result;
    }

    // =================================================================
    //  FLASHCARD UTILITIES
    // =================================================================

    /// <inheritdoc />
    public async Task<SavedStatusResponse> GetSavedStatusAsync(
        string userId,
        string term,
        string sourceLangCode,
        string targetLangCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedTerm = term.Trim().ToLowerInvariant();
        var sourceLang = sourceLangCode.Trim().ToLowerInvariant();
        var targetLang = targetLangCode.Trim().ToLowerInvariant();

        var decksRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks");

        var allDecks = await decksRef.GetSnapshotAsync(cancellationToken);
        var matchedDeckIds = new List<string>();

        // Check each deck for the term
        foreach (var deckDoc in allDecks.Documents)
        {
            var cardsRef = deckDoc.Reference.Collection("cards");
            var query = cardsRef
                .WhereEqualTo("normalized_term", normalizedTerm)
                .WhereEqualTo("source_lang_code", sourceLang)
                .WhereEqualTo("target_lang_code", targetLang)
                .Limit(1);

            var result = await query.GetSnapshotAsync(cancellationToken);
            if (result.Count > 0)
            {
                matchedDeckIds.Add(deckDoc.Id);
            }
        }

        return new SavedStatusResponse
        {
            IsSaved = matchedDeckIds.Count > 0,
            DeckIds = matchedDeckIds
        };
    }

    // =================================================================
    //  PRIVATE HELPERS
    // =================================================================

    private static DeckResponse MapToDeckResponse(DocumentSnapshot doc)
    {
        return new DeckResponse
        {
            Id = doc.Id,
            Name = doc.ContainsField("name") ? doc.GetValue<string>("name") : string.Empty,
            Description = doc.ContainsField("description") ? doc.GetValue<string>("description") : string.Empty,
            Emoji = doc.ContainsField("emoji") ? doc.GetValue<string>("emoji") : string.Empty,
            IsDefault = doc.ContainsField("is_default") && doc.GetValue<bool>("is_default"),
            VocabCount = doc.ContainsField("vocab_count") ? doc.GetValue<int>("vocab_count") : 0,
            CreatedAt = doc.ContainsField("created_at")
                ? doc.GetValue<Timestamp>("created_at").ToDateTimeOffset().ToString("o")
                : string.Empty,
            UpdatedAt = doc.ContainsField("updated_at")
                ? doc.GetValue<Timestamp>("updated_at").ToDateTimeOffset().ToString("o")
                : string.Empty
        };
    }

    private static CardResponse MapToCardResponse(DocumentSnapshot doc)
    {
        return new CardResponse
        {
            Id = doc.Id,
            Term = doc.ContainsField("term") ? doc.GetValue<string>("term") : string.Empty,
            NormalizedTerm = doc.ContainsField("normalized_term") ? doc.GetValue<string>("normalized_term") : string.Empty,
            Translation = doc.ContainsField("translation") ? doc.GetValue<string>("translation") : string.Empty,
            Pronunciation = doc.ContainsField("pronunciation") ? doc.GetValue<string>("pronunciation") : string.Empty,
            PartOfSpeech = doc.ContainsField("part_of_speech") ? doc.GetValue<string>("part_of_speech") : string.Empty,
            SourceLangCode = doc.ContainsField("source_lang_code") ? doc.GetValue<string>("source_lang_code") : string.Empty,
            TargetLangCode = doc.ContainsField("target_lang_code") ? doc.GetValue<string>("target_lang_code") : string.Empty,
            SourceVocabId = doc.ContainsField("source_vocab_id") ? doc.GetValue<string?>("source_vocab_id") : null,
            CreatedAt = doc.ContainsField("created_at")
                ? doc.GetValue<Timestamp>("created_at").ToDateTimeOffset().ToString("o")
                : string.Empty,
            UpdatedAt = doc.ContainsField("updated_at")
                ? doc.GetValue<Timestamp>("updated_at").ToDateTimeOffset().ToString("o")
                : string.Empty
        };
    }

    // =================================================================
    //  GAMIFICATION
    // =================================================================

    /// <inheritdoc />
    public async Task<UserStatsResponse> GetUserStatsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(userId);
        var snapshot = await userRef.GetSnapshotAsync(cancellationToken);

        if (!snapshot.Exists)
            return new UserStatsResponse();

        var coins = snapshot.ContainsField("coins")
            ? snapshot.GetValue<int>("coins") : 0;

        var streak = snapshot.ContainsField("current_streak")
            ? snapshot.GetValue<int>("current_streak") : 0;

        // totalPoints = coins (có thể tách riêng sau)
        var totalPoints = snapshot.ContainsField("total_points")
            ? snapshot.GetValue<int>("total_points") : coins;

        string? lastStudyDate = null;
        if (snapshot.ContainsField("last_study_date"))
        {
            var ts = snapshot.GetValue<Timestamp>("last_study_date");
            lastStudyDate = ts.ToDateTimeOffset().ToString("o");
        }

        return new UserStatsResponse
        {
            Coins = coins,
            CurrentStreak = streak,
            TotalPoints = totalPoints,
            LastStudyDate = lastStudyDate,
        };
    }

    /// <inheritdoc />
    public async Task<UserStatsResponse> RecordFlashcardStudyAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(userId);

        // Ngày hôm nay (UTC, chỉ lấy date part)
        var todayUtc = DateTime.UtcNow.Date;

        var updatedStats = await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(userRef, cancellationToken);

            if (!snapshot.Exists)
            {
                _logger.LogWarning(
                    "RecordFlashcardStudy: user '{UserId}' profile not found.", userId);
                return new UserStatsResponse();
            }

            var coins = snapshot.ContainsField("coins")
                ? snapshot.GetValue<int>("coins") : 0;
            var totalPoints = snapshot.ContainsField("total_points")
                ? snapshot.GetValue<int>("total_points") : coins;

            // Lấy last_study_date
            DateTime? lastStudyUtc = null;
            if (snapshot.ContainsField("last_study_date"))
            {
                var ts = snapshot.GetValue<Timestamp>("last_study_date");
                lastStudyUtc = ts.ToDateTimeOffset().UtcDateTime.Date;
            }

            // Idempotent: hôm nay đã ghi nhận rồi → không thay đổi gì
            if (lastStudyUtc.HasValue && lastStudyUtc.Value == todayUtc)
            {
                _logger.LogInformation(
                    "RecordFlashcardStudy: user '{UserId}' already studied today. No change.",
                    userId);

                var currentStreak = snapshot.ContainsField("current_streak")
                    ? snapshot.GetValue<int>("current_streak") : 0;

                return new UserStatsResponse
                {
                    Coins = coins,
                    CurrentStreak = currentStreak,
                    TotalPoints = totalPoints,
                    LastStudyDate = todayUtc.ToString("o"),
                };
            }

            // Tính streak mới
            int newStreak;
            var yesterdayUtc = todayUtc.AddDays(-1);

            if (lastStudyUtc.HasValue && lastStudyUtc.Value == yesterdayUtc)
            {
                // Học liên tiếp → tăng streak
                var oldStreak = snapshot.ContainsField("current_streak")
                    ? snapshot.GetValue<int>("current_streak") : 0;
                newStreak = oldStreak + 1;
            }
            else
            {
                // Bỏ ngày hoặc lần đầu học → reset về 1
                newStreak = 1;
            }

            // Cập nhật Firestore
            transaction.Update(userRef, new Dictionary<string, object>
            {
                { "current_streak", newStreak },
                { "last_study_date", Timestamp.FromDateTime(
                    DateTime.SpecifyKind(todayUtc, DateTimeKind.Utc)) },
            });

            _logger.LogInformation(
                "RecordFlashcardStudy: user '{UserId}' streak → {Streak}.",
                userId, newStreak);

            return new UserStatsResponse
            {
                Coins = coins,
                CurrentStreak = newStreak,
                TotalPoints = totalPoints,
                LastStudyDate = todayUtc.ToString("o"),
            };

        }, cancellationToken: cancellationToken);

        return updatedStats;
    }
}
