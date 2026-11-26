namespace FileManager;

internal static class Drives
{
    public static IReadOnlyList<string> DescribeLocalVolumes()
    {
        var volumes = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) ||
                    !drive.IsReady || drive.TotalSize <= 0)
                    continue;
                volumes.Add($"{drive.Name} — {drive.DriveType}, {drive.DriveFormat}: " +
                    $"всего {drive.TotalSize} байт, доступно {drive.AvailableFreeSpace} байт.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Один недоступный том не мешает показать остальные.
            }
        }
        return volumes;
    }
}
