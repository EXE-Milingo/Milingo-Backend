using Google.Cloud.Firestore;
using Grpc.Core;
using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Handles all Firestore database operations for user profiles and vocabularies.
/// Schema: users/{uid} (profile doc) → users/{uid}/vocabularies (flashcard sub-collection).
/// Idempotency ledger: users/{uid}/snap_events/{idempotencyKey}.
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

        // Use a Firestore Transaction to ensure atomicity + idempotency:
        // 1. Check if this idempotency key was already processed
        // 2. If not, save vocab + increment coins + record the event — all or nothing
        var isNewRequest = await _db.RunTransactionAsync(async transaction =>
        {
            // ─── IDEMPOTENCY CHECK ───
            // Read the snap_events ledger to see if this key was already used
            DocumentSnapshot eventSnapshot = await transaction.GetSnapshotAsync(
                eventRef, cancellationToken);

            if (eventSnapshot.Exists)
            {
                // This request was already processed — skip all writes
                _logger.LogInformation(
                    "Duplicate snap event detected for user '{UserId}', key '{Key}'. Skipping.",
                    userId, idempotencyKey);
                return false;
            }

            // ─── 1. RECORD THE SNAP EVENT (idempotency ledger) ───
            transaction.Set(eventRef, new Dictionary<string, object>
            {
                { "keyword", vocab.Keyword },
                { "processed_at", FieldValue.ServerTimestamp }
            });

            // ─── 2. ADD COINS ATOMICALLY ───
            // FieldValue.Increment eliminates the read-modify-write pattern.
            // If the user doc doesn't exist, Set + MergeAll creates it with coins = 10.
            // If it exists, coins is atomically incremented by 10 on the server side.
            transaction.Set(userRef,
                new Dictionary<string, object> { { "coins", FieldValue.Increment(10) } },
                SetOptions.MergeAll);

            // ─── 3. SAVE VOCABULARY FLASHCARD ───
            var newVocabDoc = vocabCollection.Document(); // Auto-generate document ID
            var vocabData = new Dictionary<string, object>
            {
                { "keyword", vocab.Keyword },
                { "translation", vocab.Translation },
                { "pronunciation", vocab.Pronunciation },
                { "example_sentence", vocab.ExampleSentence },
                { "mastery_level", 0.0 },
                { "created_at", FieldValue.ServerTimestamp }
            };

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
    public async Task<bool> InitUserProfileAsync(
        string uid,
        string email,
        string displayName,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var userRef = _db.Collection("users").Document(uid);

        // ─── CREATE PROFILE with initial data ───
        var profileData = new Dictionary<string, object>
        {
            { "email", email },
            { "display_name", displayName },
            { "target_language", targetLanguage },
            { "coins", 50 },               // Welcome bonus
            { "current_streak", 0 },
            { "created_at", FieldValue.ServerTimestamp }
        };

        try
        {
            // CreateAsync is atomic "create-if-not-exists".
            // If another concurrent request already created this doc, Firestore returns AlreadyExists.
            await userRef.CreateAsync(profileData, cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Created profile for user '{Uid}' with 50 welcome coins.", uid);

            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            _logger.LogInformation(
                "Profile already exists for user '{Uid}'. Skipping creation.", uid);
            return false;
        }
    }
}
