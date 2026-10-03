# Storage environment variables

Configure these on the **API server**. In local Development, copy `.env.example` to `.env` in this folder and edit `.env`; it is ignored by Git. `dotnet run --launch-profile http` loads it automatically. Process environment variables override `.env`. Outside Development, `.env` is not loaded; configure real environment variables or deployment secrets instead. Restart the API after changing any storage variable.

| Variable | Default | Purpose |
| --- | --- | --- |
| `PocketSpace__Storage__Provider` | `FileSystem` | `FileSystem` or `S3` (case-sensitive). |
| `PocketSpace__Storage__FileSystemPath` | `<server application folder>/StorageDirectory` | Local file root. An absolute path is recommended; a relative path is resolved from the server application folder. Also provides logical account paths when using S3; it is not used to store S3 bytes. |
| `PocketSpace__Storage__S3__Bucket` | None | S3 bucket name; required for `S3`. |
| `PocketSpace__Storage__S3__Region` | `us-east-1` | AWS region code; use the bucket's actual region (for example, `ap-south-1`). |
| `PocketSpace__Storage__S3__AccessKey` | None | IAM access key ID; required for `S3`. |
| `PocketSpace__Storage__S3__SecretKey` | None | IAM secret access key; required for `S3`. |

`us-east-1` is the AWS region code, not `us-east1`. A missing bucket or credential with `Provider=S3` prevents server startup. Never commit real credentials. The app's global storage limit is set separately by an admin in **Settings**. For a 5 GiB limit, check **Limit total storage**, enter `5`, and save. The limit remains in SQLite when switching providers.

To switch providers, change `PocketSpace__Storage__Provider` and restart the API. Files are not copied: each provider shows only data already in its configured location. Existing unclaimed top-level data remains admin-only. File metadata is tagged by provider so local and S3 favorites and Trash do not mix.

Before upgrading an existing local installation, set `PocketSpace__Storage__FileSystemPath` to its old path. This checkout previously used `D:\MyStorage`; its ignored local `.env` retains that path. Leaving the variable blank uses the new default folder and will make old files appear absent until the original path is restored. Any S3 credentials previously entered in the UI must be copied into environment variables before upgrading because the new migration removes them from SQLite.

For the dedicated S3 bucket, the IAM identity needs `s3:ListBucket` on the bucket and `s3:GetObject`, `s3:PutObject`, `s3:DeleteObject`, and `s3:AbortMultipartUpload` on its objects. Protect your `.env` file and deployment secrets with appropriate filesystem/service permissions.

The current client sends each file as a raw request body to `/api/upload/stream`, so the API can stream it to the selected backend without buffering the whole multipart form. S3 uploads use the hidden `.pocketspace-upload/` staging prefix and publish the final key only after upload succeeds. Normal cancellation removes the staged object. Add bucket lifecycle rules to expire `.pocketspace-upload/` objects and abort incomplete multipart uploads after a suitable retention period (for example, seven days); these rules also clean up after a process crash or an S3 outage that prevents request-time cleanup.

Place the API in the same AWS region as the bucket where practical, and measure `folder-info`, quota checks, and S3 calls separately before changing regions. When no global storage limit is configured, different accounts can upload concurrently. A configured global limit serializes uploads in this single API process and scans the bucket to enforce that limit. For multiple API replicas or sustained heavy uploads, move quota accounting and reservations into a shared transactional database and use direct-to-S3 multipart uploads so file bytes do not pass through the API server.

## Other server variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__PocketSpace` | SQLite `App_Data/pocketspace.db` | Optional database connection string. Keep the database persistent across restarts. |
| `Jwt__SigningKey` | Generated and saved in Development; required otherwise | Private signing key of at least 32 bytes. |
| `Jwt__Issuer` | `PocketSpace` | Token issuer. |
| `Jwt__Audience` | `PocketSpaceClient` | Token audience. |
| `Jwt__LifetimeMinutes` | `60` | Token lifetime, from 1 to 1440 minutes. |
| `BootstrapAdmin__Password` | `admin@123` | Initial admin password, used only when creating the account. Change it for deployment. |

`ASPNETCORE_ENVIRONMENT=Development` is set by the local launch profile. Production should use a private signing key, a non-default bootstrap password, and HTTPS. The SQLite database and signing key live under `App_Data`, not under the file-storage root.
