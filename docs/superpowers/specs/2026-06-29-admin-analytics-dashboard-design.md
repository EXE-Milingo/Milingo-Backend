# Admin Analytics Dashboard Design

Date: 2026-06-29

## Goal

Add an admin dashboard that shows four business criteria:

- Revenue from paid transactions.
- User count, including admins and customers, expected to display correctly above 20 users.
- Transaction count.
- App review counts for 3-star, 4-star, and 5-star reviews, including average rating.

The dashboard also needs growth charts by day, month, and year, plus chart and pie visuals.

## Current Payment Detection

PayOS detection already exists in the backend:

1. Mobile client calls `POST /api/v1/payments/payos/create-order`.
2. Backend creates a PayOS checkout order and stores `payment_orders/{orderCode}` with `status = PENDING`.
3. PayOS calls `POST /api/v1/payments/payos/webhook`.
4. Backend validates the PayOS HMAC signature, checks the amount, grants premium, and writes `status = PAID`.
5. Admin analytics should treat paid PayOS orders as records where `payment_orders.status` is `PAID`, `SUCCESS`, or `COMPLETED`.

The existing admin website does not detect payment completion directly. It only reads Firestore user/deck data. The new design makes the backend summarize payment data for admin.

## Chosen Approach

Use a backend Admin Analytics API.

Reason:

- Keeps payment and revenue logic server-side.
- Avoids exposing all payment documents directly to the browser.
- Gives the React admin app one stable response shape.
- Makes future authorization easier through Firebase custom claims or an allowlisted admin email list.

## Backend Design

Add `AdminAnalyticsController` under `api/v1/admin/analytics`.

Primary endpoint:

`GET /api/v1/admin/analytics/overview?granularity=day|month|year&from=YYYY-MM-DD&to=YYYY-MM-DD`

Response shape:

```json
{
  "totals": {
    "revenue": 12400000,
    "users": 24,
    "admins": 2,
    "customers": 22,
    "transactions": 38,
    "reviews": 16,
    "averageRating": 4.3
  },
  "growth": [
    {
      "period": "2026-06-29",
      "revenue": 1200000,
      "users": 3,
      "transactions": 4,
      "reviews": 2,
      "averageRating": 4.5
    }
  ],
  "ratings": {
    "threeStar": 3,
    "fourStar": 5,
    "fiveStar": 8
  },
  "transactions": [
    {
      "id": "123456789012",
      "orderCode": 123456789012,
      "userId": "firebase_uid",
      "email": "customer@example.com",
      "planId": "pro",
      "amount": 299000,
      "status": "PAID",
      "source": "payos",
      "paidAt": "2026-06-29T10:00:00Z"
    }
  ]
}
```

## Firestore Data Sources

Use current collections:

- `payment_orders`: revenue and transaction count.
- `users`: total users, admins, customers, and user growth.

Add or standardize:

- `app_reviews`: app reviews from mobile or admin import.

Suggested `app_reviews` document fields:

```json
{
  "uid": "firebase_uid",
  "rating": 5,
  "comment": "Great app",
  "createdAt": "server timestamp",
  "platform": "android"
}
```

Review analytics counts only ratings 3, 4, and 5 for the requested UI, but average rating should use all valid ratings 1 through 5 unless product rules say otherwise.

## Authorization

Admin endpoints must require Firebase auth.

Authorization can be added in one of two compatible ways:

- Preferred: Firebase custom claim `admin = true`.
- Fallback: configured allowlist `Admin:Emails` in backend config.

The admin React app already signs in with Firebase, so it can pass `Authorization: Bearer <idToken>` when calling the backend.

## Frontend Design

Replace the current simple dashboard metrics with:

- Four KPI cards: revenue, users, transactions, average review.
- User composition card or mini pie: admins vs customers.
- Growth chart with segmented control: day, month, year.
- Rating pie chart: 3-star, 4-star, 5-star.
- Recent paid transaction table.
- Refresh action and loading/error/empty states.

Use the existing React + Tailwind setup. Add a charting library only if installed or approved; otherwise use lightweight SVG/CSS charts to avoid extra dependency.

## Edge Cases

- If `payment_orders` has missing `paidAt`, use `updatedAt` or `createdAt` for paid orders.
- Only count revenue for paid statuses.
- Unknown user role defaults to customer.
- Missing review collection returns zero review counts and `averageRating = 0`.
- Date filtering uses UTC in the backend response. UI formats dates in local browser timezone.

## Verification Scope

The user requested no tests. Verification should be limited to build/static checks and manual UI review unless the user later asks for tests.
