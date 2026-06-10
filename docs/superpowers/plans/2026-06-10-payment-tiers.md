# Payment Tiers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose and enforce exactly three payment tiers: Plus, Pro, and Ultra.

**Architecture:** Keep plan prices and durations in configuration. Use one service allow-list to validate checkout requests and build the plans API response, while retaining display names for historical transactions.

**Tech Stack:** ASP.NET Core 8, C#, JSON configuration, PowerShell verification

---

### Task 1: Verify configured tiers

**Files:**
- Create: `scripts/verify-payment-plans.ps1`
- Modify: `appsettings.json`

- [ ] Add a focused script that requires only `Plus`, `Pro`, and `Ultra`, with prices `59000`, `139000`, and `510000`.
- [ ] Run it before the config change and confirm failure.
- [ ] Replace legacy monthly/yearly entries with the three approved tiers.
- [ ] Run the script again and confirm success.

### Task 2: Enforce and expose plan catalog

**Files:**
- Modify: `Models/PaymentModels.cs`
- Modify: `Services/IPaymentService.cs`
- Modify: `Services/PaymentService.cs`
- Modify: `Controller/PaymentController.cs`

- [ ] Add `SubscriptionPlanResponse`.
- [ ] Add service catalog method backed by a three-plan allow-list.
- [ ] Make checkout reject unsupported IDs.
- [ ] Add `GET /api/v1/payments/plans`.
- [ ] Keep historical display names readable.

### Task 3: Verify

- [ ] Run `powershell -ExecutionPolicy Bypass -File scripts/verify-payment-plans.ps1`.
- [ ] Run `dotnet build`.
