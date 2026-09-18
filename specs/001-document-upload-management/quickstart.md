# Quickstart: Document Upload and Management

**Feature**: `001-document-upload-management` | **Date**: 2026-09-18

## Prerequisites

- .NET 9 SDK (`dotnet --version` reports 9.x)
- No database server. The project uses SQLite; the database file is created on first run.

## Run the application

```bash
cd ContosoDashboard
dotnet restore
dotnet build
dotnet run
```

Open the URL printed in the terminal and log in as **Ni Kang (Employee)** from the dropdown.
No password is required — authentication is mocked for training.

## Reset the database

`Database.EnsureCreated()` creates the schema only when the file is absent; it does not alter
an existing database. After adding the document tables, the old database must go:

```bash
rm ContosoDashboard/ContosoDashboard.db
dotnet run --project ContosoDashboard
```

Seed data is recreated automatically. Do this before testing uploads for the first time —
rows left behind by a failed upload attempt can collide on the unique `StoragePath` index.

## Where the pieces live

```text
ContosoDashboard/
├── Models/
│   ├── Document.cs              # new
│   ├── DocumentShare.cs         # new
│   └── DocumentActivity.cs      # new
├── Data/
│   └── ApplicationDbContext.cs  # + 3 DbSets, relationships, indexes
├── Services/
│   ├── IDocumentService.cs      # new — the authorization boundary
│   ├── DocumentService.cs       # new
│   ├── IFileStorageService.cs   # new — the cloud-migration seam
│   ├── LocalFileStorageService.cs # new
│   ├── IFileValidationService.cs  # new — the malware-scanning seam
│   └── FileValidationService.cs   # new
├── Pages/
│   ├── Documents.razor          # new — My Documents, upload, search
│   └── DocumentDetails.razor    # new
├── Shared/
│   ├── DocumentUploadModal.razor   # new
│   └── RecentDocumentsWidget.razor # new
├── Endpoints/
│   └── DocumentContentEndpoint.cs  # new — authorized file serving
└── Storage/uploads/             # created at runtime, git-ignored, outside wwwroot
```

## Implementation order

Follow the phases in `tasks.md`. In outline:

1. **Foundation** — entities, `DbContext` wiring, storage and validation services, DI
   registration. Nothing user-visible; everything else depends on it.
2. **US1, upload** — the MVP. Upload page, modal, `DocumentService.UploadAsync`, My Documents
   list. Stop here and you have something demonstrable.
3. **US2/US3** — browse, filter, sort, search, download, preview.
4. **US4/US5/US6** — edit, replace, delete, share.
5. **US7/US8** — task, project, and dashboard integration; audit and reporting.

## The three traps this feature sets

**Blazor file streams.** `IBrowserFile.OpenReadStream()` defaults to a 512 KB limit — pass
the 25 MB cap explicitly or every real document fails with a confusing size error. Copy the
stream into a `MemoryStream` immediately and read `Name`, `Size`, and `ContentType` into
locals *before* opening it; the browser file is bound to the SignalR circuit and is disposed
on re-render. Null the reference afterwards and change the `@key` on `InputFile`, or a second
upload of the same file silently does nothing.

**Upload ordering.** Generate the storage path, write the file, *then* insert the row. The
reverse leaves rows with empty paths that collide on the unique index and break every
subsequent upload. If the insert fails, delete the file you just wrote.

**Enum ordering.** Append the three new `NotificationType` members to the end. EF Core stores
the enum as an integer, so inserting them anywhere else silently changes the meaning of
existing notification rows.

## Manual verification of the MVP

1. Log in as Ni Kang (Employee).
2. Open **Documents** from the navigation menu.
3. Choose a PDF under 25 MB, set Title to `Test Document` and Category to `Personal Files`.
4. Upload. A progress indicator appears, then a success message.
5. The document appears in My Documents with the right title, category, size, type, and date.
6. Confirm a file now exists under `ContosoDashboard/Storage/uploads/{userId}/personal/`
   with a GUID name, and that one `Documents` row points at it.
7. Try a file over 25 MB — expect a rejection naming the limit, no file written, no row.
8. Try a `.exe` — expect a rejection naming the permitted types.
9. Log in as a different user and request the first document's content URL directly by its
   id — expect `404`, not the file.
