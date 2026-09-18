# Implementation Plan: Document Upload and Management

**Branch**: `001-document-upload-management` | **Date**: 2026-09-18 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/001-document-upload-management/spec.md`

## Summary

Add document upload, organization, retrieval, sharing, and audit to ContosoDashboard. Files
are stored on the local filesystem outside `wwwroot` under GUID-derived paths and served only
through an endpoint that performs the same service-layer authorization check as every other
read. Metadata lives in three new EF Core entities alongside the existing schema. Two
interfaces — `IFileStorageService` and `IFileValidationService` — isolate the two places the
training build knowingly diverges from production: local disk instead of blob storage, and
content validation instead of malware scanning.

## Technical Context

**Language/Version**: C# 13 / .NET 9
**Primary Dependencies**: ASP.NET Core Blazor Server, Entity Framework Core 9 (SQLite provider)
**Storage**: SQLite (`ContosoDashboard.db`) for metadata; local filesystem under
`Storage/uploads/` for file content — both file-local and offline, per constitution Principle II
**Testing**: xUnit in a new `ContosoDashboard.Tests` project; EF Core SQLite in-memory for
integration tests, temporary directories for storage tests
**Target Platform**: Cross-platform (Windows, macOS, Linux) — the SQLite migration removed the
Windows-only LocalDB dependency
**Project Type**: Single ASP.NET Core web application; Blazor Server UI and services in one project
**Performance Goals**: 25 MB upload within 30s; 500-document list within 2s; search within 2s;
preview within 3s (SC-002 through SC-005)
**Constraints**: No network dependency at runtime; no cloud SDK references; file storage must
remain swappable for blob storage without changes to business logic, UI, or schema
**Scale/Scope**: Four seeded users, hundreds of documents, 25 MB per file. 8 user stories,
50 functional requirements, 2 new Blazor pages, 2 new shared components, 1 new endpoint,
3 new entities, 6 new service types.

## Constitution Check

*GATE: checked before Phase 0 and re-checked after Phase 1 design.*

| Principle | Status | How this design satisfies it |
|---|---|---|
| I — Security & authorization in depth | PASS | `[Authorize]` on both new pages; every `IDocumentService` method takes the acting user id and enforces permission itself; the content endpoint routes through the same service check. Unauthorized and nonexistent are indistinguishable (`404`/`null`), so identifier enumeration leaks nothing. |
| II — Offline-first, no external runtime dependencies | PASS | Local filesystem and SQLite only. `IFileStorageService` is an interface, not an Azure dependency — no cloud SDK package is added. No new outbound call of any kind. |
| III — Data integrity & validation at trust boundaries | PASS | Server-side size, extension, MIME, leading-byte, and zero-length validation independent of the client. Upload ordering is validate → path → file → row, with compensation on failure. `StoragePath` is GUID-derived and unique-indexed. |
| IV — Testability via dependency injection | PASS | All six new service types are interface-first and constructor-injected. Pages never touch `ApplicationDbContext`. A test project is introduced by this feature, closing a standing gap. |
| V — Architectural consistency | PASS | Models/Data/Services/Pages layering, integer PKs, `[Required]`/`[MaxLength]` annotations, async EF with `.Include()`, `IBrowserFile`→`MemoryStream` upload pattern — all matching existing code. |
| VI — Simplicity for the training context | PASS | Tags inline rather than a normalized tag table; no soft delete; no versioning; nothing from the spec's out-of-scope list is built. Two deviations are recorded below. |

**Non-negotiable constraints**: 25 MB limit, the file-type whitelist, storage outside
`wwwroot` with authenticated serving, integer PK, text category, 255-char MIME column, and
the four performance targets are all carried into the data model and the contract.

**Re-check after Phase 1**: no principle is violated by the resulting design. Two simplicity
deviations are tracked in Complexity Tracking below rather than left implicit.

## Project Structure

### Documentation (this feature)

```text
specs/001-document-upload-management/
├── plan.md              # This file
├── spec.md              # Feature specification (with clarifications)
├── research.md          # Phase 0 — 10 decisions with rejected alternatives
├── data-model.md        # Phase 1 — entities, relationships, indexes, validation
├── quickstart.md        # Phase 1 — setup, layout, traps, manual verification
├── contracts/
│   └── document-service.md   # Phase 1 — service and HTTP contracts, permission matrix
├── checklists/
│   └── requirements.md       # Requirements quality checklist
└── tasks.md             # Phase 2 — produced by /speckit.tasks, not by this command
```

### Source Code (repository root)

```text
ContosoDashboard/
├── Models/
│   ├── Document.cs                    # new
│   ├── DocumentShare.cs               # new
│   ├── DocumentActivity.cs            # new
│   ├── Notification.cs                # modified — 3 enum members appended
│   ├── User.cs, Project.cs, TaskItem.cs  # modified — navigation collections
├── Data/
│   └── ApplicationDbContext.cs        # modified — DbSets, relationships, indexes
├── Services/
│   ├── IDocumentService.cs            # new
│   ├── DocumentService.cs             # new
│   ├── IFileStorageService.cs         # new
│   ├── LocalFileStorageService.cs     # new
│   ├── IFileValidationService.cs      # new
│   └── FileValidationService.cs       # new
├── Endpoints/
│   └── DocumentContentEndpoint.cs     # new
├── Pages/
│   ├── Documents.razor                # new
│   ├── DocumentDetails.razor          # new
│   ├── Index.razor                    # modified — recent documents widget, count card
│   ├── ProjectDetails.razor           # modified — project documents section
│   └── Tasks.razor                    # modified — task attachments
├── Shared/
│   ├── DocumentUploadModal.razor      # new
│   ├── RecentDocumentsWidget.razor    # new
│   └── NavMenu.razor                  # modified — Documents nav entry
├── Program.cs                         # modified — DI registration, endpoint mapping
└── Storage/uploads/                   # runtime, git-ignored, outside wwwroot

ContosoDashboard.Tests/                # new
├── Unit/
│   ├── FileValidationServiceTests.cs
│   ├── StoragePathTests.cs
│   └── TagNormalizationTests.cs
└── Integration/
    ├── DocumentServiceAuthorizationTests.cs
    ├── DocumentUploadCompensationTests.cs
    └── DocumentSearchTests.cs
```

**Structure Decision**: Single-project web application. The feature extends the existing
ContosoDashboard project in place, following its Models/Data/Services/Pages layering — the
stakeholder document's constraint "must work within current application architecture (no
major rewrites)" rules out splitting into separate frontend and backend projects. The one
structural addition is a sibling test project, which the repository currently lacks.

## Phase summary

**Phase 0 — Research** (`research.md`): 10 decisions. Storage layout, the storage
abstraction, upload ordering, content validation standing in for malware scanning, Blazor
upload mechanics, authorized file serving, query performance and indexing, tag storage,
notification integration, and the testing approach. Each records the alternatives rejected
and why.

**Phase 1 — Design** (`data-model.md`, `contracts/`, `quickstart.md`): three entities with
their relationships, delete behaviors, and indexes; the `IDocumentService` surface with an
explicit permission matrix; the HTTP content endpoint with its deliberately ambiguous `404`;
four invariants; and a setup guide that names the three traps most likely to cost a day.

**Phase 2 — Tasks**: produced by `/speckit.tasks`, not by this command.

## Complexity Tracking

Two deviations from the simplicity principle, recorded rather than left implicit.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| A third entity, `DocumentActivity`, beyond the two the feature strictly needs | FR-041 and FR-042 require an audit trail of uploads, downloads, deletions, and shares, and Administrator reports over it. Neither can be derived from `Document` and `DocumentShare` alone — a download leaves no other trace. | Inferring activity from `UploadedDate` and share rows would cover uploads and shares but not downloads or deletions, which are exactly the two the compliance requirement most cares about. |
| A new `ContosoDashboard.Tests` project, which no other feature in this repository has | Constitution Principle IV requires testability, and the authorization rules and upload compensation path are where a silent regression is most costly. The repository has no test project, so the principle is otherwise unenforceable. | Manual testing alone, as the lab suggests, demonstrates the feature but leaves every permission rule unguarded against regression, and cannot exercise the storage-failure compensation path at all. |

## Known limitations carried forward

These are design decisions, not oversights, and each is stated in the spec:

- Content validation is not malware scanning. The seam exists; the engine does not.
- `DocumentActivity` rows cascade away with their document, so the audit trail does not
  outlive its subject. A compliance-grade log would denormalize the title and survive deletion.
- Metadata edits are last-write-wins with no conflict detection.
- Adding these tables requires deleting the existing SQLite file, because `EnsureCreated()`
  does not alter an existing schema. Acceptable only because all data is seeded.
