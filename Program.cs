using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Milingo.Backend.Services;
using Microsoft.OpenApi.Models;

LoadEnvironmentFile();

var builder = WebApplication.CreateBuilder(args);


// ══════════════════════════════════════════════════════════════════
//  1. CONFIGURATION — Read secrets from appsettings / env vars
// ══════════════════════════════════════════════════════════════════
var firebaseProjectId = builder.Configuration["Firebase:ProjectId"]
    ?? throw new InvalidOperationException("Firebase:ProjectId is not configured.");

var firebaseKeyPath = builder.Configuration["Firebase:ServiceAccountKeyPath"]
    ?? "firebase-key.json";

var resolvedFirebaseKeyPath = Path.IsPathRooted(firebaseKeyPath)
    ? firebaseKeyPath
    : Path.Combine(builder.Environment.ContentRootPath, firebaseKeyPath);

if (!File.Exists(resolvedFirebaseKeyPath))
{
    throw new FileNotFoundException(
        $"Firebase service account key file not found: {resolvedFirebaseKeyPath}");
}

// ══════════════════════════════════════════════════════════════════
//  2. FIREBASE ADMIN SDK — Server-side verification & Firestore
// ══════════════════════════════════════════════════════════════════
FirebaseApp.Create(new AppOptions
{
    Credential = CredentialFactory
        .FromFile<ServiceAccountCredential>(resolvedFirebaseKeyPath)
        .ToGoogleCredential()
});

// Set the environment variable so the Google.Cloud.Firestore library
// can locate the service account credentials automatically.
Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", resolvedFirebaseKeyPath);

// ══════════════════════════════════════════════════════════════════
//  3. AUTHENTICATION — Firebase JWT Bearer Token Validation
// ══════════════════════════════════════════════════════════════════
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep Firebase claim names as-is (user_id, email, sub, ...)
        options.MapInboundClaims = false;

        // Firebase issues tokens with this authority & issuer
        options.Authority = $"https://securetoken.google.com/{firebaseProjectId}";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://securetoken.google.com/{firebaseProjectId}",
            ValidateAudience = true,
            ValidAudience = firebaseProjectId,
            ValidateLifetime = true,
            NameClaimType = "user_id"
        };
    });

builder.Services.AddAuthorization();

// ══════════════════════════════════════════════════════════════════
//  4. DEPENDENCY INJECTION — Register application services
// ══════════════════════════════════════════════════════════════════

// Firestore: Singleton because FirestoreDb is thread-safe and reusable
builder.Services.AddSingleton(_ => new FirestoreDbBuilder
{
    ProjectId = firebaseProjectId,
    DatabaseId = "milingo"
}.Build());// FirestoreService: Scoped (one instance per HTTP request)
builder.Services.AddScoped<IFirestoreService, FirestoreService>();

// GeminiService disabled: requests now use OpenAiService.
// builder.Services.AddHttpClient<IGeminiService, GeminiService>(...);
builder.Services.AddHttpClient<IOpenAiService, OpenAiService>(client =>
{
    var timeoutSeconds = builder.Configuration.GetValue<int>("OpenAI:TimeoutSeconds", 30);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
});

// YoloService: Typed HttpClient for the YOLO object detection microservice
builder.Services.AddHttpClient<IYoloService, YoloService>(client =>
{
    var baseUrl = builder.Configuration["Yolo:BaseUrl"]
        ?? "http://localhost:8000";
    var timeoutSeconds = builder.Configuration.GetValue<int>("Yolo:TimeoutSeconds", 15);

    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});

// ══════════════════════════════════════════════════════════════════
//  5. API FRAMEWORK — Controllers, OpenAPI, CORS
// ══════════════════════════════════════════════════════════════════
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Milingo Backend API",
        Version = "v1"
    });

    var bearerScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "Enter: Bearer {your Firebase JWT}",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new OpenApiReference
        {
            Type = ReferenceType.SecurityScheme,
            Id = "Bearer"
        }
    };

    options.AddSecurityDefinition("Bearer", bearerScheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [bearerScheme] = Array.Empty<string>()
    });
});

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
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Milingo Backend API v1");
        options.RoutePrefix = "swagger";
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
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
