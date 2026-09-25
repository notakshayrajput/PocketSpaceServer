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

Signup starts a seven-day UTC deadline. Pending users can immediately browse, upload, download, create folders, rename, and move items to Trash within their own folder. Trash retains items for seven days with restore available before the deadline. The client displays the exact pending-account deletion deadline and a **Check approval** action. Account expiry still removes the entire private workspace, including Trash; trash retention does not extend an unapproved account's lifetime.

Admins use **Approvals** in the sidebar (`/admin/users`) to see pending accounts and approve them. The API endpoints are `GET /api/admin/users/pending` and `POST /api/admin/users/{id}/approve`; both require the `Admin` role. Approval preserves the existing folder and removes the deletion deadline. Expired accounts cannot be rescued by approval.

At exactly seven days, unapproved accounts cannot log in or access APIs, even using an existing token. Cleanup runs at startup and once per minute. It marks expired accounts as `Deleting`, removes only their generated private folder, then deletes their database account and dependent Identity rows. A storage failure leaves the inaccessible `Deleting` record for the next retry. Approved users and the initial admin are never selected for this cleanup. If the server was offline, cleanup resumes at startup.

File operations, approval, and cleanup coordinate with per-account locks in this single-server application. Pending file transfers are canceled at account expiry. Run one API instance against a SQLite database and its storage; multiple API processes would need distributed coordination before sharing this setup.

The `PendingAccounts` migration marks pre-existing accounts as approved. It does not move or delete the administrator's existing files. Retained file bytes still live on disk. The `FileFavoritesAndTrash` migration adds file identity, favorites, recent activity, and trash metadata; sharing permissions remain future work.

## Favorites, Recent files, and Trash

- **Home** shows all favorite files and the 20 most recently used files. Star/unstar files in the explorer or Home. Favorites are saved per user in SQLite and survive app-managed file/folder renames and trash restoration.
- Recent activity updates on successful uploads, accepted downloads (including files inside downloaded folders), renames, and restores. Favoriting and browsing do not change the order. Existing disk files are discovered on Home/browse and initially use their disk modification time. Home indexes the local tree; this implementation is intended for the existing small, single-server workspace.
- **Move to Trash** replaces permanent deletion for both files and folders. The item disappears from Files, Favorites, Recent files, and downloads. Trashing a folder keeps its contents together. Each trash item has its own ID, original location, size, UTC trash date, and exact seven-day expiry.
- **Trash** (`/trash`) lists retained items and their deletion dates. Restore returns an item to its original location with its original file IDs and favorites. Restore refuses to overwrite an existing item; rename or trash the conflicting item first. If the original parent folder is missing, restore or recreate it first.
- Restore is unavailable at or after the deadline. Cleanup runs at startup and once per minute, deleting only expired items from private trash. If the server was offline or a storage deletion fails, cleanup retries. Files still in Trash count toward occupied storage until purged.
- Bytes live under a reserved `.pocketspace-trash/<trash-id>` directory inside each user's storage root. Normal path APIs, recursive downloads, and listings cannot access this directory. Per-account locks coordinate file actions, trash cleanup, and pending-account cleanup. Durable move/restore/purge states allow interrupted trash operations to finish after restart.
- Existing files stay in place; the additive migration runs on server startup. Back up both the SQLite database and the full storage directory, including hidden trash directories. Perform renames and replacements through PocketSpace to preserve identity and favorites; manual disk edits are not an identity-aware sync mechanism.

Endpoints (all require authentication and operate only on the current user's data):

| Endpoint | Behavior |
| --- | --- |
| `GET /api/space/home` | `{ favorites, recent }` file collections. |
| `PUT /api/space/files/{id}/favorite` | Set `{ "isFavorite": true/false }`. |
| `DELETE /api/space/entry?path=...` | Move a file or folder to Trash. |
| `GET /api/space/trash` | List retained items, original paths, and expiry dates. |
| `POST /api/space/trash/{id}/restore` | Restore before expiry; `404` for unavailable IDs, `409` for location conflicts, `410` for expired items awaiting cleanup. |

Restart the API after updating so the new migration and cleanup worker are active.

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

Password-flow integration tests cover generic/coalesced requests, admin-only resets, password validation, lockout recovery, revoked tokens, expired accounts, and rate limiting. File-library tests cover favorites, recency, existing-file discovery, rename/restore identity, folder trash, name collisions, private trash isolation, exact expiry, interrupted operations, and cleanup retries. Quotas, richer file metadata, and granular sharing permissions remain future work.
