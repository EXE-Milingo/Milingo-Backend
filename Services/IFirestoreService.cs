using Milingo.Backend.Models;

namespace Milingo.Backend.Services;

/// <summary>
/// Abstraction for Firestore database operations.
/// </summary>
public interface IFirestoreService
{
    /// <summary>
    /// Saves a vocabulary flashcard to the user's sub-collection and awards 10 coins,
    /// all within a single Firestore transaction with idempotency protection.
    /// </summary>
    /// <param name="userId">The Firebase UID of the user.</param>
    /// <param name="vocab">The vocabulary data to save.</param>
    /// <param name="idempotencyKey">
    /// A unique key for this snap event. If the key has already been processed,
    /// the method returns <c>false</c> without making any changes.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> if the vocabulary was saved (new request);
    /// <c>false</c> if the idempotency key was already processed (duplicate request).
    /// </returns>
    Task<bool> SaveVocabAndAddCoinsAsync(
        string userId,
        VocabResponse vocab,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a user profile document at <c>users/{uid}</c> with initial data.
    /// If the document already exists, it is NOT overwritten (idempotent).
    /// </summary>
    /// <param name="uid">The Firebase UID (used as the Firestore document ID).</param>
    /// <param name="email">The user's email from the JWT.</param>
    /// <param name="displayName">The display name chosen by the user.</param>
    /// <param name="targetLanguage">The language the user wants to learn.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> if a new profile was created;
    /// <c>false</c> if the profile already existed and was left unchanged.
    /// </returns>
    Task<bool> InitUserProfileAsync(
        string uid,
        string email,
        string displayName,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
