<!--
Sync Impact Report
==================
Version change: unfilled template → 1.0.0 (initial ratification)

Modified principles: N/A (first concrete version; template placeholders replaced)

Added sections:
  - Core Principles I-VI (Security & Authorization in Depth, Offline-First / No
    External Dependencies, Data Integrity & Validation at Trust Boundaries,
    Testability via Dependency Injection, Architectural Consistency with
    Blazor Server + EF Core, Simplicity for Training Context)
  - Non-Negotiable Constraints (file upload limits, allowed types, performance
    targets, database key conventions)
  - Development Workflow & Quality Gates
  - Governance

Removed sections: N/A (template had no concrete content to remove)

Templates requiring updates:
  - .specify/templates/plan-template.md: ✅ no change needed — "Constitution
    Check" section already derives its gates dynamically from this file at
    plan time; no stack-specific edit required.
  - .specify/templates/spec-template.md: ✅ no change needed — generic,
    technology-agnostic structure already compatible with these principles.
  - .specify/templates/tasks-template.md: ✅ no change needed — "Tests are
    OPTIONAL" framing is compatible with Principle IV (design for
    testability; a suite is added when a feature spec requests it).
  - .specify/templates/commands/*.md: N/A — directory does not exist in this
    repository.
  - README.md: ✅ no change needed — this amendment only clarifies wording
    (embedded/file-local DB, offline scope, baseline SDK exceptions); it does
    not change what is currently true of the repo, so README's existing
    "SQL Server LocalDB" and offline-architecture sections remain accurate.
    When the planned SQLite migration actually lands, README's Database
    section will need a separate, dedicated update — tracked outside this
    constitution edit.
  - AGENTS.md: ✅ no change needed — does not reference persistence provider,
    offline-runtime scope, or SDK-dependency wording touched by this
    amendment.
  - .github/agents/*.md (speckit.*.agent.md): ✅ no change needed — the
    command files (e.g. speckit.plan.agent.md, speckit.analyze.agent.md)
    reference `.specify/memory/constitution.md` only by path and load its
    principles dynamically; none hard-codes a provider name or SDK assertion
    that this amendment would make stale.
  - .github/prompts/*.md (speckit.*.prompt.md): ✅ no change needed — same
    reasoning as the agent files; no provider-specific or SDK-specific
    assertions present.

Follow-up TODOs: none. No prior adoption date is recorded anywhere in repo
history, so the initial ratification is dated to this constitution's
creation (2026-09-18); see Governance below.
-->

# ContosoDashboard Constitution

## Core Principles

### I. Security & Authorization in Depth (NON-NEGOTIABLE)

Every protected page MUST carry an `[Authorize]` attribute; authentication
alone is never sufficient. Every service method that reads or writes a
specific record (task, project, document, notification, etc.) MUST accept
the requesting user's identity and independently verify — inside the service
layer, not just at the page/controller boundary — that the requester is
permitted to touch that record (owner, project member, project manager, or
Administrator, per the feature's role model). A denied check MUST return
`null`/`false`/an empty result to the caller rather than throwing, matching
the existing pattern in `ProjectService` and `TaskService`.

Role-based access follows the hierarchical model already encoded in
`Program.cs` (`Employee` ⊂ `TeamLead` ⊂ `ProjectManager` ⊂ `Administrator`).
New roles or permission tiers MUST slot into this hierarchy, not bypass it.

IDOR protection is mandatory: any endpoint or Blazor page that accepts an
identifier from the URL or a form (project id, task id, document id) MUST
re-verify ownership/membership server-side before returning or mutating
data — never trust that a user only requests IDs they are entitled to.

Rationale: this is a mock-auth training app with no real password/MFA layer
(see README "Known Limitations"), so the authorization logic in the
`[Authorize]` attributes and the service layer is the only real security
boundary in the system. Defense in depth here is not decorative — it is the
entire security model.

### II. Offline-First, No External Runtime Dependencies (NON-NEGOTIABLE)

The application's runtime services and data MUST remain fully local: no
calls to cloud storage, no hosted/networked database, no third-party SaaS
or external HTTP APIs, and no telemetry egress. All persistence MUST use an
embedded, file-local database engine accessed through EF Core (SQL Server
LocalDB on Windows, SQLite elsewhere); no networked, hosted, or cloud
database is permitted, regardless of which embedded provider a given
platform uses. All file storage uses the local filesystem. New features
MUST NOT introduce a compile-time or runtime dependency on Azure SDKs,
external HTTP APIs, or any service that requires internet connectivity for
the application's data or business logic to function in its training
configuration.

Where a feature anticipates future cloud migration (e.g., file storage), it
MUST be built behind an interface abstraction (e.g., `IFileStorageService`)
with a local implementation (`LocalFileStorageService`) registered via
dependency injection in `Program.cs`. The interface MAY be designed with
cloud migration in mind, but the concrete implementation shipped in this
repository MUST remain 100% local and offline-capable. New code MUST NOT
gain a package reference to an Azure SDK or other cloud-provider SDK in
`ContosoDashboard.csproj` for this reason (see the recorded baseline
exceptions below and the Non-Negotiable Constraints section).

**Recorded baseline exceptions** (present in the repository prior to this
constitution; these are not precedent for adding further cloud or network
dependencies, and MUST NOT be exercised or extended without a constitution
amendment):
- `ContosoDashboard.csproj` already references `Microsoft.Identity.Web`
  2.15.0, `Microsoft.Identity.Web.UI` 2.15.0, and
  `Microsoft.AspNetCore.Authentication.OpenIdConnect` 8.0.0, and
  `appsettings.json` carries a populated (placeholder-valued) `AzureAd`
  configuration block. These are inert under the mock cookie-based
  authentication actually wired up in `Program.cs`.
- `Pages/_Host.cshtml` loads Bootstrap CSS/JS and Bootstrap Icons from
  `https://cdn.jsdelivr.net` (whitelisted in the Content-Security-Policy in
  `Program.cs`). This is a static front-end asset dependency, not a runtime
  service or data dependency, but it does require network access unless the
  browser has a cached copy. Vendoring these assets locally is tracked as
  follow-up work (bead LAB14-8).

Rationale: README.md states explicitly the project "does NOT implement cloud
integration or external service dependencies (local only and offline to
maximize training availability)," and the stakeholder document for the
document-upload feature repeats this as a hard technical constraint. The
durable invariant is "no networked/hosted/cloud dependency for runtime data
or services" — the specific embedded database provider is an implementation
detail that is expected to change (SQL Server LocalDB cannot run on
non-Windows hosts, and this repository is scheduled to migrate to SQLite),
so the principle names the invariant rather than the provider.

### III. Data Integrity & Validation at Trust Boundaries

Every user-supplied input MUST be validated at the point it crosses into the
system — file upload, form submission, or query parameter — before it is
persisted or used to construct a file path. Concretely:

- File uploads MUST be rejected server-side (not just via client-side
  `accept` hints) if they exceed the configured maximum size or their
  extension/content-type is not on the allow-list.
- File paths derived from user input MUST use a generated GUID, never a
  user-supplied filename, to prevent path traversal.
- The upload sequence MUST be: validate → generate unique path → save file to
  disk → persist metadata row. Metadata MUST NOT be written before the file
  save succeeds, to avoid orphaned records or duplicate-key failures on an
  empty/reused path.
- Required metadata fields (e.g., document title, category) MUST be enforced
  by both the UI and the service layer; optional fields MUST NOT be required
  by the database schema.

Rationale: the stakeholder document for document upload calls out exactly
this failure mode ("orphaned database records if file save fails," "duplicate
key errors from empty or non-unique file paths") as a known risk in this
codebase, and the existing seed/migration pattern (`EnsureCreated()`,
GUID-based paths) depends on validation happening before persistence.

### IV. Testability via Dependency Injection

Business logic MUST live in a service class registered against an interface
(`IUserService`/`UserService`, `ITaskService`/`TaskService`, etc.) and
injected via constructor injection, following the existing pattern in
`Services/`. Blazor pages and Razor Pages MUST delegate authorization and
data-access logic to injected services rather than querying
`ApplicationDbContext` directly from the UI layer. This keeps business logic
substitutable and mockable even though this repository does not currently
ship an automated test project — when a feature spec requests tests, the
DI-based service layer is what makes them practical to add without
restructuring the codebase.

Rationale: every existing service (`UserService`, `TaskService`,
`ProjectService`, `NotificationService`, `DashboardService`) follows this
constructor-injected, interface-first shape; deviating from it in new code
would make the codebase inconsistent and harder to eventually cover with
tests.

### V. Architectural Consistency with Blazor Server + EF Core

New features MUST follow the established layering: `Models/` for EF Core
entities, `Data/ApplicationDbContext.cs` for the single `DbContext` (with
new `DbSet`s and relationships added there, including indexes for frequently
queried columns), `Services/` for business logic and authorization, and
`Pages/` for Blazor (`.razor`) or Razor Pages (`.cshtml`) presentation.
Integer primary keys MUST be used for new entities to stay consistent with
the existing `User`/`Project`/`TaskItem` keys, unless a specific feature
spec explicitly mandates otherwise (e.g., GUID-based file paths, which are a
storage-path concern, not a database primary key). Async/await MUST be used
for all `DbContext` operations, and eager loading (`.Include()`) MUST be used
to avoid N+1 query patterns, matching the pattern already present in
`ProjectService.GetUserProjectsAsync`.

File-upload handling in Blazor MUST follow the documented
`IBrowserFile` → `MemoryStream` copy pattern (extract metadata before opening
the stream, copy to a `MemoryStream` immediately, null out the `IBrowserFile`
reference afterward) to avoid the disposal and reuse issues specific to
Blazor Server's `InputFile` component.

Rationale: consistency across `Services/` and `Data/` is what lets students
(and future contributors) predict where new logic belongs; ad hoc deviations
(e.g., a raw `DbContext` call from a `.razor` page, or a GUID primary key)
would break that predictability and the training value of the codebase.

### VI. Simplicity for Training Context

Implementations MUST favor the simplest approach that satisfies the current
feature's requirements over speculative generality. Concretely: category
values are stored as plain text, not an integer enum requiring a lookup
table; mock cookie-based authentication is retained rather than integrating
a real identity provider; and out-of-scope items listed in a feature's
stakeholder document (e.g., version history, soft-delete/trash, real-time
collaborative editing) MUST NOT be implemented speculatively "for
completeness." Any deviation toward added complexity MUST be justified in
the relevant plan's Complexity Tracking section.

Rationale: this is explicitly a training codebase (README: "NOT intended as
a model for production applications... simplified for a training context");
unrequested complexity works against its pedagogical purpose and the stated
8-10 week delivery timeline for new features.

## Non-Negotiable Constraints

These limits come directly from approved feature requirements and MUST be
enforced in code, not just documented:

- **File size**: Maximum 25 MB per uploaded file; the server MUST reject
  larger files with a clear error message, independent of any client-side
  limit.
- **File types**: Uploads are restricted to PDF, Microsoft Office documents
  (Word, Excel, PowerPoint), plain text files, and images (JPEG, PNG). The
  extension/content-type allow-list MUST be enforced server-side.
- **File storage location**: Uploaded files MUST be stored outside
  `wwwroot` (e.g., under `AppData/uploads/{userId}/{projectId-or-"personal"}/
  {guid}.{extension}`), and MUST only be served through an authenticated,
  authorization-checked endpoint — never via static file serving.
- **Database key conventions**: New primary keys MUST be integers,
  consistent with `User`/`Project`/`TaskItem`. Category-style lookup fields
  (e.g., `Document.Category`) MUST be stored as text, not integer enums, per
  the stakeholder document for the document-upload feature
  (`StakeholderDocs/document-upload-and-management-feature.md`). This is a
  deliberate departure from the existing convention elsewhere in the schema,
  where `TaskStatus`, `TaskPriority`, `ProjectStatus`, and
  `AvailabilityStatus` are C# enums that EF Core persists as integers by
  default (no `HasConversion<string>()` is configured for them in
  `Data/ApplicationDbContext.cs`) — not a continuation of that convention.
  Fields storing MIME types MUST accommodate at least 255 characters (Office
  document MIME types are long).
- **Performance targets**: Document/list pages MUST load within 2 seconds
  for result sets up to 500 records; search MUST return within 2 seconds;
  uploads of files up to 25 MB MUST complete within 30 seconds on a typical
  network; previews MUST load within 3 seconds. Any implementation that
  cannot meet these targets MUST be flagged in the plan's Complexity
  Tracking section rather than shipped silently.
- **No cloud SDK dependencies**: `ContosoDashboard.csproj` MUST NOT gain a
  package reference to an Azure SDK or other cloud-provider SDK as part of
  the training implementation of any feature (see Principle II).

## Development Workflow & Quality Gates

- Every feature plan produced via `/speckit.plan` MUST pass the Constitution
  Check gate against the principles above before Phase 0 research begins,
  and MUST be re-checked after Phase 1 design.
- Any violation of a Core Principle MUST be recorded in the plan's
  Complexity Tracking table with a concrete justification and a note on why
  a simpler alternative (that would satisfy the principle) was rejected.
  Unjustified violations block the plan from proceeding.
- New service methods that read or mutate a specific record MUST include an
  explicit authorization check as part of the same change — a PR/change that
  adds a new data-access path without an accompanying authorization check
  does not satisfy Principle I and must be revised before merge.
- Changes that add a new entity to `Data/ApplicationDbContext.cs` MUST
  include appropriate indexes for the columns that new queries filter or
  sort on, consistent with Principle V.

## Governance

This constitution supersedes ad hoc conventions and prior undocumented
practice for the ContosoDashboard training codebase. Where this document and
an individual feature's stakeholder requirements conflict, the stakeholder
requirements for that feature take precedence for that feature's scope, but
any such deviation MUST be recorded via the Complexity Tracking mechanism
described above so the conflict is visible and intentional rather than
silent drift.

**Amendment procedure**: A change to this constitution is proposed as a pull
request that edits this file directly, states the rationale for the change,
and — per the versioning policy below — updates the version line and the
Sync Impact Report at the top of this file. Amendments are considered
ratified once merged to the main branch.

**Versioning policy** (semantic versioning applied to governance):
- **MAJOR**: Backward-incompatible removal or redefinition of a Core
  Principle or Non-Negotiable Constraint.
- **MINOR**: Addition of a new principle, section, or materially expanded
  guidance that does not invalidate prior guidance.
- **PATCH**: Wording clarifications, typo fixes, or non-semantic
  refinements that do not change enforceable behavior.

**Compliance review**: Every feature plan's Constitution Check gate (see
Development Workflow above) is the compliance checkpoint. A plan that cannot
satisfy a Core Principle without an unjustified violation MUST be revised
before implementation proceeds.

**Version**: 1.0.0 | **Ratified**: 2026-09-18 | **Last Amended**: 2026-09-18
