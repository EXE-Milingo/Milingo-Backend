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
}
