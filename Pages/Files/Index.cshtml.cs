using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using twoSaaSCore.Data;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomFileCatalog _catalog;
        private readonly AzureBlobOptions _blobOptions;
        private readonly IRoomPermissionService _permissions;
        private readonly IRoomInvitationService _invitations;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _db;
        private readonly IAuditLogger _auditLogger;
        private readonly IRoomAgentService _agentService;

        public IndexModel(ITenantProvider tenantProvider,
                          IRoomFileCatalog catalog,
                          IOptions<AzureBlobOptions> blobOptions,
                          IRoomPermissionService permissions,
                          IRoomInvitationService invitations,
                          UserManager<ApplicationUser> userManager,
                          ApplicationDbContext db,
                          IAuditLogger auditLogger,
                          IRoomAgentService agentService)
        {
            _tenantProvider = tenantProvider;
            _catalog = catalog;
            _blobOptions = blobOptions.Value;
            _permissions = permissions;
            _invitations = invitations;
            _userManager = userManager;
            _db = db;
            _auditLogger = auditLogger;
            _agentService = agentService;
        }

        // Query parameters
        [BindProperty(SupportsGet = true)]
        public Guid? RoomId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? FolderPath { get; set; } // normalized (may be null/empty or end with /)

        // Room list (when no RoomId)
        public List<RoomRow> Rooms { get; private set; } = new();

        // Folder list (immediate children)
        public List<VirtualFolderRow> Folders { get; private set; } = new();

        // Files (in current folder or root of room)
        public List<FileRow> Files { get; private set; } = new();

        // Effective permissions for current user in this room/folder
        public RoomPermission CurrentPermissions { get; private set; } = RoomPermission.None;

        // Members and invitations (populated when ManagePermissions)
        public List<MemberRow> Members { get; private set; } = new();
        public List<InviteRow> PendingInvitations { get; private set; } = new();
        public string? InviteLinkGenerated { get; set; }

        // NDA gate
        public bool ShowNda { get; private set; }
        public string? NdaText { get; private set; }
        public string? RoomName { get; private set; }

        // Room creation permission (Editor+ or no rooms yet)
        public bool CanCreateRooms { get; private set; }

        public class MemberRow
        {
            public string UserId { get; set; } = string.Empty;
            public string? Email { get; set; }
            public RoomRole Role { get; set; }
            public DateTimeOffset GrantedUtc { get; set; }
        }

        public class InviteRow
        {
            public int Id { get; set; }
            public string Email { get; set; } = string.Empty;
            public RoomRole Role { get; set; }
            public DateTimeOffset CreatedUtc { get; set; }
            public DateTimeOffset ExpiresUtc { get; set; }
        }

        public class RoomRow
        {
            public Guid RoomId { get; set; }
            public string Name { get; set; } = string.Empty;
            public DateTimeOffset CreatedUtc { get; set; }
        }

        public class VirtualFolderRow
        {
            public Guid FolderId { get; set; }
            public string Path { get; set; } = string.Empty;   // always ends with /
            public string Name { get; set; } = string.Empty;
            public DateTimeOffset CreatedUtc { get; set; }
            public string? CreatedBy { get; set; }
        }

        public class FileRow
        {
            public Guid FileId { get; set; }
            public string FileName { get; set; } = string.Empty;
            public long Size { get; set; }
            public DateTimeOffset UploadedUtc { get; set; }
            public string? UploadedBy { get; set; }
            public string BlobName { get; set; } = string.Empty;
            public AiIndexingStatus AiStatus { get; set; } = AiIndexingStatus.None;
        }

        private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

        public async Task<IActionResult> OnGetAsync()
        {
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Page();

            if (RoomId == null || RoomId == Guid.Empty)
            {
                // List only rooms the user has access to
                var accessibleRoomIds = await _permissions.ListAccessibleRoomIdsAsync(tenantId, GetUserId());
                await foreach (var r in _catalog.ListRoomsAsync(tenantId))
                {
                    if (accessibleRoomIds.Contains(r.RoomId))
                    {
                        Rooms.Add(new RoomRow
                        {
                            RoomId = r.RoomId,
                            Name = r.Name,
                            CreatedUtc = r.CreatedUtc
                        });
                    }
                }

                // Allow room creation for Editors, Admins, Owners — or if no rooms exist yet
                var highestRole = await _db.RoomMembers
                    .Where(m => m.TenantId == tenantId && m.UserId == GetUserId() && m.FolderPath == null)
                    .Select(m => (RoomRole?)m.Role)
                    .MaxAsync() ?? RoomRole.None;
                CanCreateRooms = highestRole >= RoomRole.Editor || Rooms.Count == 0;

                return Page();
            }

            // Normalize FolderPath (catalog expects trailing slash or empty)
            FolderPath = NormalizeFolderPath(FolderPath);

            // Check room access
            var userId = GetUserId();
            CurrentPermissions = await _permissions.GetEffectivePermissionsAsync(tenantId, RoomId.Value, userId, FolderPath);
            if (!CurrentPermissions.HasFlag(RoomPermission.AccessRoom))
                return Forbid();

            // NDA gate: check if room has NDA text and user has accepted
            var roomMeta = await _catalog.GetRoomAsync(tenantId, RoomId.Value);
            RoomName = roomMeta?.Name;
            if (!string.IsNullOrWhiteSpace(roomMeta?.NdaText))
            {
                var accepted = await _db.NdaAcceptances
                    .AnyAsync(n => n.TenantId == tenantId && n.RoomId == RoomId.Value && n.UserId == userId);
                if (!accepted)
                {
                    ShowNda = true;
                    NdaText = roomMeta.NdaText;
                    return Page();
                }
            }

            // List folders (immediate children)
            await foreach (var vf in _catalog.ListVirtualFoldersAsync(tenantId, RoomId.Value, FolderPath))
            {
                Folders.Add(new VirtualFolderRow
                {
                    FolderId = vf.FolderId,
                    Path = vf.Path,
                    Name = vf.Name,
                    CreatedUtc = vf.CreatedUtc,
                    CreatedBy = vf.CreatedByUserId
                });
            }

            // List files in folder/root
            await foreach (var fm in _catalog.ListFilesAsync(tenantId, RoomId.Value, FolderPath))
            {
                Files.Add(new FileRow
                {
                    FileId = fm.FileId,
                    FileName = fm.OriginalFileName,
                    Size = fm.Size,
                    UploadedUtc = fm.UploadedUtc,
                    UploadedBy = fm.UploadedByUserId,
                    BlobName = fm.BlobName
                });
            }

            // Resolve user IDs to display names (emails)
            var allUserIds = Files.Select(f => f.UploadedBy)
                .Concat(Folders.Select(f => f.CreatedBy))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();
            if (allUserIds.Count > 0)
            {
                var userMap = await _db.Users
                    .Where(u => allUserIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.Email ?? u.UserName ?? u.Id);
                foreach (var file in Files)
                    if (file.UploadedBy != null && userMap.TryGetValue(file.UploadedBy, out var email))
                        file.UploadedBy = email;
                foreach (var folder in Folders)
                    if (folder.CreatedBy != null && userMap.TryGetValue(folder.CreatedBy, out var fEmail))
                        folder.CreatedBy = fEmail;
            }

            // Fetch AI indexing statuses for all files in this view
            if (_agentService.IsConfigured && Files.Count > 0)
            {
                var fileIds = Files.Select(f => f.FileId).ToList();
                var statuses = await _agentService.GetIndexingStatusesAsync(tenantId, RoomId.Value, fileIds);
                foreach (var file in Files)
                {
                    if (statuses.TryGetValue(file.FileId, out var status))
                        file.AiStatus = status;
                }
            }

            // Load members & pending invitations for permission managers
            if (CurrentPermissions.HasFlag(RoomPermission.ManagePermissions))
            {
                var members = await _permissions.ListMembersAsync(tenantId, RoomId.Value);
                foreach (var m in members.Where(m => string.IsNullOrEmpty(m.FolderPath)))
                {
                    var user = await _userManager.FindByIdAsync(m.UserId);
                    Members.Add(new MemberRow
                    {
                        UserId = m.UserId,
                        Email = user?.Email,
                        Role = m.Role,
                        GrantedUtc = m.GrantedUtc
                    });
                }

                var invites = await _invitations.ListPendingInvitationsAsync(tenantId, RoomId.Value);
                foreach (var inv in invites)
                {
                    PendingInvitations.Add(new InviteRow
                    {
                        Id = inv.Id,
                        Email = inv.Email,
                        Role = inv.Role,
                        CreatedUtc = inv.CreatedUtc,
                        ExpiresUtc = inv.ExpiresUtc
                    });
                }
            }

            return Page();
        }

        // ----- Room Handlers -----

        public async Task<IActionResult> OnPostCreateRoomAsync(string name, string? ndaText)
        {
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(name)) return BadRequest();
            var userId = GetUserId();

            // Only Editors, Admins, and Owners can create rooms — unless no rooms exist yet
            var highestRole = await _db.RoomMembers
                .Where(m => m.TenantId == tenantId && m.UserId == userId && m.FolderPath == null)
                .Select(m => (RoomRole?)m.Role)
                .MaxAsync() ?? RoomRole.None;
            var anyRoomsExist = await _db.RoomMembers.AnyAsync(m => m.TenantId == tenantId);
            if (highestRole < RoomRole.Editor && anyRoomsExist)
                return Forbid();

            var room = await _catalog.CreateRoomAsync(tenantId, name.Trim(), ndaText, userId);

            // Auto-grant Owner to the creator
            await _permissions.GrantAccessAsync(tenantId, room.RoomId, userId, RoomRole.Owner, userId);

            return RedirectToPage(new { roomId = (Guid?)null });
        }

        public async Task<IActionResult> OnPostDeleteRoomAsync(Guid roomId)
        {
            if (roomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.ManageRoom))
                return Forbid();

            await _catalog.DeleteRoomAsync(tenantId, roomId);
            return RedirectToPage(new { roomId = (Guid?)null });
        }

        // ----- Folder Handlers -----

        public async Task<IActionResult> OnPostCreateFolderAsync(Guid roomId, string parentPath, string folderName)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(folderName))
                return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            parentPath = NormalizeFolderPath(parentPath);

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.ManageFolders, parentPath))
                return Forbid();

            var safeFolderSegment = folderName.Trim();
            // Compose new folder path under parent
            var fullPath = parentPath + safeFolderSegment + "/";
            var userId = GetUserId();
            await _catalog.CreateVirtualFolderAsync(tenantId, roomId, fullPath, folderName.Trim(), userId);
            return RedirectToPage(new { roomId, folderPath = parentPath });
        }

        public async Task<IActionResult> OnPostDeleteFolderAsync(Guid roomId, string folderPath, bool? recursive)
        {
            if (roomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            folderPath = NormalizeFolderPath(folderPath);
            if (string.IsNullOrEmpty(folderPath)) return BadRequest();

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.ManageFolders, folderPath))
                return Forbid();

            // Determine parent path to redirect back
            var parent = GetParentFolderPath(folderPath);
            await _catalog.DeleteVirtualFolderAsync(tenantId, roomId, folderPath, recursive == true);
            return RedirectToPage(new { roomId, folderPath = parent });
        }

        // ----- File Upload (Small) -----

        public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, Guid roomId, string? folderPath)
        {
            if (file == null || file.Length == 0) return BadRequest("Select a file.");
            if (roomId == Guid.Empty) return BadRequest("roomId required.");
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            folderPath = NormalizeFolderPath(folderPath);

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.Upload, folderPath))
                return Forbid();

            var userId = GetUserId();
            var userObj = await _userManager.FindByIdAsync(userId);
            var userEmail = userObj?.Email;
            await using var s = file.OpenReadStream();
            var stored = await _catalog.StoreFileAsync(tenantId, roomId, file.FileName, s, file.ContentType, userId, folderPath);

            await _auditLogger.LogAsync(new AuditEntry(
                tenantId, roomId, stored.FileId, "Upload", userId, userEmail,
                file.FileName, file.Length, null,
                System.IO.Path.GetExtension(file.FileName)?.ToLowerInvariant(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString().Truncate(256),
                _auditLogger.NewCorrelationId(), null));

            // Send file to the AI vector store for indexing
            await _agentService.UploadFileToVectorStoreAsync(
                tenantId, roomId, stored.FileId, stored.BlobName, file.FileName);

            return RedirectToPage(new { roomId, folderPath });
        }

        // ----- Large Upload Init / Finalize -----

        public async Task<IActionResult> OnPostInitLargeAsync([FromForm] Guid roomId,
                                                              [FromForm] string fileName,
                                                              [FromForm] string? folderPath)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(fileName)) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();
            folderPath = NormalizeFolderPath(folderPath);

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.Upload, folderPath))
                return Forbid();

            var (blobName, sas) = await _catalog.GetWriteSasAsync(tenantId, roomId, fileName, TimeSpan.FromMinutes(15), folderPath);
            return new JsonResult(new { blobName, sas });
        }

        public async Task<IActionResult> OnPostFinalizeLargeAsync(
            [FromForm] Guid roomId,
            [FromForm] string blobName,
            [FromForm] string fileName,
            [FromForm] long size,
            [FromForm] string? contentType,
            [FromForm] string? folderPath)
        {
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(blobName) || string.IsNullOrWhiteSpace(fileName))
                return BadRequest();

            folderPath = NormalizeFolderPath(folderPath);

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.Upload, folderPath))
                return Forbid();

            // Validate prefix: tenantId/roomId/(folderPath)file
            var expectedPrefix = $"{tenantId}/{roomId}/";
            if (!blobName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                return BadRequest("Invalid blob path.");
            if (!string.IsNullOrEmpty(folderPath) && !blobName.StartsWith(expectedPrefix + folderPath, StringComparison.OrdinalIgnoreCase))
                return BadRequest("Folder path mismatch.");

            var fileId = ExtractFileId(blobName);
            if (fileId == Guid.Empty) return BadRequest("Invalid blob name format.");

            // Set tags (uploaded by SAS)
            var service = new BlobServiceClient(_blobOptions.ConnectionString);
            var container = service.GetBlobContainerClient(_blobOptions.Container);
            var blob = container.GetBlobClient(blobName);

            var userId = GetUserId();
            var tags = new Dictionary<string, string>
            {
                {"tenantId", tenantId.ToString() },
                {"roomId", roomId.ToString() },
                {"fileId", fileId.ToString() },
                {"uploadedBy", TruncateForTag(userId) },
                {"uploadedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
                {"size", size.ToString() },
                {"folderPath", folderPath ?? "" },
                {"fileNameEnc", EncodeFileNameForTag(fileName) }
            };

            try
            {
                await blob.SetTagsAsync(tags);
            }
            catch
            {
                return StatusCode(500, "Failed to set tags. Retry finalize.");
            }

            await _auditLogger.LogAsync(new AuditEntry(
                tenantId, roomId, fileId, "Upload", userId,
                (await _userManager.FindByIdAsync(userId))?.Email,
                fileName, size, null,
                System.IO.Path.GetExtension(fileName)?.ToLowerInvariant(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString().Truncate(256),
                _auditLogger.NewCorrelationId(), null));

            // Write SQL file reference
            _db.RoomFileRefs.Add(new RoomFileRef
            {
                TenantId = tenantId,
                RoomId = roomId,
                FileId = fileId,
                BlobName = blobName,
                OriginalFileName = fileName,
                Size = size,
                ContentType = contentType,
                FolderPath = string.IsNullOrEmpty(folderPath) ? null : folderPath,
                AddedUtc = DateTimeOffset.UtcNow,
                AddedByUserId = userId
            });
            await _db.SaveChangesAsync();

            // Send file to the AI vector store for indexing
            await _agentService.UploadFileToVectorStoreAsync(
                tenantId, roomId, fileId, blobName, fileName);

            return new JsonResult(new { fileId });
        }

        // ----- Delete File -----

        public async Task<IActionResult> OnPostDeleteFileAsync(Guid roomId, Guid fileId, string? folderPath)
        {
            if (roomId == Guid.Empty || fileId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            folderPath = NormalizeFolderPath(folderPath);

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.DeleteFiles, folderPath))
                return Forbid();

            // Fetch file metadata before deletion for audit
            var metadata = await _catalog.GetFileAsync(tenantId, roomId, fileId, folderPath);
            await _catalog.DeleteFileAsync(tenantId, roomId, fileId, folderPath);

            var deleteUserId = GetUserId();
            await _auditLogger.LogAsync(new AuditEntry(
                tenantId, roomId, fileId, "Delete", deleteUserId,
                (await _userManager.FindByIdAsync(deleteUserId))?.Email,
                metadata?.OriginalFileName, metadata?.Size, null,
                metadata != null ? System.IO.Path.GetExtension(metadata.OriginalFileName)?.ToLowerInvariant() : null,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString().Truncate(256),
                _auditLogger.NewCorrelationId(), null));

            return RedirectToPage(new { roomId, folderPath });
        }

        // ----- Invitation Handlers -----

        public async Task<IActionResult> OnPostBulkDeleteAsync(Guid roomId, string? folderPath, [FromForm] List<Guid> fileIds)
        {
            if (roomId == Guid.Empty || fileIds == null || fileIds.Count == 0) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            folderPath = NormalizeFolderPath(folderPath);
            var userId = GetUserId();

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, userId, RoomPermission.DeleteFiles, folderPath))
                return Forbid();

            var userEmail = (await _userManager.FindByIdAsync(userId))?.Email;

            foreach (var fileId in fileIds)
            {
                var metadata = await _catalog.GetFileAsync(tenantId, roomId, fileId, folderPath);
                await _catalog.DeleteFileAsync(tenantId, roomId, fileId, folderPath);

                await _auditLogger.LogAsync(new AuditEntry(
                    tenantId, roomId, fileId, "Delete", userId, userEmail,
                    metadata?.OriginalFileName, metadata?.Size, null,
                    metadata != null ? System.IO.Path.GetExtension(metadata.OriginalFileName)?.ToLowerInvariant() : null,
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString().Truncate(256),
                    _auditLogger.NewCorrelationId(), null));
            }

            return RedirectToPage(new { roomId, folderPath });
        }

        public async Task<IActionResult> OnPostInviteUserAsync(Guid roomId, string email, RoomRole role, string? message)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(email)) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.ManagePermissions))
                return Forbid();

            var invitation = await _invitations.CreateInvitationAsync(tenantId, roomId, email.Trim(), role, GetUserId(), message?.Trim());

            // Build the invite link
            var link = Url.Page("/Files/AcceptInvite", null, new { token = invitation.Token }, Request.Scheme);
            InviteLinkGenerated = link;

            // Reload page data so the UI can display the link
            // Re-run OnGetAsync logic by redirecting with a flash
            TempData["InviteLink"] = link;
            TempData["InviteEmail"] = email.Trim();
            return RedirectToPage(new { roomId });
        }

        public async Task<IActionResult> OnPostRevokeInviteAsync(Guid roomId, int invitationId)
        {
            if (roomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.ManagePermissions))
                return Forbid();

            await _invitations.RevokeInvitationAsync(tenantId, roomId, invitationId);
            return RedirectToPage(new { roomId });
        }

        // ----- Member Management Handlers -----

        public async Task<IActionResult> OnPostChangeMemberRoleAsync(Guid roomId, string memberId, RoomRole role)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(memberId)) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var currentUserId = GetUserId();
            if (!await _permissions.HasPermissionAsync(tenantId, roomId, currentUserId, RoomPermission.ManagePermissions))
                return Forbid();

            // Prevent changing own role
            if (memberId == currentUserId) return BadRequest("Cannot change your own role.");

            await _permissions.UpdateAccessAsync(tenantId, roomId, memberId, role);
            return RedirectToPage(new { roomId });
        }

        public async Task<IActionResult> OnPostRemoveMemberAsync(Guid roomId, string memberId)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(memberId)) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var currentUserId = GetUserId();
            if (!await _permissions.HasPermissionAsync(tenantId, roomId, currentUserId, RoomPermission.ManagePermissions))
                return Forbid();

            // Prevent removing yourself
            if (memberId == currentUserId) return BadRequest("Cannot remove yourself.");

            await _permissions.RevokeAccessAsync(tenantId, roomId, memberId);
            return RedirectToPage(new { roomId });
        }

        // ----- AI Status Polling (AJAX) -----

        public async Task<IActionResult> OnGetAiStatusAsync(Guid roomId, string fileIds)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(fileIds))
                return new JsonResult(new { });

            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            if (!await _permissions.HasPermissionAsync(tenantId, roomId, GetUserId(), RoomPermission.ViewDocuments))
                return Forbid();

            var ids = fileIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();

            var statuses = await _agentService.GetIndexingStatusesAsync(tenantId, roomId, ids);

            var result = statuses.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value.ToString());

            return new JsonResult(result);
        }

        // ----- NDA Acceptance -----

        public async Task<IActionResult> OnPostCloneRoomAsync(Guid roomId)
        {
            if (roomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = GetUserId();
            if (!await _permissions.HasPermissionAsync(tenantId, roomId, userId, RoomPermission.ManageRoom))
                return Forbid();

            var sourceRoom = await _catalog.GetRoomAsync(tenantId, roomId);
            if (sourceRoom == null) return NotFound();

            var newRoom = await _catalog.CloneRoomAsync(
                tenantId, roomId,
                $"{sourceRoom.Name} (Copy)",
                sourceRoom.NdaText,
                userId);

            await _permissions.GrantAccessAsync(tenantId, newRoom.RoomId, userId, RoomRole.Owner, userId);

            return RedirectToPage(new { roomId = newRoom.RoomId });
        }

        public async Task<IActionResult> OnPostAcceptNdaAsync(Guid roomId)
        {
            if (roomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = GetUserId();
            if (!await _permissions.HasPermissionAsync(tenantId, roomId, userId, RoomPermission.AccessRoom))
                return Forbid();

            var existing = await _db.NdaAcceptances
                .AnyAsync(n => n.TenantId == tenantId && n.RoomId == roomId && n.UserId == userId);
            if (!existing)
            {
                _db.NdaAcceptances.Add(new NdaAcceptance
                {
                    TenantId = tenantId,
                    RoomId = roomId,
                    UserId = userId,
                    AcceptedUtc = DateTimeOffset.UtcNow,
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                });
                await _db.SaveChangesAsync();
            }

            return RedirectToPage(new { roomId });
        }

        // ----- Helpers -----

        private static Guid ExtractFileId(string blobName)
        {
            var last = blobName.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrEmpty(last)) return Guid.Empty;
            var idx = last.IndexOf('_');
            if (idx <= 0) return Guid.Empty;
            return Guid.TryParse(last[..idx], out var g) ? g : Guid.Empty;
        }

        private static string NormalizeFolderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                               .Select(SanitizeSegment)
                               .Where(s => !string.IsNullOrWhiteSpace(s))
                               .ToArray();
            if (segments.Length == 0) return string.Empty;
            return string.Join('/', segments) + "/";
        }

        private static string GetParentFolderPath(string path)
        {
            path = NormalizeFolderPath(path);
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count <= 1) return string.Empty;
            parts.RemoveAt(parts.Count - 1);
            return string.Join('/', parts) + "/";
        }

        private static string SanitizeSegment(string seg)
        {
            seg = seg.Trim();
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                seg = seg.Replace(c, '_');
            seg = seg.Replace("..", "_");
            if (seg.Length > 64) seg = seg[..64];
            return seg;
        }

        private const int MaxTagValueLength = 256;

        private static string EncodeFileNameForTag(string original)
        {
            if (string.IsNullOrEmpty(original)) return "";
            var trimmed = original.Trim();
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(trimmed));
            if (encoded.Length <= MaxTagValueLength) return encoded;

            var ext = System.IO.Path.GetExtension(trimmed);
            var baseName = System.IO.Path.GetFileNameWithoutExtension(trimmed);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant();
            var shortBase = baseName.Length > 40 ? baseName[..40] : baseName;
            var composite = $"{shortBase}~{hash[..16]}{ext}";
            var finalEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(composite));
            return finalEncoded.Length <= MaxTagValueLength ? finalEncoded : finalEncoded[..MaxTagValueLength];
        }

        private static string SanitizeForTag(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c) || c is ' ' or '+' or '-' or '.' or ':' or '=' or '_' or '/')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }

        private static string TruncateForTag(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sanitized = SanitizeForTag(value.Trim());
            return sanitized.Length <= MaxTagValueLength ? sanitized : sanitized[..MaxTagValueLength];
        }
    }
}