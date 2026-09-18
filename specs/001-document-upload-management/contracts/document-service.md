# Contract: IDocumentService

**Feature**: `001-document-upload-management` | **Date**: 2026-09-18

The service layer is the single authorization boundary (FR-039). Every method takes the
acting user's id and enforces permission itself; no caller may assume the UI has filtered.

## Permission model

Derived from FR-040 and the clarification session.

| Capability | Owner | Project member | Team Lead (same dept) | Project Manager (of project) | Administrator |
|---|---|---|---|---|---|
| Read / download | yes | project docs only | team members' docs | all project docs | all |
| Edit metadata | yes | no | no | no | no |
| Replace file | yes | no | no | no | no |
| Delete | yes | no | no | all project docs | no |
| Share | yes | no | no | no | no |
| View reports | no | no | no | no | yes |

A share recipient gets read and download only (FR-034). Administrator access is for audit
(FR-040) and does not extend to editing or deleting another user's document.

## Methods

```csharp
Task<DocumentUploadResult> UploadAsync(DocumentUploadRequest request, int actingUserId);
```
Validates, stores, and records a document. Enforces FR-010 project membership before
accepting `ProjectId`. Performs the R3 ordering and compensates on failure. Writes a
`DocumentActivity` of `Upload`. Notifies project members when `ProjectId` is set (FR-035).
Returns a result carrying either the created document or a validation failure — it does not
throw for expected validation outcomes.

*Failure modes*: file too large, disallowed extension, content/type mismatch, zero bytes,
filename over 255 chars, missing title or category, category not in the permitted set, tag
limits exceeded, project not a member of, storage write failure.

```csharp
Task<Document?> GetByIdAsync(int documentId, int actingUserId);
```
Returns `null` when the document does not exist **or** the user may not read it. The two are
deliberately indistinguishable to the caller so that identifier enumeration reveals nothing
(FR-019).

```csharp
Task<IReadOnlyList<Document>> GetMyDocumentsAsync(int actingUserId, DocumentQuery query);
```
The uploader's own documents, filtered and sorted per FR-021 and FR-022. Composed as
`IQueryable` and executed once (R7).

```csharp
Task<IReadOnlyList<Document>> GetProjectDocumentsAsync(int projectId, int actingUserId, DocumentQuery query);
```
Returns empty when the user is not a member of the project. Does not throw.

```csharp
Task<IReadOnlyList<Document>> GetSharedWithMeAsync(int actingUserId, DocumentQuery query);
```
Documents shared with the user, excluding any they own.

```csharp
Task<IReadOnlyList<Document>> SearchAsync(string term, int actingUserId, DocumentQuery query);
```
Matches title, description, tags, uploader display name, and project name (FR-023). The
permission filter is applied in the same query as the search predicate, never afterwards, so
no unauthorized row is ever materialized (FR-024).

```csharp
Task<Stream?> OpenContentAsync(int documentId, int actingUserId);
```
Returns the file stream, or `null` if unauthorized or missing. Writes a `Download` activity.
Callers must dispose. Returns `null` rather than throwing when the metadata row exists but
the file does not, so the UI can report it cleanly (US3 scenario 4).

```csharp
Task<bool> UpdateMetadataAsync(int documentId, DocumentMetadataUpdate update, int actingUserId);
```
Owner only. Returns `false` when unauthorized or absent.

```csharp
Task<bool> ReplaceFileAsync(int documentId, DocumentFileReplacement replacement, int actingUserId);
```
Owner only. Validates as for upload, writes the new file, updates `StoragePath`,
`FileSizeBytes`, `ContentType`, `OriginalFileName`, and `UpdatedDate`, then removes the
superseded file — in that order, so a failure never leaves the document pointing at a file
that is gone (FR-029).

```csharp
Task<bool> DeleteAsync(int documentId, int actingUserId);
```
Owner, or Project Manager of the document's project. Removes shares, notifies each former
recipient (FR-031a), deletes the metadata row, then removes the file. Writes a `Delete`
activity before the row is removed. A missing file does not fail the deletion.

```csharp
Task<bool> ShareAsync(int documentId, int recipientUserId, int actingUserId);
```
Owner only. Idempotent — re-sharing with the same recipient does not create a second row and
does not re-notify. Rejects sharing with the owner. Writes a `Share` activity and notifies
the recipient (FR-033).

```csharp
Task<IReadOnlyList<Document>> GetRecentAsync(int actingUserId, int count);
Task<int> GetCountAsync(int actingUserId);
```
Back the dashboard widget and summary card (FR-038).

```csharp
Task<DocumentReport> GetReportAsync(int actingUserId);
```
Administrator only; returns empty/denied otherwise. Aggregates most-uploaded types, most
active uploaders, and access patterns (FR-042).

## Supporting contracts

```csharp
public interface IFileStorageService
{
    Task<string> UploadAsync(Stream content, string storagePath, string contentType);
    Task<Stream?> DownloadAsync(string storagePath);
    Task DeleteAsync(string storagePath);
    Task<string> GetUrlAsync(string storagePath, TimeSpan expiration);
}
```
`GetUrlAsync` returns the local content endpoint path in this build and a signed blob URL in
a future one; `expiration` is accepted and ignored locally so the signature survives the
migration unchanged (R2).

```csharp
public interface IFileValidationService
{
    FileValidationResult Validate(string fileName, string contentType, long sizeBytes, Stream content);
}
```
The seam where real malware scanning belongs (R4). The shipped implementation checks
extension, MIME type, size, zero length, and leading bytes.

## HTTP contract

```
GET /api/documents/{id}/content?disposition=inline|attachment
```

| Condition | Response |
|---|---|
| Authorized, file present | `200` with the file, correct `Content-Type`, `Content-Disposition` carrying the sanitized `OriginalFileName` |
| Not authenticated | `302` to `/login` |
| Authorized but file missing from storage | `404` |
| Not authorized, or no such document | `404` — deliberately identical to the previous row, so enumeration distinguishes nothing (FR-019) |

## Invariants

1. No `Document` row exists whose `StoragePath` has no file behind it, and no stored file
   lacks a `Document` row — except transiently inside `UploadAsync` and `ReplaceFileAsync`,
   which compensate on failure.
2. `StoragePath` never contains a byte derived from user input.
3. Every read path applies its permission filter inside the database query, not after it.
4. Every upload, download, delete, and share writes exactly one `DocumentActivity`.
