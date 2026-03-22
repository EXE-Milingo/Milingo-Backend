using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Milingo.Backend.Services;

LoadEnvironmentFile();

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════════════════════════════
//  1. CONFIGURATION — Read secrets from appsettings / env vars
// ══════════════════════════════════════════════════════════════════
var firebaseProjectId = builder.Configuration["Firebase:ProjectId"]
    ?? throw new InvalidOperationException("Firebase:ProjectId is not configured.");

var firebaseKeyPath = builder.Configuration["Firebase:ServiceAccountKeyPath"]
    ?? "firebase-key.json";

// ══════════════════════════════════════════════════════════════════
//  2. FIREBASE ADMIN SDK — Server-side verification & Firestore
// ══════════════════════════════════════════════════════════════════
FirebaseApp.Create(new AppOptions
{
    Credential = GoogleCredential.FromFile(firebaseKeyPath)
});

// Set the environment variable so the Google.Cloud.Firestore library
// can locate the service account credentials automatically.
Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", firebaseKeyPath);

// ══════════════════════════════════════════════════════════════════
//  3. AUTHENTICATION — Firebase JWT Bearer Token Validation
// ══════════════════════════════════════════════════════════════════
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Firebase issues tokens with this authority & issuer
        options.Authority = $"https://securetoken.google.com/{firebaseProjectId}";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://securetoken.google.com/{firebaseProjectId}",
            ValidateAudience = true,
            ValidAudience = firebaseProjectId,
            ValidateLifetime = true
        };
    });

builder.Services.AddAuthorization();

// ══════════════════════════════════════════════════════════════════
//  4. DEPENDENCY INJECTION — Register application services
// ══════════════════════════════════════════════════════════════════

// Firestore: Singleton because FirestoreDb is thread-safe and reusable
builder.Services.AddSingleton(_ => FirestoreDb.Create(firebaseProjectId));

// FirestoreService: Scoped (one instance per HTTP request)
builder.Services.AddScoped<IFirestoreService, FirestoreService>();

// GeminiService: Typed HttpClient via IHttpClientFactory (prevents socket exhaustion)
builder.Services.AddHttpClient<IGeminiService, GeminiService>(client =>
{
    var timeoutSeconds = builder.Configuration.GetValue<int>("Gemini:TimeoutSeconds", 30);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
});

// ══════════════════════════════════════════════════════════════════
//  5. API FRAMEWORK — Controllers, OpenAPI, CORS
// ══════════════════════════════════════════════════════════════════
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// ══════════════════════════════════════════════════════════════════
//  BUILD & CONFIGURE MIDDLEWARE PIPELINE
// ══════════════════════════════════════════════════════════════════
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors();

// IMPORTANT: Authentication must come before Authorization
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

static void LoadEnvironmentFile()
{
    var candidatePaths = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(Directory.GetCurrentDirectory(), "Milingo.Backend", ".env")
    };

    var envPath = candidatePaths.FirstOrDefault(File.Exists);
    if (envPath is null)
    {
        return;
    }

    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
        {
            continue;
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim().Trim('"');

        if (!string.IsNullOrEmpty(key) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
