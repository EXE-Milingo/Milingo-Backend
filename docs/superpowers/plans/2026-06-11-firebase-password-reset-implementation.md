# Firebase Password Reset Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden the existing Flutter forgot-password flow so it validates email locally, sends a Vietnamese Firebase reset email, and always uses a privacy-safe confirmation for valid-looking addresses.

**Architecture:** Keep Firebase Auth calls in the existing forgot-password screen because authentication is client-owned in this project. Extract only the pure email validation rule into a small auth utility so it can be tested without introducing Firebase mocks or backend changes.

**Tech Stack:** Flutter, Dart, `firebase_auth`, Flutter test

---

## File Structure

- Create `lib/features/auth/utils/password_reset_validation.dart`: pure email validation and Vietnamese validation messages.
- Create `test/password_reset_validation_test.dart`: focused unit tests for empty, malformed, trimmed, and valid addresses.
- Modify `lib/features/auth/screens/forgot_password_screen.dart`: use the validator, set Firebase email language to Vietnamese, and use privacy-safe confirmation/error text.
- No ASP.NET Core backend files change.

### Task 1: Add Password Reset Email Validation

**Files:**
- Create: `lib/features/auth/utils/password_reset_validation.dart`
- Test: `test/password_reset_validation_test.dart`

- [x] **Step 1: Write the failing validation tests**

Create tests that assert:

```dart
expect(validatePasswordResetEmail(''), 'Vui lòng nhập email để khôi phục mật khẩu.');
expect(validatePasswordResetEmail('not-an-email'), 'Email không hợp lệ. Vui lòng kiểm tra lại.');
expect(validatePasswordResetEmail(' user@example.com '), isNull);
expect(validatePasswordResetEmail('name+tag@example.co.uk'), isNull);
```

- [x] **Step 2: Run the focused test and verify RED**

Run:

```powershell
flutter test test/password_reset_validation_test.dart
```

Expected: failure because `password_reset_validation.dart` and
`validatePasswordResetEmail` do not exist yet.

- [x] **Step 3: Implement the minimal validator**

Add a pure function that trims input, returns the required empty message,
checks a modest `local@domain.tld` shape, and returns the malformed message or
`null`.

- [x] **Step 4: Run the focused test and verify GREEN**

Run:

```powershell
flutter test test/password_reset_validation_test.dart
```

Expected: all validation tests pass.

### Task 2: Harden the Existing Firebase Reset Flow

**Files:**
- Modify: `lib/features/auth/screens/forgot_password_screen.dart`

- [x] **Step 1: Use local validation before Firebase**

Import the validator, trim the email once, and stop before changing loading
state when validation returns a message.

- [x] **Step 2: Localize and send through Firebase**

Use the existing `FirebaseAuth.instance`, then:

```dart
await auth.setLanguageCode('vi');
await auth.sendPasswordResetEmail(email: email);
```

- [x] **Step 3: Make confirmation privacy-safe**

For every successful Firebase request, show the sent panel with:

```text
Nếu email đã được đăng ký, Firebase sẽ gửi liên kết khôi phục tới <email>.
Vui lòng kiểm tra hộp thư và thư rác.
```

Use the same generic wording in the success snackbar. Do not handle
`user-not-found` as an account-existence message.

- [x] **Step 4: Keep only actionable error messages**

Retain specific messages for invalid/missing email, network failure, and rate
limiting. Replace the raw-code fallback with:

```text
Không thể gửi email khôi phục lúc này. Vui lòng thử lại.
```

Keep the existing loading guard, sent state, resend action, and navigation.

### Task 3: Targeted Verification

**Files:**
- Verify: `lib/features/auth/screens/forgot_password_screen.dart`
- Verify: `lib/features/auth/utils/password_reset_validation.dart`
- Verify: `test/password_reset_validation_test.dart`

- [x] **Step 1: Format only changed Dart files**

Run:

```powershell
dart format lib/features/auth/screens/forgot_password_screen.dart lib/features/auth/utils/password_reset_validation.dart test/password_reset_validation_test.dart
```

- [x] **Step 2: Run the focused unit test**

Run:

```powershell
flutter test test/password_reset_validation_test.dart
```

Expected: all tests pass.

- [x] **Step 3: Run static analysis**

Run:

```powershell
flutter analyze
```

Expected: no new errors caused by the password-reset change. Existing unrelated
warnings, if any, will be reported separately.

- [x] **Step 4: Review the diff**

Confirm the diff contains no backend changes, no custom reset page, no app-link
configuration, no package changes, and no account-existence disclosure.
