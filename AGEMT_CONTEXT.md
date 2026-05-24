# Milingo Backend Agent Context

Generated on: 2026-05-15
Workspace: `c:\FPTUniversity\MILINGO\PROJECT\BACKEND\Milingo-Backend`
Current branch observed: `Flutter`

Note: this file intentionally follows the requested filename `AGEMT_CONTEXT.md`.
The `.gitignore` currently ignores `AGENT_CONTEXT.md`, but not this misspelled
filename.

## Project Summary

Milingo Backend is an ASP.NET Core Web API for a language-learning app. It uses:

- ASP.NET Core on `.NET 8`
- Firebase Authentication JWT bearer validation
- Google Cloud Firestore as the primary database
- Firebase Admin SDK for server-side Firebase credentials
- Google Gemini API for image-to-vocabulary extraction
- A YOLO FastAPI microservice for object detection and cropped-object analysis
- Swagger/OpenAPI for local API exploration

The backend supports user profile initialization, supported-language metadata,
snap-and-learn image analysis, vocabulary persistence, gamification stats,
flashcard deck CRUD, card CRUD, and saved-card status checks.

## Repository Structure

```text
.
|-- .env.example
|-- .gitignore
|-- AGEMT_CONTEXT.md
|-- CONCLUSION.md
|-- Milingo.Backend.csproj
|-- Milingo.Backend.http
|-- Milingo-Backend.sln
|-- Program.cs
|-- appsettings.Development.json
|-- appsettings.json
|-- Controller/
|   |-- DeckController.cs
|   |-- FlashcardController.cs
|   |-- SnapController.cs
|   `-- UserController.cs
|-- Extensions/
|   `-- ClaimsPrincipalExtensions.cs
|-- Models/
|   |-- ApiResponse.cs
|   |-- CardModels.cs
|   |-- DeckModels.cs
|   |-- InitProfileRequest.cs
|   |-- SnapAnalysisResponse.cs
|   |-- SupportedLanguages.cs
|   |-- UserStatsModel.cs
|   |-- VocabResponse.cs
|   `-- Yolo/
|       |-- DetectedObject.cs
|       `-- YoloDetectionResponse.cs
|-- Properties/
|   `-- launchSettings.json
`-- Services/
    |-- FirestoreService.cs
    |-- GeminiService.cs
    |-- IFirestoreService.cs
    |-- IGeminiService.cs
    |-- IYoloService.cs
    `-- YoloService.cs
```

Generated/build/editor folders also exist locally and should remain ignored:
`.git/`, `.vs/`, `bin/`, and `obj/`.

Local secret files also exist or are expected and should not be committed:
`.env` and `firebase-key.json`.

## Project File and Packages

`Milingo.Backend.csproj`

- Target framework: `net8.0`
- Nullable reference types enabled
- Implicit usings enabled
- NuGet packages:
  - `FirebaseAdmin` 3.5.0
  - `Google.Cloud.Firestore` 4.2.0
  - `Microsoft.AspNetCore.Authentication.JwtBearer` 8.0.0
  - `Microsoft.AspNetCore.OpenApi` 8.0.0
  - `Swashbuckle.AspNetCore` 6.6.2
- `firebase-key.json` is configured with `CopyToOutputDirectory=PreserveNewest`.

`Milingo-Backend.sln`

- Visual Studio solution containing project `Milingo.Backend`.
- Observed as untracked in `git status --short` at the time this file was created.

## Runtime Configuration

`Program.cs` loads `.env` manually before building the app configuration. It checks:

- `./.env`
- `./Milingo.Backend/.env`

The app expects these configuration keys:

```text
Firebase:ProjectId
Firebase:ServiceAccountKeyPath
Gemini:ApiKey
Gemini:Model
Gemini:TimeoutSeconds
Yolo:BaseUrl
Yolo:TimeoutSeconds
```

`.env.example` uses double-underscore environment variable names:

```text
Firebase__ProjectId=your-firebase-project-id
Firebase__ServiceAccountKeyPath=firebase-key.json
Gemini__ApiKey=your-gemini-api-key
Gemini__Model=gemini-1.5-flash
Gemini__TimeoutSeconds=30
```

`appsettings.json` contains empty Firebase/Gemini values and default YOLO values:

- `Yolo:BaseUrl = http://localhost:8000`
- `Yolo:TimeoutSeconds = 15`
- `Gemini:TimeoutSeconds = 30`

`launchSettings.json`

- HTTP profile: `http://0.0.0.0:5098`
- HTTPS profile: `https://0.0.0.0:7175;http://0.0.0.0:5098`
- Launch URL: `swagger`
- Environment: `Development`

## Startup and Middleware

`Program.cs` does the following:

1. Loads `.env` key/value pairs into environment variables when not already set.
2. Reads Firebase project ID and service-account key path.
3. Fails fast if the Firebase service account key file is missing.
4. Creates a Firebase Admin `FirebaseApp`.
5. Sets `GOOGLE_APPLICATION_CREDENTIALS` for Google Cloud Firestore.
6. Configures Firebase JWT bearer auth:
   - Authority: `https://securetoken.google.com/{FirebaseProjectId}`
   - Issuer and audience validation enabled
   - Lifetime validation enabled
   - `NameClaimType = user_id`
   - `MapInboundClaims = false`
7. Registers authorization.
8. Registers Firestore:
   - `FirestoreDb` singleton
   - Project ID from config
   - Database ID hardcoded as `milingo`
9. Registers app services:
   - `IFirestoreService -> FirestoreService` scoped
   - `IGeminiService -> GeminiService` typed `HttpClient`
   - `IYoloService -> YoloService` typed `HttpClient`
10. Adds controllers, Swagger/OpenAPI, and permissive CORS.
11. Enables Swagger only in development.
12. Enables HTTPS redirection only outside development.
13. Uses CORS, authentication, authorization, and maps controllers.

## Authentication Helpers

`Extensions/ClaimsPrincipalExtensions.cs`

- `GetFirebaseUid()` searches claims in this order:
  - `user_id`
  - `sub`
  - `ClaimTypes.NameIdentifier`
  - `uid`
- `GetFirebaseEmailOrEmpty()` searches:
  - `email`
  - `ClaimTypes.Email`

Most API endpoints require `[Authorize]`, except supported languages.

## API Response Shape

All controllers generally return `ApiResponse<T>`:

```json
{
  "status": "success",
  "message": "...",
  "data": {}
}
```

The model itself uses PascalCase C# properties without explicit JSON names:

- `Status`
- `Message`
- `Data`

ASP.NET Core default JSON settings usually serialize these as camelCase unless
configured otherwise.

## Controllers and Endpoints

### UserController

Base route: `/api/v1/users`

- `POST /api/v1/users/init-profile`
  - Requires Firebase JWT.
  - Body: `InitProfileRequest`.
  - Creates a Firestore user profile idempotently.
  - Pulls UID and email from the verified JWT, not the request body.
  - Initial profile fields include email, display name, target language, 50 coins,
    zero current streak, and created timestamp.

- `GET /api/v1/users/supported-languages`
  - Public endpoint via `[AllowAnonymous]`.
  - Returns supported target-language metadata.

- `GET /api/v1/users/stats`
  - Requires Firebase JWT.
  - Returns `UserStatsResponse` with coins, current streak, total points, and
    last study date.

- `POST /api/v1/users/record-study`
  - Requires Firebase JWT.
  - Records that the current user studied flashcards today.
  - Updates streak idempotently:
    - same UTC day: no change
    - yesterday: increment streak
    - older or first study: reset streak to 1

### SnapController

Base route: `/api/v1/snap`

- `POST /api/v1/snap/analyze`
  - Requires Firebase JWT.
  - Accepts multipart image upload in form field `image`.
  - Requires `Idempotency-Key` header.
  - Request size limit: 10 MB.
  - Allowed MIME types: `image/jpeg`, `image/png`, `image/webp`.
  - Performs magic-number validation for JPEG, PNG, and WebP.
  - Checks Firestore idempotency cache before AI/YOLO calls.
  - Sends the image to YOLO first.
  - If YOLO returns valid object detections, sends each cropped object to Gemini
    in parallel.
  - If YOLO is unavailable, returns no objects, or Gemini fails for all detected
    objects, falls back to full-image Gemini analysis.
  - Saves vocabulary items and snap event data to Firestore.
  - Awards 10 coins per vocabulary item for a new idempotency key.
  - Duplicate idempotency keys return cached data and award 0 coins.

### DeckController

Base route: `/api/v1/decks`

- `GET /api/v1/decks`
  - Returns all decks for current user ordered by `created_at` ascending.

- `POST /api/v1/decks`
  - Creates a new flashcard deck.
  - Request: `CreateDeckRequest`.
  - Sets `is_default=false`, `is_favorite=false`, and `vocab_count=0`.

- `PATCH /api/v1/decks/{deckId}`
  - Updates deck name, description, and/or emoji.
  - Does not allow updating `is_default` or `vocab_count`.
  - Requires at least one editable field.

- `PATCH /api/v1/decks/{deckId}/favorite`
  - Body: `{ "isFavorite": true|false }`.
  - Marks or unmarks a deck as favorite for the current user.

- `DELETE /api/v1/decks/{deckId}`
  - Deletes a non-default deck and all cards in its `cards` subcollection.
  - Refuses to delete decks with `is_default=true`.

- `GET /api/v1/decks/{deckId}/cards`
  - Returns all cards in a deck ordered by `created_at` descending.

- `POST /api/v1/decks/{deckId}/cards`
  - Adds a card to a deck inside a Firestore transaction.
  - Checks duplicates by:
    - `normalized_term`
    - `source_lang_code`
    - `target_lang_code`
  - Creates the card, increments `deck.vocab_count`, and updates deck timestamp.
  - Returns `409 Conflict` on duplicate.

- `PATCH /api/v1/decks/{deckId}/cards/{cardId}/favorite`
  - Body: `{ "isFavorite": true|false }`.
  - Marks or unmarks a card as favorite for the current user.

- `DELETE /api/v1/decks/{deckId}/cards/{cardId}`
  - Deletes a card in a transaction.
  - Decrements `deck.vocab_count`, floored at zero.

### FlashcardController

Base route: `/api/v1/flashcards`

- `GET /api/v1/flashcards/saved-status?term=...&sourceLangCode=...&targetLangCode=...`
  - Checks all of the current user's decks for a matching normalized card.
  - Returns `SavedStatusResponse`:
    - `isSaved`
    - `deckIds`

## Service Layer

### FirestoreService

Single service that owns Firestore access for:

- Snap idempotency cache
- Vocabulary saving
- Coin increments
- User profile creation
- Deck CRUD
- Card CRUD
- Deck/card favorite status updates
- Saved-status lookup
- Gamification stats and streak updates

Important implementation details:

- `InitUserProfileAsync` uses `CreateAsync` and treats Firestore
  `AlreadyExists` as a successful no-op.
- Snap saving uses Firestore transactions to avoid duplicate idempotency writes.
- Multi-object snap saving writes:
  - a `snap_events/{idempotencyKey}` document
  - one vocabulary document per detected/analyzed item
  - coin increment on the user document
- Card adding uses a transaction to check duplicates before writing the card and
  incrementing deck count.
- Card deleting uses a transaction to delete the card and update count.
- Streak recording uses a transaction against the user document.

### GeminiService

Typed `HttpClient` service for Google Gemini image analysis.

- Reads `Gemini:ApiKey` and `Gemini:Model` from config.
- Full-image analysis:
  - Converts uploaded stream to base64.
  - Sends image plus prompt to Gemini.
  - Expects JSON vocabulary data.
- Cropped-object analysis:
  - Uses YOLO label as context in the Gemini prompt.
  - Expects the same vocabulary shape.
- Calls:
  - `https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}`
- Requests JSON response MIME type.
- Parses Gemini response envelope and deserializes the text into `VocabResponse`.
- Removes possible markdown JSON fences if Gemini returns them.
- Throws `InvalidOperationException` for malformed/incomplete AI output.
- Throws `HttpRequestException` for non-success Gemini HTTP responses.

### YoloService

Typed `HttpClient` service for a YOLO FastAPI object detection microservice.

- Base URL comes from `Yolo:BaseUrl`.
- Sends multipart image upload to:
  - `/detect?confidence_threshold=0.5&max_objects=3`
- Returns `YoloDetectionResponse` on success.
- Returns `null` on service errors, non-success status codes, bad response data,
  timeout, or network/deserialization failure.
- Propagates cancellation when the client disconnects.
- `null` is intentionally used by `SnapController` to trigger full-image Gemini
  fallback.

## Models

### Common

- `ApiResponse<T>`
  - `Status`
  - `Message`
  - `Data`

### User Profile

- `InitProfileRequest`
  - `DisplayName`
  - `TargetLanguage`
  - Validated against `SupportedLanguages`

- `SupportedLanguages`
  - English, Japanese, Chinese, Korean, French, German, Spanish, Italian
  - Includes language code, native name, and flag emoji metadata.
  - Some Vietnamese/emoji text appears mojibake in the current local files,
    likely due to encoding display or prior save encoding.

### Snap and Vocabulary

- `VocabResponse`
  - `keyword`
  - `translation`
  - `pronunciation`
  - `example_sentence`

- `SnapVocabItem`
  - Same vocabulary fields as `VocabResponse`
  - Optional `detection_label`
  - Optional `detection_confidence`

- `SnapDetectionDetail`
  - `Label`
  - `Confidence`
  - `X`
  - `Y`
  - `Width`
  - `Height`

- `SnapAnalysisResponse`
  - Legacy top-level single-vocab fields for backward compatibility:
    - `keyword`
    - `translation`
    - `pronunciation`
    - `example_sentence`
  - Multi-object fields:
    - `snap_group_id`
    - `object_count`
    - `used_fallback`
    - `vocab_items`
    - `coins_awarded`

### YOLO

- `YoloDetectionResponse`
  - `success`
  - `objects`
  - `totalDetected`
  - `returnedCount`
  - `processingTimeMs`
  - optional `error`

- `DetectedObject`
  - `label`
  - `confidence`
  - `boundingBox`
  - `croppedImageBase64`

- `BoundingBox`
  - `x`
  - `y`
  - `width`
  - `height`

### Decks and Cards

- `CreateDeckRequest`
  - `name`
  - `description`
  - `emoji`

- `UpdateDeckRequest`
  - optional `name`
  - optional `description`
  - optional `emoji`

- `FavoriteRequest`
  - `isFavorite`

- `DeckResponse`
  - `id`
  - `name`
  - `description`
  - `emoji`
  - `is_default`
  - `is_favorite`
  - `vocab_count`
  - `created_at`
  - `updated_at`

- `AddCardRequest`
  - `term`
  - `translation`
  - `pronunciation`
  - `partOfSpeech`
  - `sourceLangCode`
  - `targetLangCode`
  - optional `sourceVocabId`

- `CardResponse`
  - `id`
  - `term`
  - `normalized_term`
  - `translation`
  - `pronunciation`
  - `part_of_speech`
  - `source_lang_code`
  - `target_lang_code`
  - optional `source_vocab_id`
  - optional `image_url`
  - `is_favorite`
  - `created_at`
  - `updated_at`

- `SavedStatusResponse`
  - `isSaved`
  - `deckIds`

### Gamification

- `UserStatsResponse`
  - `Coins`
  - `CurrentStreak`
  - `TotalPoints`
  - optional `LastStudyDate`

- `RecordStudyRequest`
  - Placeholder class for future request-body expansion.
  - Current endpoint does not require a body.

## Firestore Data Shape

Primary root collection:

```text
users/{userId}
```

Known user document fields:

```text
email
display_name
target_language
coins
current_streak
total_points
last_study_date
created_at
```

`total_points` is optional in current logic. If missing, stats response uses
`coins` as total points.

### Vocabularies

```text
users/{userId}/vocabularies/{vocabId}
```

Known fields:

```text
keyword
translation
pronunciation
example_sentence
snap_group_id
mastery_level
created_at
detection_label
detection_confidence
```

`snap_group_id`, `detection_label`, and `detection_confidence` are used in the
multi-object snap path. The older single-vocab save method writes no
`snap_group_id`.

### Snap Events

```text
users/{userId}/snap_events/{idempotencyKey}
```

Known fields:

```text
keyword
keywords
object_count
coins_awarded
used_fallback
cached_response
processed_at
detections
```

The current multi-object path stores `cached_response` as serialized JSON and
uses it for idempotent duplicate responses.

### Flashcard Decks

```text
users/{userId}/flashcard_decks/{deckId}
```

Known fields:

```text
name
description
emoji
is_default
is_favorite
vocab_count
created_at
updated_at
```

### Cards

```text
users/{userId}/flashcard_decks/{deckId}/cards/{cardId}
```

Known fields:

```text
term
normalized_term
translation
pronunciation
part_of_speech
source_lang_code
target_lang_code
source_vocab_id
image_url
is_favorite
created_at
updated_at
```

Duplicate detection uses `normalized_term`, `source_lang_code`, and
`target_lang_code`.

Fast duplicate/status indexes:

```text
users/{userId}/flashcard_decks/{deckId}/card_keys/{hash}
users/{userId}/flashcard_card_index/{hash}
```

`hash` is SHA-256 of `normalized_term|source_lang_code|target_lang_code`.
`card_keys` gives per-deck duplicate checks a point-read path.
`flashcard_card_index` stores `deck_ids` so saved-status avoids scanning every
deck. Legacy cards without these index docs still work through fallback queries
and are backfilled lazily when duplicate/status checks touch them.

## Existing API Scratch File

`Milingo.Backend.http` includes examples for:

- `POST /api/v1/snap/analyze`
- Duplicate snap request behavior
- Missing idempotency key behavior
- Missing auth behavior
- Direct YOLO health check
- Direct YOLO detection
- `POST /api/v1/users/init-profile`

The `init-profile` example currently sends `"targetLanguage": "vi"`, but the
backend validator expects language names such as `"English"` or `"Japanese"`,
not language codes.

## Current Known Work Completed

Implemented backend capabilities observed in this snapshot:

- Firebase JWT authentication setup.
- Firestore credential loading and `milingo` database connection.
- User profile initialization with welcome coins.
- Supported-language endpoint and validation.
- Snap image upload with idempotency key enforcement.
- MIME type and magic-number image validation.
- YOLO-first object detection flow.
- Gemini fallback full-image flow.
- Gemini cropped-object analysis flow.
- Multi-object vocabulary response shape while preserving legacy fields.
- Firestore persistence for snap events and vocabularies.
- Coin awards for snap analysis.
- Deck create/read/update/delete.
- Card create/read/delete.
- Deck/card favorite status update.
- Duplicate card prevention.
- Saved-card status lookup across all decks.
- User stats endpoint.
- Flashcard study streak recording endpoint.

`CONCLUSION.md` also notes frontend work that was planned or done outside this
backend repo, including Flutter/Riverpod gamification providers and API methods.
That frontend code is not present in this backend workspace.

## Known Gaps and Risks

- No automated tests are present in this repository.
- No explicit CI workflow files were found under `.github`.
- `.env` and `firebase-key.json` exist locally; they are ignored and should stay
  out of source control.
- `Milingo-Backend.sln` was untracked when inspected.
- Several source comments and localized strings appear mojibake in terminal
  output, suggesting encoding issues in some files.
- `appsettings.json` contains empty Firebase and Gemini values, so local runs
  require `.env` or environment variables.
- `Program.cs` hardcodes Firestore database ID as `milingo`.
- `SaveVocabAndAddCoinsAsync` remains in the service for the older single-vocab
  path, but the current `SnapController` uses `SaveMultiVocabAndAddCoinsAsync`.
- CORS currently allows any origin, header, and method.
- Swagger is development-only.
- HTTPS redirection is disabled in development and enabled outside development.
- YOLO failures intentionally fall back to Gemini, which is user-friendly but can
  hide object-detection service outages unless logs are monitored.
- Saved-status lookup scans every deck and queries cards per deck. This is simple
  but may become expensive with many decks.
- Deck delete batches all card deletes in one batch and assumes the count is
  within Firestore batch limits.
- `RecordFlashcardStudyAsync` uses UTC dates, not the user's local timezone.
- Stats response property JSON casing may depend on ASP.NET Core default naming
  policy because `UserStatsResponse` does not specify `JsonPropertyName`.

## How to Run Locally

Expected setup:

1. Install .NET 8 SDK.
2. Create `.env` from `.env.example`.
3. Place Firebase service account JSON at the configured key path.
4. Ensure Firebase project ID matches the project that issues client JWTs.
5. Start the YOLO service on `http://localhost:8000` if testing object detection.
6. Run the backend:

```powershell
dotnet run
```

Development Swagger URL:

```text
http://localhost:5098/swagger
```

## Recommended Next Steps

- Add backend tests for Firestore-facing service logic with fakes or an emulator.
- Add controller tests for auth-required, bad-request, duplicate, and not-found
  paths.
- Decide whether the supported-language API should accept names, codes, or both.
- Fix text encoding in comments/docs where mojibake appears.
- Consider indexing or denormalizing saved-card status for larger user libraries.
- Consider user-local timezone handling for streaks.
- Tighten CORS for production.
- Review whether `firebase-key.json` should be copied to output in all builds.
- Add a CI workflow for build/test.
