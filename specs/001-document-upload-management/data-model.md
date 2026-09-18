# Phase 1 Data Model: Document Upload and Management

**Feature**: `001-document-upload-management` | **Date**: 2026-09-18

Three new entities join the existing schema. Conventions follow the existing models:
integer surrogate keys named `{Entity}Id`, `[Required]` and `[MaxLength]` data annotations,
`DateTime.UtcNow` defaults, and virtual navigation properties with `[ForeignKey]`.

## Document

The uploaded file and its metadata.

| Property | Type | Constraints | Notes |
|---|---|---|---|
| `DocumentId` | `int` | PK, identity | Integer per FR-044, matching `User` and `Project` |
| `Title` | `string` | Required, max 255 | User-supplied |
| `Description` | `string?` | Max 2000 | Optional; matches `Project.Description` width |
| `Category` | `string` | Required, max 50 | Text per FR-045, not an enum. One of the six permitted values |
| `Tags` | `string?` | Max 550 | Delimited; at most 10 tags of 50 chars plus separators |
| `OriginalFileName` | `string` | Required, max 255 | Preserved for display and download (FR-017a, FR-017b) |
| `StoragePath` | `string` | Required, max 500, unique | `{userId}/{projectId\|personal}/{guid}.{ext}` — never derived from user input |
| `FileSizeBytes` | `long` | Required | Captured automatically; 25 MB ceiling enforced before write |
| `ContentType` | `string` | Required, max 255 | 255 chars per FR-009, for long Office MIME types |
| `UploadedByUserId` | `int` | Required, FK → `User` | Owner |
| `ProjectId` | `int?` | FK → `Project`, nullable | Null means a personal document |
| `TaskItemId` | `int?` | FK → `TaskItem`, nullable | Set when uploaded from a task (FR-036) |
| `UploadedDate` | `DateTime` | Default `UtcNow` | |
| `UpdatedDate` | `DateTime` | Default `UtcNow` | Touched on metadata edit or file replacement |

**Relationships**
- `Document` → `User` (uploader), many-to-one, `DeleteBehavior.Restrict`. A user with
  documents cannot be deleted out from under them.
- `Document` → `Project`, many-to-one, optional, **`DeleteBehavior.SetNull`**. This is what
  implements FR-031b: deleting a project disassociates its documents instead of destroying
  them, leaving them as the uploader's personal documents.
- `Document` → `TaskItem`, many-to-one, optional, `DeleteBehavior.SetNull`.
- `Document` → `DocumentShare`, one-to-many, `DeleteBehavior.Cascade`. Deleting a document
  removes its shares (FR-031a).
- `Document` → `DocumentActivity`, one-to-many, `DeleteBehavior.Cascade`.

**Indexes**
- `UploadedByUserId` — the "My Documents" query
- `ProjectId` — the project documents query
- `Category` — category filtering
- `UploadedDate` — date-range filtering and the recent-documents widget
- `StoragePath` unique — guards the duplicate-key failure the stakeholder document warns
  about, and makes an orphaned empty path impossible to insert twice

**Validation rules**
- `Category` must be one of: Project Documents, Team Resources, Personal Files, Reports,
  Presentations, Other (FR-007)
- `FileSizeBytes` must be greater than zero (FR-013b) and at most 26,214,400 (FR-002)
- File extension must be one of `.pdf .doc .docx .xls .xlsx .ppt .pptx .txt .jpg .jpeg .png`
  (FR-003)
- Leading bytes must be consistent with the declared content type (FR-013)
- `ProjectId`, when set, must reference a project the uploader is a member of (FR-010)

## DocumentShare

A grant of read access to a document for a user who is not its owner.

| Property | Type | Constraints | Notes |
|---|---|---|---|
| `DocumentShareId` | `int` | PK, identity | |
| `DocumentId` | `int` | Required, FK → `Document` | |
| `SharedWithUserId` | `int` | Required, FK → `User` | Recipient |
| `SharedByUserId` | `int` | Required, FK → `User` | Always the document owner in this release |
| `SharedDate` | `DateTime` | Default `UtcNow` | |

**Relationships**
- → `Document`, many-to-one, `DeleteBehavior.Cascade`
- → `User` (recipient and sharer), many-to-one, `DeleteBehavior.Restrict`

**Indexes**
- Unique on `(DocumentId, SharedWithUserId)` — sharing the same document with the same person
  twice is a no-op, not a second row
- `SharedWithUserId` — the "Shared with Me" query

**Validation rules**
- A document cannot be shared with its own owner
- Only the owner may create a share (FR-032)

## DocumentActivity

An append-only audit record. Never updated, never deleted except by cascade.

| Property | Type | Constraints | Notes |
|---|---|---|---|
| `DocumentActivityId` | `int` | PK, identity | |
| `DocumentId` | `int` | Required, FK → `Document` | |
| `UserId` | `int` | Required, FK → `User` | The actor |
| `Action` | `string` | Required, max 20 | `Upload`, `Download`, `Delete`, `Share` — text, consistent with the category decision |
| `OccurredDate` | `DateTime` | Default `UtcNow` | |

**Relationships**
- → `Document`, many-to-one, `DeleteBehavior.Cascade`
- → `User`, many-to-one, `DeleteBehavior.Restrict`

**Indexes**
- `DocumentId`
- `(UserId, OccurredDate)` — the most-active-uploaders report
- `(Action, OccurredDate)` — the document-type and access-pattern reports

**Note on cascade**: activity rows are removed when their document is deleted. This is a
deliberate simplification — a compliance-grade audit log would outlive its subject and store
a denormalized document title instead of a foreign key. Recorded as a limitation rather than
silently accepted.

## Changes to existing entities

**`User`** — add `Documents`, `DocumentShares`, and `DocumentActivities` collection
navigation properties. No column changes.

**`Project`** — add a `Documents` collection navigation property. No column changes.

**`TaskItem`** — add a `Documents` collection navigation property. No column changes.

**`NotificationType`** — append `DocumentShared`, `DocumentAddedToProject`, and
`DocumentDeleted`. **These must be appended at the end of the enum.** EF Core persists the
enum as its integer value, so inserting members in the middle would silently reinterpret
every existing notification row.

**`ApplicationDbContext`** — add `DbSet<Document>`, `DbSet<DocumentShare>`, and
`DbSet<DocumentActivity>`; configure the relationships, delete behaviors, and indexes above
in `OnModelCreating`.

## Schema creation

The application calls `context.Database.EnsureCreated()` at startup, which creates the schema
only when the database file does not yet exist — it will not alter an existing one. Adding
these tables to a database created before this feature therefore requires deleting
`ContosoDashboard.db` and letting it be recreated with seed data. This is acceptable for a
training project with seeded-only data and is called out in `quickstart.md`.

## State transitions

A document has no status field; its lifecycle is structural:

```
(none) ──upload──▶ Stored ──edit metadata──▶ Stored
                     │
                     ├──replace file──▶ Stored (new StoragePath, old file removed)
                     ├──share──────────▶ Stored (+ DocumentShare rows)
                     └──delete─────────▶ (none) (file removed, shares cascaded,
                                                 recipients notified)
```

A failed upload reaches no state: the file is removed and no row is created (FR-012).
