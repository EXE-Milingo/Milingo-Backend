# Admin Analytics Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a backend-powered admin analytics dashboard for revenue, users, transactions, and app reviews.

**Architecture:** Add a focused backend admin analytics service and controller that aggregate Firestore data from `payment_orders`, `users`, and `app_reviews`. Update the React admin dashboard to call the backend with the Firebase ID token and render KPI cards, growth charts, rating pie, user split, and recent paid transactions.

**Tech Stack:** ASP.NET Core 8, Firebase JWT auth, Google Cloud Firestore, React 18, Firebase Web SDK, Tailwind CSS, lucide-react.

---

## File Structure

- Create `Models/AdminAnalyticsModels.cs`: response DTOs and query normalization model.
- Create `Services/IAdminAnalyticsService.cs`: service contract.
- Create `Services/AdminAnalyticsService.cs`: Firestore aggregation implementation.
- Create `Controller/AdminAnalyticsController.cs`: authorized admin analytics endpoint.
- Modify `Program.cs`: register `IAdminAnalyticsService`.
- Modify `C:\FPTU\SP26\EXE\milingo_admin\src\pages\Dashboard.jsx`: replace Firestore-only dashboard with API-backed analytics UI and inline SVG/CSS charts.
- Modify `C:\FPTU\SP26\EXE\milingo_admin\README.md`: document `REACT_APP_API_BASE_URL`.

No automated tests will be added because the user explicitly requested no tests. Verification is build and manual UI review.

---

### Task 1: Backend Analytics Models

**Files:**
- Create: `Models/AdminAnalyticsModels.cs`

- [ ] **Step 1: Add response DTOs**

Create DTOs with these properties:

```csharp
namespace Milingo.Backend.Models;

public class AdminAnalyticsOverviewResponse
{
    public AdminAnalyticsTotalsResponse Totals { get; set; } = new();
    public List<AdminAnalyticsGrowthPointResponse> Growth { get; set; } = new();
    public AdminAnalyticsRatingsResponse Ratings { get; set; } = new();
    public List<AdminAnalyticsTransactionResponse> Transactions { get; set; } = new();
}
```

Include totals for `Revenue`, `Users`, `Admins`, `Customers`, `Transactions`, `Reviews`, and `AverageRating`.

- [ ] **Step 2: Add growth, ratings, and transaction DTOs**

Add `AdminAnalyticsGrowthPointResponse`, `AdminAnalyticsRatingsResponse`, and `AdminAnalyticsTransactionResponse` with JSON-friendly PascalCase properties. The project uses ASP.NET Core web defaults, so response JSON becomes camelCase.

---

### Task 2: Backend Analytics Service

**Files:**
- Create: `Services/IAdminAnalyticsService.cs`
- Create: `Services/AdminAnalyticsService.cs`

- [ ] **Step 1: Add service contract**

Expose:

```csharp
Task<AdminAnalyticsOverviewResponse> GetOverviewAsync(
    string granularity,
    DateTime? from,
    DateTime? to,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 2: Implement Firestore reads**

Read:

- `payment_orders` for paid orders only.
- `users` for total/admin/customer users.
- `app_reviews` for ratings. Missing collection returns empty result.

Paid statuses are `PAID`, `SUCCESS`, and `COMPLETED`.

- [ ] **Step 3: Implement date bucketing**

Normalize granularity:

- `day`: period key `yyyy-MM-dd`.
- `month`: period key `yyyy-MM`.
- `year`: period key `yyyy`.

Use `paidAt ?? updatedAt ?? createdAt` for paid order dates. Use `created_at ?? createdAt` for user dates. Use `createdAt ?? created_at` for review dates.

- [ ] **Step 4: Implement totals**

Compute:

- revenue = sum of paid order amounts in date range.
- transactions = count of paid orders in date range.
- users = total users in date range if user creation date exists, otherwise include in all-time totals.
- admins = user has `role = admin`, `isAdmin = true`, `admin = true`, or email appears in `Admin:Emails`.
- customers = users - admins.
- review distribution = rating 3, 4, 5.
- average rating = average of all valid ratings 1 through 5.

---

### Task 3: Backend Controller and DI

**Files:**
- Create: `Controller/AdminAnalyticsController.cs`
- Modify: `Program.cs`

- [ ] **Step 1: Add controller**

Create `AdminAnalyticsController` with route:

```csharp
[ApiController]
[Route("api/v1/admin/analytics")]
[Authorize]
public class AdminAnalyticsController : ControllerBase
```

Endpoint:

```csharp
[HttpGet("overview")]
public async Task<IActionResult> GetOverview(
    [FromQuery] string granularity = "day",
    [FromQuery] DateTime? from = null,
    [FromQuery] DateTime? to = null,
    CancellationToken cancellationToken = default)
```

- [ ] **Step 2: Add admin guard**

Authorize if either:

- JWT has claim `admin = true`.
- JWT email appears in `Admin:Emails`.

Return `403` with `ApiResponse<object>` when not admin.

- [ ] **Step 3: Register service**

Add:

```csharp
builder.Services.AddScoped<IAdminAnalyticsService, AdminAnalyticsService>();
```

near the other scoped services in `Program.cs`.

---

### Task 4: Admin Dashboard API Client and UI

**Files:**
- Modify: `C:\FPTU\SP26\EXE\milingo_admin\src\pages\Dashboard.jsx`

- [ ] **Step 1: Replace direct Firestore dashboard load**

Use `user.getIdToken()` and `fetch`:

```js
const token = await user.getIdToken();
const response = await fetch(`${apiBaseUrl}/api/v1/admin/analytics/overview?granularity=${granularity}`, {
  headers: { Authorization: `Bearer ${token}` }
});
```

Default `apiBaseUrl`:

```js
const API_BASE_URL = process.env.REACT_APP_API_BASE_URL || "http://localhost:5098";
```

- [ ] **Step 2: Render KPI cards**

Cards:

- Revenue.
- Users, with admin/customer helper.
- Transactions.
- Average review, with review count helper.

- [ ] **Step 3: Render charts**

Use lightweight inline SVG/CSS:

- Bar/line hybrid growth chart for revenue, users, transactions, reviews.
- Segmented control for `day`, `month`, `year`.
- Pie chart for rating 3, 4, 5.
- Mini split bar for admins vs customers.

- [ ] **Step 4: Render recent transactions**

Table columns:

- Customer.
- Plan.
- Amount.
- Source.
- Paid date.
- Status.

Keep loading, error, empty states and refresh.

---

### Task 5: Admin README

**Files:**
- Modify: `C:\FPTU\SP26\EXE\milingo_admin\README.md`

- [ ] **Step 1: Document backend API URL**

Add:

```text
REACT_APP_API_BASE_URL=http://localhost:5098
```

Explain that the admin app sends the Firebase ID token to the backend admin analytics endpoint.

---

### Task 6: Verification

**Files:**
- No source edits.

- [ ] **Step 1: Build backend**

Run:

```powershell
dotnet build
```

Expected: exit code `0`.

- [ ] **Step 2: Build admin**

Run from `C:\FPTU\SP26\EXE\milingo_admin`:

```powershell
npm run build
```

Expected: exit code `0`.

- [ ] **Step 3: Manual browser review**

Start backend and admin only if needed for visual review. Confirm dashboard has:

- four KPI cards.
- growth chart.
- rating pie.
- admin/customer split.
- recent transactions table.
- loading/error/empty states.
