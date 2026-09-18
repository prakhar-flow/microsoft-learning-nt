# Tasks: Document Upload and Management

**Input**: Design documents from `/specs/001-document-upload-management/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/document-service.md

**Tests**: Included. The constitution's testability principle and research decision R10 call
for them, and the repository currently has no test project.

**Organization**: Grouped by phase, then by user story, so each story can be implemented and
demonstrated independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: may run in parallel — different files, no dependency on another incomplete task
- **[USn]**: the user story this task serves

## Path Conventions

Paths are relative to the repository root. Application code lives in `ContosoDashboard/`,
tests in `ContosoDashboard.Tests/`.

---

## Phase 1: Setup

**Purpose**: Groundwork that touches no feature behavior.

- [ ] T001 Create `ContosoDashboard.Tests` xUnit project targeting net9.0, referencing `ContosoDashboard`, with `Microsoft.EntityFrameworkCore.Sqlite` and `Microsoft.EntityFrameworkCore.InMemory`
- [ ] T002 Add a solution file at the repository root and add both projects to it
- [ ] T003 [P] Add `ContosoDashboard/Storage/` to `.gitignore` so uploaded files are never committed
- [ ] T004 [P] Add a `DocumentStorage:RootPath` setting to `appsettings.json` defaulting to `Storage/uploads`, read through `IConfiguration`

**Checkpoint**: `dotnet build` and `dotnet test` both succeed with zero tests.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Entities, persistence, and the two abstractions every story depends on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T005 [P] Create `ContosoDashboard/Models/Document.cs` with all 14 properties, data annotations, and navigation properties per data-model.md
- [ ] T006 [P] Create `ContosoDashboard/Models/DocumentShare.cs` per data-model.md
- [ ] T007 [P] Create `ContosoDashboard/Models/DocumentActivity.cs` per data-model.md
- [ ] T008 Append `DocumentShared`, `DocumentAddedToProject`, `DocumentDeleted` to the **end** of the `NotificationType` enum in `ContosoDashboard/Models/Notification.cs` — appending is required because EF persists the enum as an integer
- [ ] T009 Add `Documents`, `DocumentShares`, `DocumentActivities` collection navigation properties to `Models/User.cs`, and `Documents` to `Models/Project.cs` and `Models/TaskItem.cs`
- [ ] T010 Add the three `DbSet` properties to `ContosoDashboard/Data/ApplicationDbContext.cs`
- [ ] T011 Configure relationships in `OnModelCreating`: `Document`→`User` Restrict, `Document`→`Project` **SetNull**, `Document`→`TaskItem` SetNull, `DocumentShare`→`Document` Cascade, `DocumentActivity`→`Document` Cascade. SetNull on `ProjectId` is what implements FR-031b
- [ ] T012 Configure indexes in `OnModelCreating`: `Document` on `UploadedByUserId`, `ProjectId`, `Category`, `UploadedDate`, and unique on `StoragePath`; `DocumentShare` unique on `(DocumentId, SharedWithUserId)` and on `SharedWithUserId`; `DocumentActivity` on `DocumentId`, `(UserId, OccurredDate)`, `(Action, OccurredDate)`
- [ ] T013 [P] Create `ContosoDashboard/Services/IFileStorageService.cs` with the four methods from contracts/document-service.md
- [ ] T014 Implement `ContosoDashboard/Services/LocalFileStorageService.cs` using `System.IO`, resolving the root from configuration, creating directories as needed, and returning the content endpoint path from `GetUrlAsync`
- [ ] T015 [P] Create `ContosoDashboard/Services/IFileValidationService.cs` and `FileValidationResult`
- [ ] T016 Implement `ContosoDashboard/Services/FileValidationService.cs`: extension whitelist, MIME check, 25 MB ceiling, zero-length rejection, 255-char filename limit, and leading-byte signature check for PDF, JPEG, PNG, ZIP-based Office formats
- [ ] T017 Implement storage path generation `{userId}/{projectId|personal}/{guid}.{ext}` — never incorporating the user-supplied filename
- [ ] T018 Implement tag normalization: split, trim, lowercase, deduplicate, reject more than 10 tags or any tag over 50 characters
- [ ] T019 Register `IFileStorageService`, `IFileValidationService`, and `IDocumentService` as scoped services in `ContosoDashboard/Program.cs`
- [ ] T020 [P] Unit-test `FileValidationService` in `ContosoDashboard.Tests/Unit/FileValidationServiceTests.cs`: oversized, zero-byte, disallowed extension, extension/content mismatch, overlong filename, each allowed type
- [ ] T021 [P] Unit-test path generation in `ContosoDashboard.Tests/Unit/StoragePathTests.cs`, including that a filename containing `../` or a path separator cannot affect the generated path
- [ ] T022 [P] Unit-test tag normalization in `ContosoDashboard.Tests/Unit/TagNormalizationTests.cs`

**Checkpoint**: schema creates cleanly on a fresh database; validation, path, and tag logic are covered by passing tests.

---

## Phase 3: User Story 1 — Upload a document with metadata (P1) 🎯 MVP

**Goal**: An employee can upload a document with metadata and see it in My Documents.

**Independent Test**: Log in as Ni Kang, upload a PDF under 25 MB with a title and category, confirm it appears in My Documents with correct metadata and a file on disk under a GUID name.

### Tests for User Story 1

- [ ] T023 [P] [US1] Integration test in `ContosoDashboard.Tests/Integration/DocumentUploadTests.cs`: a valid upload creates exactly one row and one file
- [ ] T024 [P] [US1] Integration test: upload rejected for oversized, disallowed-type, zero-byte, and missing-required-field inputs, with no row and no file left behind
- [ ] T025 [P] [US1] Integration test in `DocumentUploadCompensationTests.cs`: when the metadata write fails, the written file is removed and no row survives
- [ ] T026 [P] [US1] Integration test: associating a document with a project the user does not belong to is rejected

### Implementation for User Story 1

- [ ] T027 [US1] Create `ContosoDashboard/Services/IDocumentService.cs` with the full method surface from contracts/document-service.md
- [ ] T028 [US1] Implement `DocumentService.UploadAsync` in `ContosoDashboard/Services/DocumentService.cs` following the R3 ordering — validate, authorize project membership, generate path, write file, persist row — with file cleanup if the row cannot be saved
- [ ] T029 [US1] Implement `DocumentService.GetMyDocumentsAsync` returning the acting user's documents, composed as `IQueryable` and executed once
- [ ] T030 [US1] Implement `DocumentService.GetCountAsync`
- [ ] T031 [US1] Write `ContosoDashboard/Pages/Documents.razor` with `@attribute [Authorize]`, `@page "/documents"`, and a table of the user's documents showing title, category, upload date, file size, and project
- [ ] T032 [US1] Write `ContosoDashboard/Shared/DocumentUploadModal.razor` using `InputFile` with a `@key` that changes after each upload; read `Name`, `Size`, `ContentType` into locals before opening the stream; pass the 25 MB cap to `OpenReadStream`; copy to `MemoryStream`; null the `IBrowserFile` and call `StateHasChanged`
- [ ] T033 [US1] Add the metadata form to the modal: required title, required category from the six permitted values, optional description, optional project restricted to the user's projects, optional tags
- [ ] T034 [US1] Add an upload progress indicator and explicit success and error messages
- [ ] T035 [US1] Add client-side size and type pre-checks that mirror — and never replace — the server-side validation
- [ ] T036 [US1] Add a **Documents** entry to `ContosoDashboard/Shared/NavMenu.razor`
- [ ] T037 [US1] Write a `DocumentActivity` of `Upload` on every successful upload
- [ ] T038 [US1] Notify project members when a document is uploaded to their project, using the appended `DocumentAddedToProject` notification type

**Checkpoint**: 🎯 **MVP complete and demonstrable.** Upload works end to end; the MVP acceptance scenarios in spec.md US1 can be walked through by hand.

---

## Phase 4: User Story 2 — Find a document again (P2)

**Goal**: Sort, filter, and search across accessible documents.

**Independent Test**: With seeded documents, exercise every sort column, every filter, and search over title, description, tags, uploader, and project; confirm inaccessible documents never appear.

- [ ] T039 [P] [US2] Integration test in `DocumentSearchTests.cs`: search never returns a document the acting user cannot access
- [ ] T040 [P] [US2] Integration test: each filter and each sort column returns the expected set and order
- [ ] T041 [US2] Define `DocumentQuery` carrying sort column, sort direction, category, project, date range, and paging
- [ ] T042 [US2] Implement sorting by title, upload date, category, and file size in the database query
- [ ] T043 [US2] Implement filtering by category, project, and date range in the database query
- [ ] T044 [US2] Implement `DocumentService.SearchAsync` over title, description, tags, uploader display name, and project name, applying the permission filter inside the same query
- [ ] T045 [US2] Implement `DocumentService.GetProjectDocumentsAsync`, returning empty for non-members
- [ ] T046 [US2] Add sort controls, filter controls, and a search box to `Documents.razor`
- [ ] T047 [US2] Add pagination to keep the 500-document target within the 2-second budget

**Checkpoint**: US1 and US2 both work independently.

---

## Phase 5: User Story 3 — Retrieve and preview a document (P2)

**Goal**: Download any accessible document; preview PDFs and images in the browser.

**Independent Test**: Download as owner, as authorized project member, and as an unauthorized user; confirm the third is refused. Preview a PDF and a PNG.

- [ ] T048 [P] [US3] Integration test in `DocumentAuthorizationTests.cs`: requesting another user's document by id returns nothing, indistinguishable from a nonexistent id
- [ ] T049 [P] [US3] Integration test: a metadata row whose file is missing yields a clean failure, not an unhandled exception
- [ ] T050 [US3] Implement `DocumentService.GetByIdAsync` returning `null` for both unauthorized and nonexistent
- [ ] T051 [US3] Implement `DocumentService.OpenContentAsync`, writing a `Download` activity
- [ ] T052 [US3] Create `ContosoDashboard/Endpoints/DocumentContentEndpoint.cs` serving `GET /api/documents/{id}/content`, honoring `disposition=inline|attachment`, returning `404` identically for unauthorized and missing
- [ ] T053 [US3] Map the endpoint in `Program.cs` behind authentication
- [ ] T054 [US3] Sanitize `OriginalFileName` for the `Content-Disposition` header, preserving non-Latin characters via RFC 5987 encoding
- [ ] T055 [US3] Add download buttons to the document list and detail views
- [ ] T056 [US3] Add in-browser preview for PDF and image types, and suppress the preview control for types that cannot be previewed

**Checkpoint**: the round trip — upload, find, retrieve — is complete.

---

## Phase 6: User Story 4 — Correct or update a document (P3)

- [ ] T057 [P] [US4] Integration test: a non-owner cannot edit metadata or replace the file
- [ ] T058 [US4] Implement `DocumentService.UpdateMetadataAsync`, owner-only, touching `UpdatedDate`
- [ ] T059 [US4] Implement `DocumentService.ReplaceFileAsync`: validate, write new file, update row, then remove the superseded file — in that order
- [ ] T060 [US4] Create `ContosoDashboard/Pages/DocumentDetails.razor` with `@attribute [Authorize]`
- [ ] T061 [US4] Add the metadata edit form and the file replacement control, both visible only to the owner

---

## Phase 7: User Story 5 — Remove a document (P3)

- [ ] T062 [P] [US5] Integration test: owner and Project Manager can delete; an unrelated employee cannot
- [ ] T063 [P] [US5] Integration test: deletion removes the row, the file, and every share
- [ ] T064 [US5] Implement `DocumentService.DeleteAsync` with the owner-or-project-manager rule, writing a `Delete` activity before removing the row, and tolerating a file that is already gone
- [ ] T065 [US5] Add a delete control with an explicit confirmation step to the list and detail views

---

## Phase 8: User Story 6 — Share a document (P3)

- [ ] T066 [P] [US6] Integration test: a recipient can read and download but cannot edit, replace, or delete
- [ ] T067 [P] [US6] Integration test: re-sharing with the same recipient is idempotent and does not re-notify
- [ ] T068 [US6] Implement `DocumentService.ShareAsync`, owner-only, idempotent, rejecting a share with the owner, writing a `Share` activity
- [ ] T069 [US6] Implement `DocumentService.GetSharedWithMeAsync`
- [ ] T070 [US6] Notify recipients on share using the `DocumentShared` notification type
- [ ] T071 [US6] Notify former recipients when an owner deletes a shared document, using `DocumentDeleted` — implements FR-031a
- [ ] T072 [US6] Add a share dialog listing selectable users
- [ ] T073 [US6] Add a "Shared with Me" view, read and download only

---

## Phase 9: User Story 7 — Documents in the flow of work (P4)

- [ ] T074 [P] [US7] Integration test: a document uploaded from a task is associated with that task's project automatically
- [ ] T075 [US7] Add a related-documents section and an upload control to the task detail view in `Pages/Tasks.razor`
- [ ] T076 [US7] Derive `ProjectId` from the task when uploading from a task context
- [ ] T077 [US7] Add a project documents section to `Pages/ProjectDetails.razor`
- [ ] T078 [US7] Implement `DocumentService.GetRecentAsync`
- [ ] T079 [P] [US7] Create `ContosoDashboard/Shared/RecentDocumentsWidget.razor` showing the five most recent documents
- [ ] T080 [US7] Add the widget and a document count summary card to `Pages/Index.razor`

---

## Phase 10: User Story 8 — Audit and oversight (P5)

- [ ] T081 [P] [US8] Integration test: every upload, download, delete, and share writes exactly one activity record
- [ ] T082 [P] [US8] Integration test: a non-Administrator is refused access to reporting
- [ ] T083 [US8] Verify activity recording is in place on all four action types and add any that are missing
- [ ] T084 [US8] Implement `DocumentService.GetReportAsync` aggregating most-uploaded types, most active uploaders, and access patterns, Administrator-only
- [ ] T085 [US8] Add an Administrator-only reporting view
- [ ] T086 [US8] Grant Administrators read access to all documents for audit, without granting edit or delete

---

## Phase 11: Polish and verification

- [ ] T087 [P] Verify SC-002 through SC-005 against seeded data: 25 MB upload under 30s, 500-document list under 2s, search under 2s, preview under 3s
- [ ] T088 [P] Walk every acceptance scenario in spec.md by hand and record the outcome
- [ ] T089 Confirm the application functions with the machine disconnected from the network (SC-012)
- [ ] T090 Update `README.md` with the document management feature and its known limitations
- [ ] T091 Confirm no Azure or cloud SDK package reference was added, and no new outbound network call exists
- [ ] T092 Re-run the constitution check in plan.md against the delivered implementation

---

## Dependencies

- **Phase 1** → **Phase 2** → everything else. Phase 2 is a hard gate.
- **US1 (Phase 3)** depends only on Phase 2 and is the MVP.
- **US2, US3** depend on US1 having created documents to find and retrieve.
- **US4, US5, US6** depend on US1; they are independent of each other and of US2/US3.
- **US7** depends on US1; its dashboard widget additionally needs T078.
- **US8** depends on all preceding stories generating activity.
- **Phase 11** depends on everything.

Within a phase, tasks marked **[P]** touch different files and may proceed together. Tasks
without **[P]** touch a file another task in the same phase also touches — chiefly
`DocumentService.cs`, `Program.cs`, `ApplicationDbContext.cs`, and `Documents.razor`.

## Implementation Strategy

### MVP first (recommended)

Implement **T001 – T038**: Setup, Foundational, and User Story 1. That range delivers a
working, demonstrable upload feature — an employee can upload a document with metadata and
see it listed — and establishes every abstraction the remaining stories build on. Stop there,
verify the US1 acceptance scenarios by hand, and only then continue.

### Incremental delivery after the MVP

1. **T039 – T056** (US2, US3) — completes the upload/find/retrieve round trip. This is the
   smallest genuinely useful feature set.
2. **T057 – T073** (US4, US5, US6) — management and sharing.
3. **T074 – T086** (US7, US8) — integration, audit, reporting.
4. **T087 – T092** — performance verification, offline verification, and the constitution
   re-check.

### Notes

- Task count: 92 across 11 phases.
- Test tasks precede the implementation they cover within each story.
- Three traps are worth re-reading in quickstart.md before starting T032, T028, and T008
  respectively: Blazor stream disposal, upload ordering, and enum append-only ordering.
