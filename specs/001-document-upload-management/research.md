# Phase 0 Research: Document Upload and Management

**Feature**: `001-document-upload-management` | **Date**: 2026-09-18

Decisions taken before design, each with the alternatives that were rejected. Every decision
is constrained by the constitution's offline-first and local-only persistence principles.

## R1 — File storage location and layout

**Decision**: Store uploaded files under a `Storage/uploads` directory at the application
content root, outside `wwwroot`, organized as `{userId}/{projectId|"personal"}/{guid}.{ext}`.

**Rationale**: Files under `wwwroot` are served by static file middleware with no
authorization, which would make every uploaded document world-readable to anyone who can
guess a path — a direct violation of FR-014 and FR-015. Placing them outside the web root
forces retrieval through an endpoint that can check permissions. The path segments give
cheap per-user and per-project isolation on disk, and the GUID filename removes any
influence of user input on the path (FR-016).

**Alternatives rejected**:
- *Files in the database as `BLOB` columns* — keeps everything transactional and avoids
  orphans entirely, but loads whole files into memory on every read, bloats the SQLite file,
  and makes the future blob-storage migration a schema change rather than a service swap.
- *Flat directory of GUIDs* — simpler, but loses the per-user/per-project structure that the
  stakeholder document calls for and that maps directly onto blob container prefixes later.

## R2 — Storage abstraction for future cloud migration

**Decision**: Define `IFileStorageService` with `UploadAsync`, `DownloadAsync`,
`DeleteAsync`, and `GetUrlAsync`, implemented by `LocalFileStorageService` using
`System.IO`. Register it in DI so an alternative implementation can be substituted through
configuration alone.

**Rationale**: Required by the stakeholder document and by FR-018. The same
`{userId}/{projectId}/{guid}.{ext}` string is a valid blob name, so the database column
needs no change when the implementation is swapped. The constitution permits the interface
while forbidding any cloud SDK reference in this build.

**Alternatives rejected**:
- *Call `System.IO` directly from `DocumentService`* — fewer moving parts, but welds the
  business logic to the filesystem and makes the migration a rewrite of the service.
- *Ship an `AzureBlobStorageService` alongside it* — would add an Azure SDK package
  reference, which the constitution forbids outright.

## R3 — Upload ordering and orphan prevention

**Decision**: Validate → generate storage path → write file to disk → persist metadata. If
the metadata write fails, delete the file just written before surfacing the error.

**Rationale**: The stakeholder document calls this out explicitly, and for good reason: the
reverse order produces database rows with empty or non-unique file paths, which then collide
on the unique index and break subsequent uploads. Compensating on failure (FR-012, FR-017c)
is necessary because a filesystem write cannot enlist in the database transaction.

**Alternatives rejected**:
- *Persist metadata first, then write the file* — creates rows pointing at files that may
  never exist, the exact failure the stakeholder document warns about.
- *Distributed transaction across filesystem and database* — not available to SQLite and far
  beyond the complexity this feature warrants.

## R4 — Content validation in place of malware scanning

**Decision**: Validate extension against a whitelist, validate the declared MIME type, and
verify the file's leading bytes match the declared type. Perform this behind
`IFileValidationService` so a real scanner can replace it without touching the upload flow.

**Rationale**: The stakeholder document requires malware scanning and simultaneously forbids
external dependencies; both cannot hold. The clarification session resolved this as a
documented limitation with the seam preserved. Leading-byte checking catches the cheap attack
of renaming an executable to `.pdf`, which extension checking alone misses.

**Alternatives rejected**:
- *Claim the extension check satisfies the scanning requirement* — dishonest, and would leave
  a reader of the spec believing the system scans when it does not.
- *Integrate ClamAV or similar* — a real scanner, but an external service dependency that
  breaks offline operation and the constitution's non-negotiable second principle.

## R5 — Blazor Server file upload mechanics

**Decision**: Use `InputFile` with a `@key` that changes after each successful upload. Read
name, size, and content type into locals before opening the stream, copy
`OpenReadStream(maxAllowedSize)` into a `MemoryStream` immediately, then null the
`IBrowserFile` reference and call `StateHasChanged`.

**Rationale**: `IBrowserFile`'s stream is bound to the SignalR circuit and is disposed once
the component re-renders; touching it afterwards throws. Copying eagerly into a
`MemoryStream` decouples the upload from the circuit's lifetime. The `@key` change forces
`InputFile` to re-render so a second upload of the same file is possible. `OpenReadStream`
defaults to a 512 KB cap and must be passed the 25 MB limit explicitly or every real document
fails.

**Alternatives rejected**:
- *Stream `IBrowserFile` straight to disk* — avoids buffering 25 MB in memory, but the stream
  disposal semantics make it fragile in Blazor Server, which is the specific trap the
  stakeholder document documents at length.

## R6 — Serving files with authorization

**Decision**: Serve downloads and previews from a minimal API endpoint,
`GET /api/documents/{id}/content`, that resolves the document, runs the same service-layer
permission check as every other read, then streams from `IFileStorageService`. Preview uses
`Content-Disposition: inline`, download uses `attachment`.

**Rationale**: Files outside `wwwroot` need an endpoint to reach the browser. Routing it
through the service layer means the authorization rule lives in exactly one place (FR-039),
and an attacker enumerating identifiers gets a refusal rather than content (FR-019).

**Alternatives rejected**:
- *Blazor component streaming via `NavigationManager`* — awkward for large files and for PDF
  preview, which wants a real URL.
- *Time-limited signed URLs* — the shape blob storage will use, but pointless complexity
  against a local filesystem where the endpoint already has the user's identity.

## R7 — Search and list performance

**Decision**: Filter, sort, and paginate in the database via `IQueryable`, never in memory.
Index `UploadedByUserId`, `ProjectId`, `Category`, and `UploadedDate` on `Documents`, and
`(SharedWithUserId, DocumentId)` on `DocumentShares`.

**Rationale**: SC-003 requires 500 documents to list within 2 seconds and SC-004 requires
search within 2 seconds. Materializing the table and filtering with LINQ-to-objects would
meet neither as data grows, and would violate the constitution's prohibition on unbounded
in-memory accumulation. The chosen indexes match the filter and sort columns the spec names.

**Alternatives rejected**:
- *Full-text search* — SQLite FTS5 would serve description and tag search better, but adds a
  shadow table and migration complexity disproportionate to a 500-document target.

## R8 — Tag storage

**Decision**: Store tags as a single delimited text column on `Document`, bounded to 10 tags
of 50 characters each, normalized to lowercase for matching.

**Rationale**: The clarification session bounded tags precisely so they can be stored
inline. A separate `Tag` entity plus join table is the correct relational model, but at 10
tags per document with no tag-management UI in scope, it adds two tables and a join to every
query for no behavior the spec asks for. The constitution's simplicity principle prefers the
smaller model, and the stakeholder document pushes the same way with its integer-key and
text-category constraints.

**Alternatives rejected**:
- *Normalized `Tag` + `DocumentTag` tables* — cleaner and what a production system should do;
  revisit if tag management or tag-based navigation enters scope.

## R9 — Notification integration

**Decision**: Reuse the existing `INotificationService` and `Notification` entity, extending
the `NotificationType` enum with `DocumentShared`, `DocumentAddedToProject`, and
`DocumentDeleted`.

**Rationale**: FR-033, FR-035, and FR-031a all call for in-app notification, and the
application already has a working notification pipeline with an unread-count UI. Extending
the existing enum is the smallest change that satisfies them.

**Note**: `NotificationType` is persisted as an integer by EF Core, so the three new members
must be appended to the end of the enum. Inserting them in the middle would silently
re-map the meaning of every notification row already seeded.

**Alternatives rejected**:
- *A separate document notification channel* — duplicates a working system for no benefit.

## R10 — Testing approach

**Decision**: Add a `ContosoDashboard.Tests` xUnit project. Unit-test validation, tag
normalization, and path generation as pure logic. Integration-test `DocumentService`
authorization and the upload compensation path against EF Core's SQLite in-memory provider
and a temporary directory.

**Rationale**: The repository currently has no test project at all, so the constitution's
testability principle is aspirational until one exists. Authorization rules and the orphan
compensation path are where a silent regression would be most costly, and both are reachable
only through the service layer.

**Alternatives rejected**:
- *Manual testing only, as the lab suggests* — adequate to demonstrate the feature, but
  leaves every authorization rule unguarded against regression.
