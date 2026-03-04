Multitenancy (Subdomain) for Razor Pages (.NET 9)

## GitHub Codespaces setup

This repository is configured for Codespaces using `.devcontainer`.

### What is included
- .NET 9 SDK dev container
- SQL Server 2022 sidecar container
- Environment override for `ConnectionStrings__DefaultConnection`
- Automatic `dotnet restore`
- Automatic `dotnet ef database update`

### Start in Codespaces
1. Open the repository in GitHub Codespaces.
2. Wait for the post-create script to finish.
3. Run the app:
   - `dotnet run --project ./twoSaaSCore.csproj --urls http://0.0.0.0:5000`
4. Open the forwarded port `5000`.

### Notes
- The SQL container uses development credentials from `.devcontainer/docker-compose.yml`.
- App settings from `appsettings.json` are overridden in Codespaces via environment variables.

This project implements multitenancy in a single sharded database using subdomains to associate requests and users with a tenant.

Key features
- Tenant model with TenantId (Guid), TenantName, and Subdomain
- ITenantEntity interface for tenant-scoped entities
- ApplicationUser extended with TenantId and Subdomain
- Tenant-scoped Files entity stored in Azure Blob Storage with metadata in DB
- Registration collects subdomain, creates or associates a Tenant, and stores tenant info with the user
- Middleware extracts the subdomain from the request host and stores it in HttpContext.Items
- After login, users are redirected to their subdomain URL
- Global query filter in ApplicationDbContext filters tenant-scoped entities
- IHttpContextAccessor registered in DI
- TenantId added as a claim during login

Data model
- Tenant
  - TenantId (Guid)
  - TenantName (string)
  - Subdomain (string, unique)
- ApplicationUser
  - Inherits IdentityUser
  - TenantId (Guid)
  - Subdomain (string)
- ITenantEntity
  - Guid TenantId { get; set; }
  - Implement on all tenant-scoped entities
- TenantFile (tenant-scoped)
  - Id (int)
  - TenantId (Guid)
  - FileName (string)
  - ContentType (string)
  - BlobName (string)
  - BlobUri (string)
  - Size (long)
  - UploadedAt (DateTimeOffset)
  - UploadedByUserId (string)

How tenancy is resolved
1) Subdomain captured by middleware: foo.example.com ? subdomain = "foo" stored in HttpContext.Items
2) For authenticated users, TenantId is read from the authentication claims
3) DbContext global filters use the TenantId from the current request (claims). If none is present, filters default to Guid.Empty to return no tenant-scoped data

Global query filter
- Any entity implementing ITenantEntity is automatically filtered in ApplicationDbContext using the current TenantId
- This ensures queries only see data for the current tenant

Identity customization
- Register asks for Subdomain (and Tenant Name for new tenants)
- If a tenant with the given subdomain exists, the user is associated with it; otherwise a new tenant is created
- On successful login, a "tenant_id" claim is issued and the user is redirected to their subdomain

Files (tenant-scoped uploads)
- Razor Page at /Files supports uploading a file to Azure Blob Storage
- Metadata is saved to the TenantFiles table and filtered by TenantId
- Deleting removes both the blob and the DB record
- Download handler at /Files/Download?id={id} streams the blob via server to enforce auth and tenant isolation

Configuration
- Program.cs
  - Registers IHttpContextAccessor
  - Registers ITenantProvider
  - Registers IFileStorage with Azure blob implementation
  - Uses AddDefaultIdentity<ApplicationUser>()
  - Adds SubdomainTenantMiddleware
  - Ensures UseAuthentication is added before UseAuthorization
- appsettings.json
  - AzureBlobs: ConnectionString, Container, CreateContainerIfNotExists
  - For local dev with Azurite, ConnectionString is pre-set to UseDevelopmentStorage=true

Development notes
- Migrations must be created/updated after these changes to add Tenant, TenantFiles tables and new columns (TenantId, Subdomain) to AspNetUsers
- Example commands:
  - dotnet ef migrations add AddMultitenancyAndFiles
  - dotnet ef database update
- For local development with ports, redirect URLs retain the incoming port
- To run Azurite locally:
  - npm install -g azurite
  - azurite --blobPort 10000 --queuePort 10001 --tablePort 10002
  - Or use Azure Storage Emulator-compatible connection string (UseDevelopmentStorage=true)

Security
- The global filter only applies to entities implementing ITenantEntity; ensure every tenant-scoped entity implements the interface
- For background services or contexts created outside HTTP, ensure TenantId is set explicitly on entities and queries
- Do not trust the file name; a GUID is prefixed in the blob path to avoid collisions
- Downloads are served through the app to enforce auth; blobs can remain private

Routing and redirects
- After login, redirects are constructed as https://{sub}.{rootHost}:{port}/ when the current request includes a port; otherwise https://{sub}.{rootHost}/

Files added or updated
- Models/Tenant.cs
- Models/ITenantEntity.cs
- Models/ApplicationUser.cs
- Models/TenantFile.cs
- Constants/TenantConstants.cs
- Services/ITenantProvider.cs
- Services/HttpContextTenantProvider.cs
- Services/IFileStorage.cs
- Services/AzureBlobFileStorage.cs
- Middleware/SubdomainTenantMiddleware.cs
- Data/ApplicationDbContext.cs (updated)
- Program.cs (updated)
- Areas/Identity/Pages/Account/Register.cshtml + .cshtml.cs (new)
- Areas/Identity/Pages/Account/Login.cshtml + .cshtml.cs (new)
- Pages/Files/Index.cshtml + .cshtml.cs (new)
- Pages/Files/Download.cshtml.cs (new)

