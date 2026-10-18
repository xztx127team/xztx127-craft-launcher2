namespace XCL2.App.Models;

/// <summary>动态 JVM 堆内存的两种计算方式；百分比的分母始终是总物理内存。</summary>
public enum MemoryAllocationMode
{
    FixedRange,
    TotalPercentage
}

/// <summary>只改变内存信息的显示，不改变动态分配的计算依据。</summary>
public enum MemoryDisplayBasis
{
    Total,
    Available
}
