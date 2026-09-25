# PocketSpace server

## Run locally

Run `dotnet run --launch-profile http` from this folder. SQLite migrations run at startup and create `App_Data/pocketspace.db`. The first startup creates the `admin` user with password `admin@123` and the `Admin` role. Existing accounts and passwords are preserved on subsequent startups.

Start the React client with `npm run dev` in `../pocket-space-client`. Its Vite proxy connects to this API on port 5008.

## Authentication

- `POST /api/auth/login` accepts `{ "username": "admin", "password": "admin@123" }` and returns `accessToken`, `expiresAt`, and `user`.
- `POST /api/auth/signup` accepts a username and password, creates a pending account with a private folder, and returns a JWT immediately. The client offers signup from the login screen.
- `GET /api/auth/me` returns the current user's ID, username, roles, approval status, and deletion deadline.
- `POST /api/auth/password-reset-request` accepts `{ "username": "alice" }` without authentication and adds an active account to the admin reset queue. Unknown and inactive usernames receive the same confirmation. Repeated requests keep the original request time. This shares the login/signup rate limit.
- `POST /api/auth/change-password` requires authentication and accepts `{ "currentPassword": "old@1234", "newPassword": "new@5678" }`. It validates the current password and Identity password rules, clears pending reset requests, and invalidates existing tokens. Sign in again after success.
- All other API endpoints require `Authorization: Bearer <accessToken>`.
- New accounts can only access their own folder under `<TargetDirectory>/.users/<user-id>`, both before and after approval. The admin retains the existing storage root; the reserved `.users` subtree is hidden from its file APIs. File names and relative paths never select another user's root.
- Passwords are stored using ASP.NET Core Identity's password hasher in SQLite. Five failed attempts lock the account for five minutes; login requests are also limited to 20 per minute per remote IP.
- JWTs expire after 60 minutes, capped at the pending account's deletion deadline. The client keeps the token in tab-scoped `sessionStorage`, restores login on reload using `/auth/me`, and clears it on logout or expiry. Each authenticated request also verifies that the account still exists, is active, and has the same Identity security stamp. Password changes and admin resets invalidate earlier tokens. Client logout alone does not revoke copied tokens. Tokens created before this security-stamp check was added require a fresh login.
- `/ws` also requires authentication. Browser clients offer `pocketspace` and `bearer.<JWT>` as WebSocket subprotocols; the server negotiates only `pocketspace`. Connections close at token expiry. Tokens do not appear in URLs.

Swagger is available in Development. Use the login endpoint, then paste the returned token into **Authorize**.

## Password recovery and Settings

Users select **Forgot password?** on the login page and submit their username. Admins open **Password resets** (`/admin/password-resets`) to view the queue, verify the requester's identity through their usual contact channel, and set a new password. The admin shares that password directly; PocketSpace does not send email or messages and cannot retrieve saved passwords. Users sign in with that password and open **Settings → Change password** (`/settings`) to choose their own password using the current password and a matching confirmation. Changing it is optional, and the UI explains how to do so.

The admin endpoints are `GET /api/admin/users/password-reset-requests` and `POST /api/admin/users/{id}/reset-password` with `{ "newPassword": "temporary@123" }`; both require the `Admin` role. A reset requires an active account and an outstanding request. Success clears the request and login lockout, preserves approval deadlines and files, and revokes earlier tokens. Failed password validation leaves the request and credentials intact. Resetting the current admin's password signs that admin out too. Already-open status WebSockets still close at their original token expiry; new connections use the updated token check.

Restart the API after updating. The additive `PasswordResetRequests` migration runs automatically at startup and preserves existing accounts.

## Signup, approval, and automatic deletion

Signup starts a seven-day UTC deadline. Pending users can immediately browse, upload, download, create folders, rename, and delete within their own folder. Deleting through the file manager is permanent and requires confirmation in the client; trash/restore is future work. The client displays the exact pending-account deletion deadline and a **Check approval** action.

Admins use **Approvals** in the sidebar (`/admin/users`) to see pending accounts and approve them. The API endpoints are `GET /api/admin/users/pending` and `POST /api/admin/users/{id}/approve`; both require the `Admin` role. Approval preserves the existing folder and removes the deletion deadline. Expired accounts cannot be rescued by approval.

At exactly seven days, unapproved accounts cannot log in or access APIs, even using an existing token. Cleanup runs at startup and once per minute. It marks expired accounts as `Deleting`, removes only their generated private folder, then deletes their database account and dependent Identity rows. A storage failure leaves the inaccessible `Deleting` record for the next retry. Approved users and the initial admin are never selected for this cleanup. If the server was offline, cleanup resumes at startup.

File operations, approval, and cleanup coordinate with per-account locks in this single-server application. Pending file transfers are canceled at account expiry. Run one API instance against a SQLite database and its storage; multiple API processes would need distributed coordination before sharing this setup.

The `PendingAccounts` migration marks pre-existing accounts as approved. It does not move or delete the administrator's existing files. Retained file bytes still live on disk; database-backed file metadata and sharing permissions remain future work.

## Configuration

| Setting / environment variable | Default / purpose |
| --- | --- |
| `ConnectionStrings__PocketSpace` | SQLite connection string; defaults to `App_Data/pocketspace.db` under the server content root. |
| `Jwt__SigningKey` | Random secret of at least 32 bytes. Required outside Development. Development automatically creates and reuses an ignored `App_Data/jwt-signing-key`. |
| `Jwt__Issuer` | `PocketSpace` |
| `Jwt__Audience` | `PocketSpaceClient` |
| `Jwt__LifetimeMinutes` | `60` (1–1440) |
| `BootstrapAdmin__Password` | Overrides `admin@123` when creating the initial admin only. |
| `PocketSpace__DirectorySettings__TargetDirectory` | Existing file-storage directory, configured in `appsettings.json`. |

Use a private signing key, a non-default bootstrap password, and HTTPS for deployment. Keep `App_Data` outside the file-storage directory and preserve the SQLite database across deployments. The database and local signing key are excluded from Git and publish output. Overriding the bootstrap password does not change an account already in the database.

## Migrations and tests

```powershell
dotnet tool restore
dotnet ef migrations add MigrationName
dotnet test tests/PocketSpaceServer.Tests/PocketSpaceServer.Tests.csproj --configuration Release
```

Tests use isolated temporary SQLite databases and storage folders. They cover admin seeding, password persistence, lockout, token validation, API protection, authenticated WebSockets, signup, private-file isolation and management, admin-only approval, the seven-day expiry boundary, and cleanup retries.

Password-flow integration tests cover generic/coalesced requests, admin-only resets, password validation, lockout recovery, revoked tokens, expired accounts, and rate limiting. File metadata, quotas, and granular sharing permissions remain future work.
