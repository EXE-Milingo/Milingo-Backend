using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Abstraction for Firestore database operations.
/// </summary>
public interface IFirestoreService
{
    // =================================================================
    //  SNAP & LEARN
    // =================================================================

    Task<SnapAnalysisResponse?> GetCachedSnapResultAsync(
        string userId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<SnapQuotaStatus> GetSnapQuotaStatusAsync(
        string userId,
        int freeDailyLimit,
        CancellationToken cancellationToken = default);

    Task<bool> SaveVocabAndAddCoinsAsync(
        string userId,
        VocabResponse vocab,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<SnapSaveResult> SaveMultiVocabAndAddCoinsAsync(
        string userId,
        List<SnapVocabItem> vocabItems,
        string idempotencyKey,
        bool usedFallback,
        List<SnapDetectionDetail> detectionDetails,
        int freeDailyLimit,
        CancellationToken cancellationToken = default);

    // =================================================================
    //  AI TUTOR CHAT QUOTA
    // =================================================================

    /// <summary>
    /// Returns the current daily chat quota status for a user.
    /// Premium users bypass the limit.
    /// </summary>
    Task<ChatQuotaInfo> GetChatQuotaStatusAsync(
        string userId,
        int freeDailyLimit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments the user's daily chat usage counter.
    /// Returns the updated quota status after the increment.
    /// </summary>
    Task<ChatQuotaInfo> IncrementChatUsageAsync(
        string userId,
        int freeDailyLimit,
        CancellationToken cancellationToken = default);



    // =================================================================
    //  USER PROFILE
    // =================================================================

    Task<bool> InitUserProfileAsync(
        string uid,
        string email,
        string displayName,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    Task<UserProfileResponse?> GetUserProfileAsync(
        string uid,
        CancellationToken cancellationToken = default);

    Task<UserProfileResponse> UpdateUserProfileAsync(
        string uid,
        string email,
        UpdateUserProfileRequest request,
        CancellationToken cancellationToken = default);

    // =================================================================
    //  DECKS
    // =================================================================

    /// <summary>
    /// Returns all flashcard decks for a user, ordered by created_at ascending.
    /// </summary>
    Task<List<DeckResponse>> GetDecksAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new flashcard deck. Sets is_default=false, vocab_count=0.
    /// </summary>
    /// <returns>The created deck with its generated ID.</returns>
    Task<DeckResponse> CreateDeckAsync(
        string userId,
        CreateDeckRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates name, description, and/or emoji of an existing deck.
    /// Does NOT allow changing is_default or vocab_count.
    /// </summary>
    /// <returns>The updated deck, or null if the deck was not found.</returns>
    Task<DeckResponse?> UpdateDeckAsync(
        string userId,
        string deckId,
        UpdateDeckRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks or unmarks a deck as a favorite for the current user.
    /// </summary>
    /// <returns>The updated deck, or null if the deck was not found.</returns>
    Task<DeckResponse?> SetDeckFavoriteAsync(
        string userId,
        string deckId,
        bool isFavorite,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a deck and all its cards.
    /// Fails if the deck is a default deck (is_default=true).
    /// </summary>
    /// <returns>
    /// A tuple: (success, errorReason).
    /// success=true if deleted; false if not found or is default.
    /// </returns>
    Task<(bool Success, string? ErrorReason)> DeleteDeckAsync(
        string userId,
        string deckId,
        CancellationToken cancellationToken = default);

    // =================================================================
    //  CARDS
    // =================================================================

    /// <summary>
    /// Returns all cards in a deck, ordered by created_at descending.
    /// Returns null if the deck does not exist.
    /// </summary>
    Task<List<CardResponse>?> GetCardsAsync(
        string userId,
        string deckId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a card to a deck using a Firestore transaction:
    ///   1. Check for duplicate (normalized_term + source_lang_code + target_lang_code)
    ///   2. Create the card document
    ///   3. Increment deck.vocab_count by 1
    ///   4. Update deck.updated_at
    /// </summary>
    /// <returns>
    /// A tuple: (card, conflictMessage).
    /// card is non-null on success; conflictMessage is non-null on duplicate.
    /// Both null means deck not found.
    /// </returns>
    Task<(CardResponse? Card, string? ConflictMessage, bool DeckNotFound)> AddCardAsync(
        string userId,
        string deckId,
        AddCardRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks or unmarks a card as a favorite for the current user.
    /// </summary>
    /// <returns>The updated card, or null if the deck or card was not found.</returns>
    Task<CardResponse?> SetCardFavoriteAsync(
        string userId,
        string deckId,
        string cardId,
        bool isFavorite,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a card using a Firestore transaction:
    ///   1. Delete the card document
    ///   2. Decrement deck.vocab_count by 1 (floor at 0)
    ///   3. Update deck.updated_at
    /// </summary>
    /// <returns>
    /// A tuple: (success, errorReason).
    /// success=true if deleted; false if deck or card not found.
    /// </returns>
    Task<(bool Success, string? ErrorReason)> DeleteCardAsync(
        string userId,
        string deckId,
        string cardId,
        CancellationToken cancellationToken = default);

    // =================================================================
    //  STUDY
    // =================================================================

    Task<List<CardResponse>> GetDueCardsAsync(
        string userId,
        string deckId,
        int limit = 20,
        string? targetLanguageCode = null,
        CancellationToken cancellationToken = default);

    Task FixIncorrectCardsNextReviewTimeAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns due cards across all decks for the current user's daily review.
    /// First-review cards are included first, then cards due by review date.
    /// </summary>
    Task<List<CardResponse>> GetAllDueCardsAsync(
        string userId,
        int limit = 30,
        string? targetLanguageCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One-time SRS backfill for existing cards. Scans every deck owned by
    /// the user and writes default SRS fields only to cards missing srs_state.
    /// </summary>
    Task<int> MigrateSrsFieldsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<List<CardResponse>> GetDistractorCardsAsync(
        string userId,
        string deckId,
        string targetLanguageCode,
        IEnumerable<string> excludeCardIds,
        int count = 3,
        CancellationToken cancellationToken = default);

    Task<SubmitStudyAnswerResponse> UpdateCardSrsAsync(
        string userId,
        string deckId,
        string cardId,
        int quality,
        string mode,
        CancellationToken cancellationToken = default);

    Task CacheDistractorsAsync(
        string userId,
        string deckId,
        string cardId,
        List<string> distractors,
        CancellationToken cancellationToken = default);

    Task<DeckStudyStats> GetDeckStudyStatsAsync(
        string userId,
        string deckId,
        CancellationToken cancellationToken = default);

    // =================================================================
    //  FLASHCARD UTILITIES
    // =================================================================

    /// <summary>
    /// Checks whether a normalized term (with lang codes) has been saved
    /// in any of the user's decks.
    /// </summary>
    Task<SavedStatusResponse> GetSavedStatusAsync(
        string userId,
        string term,
        string sourceLangCode,
        string targetLangCode,
        CancellationToken cancellationToken = default);

    // =================================================================
    //  PREMIUM
    // =================================================================

    Task SetPremiumAsync(
        string uid,
        DateTime expiresAt,
        string source,
        CancellationToken cancellationToken = default);

    Task<PremiumStatusResponse> GetPremiumStatusAsync(
        string uid,
        CancellationToken cancellationToken = default);

    // =================================================================
    //  GAMIFICATION
    // =================================================================

    /// <summary>
    /// Trả về stats của user: coins, streak, totalPoints.
    /// Trả về UserStatsResponse với giá trị mặc định 0 nếu user chưa có profile.
    /// </summary>
    Task<UserStatsResponse> GetUserStatsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ghi nhận user học flashcard hôm nay.
    /// - Nếu hôm nay đã ghi nhận rồi → không làm gì (idempotent).
    /// - Nếu hôm qua có ghi nhận → tăng streak +1.
    /// - Nếu bỏ ngày (quá hôm qua) → reset streak về 1.
    /// Trả về UserStatsResponse sau khi cập nhật.
    /// </summary>
    Task<UserStatsResponse> RecordFlashcardStudyAsync(
        string userId,
        CancellationToken cancellationToken = default);

    // =================================================================
    //  RANKING / LEADERBOARD
    // =================================================================

    Task<LeaderboardResponse> GetLeaderboardAsync(
        string userId,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);
}
