# Firebase Password Reset Design

## Goal

Allow a signed-out MiLingo user to request a Firebase password-reset email
from the Flutter app. The email link opens Firebase's hosted action page,
where the user sets a new password.

## Current State

- The Flutter app owns Firebase Authentication operations.
- The ASP.NET Core backend validates Firebase ID tokens but does not create,
  authenticate, or recover Firebase Auth users.
- `ForgotPasswordScreen` already calls
  `FirebaseAuth.instance.sendPasswordResetEmail`.
- Firebase's password-reset email template and hosted action handler are
  configured in the Firebase console.

## Chosen Approach

Keep password reset entirely in the Flutter and Firebase Auth boundary.

The app validates the email input, asks Firebase Auth to send the reset email,
and displays a generic confirmation for all syntactically valid email
addresses. Firebase sends the configured template and handles password entry
on its hosted page.

No backend endpoint, Firestore write, custom SMTP service, custom reset page,
or app-link handler will be added.

## Alternatives Considered

### Backend proxy to Firebase Auth REST API

Rejected because it adds a public unauthenticated endpoint, Firebase Web API
configuration, error translation, and abuse controls without improving the
user flow.

### Firebase Admin SDK with custom email delivery

Rejected because the Admin SDK generates reset links but does not send the
standard Firebase template. This approach would require custom SMTP,
templating, delivery monitoring, and a larger security surface.

## User Flow

1. The user opens the forgot-password screen from the login screen.
2. The user enters an email address and taps `Gửi email khôi phục`.
3. The app rejects an empty or syntactically invalid email locally.
4. The app sets Firebase Auth's email language to Vietnamese.
5. The app calls `sendPasswordResetEmail(email: email)`.
6. For a valid-looking email, the app shows a generic confirmation:

   `Nếu email đã được đăng ký, Firebase sẽ gửi liên kết khôi phục. Vui lòng kiểm tra hộp thư và thư rác.`

7. If the address belongs to an Email/Password account, Firebase sends the
   configured reset email.
8. The user opens the email link in a browser and sets a new password on
   Firebase's hosted action page.
9. The user returns to MiLingo and signs in with the new password.

The existing resend action remains available and uses the same email address
and generic confirmation.

## Components

### Flutter forgot-password screen

`lib/features/auth/screens/forgot_password_screen.dart` remains the owner of
this small, screen-local operation.

Responsibilities:

- Trim and validate email input.
- Prevent duplicate submissions while a request is active.
- Set Firebase Auth email localization to Vietnamese.
- Call Firebase Auth directly.
- Show the sent state and resend action.
- Translate actionable failures into Vietnamese messages.

The reset call will not be routed through `MilingoApiService`, because it is a
Firebase client-auth operation rather than a MiLingo backend operation.

### Firebase Authentication

Firebase remains responsible for:

- Determining whether the email belongs to an eligible account.
- Sending the password-reset email.
- Producing and validating the out-of-band action code.
- Hosting the page where the new password is entered.
- Applying Firebase password policy and updating the credential.

### ASP.NET Core backend

No changes are required. Password reset occurs before authentication and does
not affect the Firestore profile or application data.

## Validation and Error Handling

### Local validation

- Empty input: `Vui lòng nhập email để khôi phục mật khẩu.`
- Malformed email: `Email không hợp lệ. Vui lòng kiểm tra lại.`

The validation should be intentionally modest rather than attempting to fully
implement the email RFC. It should catch common malformed values while
allowing Firebase to remain the final authority.

### Firebase and transport errors

- `too-many-requests`: tell the user to wait before retrying.
- `network-request-failed`: tell the user to check the connection.
- `invalid-email` or `missing-email`: map to the local validation messages as
  defensive handling.
- Other Firebase errors: show a generic retry message without exposing raw
  error codes to the user.

After a submission reaches Firebase without an actionable transport or quota
failure, the UI uses the generic confirmation. It does not show whether the
email exists.

## Security and Privacy

Firebase Email Enumeration Protection remains enabled.

The app must not depend on `user-not-found` and must not tell a caller whether
an email is registered. This prevents the forgot-password screen from becoming
an account-discovery endpoint.

The screen collects no password and stores no reset code. Reset links and new
passwords are handled only by Firebase.

Repeated taps are blocked while sending. Firebase remains the primary
rate-limit authority; no custom client-side cooldown is added unless real
usage shows it is necessary.

## Firebase Console Requirements

- Email/Password sign-in is enabled.
- The password-reset template has the intended MiLingo sender, subject, and
  Vietnamese content.
- Email Enumeration Protection is enabled.
- The default Firebase hosted action handler remains active.
- The action link uses the Firebase project shown in the app's platform
  configuration.

## Testing and Verification

Testing is deliberately scoped to the changed behavior:

1. Format the modified Dart file.
2. Run Flutter static analysis for compile and lint regressions.
3. Add or update one focused test around local email validation and the
   privacy-safe confirmation if the current screen can be tested without
   introducing a large Firebase mocking framework.
4. Manually submit one known Email/Password account and verify that:
   - the confirmation state appears;
   - the reset email arrives;
   - the link opens Firebase's hosted page;
   - a new password can be set and used to sign in.
5. Submit one malformed address and verify that Firebase is not called.

The full Flutter test suite is not required unless the targeted checks expose
a shared regression.

## Out of Scope

- A custom password-reset page in Flutter or on the web.
- Android App Links, iOS Universal Links, or Firebase Dynamic Links.
- Custom SMTP delivery.
- A backend password-reset API.
- Firestore profile changes.
- Password reset for social-provider-only accounts.
- Disabling Email Enumeration Protection.
