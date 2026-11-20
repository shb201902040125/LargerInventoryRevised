using System;

namespace LargerInventory.Backend.Storage;

/// <summary>
/// 页修复结果。修复会用空白页覆盖无法读取的页；报告用于单机提示和联机回包。
/// </summary>
public sealed class RepairReport
{
    public int TotalPages;
    public int ScannedPages;
    public int HealthyPages;
    public int RecoverablePages;
    public int RepairedPages;
    public int SkippedInUsePages;
    public int LockFailedPages;
    public int FailedPages;
    public int RemainingFailedPages;
    public int IoErrorPages;
    public int UnsupportedVersionPages;
    public int ModErrorPages;
    public string ArchiveDirectory;
    public bool Canceled;
    public bool MetaSaveFailed;
    public int[] RepairedPageIndices = Array.Empty<int>();
    public int[] LockFailedPageIndices = Array.Empty<int>();

    internal RepairReport Copy()
    {
        var copy = (RepairReport)MemberwiseClone();
        copy.RepairedPageIndices = (int[])RepairedPageIndices.Clone();
        copy.LockFailedPageIndices = (int[])LockFailedPageIndices.Clone();
        return copy;
    }
}
