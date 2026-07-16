using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    public async Task<SnapQuotaStatus> GetSnapQuotaStatusAsync(
        string userId,
        int freeDailyLimit,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(userId);
        var quotaClock = GetSnapQuotaClock();
        var usageRef = userRef.Collection("snap_usage").Document(quotaClock.DayKey);

        var userSnapshotTask = userRef.GetSnapshotAsync(cancellationToken);
        var usageSnapshotTask = usageRef.GetSnapshotAsync(cancellationToken);
        await Task.WhenAll(userSnapshotTask, usageSnapshotTask);

        var isPremium = IsActivePremium(userSnapshotTask.Result);
        var usedToday = GetInt(usageSnapshotTask.Result, "count");

        return BuildSnapQuotaStatus(
            isPremium,
            freeDailyLimit,
            usedToday,
            quotaClock.ResetAtUtc);
    }

    // =================================================================
    //  AI TUTOR CHAT QUOTA
    // =================================================================

    /// <inheritdoc />
    public async Task<ChatQuotaInfo> GetChatQuotaStatusAsync(
        string userId,
        int freeDailyLimit,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(userId);
        var dayKey = GetVietnamDayKey();
        var usageRef = userRef.Collection("chat_usage").Document(dayKey);

        var userSnapshotTask = userRef.GetSnapshotAsync(cancellationToken);
        var usageSnapshotTask = usageRef.GetSnapshotAsync(cancellationToken);
        await Task.WhenAll(userSnapshotTask, usageSnapshotTask);

        var isPremium = IsActivePremium(userSnapshotTask.Result);
        var usedToday = GetInt(usageSnapshotTask.Result, "count");

        return BuildChatQuotaInfo(isPremium, freeDailyLimit, usedToday);
    }

    /// <inheritdoc />
    public async Task<ChatQuotaInfo> IncrementChatUsageAsync(
        string userId,
        int freeDailyLimit,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(userId);
        var dayKey = GetVietnamDayKey();
        var usageRef = userRef.Collection("chat_usage").Document(dayKey);

        // Read premium status and current usage in parallel
        var userSnapshotTask = userRef.GetSnapshotAsync(cancellationToken);
        var usageSnapshotTask = usageRef.GetSnapshotAsync(cancellationToken);
        await Task.WhenAll(userSnapshotTask, usageSnapshotTask);

        var isPremium = IsActivePremium(userSnapshotTask.Result);
        var usedBefore = GetInt(usageSnapshotTask.Result, "count");

        // Premium users bypass quota — still track for analytics but don't block
        var newCount = usedBefore + 1;

        // Atomically increment (merge so the document is created on first use)
        await usageRef.SetAsync(
            new Dictionary<string, object>
            {
                { "count", FieldValue.Increment(1) },
                { "updated_at", FieldValue.ServerTimestamp },
            },
            SetOptions.MergeAll,
            cancellationToken);

        return BuildChatQuotaInfo(isPremium, freeDailyLimit, newCount);
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
            var vocabData = new Dictionary<string, object>
            {
                { "keyword", vocab.Keyword },
                { "translation", vocab.Translation },
                { "pronunciation", vocab.Pronunciation },
                { "example_sentence", vocab.ExampleSentence },
                { "mastery_level", 0.0 },
                { "created_at", FieldValue.ServerTimestamp }
            };

            if (vocab.RelatedWords.Count > 0)
            {
                vocabData["related_words"] = vocab.RelatedWords
                    .Select(word => new Dictionary<string, object>
                    {
                        { "keyword", word.Keyword },
                        { "translation", word.Translation },
                        { "pronunciation", word.Pronunciation }
                    })
                    .ToList();
            }

            transaction.Set(newVocabDoc, vocabData);

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
    public async Task<SnapSaveResult> SaveMultiVocabAndAddCoinsAsync(
        string userId,
        List<SnapVocabItem> vocabItems,
        string idempotencyKey,
        bool usedFallback,
        List<SnapDetectionDetail> detectionDetails,
        int freeDailyLimit,
        CancellationToken cancellationToken = default)
    {
        if (vocabItems.Count == 0)
        {
            _logger.LogWarning("SaveMultiVocabAndAddCoinsAsync called with 0 items for user '{UserId}'.", userId);
            return new SnapSaveResult
            {
                IsNew = false,
                Quota = BuildSnapQuotaStatus(false, freeDailyLimit, 0, GetSnapQuotaClock().ResetAtUtc)
            };
        }

        var userRef = _db.Collection("users").Document(userId);
        var vocabCollection = userRef.Collection("vocabularies");
        var eventRef = userRef.Collection("snap_events").Document(idempotencyKey);
        var quotaClock = GetSnapQuotaClock();
        var usageRef = userRef.Collection("snap_usage").Document(quotaClock.DayKey);
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

        var saveResult = await _db.RunTransactionAsync(async transaction =>
        {
            DocumentSnapshot eventSnapshot = await transaction.GetSnapshotAsync(
                eventRef, cancellationToken);
            DocumentSnapshot userSnapshot = await transaction.GetSnapshotAsync(
                userRef, cancellationToken);
            DocumentSnapshot usageSnapshot = await transaction.GetSnapshotAsync(
                usageRef, cancellationToken);

            var isPremium = IsActivePremium(userSnapshot);
            var usedToday = GetInt(usageSnapshot, "count");
            var quotaBeforeWrite = BuildSnapQuotaStatus(
                isPremium,
                freeDailyLimit,
                usedToday,
                quotaClock.ResetAtUtc);

            if (eventSnapshot.Exists)
            {
                _logger.LogInformation(
                    "Duplicate snap event detected for user '{UserId}', key '{Key}'. Skipping.",
                    userId, idempotencyKey);
                return new SnapSaveResult
                {
                    IsNew = false,
                    Quota = quotaBeforeWrite
                };
            }

            if (quotaBeforeWrite.IsLimitReached)
            {
                throw new SnapQuotaExceededException(quotaBeforeWrite);
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
                eventData["detections"] = detectionDetails.Select(d =>
                {
                    var detectionData = new Dictionary<string, object>
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
                    };

                    if (d.Segmentation?.Points.Count > 0)
                    {
                        detectionData["segmentation"] = new Dictionary<string, object>
                        {
                            {
                                "points",
                                d.Segmentation.Points.Select(p => new Dictionary<string, object>
                                {
                                    { "x", p.X },
                                    { "y", p.Y }
                                }).ToList()
                            }
                        };
                    }

                    return detectionData;
                }).ToList();
            }

            transaction.Set(eventRef, eventData);

            // Calculate and update daily streak (local timezone UTC+7)
            var todayLocal = DateTime.UtcNow.AddHours(7).Date;
            var yesterdayLocal = todayLocal.AddDays(-1);

            DateTime? lastStudyLocal = null;
            if (userSnapshot.ContainsField("last_study_date"))
            {
                var ts = userSnapshot.GetValue<Timestamp>("last_study_date");
                lastStudyLocal = ts.ToDateTimeOffset().UtcDateTime.Date;
            }

            int newStreak = 0;
            bool shouldUpdateStreak = false;

            if (lastStudyLocal.HasValue && lastStudyLocal.Value == todayLocal)
            {
                newStreak = userSnapshot.ContainsField("current_streak")
                    ? userSnapshot.GetValue<int>("current_streak") : 0;
            }
            else
            {
                shouldUpdateStreak = true;
                if (lastStudyLocal.HasValue && lastStudyLocal.Value == yesterdayLocal)
                {
                    var oldStreak = userSnapshot.ContainsField("current_streak")
                        ? userSnapshot.GetValue<int>("current_streak") : 0;
                    newStreak = oldStreak + 1;
                }
                else
                {
                    newStreak = 1;
                }
            }

            var userUpdates = new Dictionary<string, object>
            {
                { "coins", FieldValue.Increment(coinsToAward) },
                { "total_points", FieldValue.Increment(coinsToAward) }
            };

            if (shouldUpdateStreak)
            {
                userUpdates["current_streak"] = newStreak;
                userUpdates["last_study_date"] = Timestamp.FromDateTime(
                    DateTime.SpecifyKind(todayLocal, DateTimeKind.Utc));
            }

            transaction.Set(userRef, userUpdates, SetOptions.MergeAll);

            transaction.Set(usageRef, new Dictionary<string, object>
            {
                { "date_key", quotaClock.DayKey },
                { "count", FieldValue.Increment(1) },
                { "reset_at", Timestamp.FromDateTime(quotaClock.ResetAtUtc) },
                { "updated_at", FieldValue.ServerTimestamp }
            }, SetOptions.MergeAll);

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
                if (item.RelatedWords.Count > 0)
                {
                    vocabData["related_words"] = item.RelatedWords
                        .Select(word => new Dictionary<string, object>
                        {
                            { "keyword", word.Keyword },
                            { "translation", word.Translation },
                            { "pronunciation", word.Pronunciation }
                        })
                        .ToList();
                }

                transaction.Set(newVocabDoc, vocabData);
            }

            return new SnapSaveResult
            {
                IsNew = true,
                Quota = BuildSnapQuotaStatus(
                    isPremium,
                    freeDailyLimit,
                    usedToday + 1,
                    quotaClock.ResetAtUtc)
            };

        }, cancellationToken: cancellationToken);

        if (saveResult.IsNew)
        {
            _logger.LogInformation(
                "Saved {Count} vocabularies and awarded {Coins} coins to user '{UserId}' (key: {Key}).",
                vocabItems.Count, coinsToAward, userId, idempotencyKey);
        }

        return saveResult;
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
            { "total_points", 50 },
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

    /// <inheritdoc />
    public async Task<UserProfileResponse?> GetUserProfileAsync(
        string uid,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(uid);
        var snapshot = await userRef.GetSnapshotAsync(cancellationToken);

        return snapshot.Exists ? MapToUserProfileResponse(uid, snapshot) : null;
    }

    /// <inheritdoc />
    public async Task<UserProfileResponse> UpdateUserProfileAsync(
        string uid,
        string email,
        UpdateUserProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(uid);
        var snapshot = await userRef.GetSnapshotAsync(cancellationToken);
        var updates = new Dictionary<string, object>
        {
            { "updated_at", FieldValue.ServerTimestamp }
        };

        if (!string.IsNullOrWhiteSpace(email))
        {
            updates["email"] = email.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            updates["display_name"] = request.DisplayName.Trim();
        }

        if (request.NativeLanguage is not null)
        {
            updates["native_language"] = request.NativeLanguage.Trim();
        }

        if (request.TargetLanguage is not null)
        {
            updates["target_language"] = request.TargetLanguage.Trim();
        }

        if (request.CefrLevel is not null)
        {
            updates["cefr_level"] = request.CefrLevel.Trim();
        }

        if (request.PhotoUrl is not null)
        {
            updates["photo_url"] = request.PhotoUrl.Trim();
        }

        if (!snapshot.Exists)
        {
            updates["created_at"] = FieldValue.ServerTimestamp;
            updates["coins"] = 50;
            updates["current_streak"] = 0;
            updates["total_points"] = 50;

            if (!updates.ContainsKey("display_name"))
            {
                updates["display_name"] = string.IsNullOrWhiteSpace(email)
                    ? "Người dùng"
                    : email.Split('@')[0];
            }
        }

        await userRef.SetAsync(updates, SetOptions.MergeAll, cancellationToken);

        var updatedSnapshot = await userRef.GetSnapshotAsync(cancellationToken);
        return MapToUserProfileResponse(uid, updatedSnapshot);
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
            { "is_favorite", false },
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
    public async Task<DeckResponse?> SetDeckFavoriteAsync(
        string userId,
        string deckId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        var deckRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId);

        var snapshot = await deckRef.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Exists)
            return null;

        await deckRef.UpdateAsync(new Dictionary<string, object>
        {
            { "is_favorite", isFavorite },
            { "updated_at", FieldValue.ServerTimestamp }
        }, cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Set favorite={IsFavorite} for deck '{DeckId}' and user '{UserId}'.",
            isFavorite, deckId, userId);

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
        var cardKeySnapshots = await deckRef.Collection("card_keys")
            .GetSnapshotAsync(cancellationToken);

        // Firestore batch delete (max 500 per batch, sufficient for flashcards)
        var batch = _db.StartBatch();
        foreach (var cardDoc in cardSnapshots.Documents)
        {
            if (cardDoc.ContainsField("normalized_term") &&
                cardDoc.ContainsField("source_lang_code") &&
                cardDoc.ContainsField("target_lang_code"))
            {
                var cardIndexKey = BuildCardIndexKey(
                    cardDoc.GetValue<string>("normalized_term"),
                    cardDoc.GetValue<string>("source_lang_code"),
                    cardDoc.GetValue<string>("target_lang_code"));

                batch.Set(
                    _db.Collection("users").Document(userId)
                        .Collection("flashcard_card_index").Document(cardIndexKey),
                    new Dictionary<string, object>
                    {
                        { "deck_ids", FieldValue.ArrayRemove(deckId) },
                        { "updated_at", FieldValue.ServerTimestamp }
                    },
                    SetOptions.MergeAll);
            }

            batch.Delete(cardDoc.Reference);
        }
        foreach (var cardKeyDoc in cardKeySnapshots.Documents)
        {
            batch.Delete(cardKeyDoc.Reference);
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

        return snapshots.Documents
            .Select(doc => MapToCardResponse(doc, deckId))
            .ToList();
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
        var cardIndexKey = BuildCardIndexKey(normalizedTerm, sourceLang, targetLang);
        var deckCardKeyRef = deckRef.Collection("card_keys").Document(cardIndexKey);
        var userCardIndexRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_card_index").Document(cardIndexKey);

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
            var deckCardKeySnapshot = await transaction.GetSnapshotAsync(deckCardKeyRef, cancellationToken);
            if (deckCardKeySnapshot.Exists)
            {
                var existingTerm = deckCardKeySnapshot.ContainsField("term")
                    ? deckCardKeySnapshot.GetValue<string>("term")
                    : normalizedTerm;

                return (null, $"Card '{existingTerm}' already exists in this deck.", false);
            }

            // Legacy fallback for cards created before card_keys existed.
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

                transaction.Set(deckCardKeyRef, new Dictionary<string, object>
                {
                    { "card_id", existing.Id },
                    { "term", existingTerm },
                    { "normalized_term", normalizedTerm },
                    { "source_lang_code", sourceLang },
                    { "target_lang_code", targetLang },
                    { "user_id", userId },
                    { "deck_id", deckId },
                    { "updated_at", FieldValue.ServerTimestamp }
                }, SetOptions.MergeAll);

                transaction.Set(userCardIndexRef, new Dictionary<string, object>
                {
                    { "normalized_term", normalizedTerm },
                    { "source_lang_code", sourceLang },
                    { "target_lang_code", targetLang },
                    { "deck_ids", FieldValue.ArrayUnion(deckId) },
                    { "updated_at", FieldValue.ServerTimestamp }
                }, SetOptions.MergeAll);

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
                { "is_favorite", false },
                { "srs_state", "new" },
                { "srs_repetitions", 0 },
                { "srs_easiness_factor", 2.5 },
                { "srs_interval", 0 },
                { "created_at", FieldValue.ServerTimestamp },
                { "updated_at", FieldValue.ServerTimestamp }
            };

            if (request.SourceVocabId is not null)
                cardData["source_vocab_id"] = request.SourceVocabId;
            // When SourceVocabId is null, simply omit the field from the document

            if (!string.IsNullOrWhiteSpace(request.ImageUrl))
                cardData["image_url"] = request.ImageUrl.Trim();

            transaction.Set(newCardRef, cardData);
            transaction.Set(deckCardKeyRef, new Dictionary<string, object>
            {
                { "card_id", newCardRef.Id },
                { "term", request.Term.Trim() },
                { "normalized_term", normalizedTerm },
                { "source_lang_code", sourceLang },
                { "target_lang_code", targetLang },
                { "user_id", userId },
                { "deck_id", deckId },
                { "created_at", FieldValue.ServerTimestamp },
                { "updated_at", FieldValue.ServerTimestamp }
            });

            transaction.Set(userCardIndexRef, new Dictionary<string, object>
            {
                { "normalized_term", normalizedTerm },
                { "source_lang_code", sourceLang },
                { "target_lang_code", targetLang },
                { "deck_ids", FieldValue.ArrayUnion(deckId) },
                { "updated_at", FieldValue.ServerTimestamp }
            }, SetOptions.MergeAll);

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
                SourceVocabId = request.SourceVocabId,
                IsFavorite = false,
                SrsState = "new",
                SrsRepetitions = 0,
                SrsEasinessFactor = 2.5,
                SrsIntervalDays = 0,
                ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl)
                    ? null
                    : request.ImageUrl.Trim()
            };

            return (card, (string?)null, false);

        }, cancellationToken: cancellationToken);

        var (card, conflictMessage, deckNotFound) = result;

        if (card is not null)
        {
            _logger.LogInformation(
                "Added card '{Term}' to deck '{DeckId}' for user '{UserId}'.",
                request.Term, deckId, userId);

            return (card, null, false);
        }

        return (card, conflictMessage, deckNotFound);
    }

    /// <inheritdoc />
    public async Task<CardResponse?> SetCardFavoriteAsync(
        string userId,
        string deckId,
        string cardId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        var deckRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId);
        var cardRef = deckRef.Collection("cards").Document(cardId);

        var deckSnapshot = await deckRef.GetSnapshotAsync(cancellationToken);
        if (!deckSnapshot.Exists)
            return null;

        var cardSnapshot = await cardRef.GetSnapshotAsync(cancellationToken);
        if (!cardSnapshot.Exists)
            return null;

        var batch = _db.StartBatch();
        batch.Update(cardRef, new Dictionary<string, object>
        {
            { "is_favorite", isFavorite },
            { "updated_at", FieldValue.ServerTimestamp }
        });
        batch.Update(deckRef, new Dictionary<string, object>
        {
            { "updated_at", FieldValue.ServerTimestamp }
        });
        await batch.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Set favorite={IsFavorite} for card '{CardId}' in deck '{DeckId}' and user '{UserId}'.",
            isFavorite, cardId, deckId, userId);

        cardSnapshot = await cardRef.GetSnapshotAsync(cancellationToken);
        return MapToCardResponse(cardSnapshot, deckId);
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

            var normalizedTerm = cardSnapshot.ContainsField("normalized_term")
                ? cardSnapshot.GetValue<string>("normalized_term")
                : string.Empty;
            var sourceLang = cardSnapshot.ContainsField("source_lang_code")
                ? cardSnapshot.GetValue<string>("source_lang_code")
                : string.Empty;
            var targetLang = cardSnapshot.ContainsField("target_lang_code")
                ? cardSnapshot.GetValue<string>("target_lang_code")
                : string.Empty;

            // Delete the card
            transaction.Delete(cardRef);
            if (!string.IsNullOrWhiteSpace(normalizedTerm) &&
                !string.IsNullOrWhiteSpace(sourceLang) &&
                !string.IsNullOrWhiteSpace(targetLang))
            {
                var cardIndexKey = BuildCardIndexKey(normalizedTerm, sourceLang, targetLang);
                transaction.Delete(deckRef.Collection("card_keys").Document(cardIndexKey));
                transaction.Set(
                    _db.Collection("users").Document(userId)
                        .Collection("flashcard_card_index").Document(cardIndexKey),
                    new Dictionary<string, object>
                    {
                        { "deck_ids", FieldValue.ArrayRemove(deckId) },
                        { "updated_at", FieldValue.ServerTimestamp }
                    },
                    SetOptions.MergeAll);
            }

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
    //  STUDY
    // =================================================================

    /// <inheritdoc />
    public async Task<List<CardResponse>> GetDueCardsAsync(
        string userId,
        string deckId,
        int limit = 20,
        string? targetLanguageCode = null,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var deckRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId);

        var deckSnapshot = await deckRef.GetSnapshotAsync(cancellationToken);
        if (!deckSnapshot.Exists)
            return new List<CardResponse>();

        var deckName = deckSnapshot.ContainsField("name")
            ? deckSnapshot.GetValue<string>("name")
            : string.Empty;

        return await GetDueCardsFromDeckAsync(
            deckRef,
            deckId,
            deckName,
            limit,
            targetLanguageCode,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task FixIncorrectCardsNextReviewTimeAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var decksSnapshot = await _db.Collection("users").Document(userId)
                .Collection("flashcard_decks")
                .GetSnapshotAsync(cancellationToken);

            var tasks = decksSnapshot.Documents.Select(async deckDoc =>
            {
                var cardsSnapshot = await deckDoc.Reference.Collection("cards")
                    .WhereEqualTo("srs_state", "learning")
                    .WhereEqualTo("srs_repetitions", 0)
                    .GetSnapshotAsync(cancellationToken);

                foreach (var cardDoc in cardsSnapshot.Documents)
                {
                    if (cardDoc.ContainsField("srs_next_review_at"))
                    {
                        var nextReview = cardDoc.GetValue<Timestamp>("srs_next_review_at").ToDateTime();
                        if (nextReview > DateTime.UtcNow)
                        {
                            await cardDoc.Reference.UpdateAsync("srs_next_review_at", Timestamp.FromDateTime(DateTime.UtcNow));
                            _logger.LogInformation(
                                "[DIAG] Auto-corrected next review time to now for card {CardId} ({Term}) in deck {DeckId} because it was incorrectly scheduled.",
                                cardDoc.Id,
                                cardDoc.ContainsField("term") ? cardDoc.GetValue<string>("term") : string.Empty,
                                deckDoc.Id);
                        }
                    }
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-correct incorrect review times for user {UserId}", userId);
        }
    }

    /// <inheritdoc />
    public async Task<List<CardResponse>> GetAllDueCardsAsync(
        string userId,
        int limit = 30,
        string? targetLanguageCode = null,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var decksSnapshot = await _db.Collection("users").Document(userId)
            .Collection("flashcard_decks")
            .GetSnapshotAsync(cancellationToken);

        if (decksSnapshot.Count == 0)
            return new List<CardResponse>();

        var tasks = decksSnapshot.Documents.Select(deckDoc =>
        {
            var deckName = deckDoc.ContainsField("name")
                ? deckDoc.GetValue<string>("name")
                : string.Empty;

            return GetDueCardsFromDeckAsync(
                deckDoc.Reference,
                deckDoc.Id,
                deckName,
                limit,
                targetLanguageCode,
                cancellationToken);
        });

        var dueCards = (await Task.WhenAll(tasks))
            .SelectMany(cards => cards)
            .GroupBy(card => $"{card.DeckId}/{card.Id}")
            .Select(group => group.First())
            .OrderBy(card => card.SrsRepetitions > 0 ? 1 : 0)
            .ThenBy(card => ParseIsoUtc(card.SrsNextReviewAt) ?? DateTime.MinValue)
            .Take(limit)
            .ToList();

        return dueCards;
    }

    private async Task<List<CardResponse>> GetDueCardsFromDeckAsync(
        DocumentReference deckRef,
        string deckId,
        string deckName,
        int limit,
        string? targetLanguageCode,
        CancellationToken cancellationToken)
    {
        var cardsRef = deckRef.Collection("cards");
        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var result = new List<CardResponse>();
        var seenIds = new HashSet<string>();
        var targetLangFilter = targetLanguageCode?.Trim().ToLowerInvariant();
        int fetchLimit = string.IsNullOrEmpty(targetLangFilter) ? limit : limit * 5;

        var brandNewSnapshot = await cardsRef
            .OrderByDescending("created_at")
            .Limit(fetchLimit * 3)
            .GetSnapshotAsync(cancellationToken);

        foreach (var doc in brandNewSnapshot.Documents)
        {
            if (result.Count >= limit)
                break;

            if (!doc.ContainsField("srs_state") && seenIds.Add(doc.Id))
            {
                var card = MapToCardResponse(doc, deckId, deckName);
                if (string.IsNullOrEmpty(targetLangFilter) || string.Equals(card.TargetLangCode, targetLangFilter, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(card);
                }
            }
        }

        if (result.Count < limit)
        {
            var newStateSnapshot = await cardsRef
                .WhereEqualTo("srs_state", "new")
                .Limit(fetchLimit)
                .GetSnapshotAsync(cancellationToken);

            foreach (var doc in newStateSnapshot.Documents)
            {
                if (result.Count >= limit)
                    break;

                if (seenIds.Add(doc.Id))
                {
                    var card = MapToCardResponse(doc, deckId, deckName);
                    if (string.IsNullOrEmpty(targetLangFilter) || string.Equals(card.TargetLangCode, targetLangFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(card);
                    }
                }
            }
        }

        if (result.Count < limit)
        {
            var dueSnapshot = await cardsRef
                .WhereLessThanOrEqualTo("srs_next_review_at", now)
                .OrderBy("srs_next_review_at")
                .Limit(fetchLimit)
                .GetSnapshotAsync(cancellationToken);

            foreach (var doc in dueSnapshot.Documents)
            {
                if (result.Count >= limit)
                    break;

                if (seenIds.Add(doc.Id))
                {
                    var card = MapToCardResponse(doc, deckId, deckName);
                    if (string.IsNullOrEmpty(targetLangFilter) || string.Equals(card.TargetLangCode, targetLangFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(card);
                    }
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> MigrateSrsFieldsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var decksSnapshot = await _db.Collection("users").Document(userId)
            .Collection("flashcard_decks")
            .GetSnapshotAsync(cancellationToken);

        const int batchLimit = 500;
        var totalUpdated = 0;

        foreach (var deckDoc in decksSnapshot.Documents)
        {
            var cardsSnapshot = await deckDoc.Reference.Collection("cards")
                .GetSnapshotAsync(cancellationToken);

            var cardsToMigrate = cardsSnapshot.Documents
                .Where(doc => !doc.ContainsField("srs_state"))
                .ToList();

            if (cardsToMigrate.Count == 0)
            {
                _logger.LogInformation(
                    "SRS migration skipped deck {DeckId}; all {TotalCards} card(s) already have SRS fields.",
                    deckDoc.Id,
                    cardsSnapshot.Count);
                continue;
            }

            _logger.LogInformation(
                "SRS migration updating {CardCount}/{TotalCards} card(s) in deck {DeckId}.",
                cardsToMigrate.Count,
                cardsSnapshot.Count,
                deckDoc.Id);

            foreach (var chunk in cardsToMigrate.Chunk(batchLimit))
            {
                var batch = _db.StartBatch();

                foreach (var cardDoc in chunk)
                {
                    batch.Update(cardDoc.Reference, new Dictionary<string, object>
                    {
                        { "srs_state", "new" },
                        { "srs_repetitions", 0 },
                        { "srs_easiness_factor", 2.5 },
                        { "srs_interval", 0 }
                    });
                }

                await batch.CommitAsync(cancellationToken);
                totalUpdated += chunk.Length;
            }
        }

        _logger.LogInformation(
            "SRS migration completed for user {UserId}. Updated {TotalUpdated} card(s).",
            userId,
            totalUpdated);

        return totalUpdated;
    }

    /// <inheritdoc />
    public async Task<List<CardResponse>> GetDistractorPoolAsync(
        string userId,
        string targetLanguageCode,
        int perDeckLimit = 10,
        int maxCandidates = 50,
        CancellationToken cancellationToken = default)
    {
        perDeckLimit = Math.Clamp(perDeckLimit, 1, 50);
        maxCandidates = Math.Clamp(maxCandidates, 1, 200);
        var normalizedLanguage =
            SupportedLanguages.NormalizeLanguageCode(targetLanguageCode);
        if (normalizedLanguage.Length == 0)
            return new List<CardResponse>();

        var decksSnapshot = await _db.Collection("users").Document(userId)
            .Collection("flashcard_decks")
            .GetSnapshotAsync(cancellationToken);

        var deckTasks = decksSnapshot.Documents.Select(async deck =>
        {
            var deckName = deck.ContainsField("name")
                ? deck.GetValue<string>("name")
                : string.Empty;
            var cards = await deck.Reference.Collection("cards")
                .WhereEqualTo("target_lang_code", normalizedLanguage)
                .Limit(perDeckLimit)
                .GetSnapshotAsync(cancellationToken);

            return cards.Documents.Select(card =>
                MapToCardResponse(card, deck.Id, deckName));
        });

        var candidates = (await Task.WhenAll(deckTasks))
            .SelectMany(cards => cards)
            .OrderBy(_ => Guid.NewGuid());

        return StudyDistractorPolicy.BuildPool(
            candidates,
            normalizedLanguage,
            maxCandidates);
    }

    /// <inheritdoc />
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
            var cardSnapshot = await transaction.GetSnapshotAsync(cardRef, cancellationToken);
            if (!cardSnapshot.Exists)
            {
                _logger.LogWarning(
                    "UpdateCardSrs: card '{CardId}' in deck '{DeckId}' not found for user '{UserId}'.",
                    cardId,
                    deckId,
                    userId);
                return 0;
            }

            var repetitions = cardSnapshot.ContainsField("srs_repetitions")
                ? cardSnapshot.GetValue<int>("srs_repetitions")
                : 0;
            var easinessFactor = cardSnapshot.ContainsField("srs_easiness_factor")
                ? cardSnapshot.GetValue<double>("srs_easiness_factor")
                : 2.5;
            var intervalDays = cardSnapshot.ContainsField("srs_interval")
                ? cardSnapshot.GetValue<int>("srs_interval")
                : 0;

            var srs = Sm2Algorithm.Calculate(
                repetitions,
                easinessFactor,
                intervalDays,
                quality);
            var coins = quality >= 3 ? 5 : 0;

            transaction.Update(cardRef, new Dictionary<string, object>
            {
                { "srs_state", srs.State },
                { "srs_repetitions", srs.Repetitions },
                { "srs_easiness_factor", srs.EasinessFactor },
                { "srs_interval", srs.IntervalDays },
                { "srs_next_review_at", Timestamp.FromDateTime(srs.NextReviewAt) },
                { "srs_last_reviewed_at", FieldValue.ServerTimestamp },
                { "updated_at", FieldValue.ServerTimestamp }
            });

            if (coins > 0)
            {
                transaction.Set(userRef, new Dictionary<string, object>
                {
                    { "coins", FieldValue.Increment(coins) },
                    { "total_points", FieldValue.Increment(coins) }
                }, SetOptions.MergeAll);
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

        return response ?? new SubmitStudyAnswerResponse
        {
            CardId = cardId,
            Mode = mode,
            QualityApplied = quality
        };
    }

    /// <inheritdoc />
    public async Task CacheDistractorsAsync(
        string userId,
        string deckId,
        string cardId,
        List<string> distractors,
        CancellationToken cancellationToken = default)
    {
        var cleaned = distractors
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (cleaned.Count == 0)
            return;

        var cardRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId)
            .Collection("cards").Document(cardId);

        await cardRef.UpdateAsync(new Dictionary<string, object>
        {
            { "srs_distractors", cleaned },
            { "updated_at", FieldValue.ServerTimestamp }
        }, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DeckStudyStats> GetDeckStudyStatsAsync(
        string userId,
        string deckId,
        CancellationToken cancellationToken = default)
    {
        var deckRef = _db.Collection("users").Document(userId)
            .Collection("flashcard_decks").Document(deckId);

        var deckSnapshot = await deckRef.GetSnapshotAsync(cancellationToken);
        if (!deckSnapshot.Exists)
        {
            return new DeckStudyStats { DeckId = deckId };
        }

        var snapshot = await deckRef.Collection("cards").GetSnapshotAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var stats = new DeckStudyStats
        {
            DeckId = deckId,
            TotalCards = snapshot.Count
        };

        foreach (var doc in snapshot.Documents)
        {
            var state = doc.ContainsField("srs_state")
                ? doc.GetValue<string>("srs_state")
                : "new";

            switch (state)
            {
                case "learning":
                    stats.LearningCount++;
                    break;
                case "review":
                    stats.ReviewCount++;
                    break;
                case "mastered":
                    stats.MasteredCount++;
                    break;
                default:
                    stats.NewCount++;
                    break;
            }

            var isDue = !doc.ContainsField("srs_next_review_at");
            if (!isDue)
            {
                var nextReviewAt = doc.GetValue<Timestamp>("srs_next_review_at")
                    .ToDateTimeOffset()
                    .UtcDateTime;
                isDue = nextReviewAt <= now;
            }

            if (isDue)
            {
                stats.DueToday++;
            }
        }

        return stats;
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

        var cardIndexKey = BuildCardIndexKey(normalizedTerm, sourceLang, targetLang);
        var indexSnapshot = await _db.Collection("users").Document(userId)
            .Collection("flashcard_card_index").Document(cardIndexKey)
            .GetSnapshotAsync(cancellationToken);
        if (indexSnapshot.Exists && indexSnapshot.ContainsField("deck_ids"))
        {
            var deckIds = indexSnapshot.GetValue<List<string>>("deck_ids")
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            return new SavedStatusResponse
            {
                IsSaved = deckIds.Count > 0,
                DeckIds = deckIds
            };
        }

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

        if (matchedDeckIds.Count > 0)
        {
            await _db.Collection("users").Document(userId)
                .Collection("flashcard_card_index").Document(cardIndexKey)
                .SetAsync(new Dictionary<string, object>
                {
                    { "normalized_term", normalizedTerm },
                    { "source_lang_code", sourceLang },
                    { "target_lang_code", targetLang },
                    { "deck_ids", matchedDeckIds.Distinct().ToList() },
                    { "updated_at", FieldValue.ServerTimestamp }
                }, SetOptions.MergeAll, cancellationToken);
        }

        return new SavedStatusResponse
        {
            IsSaved = matchedDeckIds.Count > 0,
            DeckIds = matchedDeckIds
        };
    }

    // =================================================================
    //  PREMIUM
    // =================================================================

    /// <inheritdoc />
    public async Task SetPremiumAsync(
        string uid,
        DateTime expiresAt,
        string source,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(uid);
        var expiresAtUtc = DateTime.SpecifyKind(expiresAt.ToUniversalTime(), DateTimeKind.Utc);

        await userRef.SetAsync(new Dictionary<string, object>
        {
            { "isPremium", true },
            { "premiumExpiresAt", Timestamp.FromDateTime(expiresAtUtc) },
            { "premiumSource", source },
            { "updatedAt", FieldValue.ServerTimestamp }
        }, SetOptions.MergeAll, cancellationToken);

        _logger.LogInformation(
            "Premium set for user '{Uid}' via '{Source}' until {ExpiresAt:o}.",
            uid, source, expiresAtUtc);
    }

    /// <inheritdoc />
    public async Task<PremiumStatusResponse> GetPremiumStatusAsync(
        string uid,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(uid);
        var snapshot = await userRef.GetSnapshotAsync(cancellationToken);

        if (!snapshot.Exists)
        {
            return new PremiumStatusResponse();
        }

        var isPremiumFlag = snapshot.ContainsField("isPremium")
            && snapshot.GetValue<bool>("isPremium");
        var expiresAt = GetTimestampUtc(snapshot, "premiumExpiresAt");
        var source = snapshot.ContainsField("premiumSource")
            ? snapshot.GetValue<string?>("premiumSource")
            : null;

        var isPremium = isPremiumFlag
            && expiresAt.HasValue
            && expiresAt.Value > DateTime.UtcNow;

        return new PremiumStatusResponse
        {
            IsPremium = isPremium,
            ExpiresAt = expiresAt,
            Source = source
        };
    }

    // =================================================================
    //  PRIVATE HELPERS
    // =================================================================

    private static SnapQuotaStatus BuildSnapQuotaStatus(
        bool isPremium,
        int freeDailyLimit,
        int usedToday,
        DateTime resetAtUtc)
    {
        var limit = Math.Max(0, freeDailyLimit);
        var normalizedUsed = Math.Max(0, usedToday);
        var remaining = isPremium ? limit : Math.Max(0, limit - normalizedUsed);

        return new SnapQuotaStatus
        {
            IsPremium = isPremium,
            DailyLimit = limit,
            UsedToday = normalizedUsed,
            RemainingToday = remaining,
            IsLimitReached = !isPremium && normalizedUsed >= limit,
            ResetAt = resetAtUtc
        };
    }

    private static SnapQuotaClock GetSnapQuotaClock()
    {
        var vietnamOffset = TimeSpan.FromHours(7);
        var nowLocal = DateTimeOffset.UtcNow.ToOffset(vietnamOffset);
        var nextLocalMidnight = new DateTimeOffset(
            nowLocal.Date.AddDays(1),
            vietnamOffset);

        return new SnapQuotaClock(
            nowLocal.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            nextLocalMidnight.UtcDateTime);
    }

    private static string GetVietnamDayKey()
    {
        var vietnamOffset = TimeSpan.FromHours(7);
        var nowLocal = DateTimeOffset.UtcNow.ToOffset(vietnamOffset);
        return nowLocal.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    private static ChatQuotaInfo BuildChatQuotaInfo(bool isPremium, int freeDailyLimit, int usedToday)
    {
        var limit = Math.Max(0, freeDailyLimit);
        var normalizedUsed = Math.Max(0, usedToday);
        var remaining = isPremium ? int.MaxValue : Math.Max(0, limit - normalizedUsed);

        return new ChatQuotaInfo
        {
            IsPremium = isPremium,
            FreeLimit = limit,
            UsedToday = normalizedUsed,
            RemainingToday = isPremium ? int.MaxValue : remaining,
            IsLimitReached = !isPremium && normalizedUsed >= limit,
        };
    }

    private static bool IsActivePremium(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists)
            return false;

        var isPremiumFlag = snapshot.ContainsField("isPremium")
            && snapshot.GetValue<bool>("isPremium");
        var expiresAt = GetTimestampUtc(snapshot, "premiumExpiresAt");

        return isPremiumFlag
            && expiresAt.HasValue
            && expiresAt.Value > DateTime.UtcNow;
    }

    private static int GetInt(DocumentSnapshot snapshot, string fieldName)
    {
        return snapshot.Exists && snapshot.ContainsField(fieldName)
            ? snapshot.GetValue<int>(fieldName)
            : 0;
    }

    private static DateTime? GetTimestampUtc(DocumentSnapshot snapshot, string fieldName)
    {
        if (!snapshot.ContainsField(fieldName))
            return null;

        return snapshot.GetValue<Timestamp>(fieldName).ToDateTime();
    }

    private static UserProfileResponse MapToUserProfileResponse(
        string uid,
        DocumentSnapshot snapshot)
    {
        var expiresAt = GetTimestampUtc(snapshot, "premiumExpiresAt");
        var isPremiumFlag = snapshot.ContainsField("isPremium")
            && snapshot.GetValue<bool>("isPremium");

        return new UserProfileResponse
        {
            Id = uid,
            DisplayName = GetString(snapshot, "display_name"),
            Email = GetString(snapshot, "email"),
            PhotoUrl = GetNullableString(snapshot, "photo_url"),
            NativeLanguage = GetNullableString(snapshot, "native_language"),
            TargetLanguage = GetNullableString(snapshot, "target_language"),
            CefrLevel = GetString(snapshot, "cefr_level", "A1"),
            IsPremium = isPremiumFlag
                && expiresAt.HasValue
                && expiresAt.Value > DateTime.UtcNow
        };
    }

    private static string GetString(
        DocumentSnapshot snapshot,
        string fieldName,
        string fallback = "")
    {
        return snapshot.ContainsField(fieldName)
            ? snapshot.GetValue<string>(fieldName)
            : fallback;
    }

    private static string? GetNullableString(DocumentSnapshot snapshot, string fieldName)
    {
        return snapshot.ContainsField(fieldName)
            ? snapshot.GetValue<string?>(fieldName)
            : null;
    }

    private sealed record SnapQuotaClock(string DayKey, DateTime ResetAtUtc);

    private static DateTime? ParseIsoUtc(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private static string BuildCardIndexKey(
        string normalizedTerm,
        string sourceLang,
        string targetLang)
    {
        var raw = string.Join(
            "|",
            normalizedTerm.Trim().ToLowerInvariant(),
            sourceLang.Trim().ToLowerInvariant(),
            targetLang.Trim().ToLowerInvariant());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant();
    }

    private static DeckResponse MapToDeckResponse(DocumentSnapshot doc)
    {
        return new DeckResponse
        {
            Id = doc.Id,
            Name = doc.ContainsField("name") ? doc.GetValue<string>("name") : string.Empty,
            Description = doc.ContainsField("description") ? doc.GetValue<string>("description") : string.Empty,
            Emoji = doc.ContainsField("emoji") ? doc.GetValue<string>("emoji") : string.Empty,
            IsDefault = doc.ContainsField("is_default") && doc.GetValue<bool>("is_default"),
            IsFavorite = doc.ContainsField("is_favorite") && doc.GetValue<bool>("is_favorite"),
            VocabCount = doc.ContainsField("vocab_count") ? doc.GetValue<int>("vocab_count") : 0,
            CreatedAt = doc.ContainsField("created_at")
                ? doc.GetValue<Timestamp>("created_at").ToDateTimeOffset().ToString("o")
                : string.Empty,
            UpdatedAt = doc.ContainsField("updated_at")
                ? doc.GetValue<Timestamp>("updated_at").ToDateTimeOffset().ToString("o")
                : string.Empty
        };
    }

    private static CardResponse MapToCardResponse(
        DocumentSnapshot doc,
        string deckId = "",
        string deckName = "")
    {
        return new CardResponse
        {
            Id = doc.Id,
            DeckId = deckId,
            DeckName = deckName,
            Term = doc.ContainsField("term") ? doc.GetValue<string>("term") : string.Empty,
            NormalizedTerm = doc.ContainsField("normalized_term") ? doc.GetValue<string>("normalized_term") : string.Empty,
            Translation = doc.ContainsField("translation") ? doc.GetValue<string>("translation") : string.Empty,
            Pronunciation = doc.ContainsField("pronunciation") ? doc.GetValue<string>("pronunciation") : string.Empty,
            PartOfSpeech = doc.ContainsField("part_of_speech") ? doc.GetValue<string>("part_of_speech") : string.Empty,
            SourceLangCode = doc.ContainsField("source_lang_code") ? doc.GetValue<string>("source_lang_code") : string.Empty,
            TargetLangCode = doc.ContainsField("target_lang_code") ? doc.GetValue<string>("target_lang_code") : string.Empty,
            SourceVocabId = doc.ContainsField("source_vocab_id") ? doc.GetValue<string?>("source_vocab_id") : null,
            ImageUrl = doc.ContainsField("image_url") ? doc.GetValue<string?>("image_url") : null,
            IsFavorite = doc.ContainsField("is_favorite") && doc.GetValue<bool>("is_favorite"),
            CreatedAt = doc.ContainsField("created_at")
                ? doc.GetValue<Timestamp>("created_at").ToDateTimeOffset().ToString("o")
                : string.Empty,
            UpdatedAt = doc.ContainsField("updated_at")
                ? doc.GetValue<Timestamp>("updated_at").ToDateTimeOffset().ToString("o")
                : string.Empty,
            SrsState = doc.ContainsField("srs_state") ? doc.GetValue<string>("srs_state") : "new",
            SrsRepetitions = doc.ContainsField("srs_repetitions") ? doc.GetValue<int>("srs_repetitions") : 0,
            SrsEasinessFactor = doc.ContainsField("srs_easiness_factor") ? doc.GetValue<double>("srs_easiness_factor") : 2.5,
            SrsIntervalDays = doc.ContainsField("srs_interval") ? doc.GetValue<int>("srs_interval") : 0,
            SrsNextReviewAt = doc.ContainsField("srs_next_review_at")
                ? doc.GetValue<Timestamp>("srs_next_review_at").ToDateTimeOffset().ToString("o")
                : null,
            SrsDistractors = doc.ContainsField("srs_distractors")
                ? doc.GetValue<List<string>>("srs_distractors")
                : new List<string>()
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
            var lastStudyLocal = ts.ToDateTimeOffset().UtcDateTime.Date;
            lastStudyDate = ts.ToDateTimeOffset().ToString("o");

            // Daily streak expires if the user didn't study yesterday or today
            var todayLocal = DateTime.UtcNow.AddHours(7).Date;
            var yesterdayLocal = todayLocal.AddDays(-1);
            if (lastStudyLocal < yesterdayLocal && lastStudyLocal != todayLocal)
            {
                streak = 0;
            }
        }
        else
        {
            streak = 0;
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

        // Ngày hôm nay (UTC+7 Vietnam)
        var todayLocal = DateTime.UtcNow.AddHours(7).Date;

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
            DateTime? lastStudyLocal = null;
            if (snapshot.ContainsField("last_study_date"))
            {
                var ts = snapshot.GetValue<Timestamp>("last_study_date");
                lastStudyLocal = ts.ToDateTimeOffset().UtcDateTime.Date;
            }

            // Idempotent: hôm nay đã ghi nhận rồi → không thay đổi gì
            if (lastStudyLocal.HasValue && lastStudyLocal.Value == todayLocal)
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
                    LastStudyDate = todayLocal.ToString("o"),
                };
            }

            // Tính streak mới
            int newStreak;
            var yesterdayLocal = todayLocal.AddDays(-1);

            if (lastStudyLocal.HasValue && lastStudyLocal.Value == yesterdayLocal)
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
                    DateTime.SpecifyKind(todayLocal, DateTimeKind.Utc)) },
            });

            _logger.LogInformation(
                "RecordFlashcardStudy: user '{UserId}' streak → {Streak}.",
                userId, newStreak);

            return new UserStatsResponse
            {
                Coins = coins,
                CurrentStreak = newStreak,
                TotalPoints = totalPoints,
                LastStudyDate = todayLocal.ToString("o"),
            };

        }, cancellationToken: cancellationToken);

        return updatedStats;
    }

    public async Task<LeaderboardResponse> GetLeaderboardAsync(
        string userId,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var usersCollection = _db.Collection("users");
        var snapshot = await usersCollection.GetSnapshotAsync(cancellationToken);

        var allUsers = snapshot.Documents
            .Select(doc =>
            {
                var id = doc.Id;
                var displayName = doc.ContainsField("display_name")
                    ? doc.GetValue<string>("display_name")
                    : (doc.ContainsField("displayName") ? doc.GetValue<string>("displayName") : null);
                if (string.IsNullOrWhiteSpace(displayName) && doc.ContainsField("email"))
                {
                    var email = doc.GetValue<string>("email");
                    if (!string.IsNullOrWhiteSpace(email))
                    {
                        displayName = email.Split('@')[0];
                    }
                }
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = "Milingo User";
                }
                var photoUrl = doc.ContainsField("photo_url")
                    ? doc.GetValue<string?>("photo_url")
                    : null;
                var coins = doc.ContainsField("coins")
                    ? doc.GetValue<int>("coins")
                    : 0;
                var totalPoints = doc.ContainsField("total_points")
                    ? doc.GetValue<int>("total_points")
                    : coins;

                return new LeaderboardUser
                {
                    Id = id,
                    DisplayName = displayName,
                    PhotoUrl = photoUrl,
                    TotalPoints = totalPoints
                };
            })
            .OrderByDescending(u => u.TotalPoints)
            .ToList();

        // Assign ranks (1-based)
        for (int i = 0; i < allUsers.Count; i++)
        {
            allUsers[i].Rank = i + 1;
        }

        var paginatedUsers = allUsers.Skip(offset).Take(limit).ToList();
        var currentUser = allUsers.FirstOrDefault(u => u.Id == userId);

        return new LeaderboardResponse
        {
            Users = paginatedUsers,
            CurrentUser = currentUser
        };
    }
}
