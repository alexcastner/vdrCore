using System;

namespace twoSaaSCore.Models
{
    [Flags]
    public enum RoomPermission
    {
        None              = 0,
        AccessRoom        = 1 << 0,
        ViewDocuments     = 1 << 1,
        Download          = 1 << 2,
        Print             = 1 << 3,
        Upload            = 1 << 4,
        DeleteFiles       = 1 << 5,
        ManageFolders     = 1 << 6,
        ManagePermissions = 1 << 7,
        ManageRoom        = 1 << 8,
        ViewAuditLog      = 1 << 9,
    }
}
