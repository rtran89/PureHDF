﻿namespace PureHDF.VOL.Native;

internal struct BTree2Record02 : IBTree2Record
{
    #region Constructors

    public BTree2Record02(NativeContext context)
    {
        var (driver, superblock) = context;

        FilteredHugeObjectAddress = superblock.ReadOffset(driver);
        FilteredHugeObjectLength = superblock.ReadLength(driver);
        FilterMask = driver.ReadUInt32();
        FilteredHugeObjectMemorySize = superblock.ReadLength(driver);
        HugeObjectId = superblock.ReadLength(driver);
    }

    #endregion

    #region Properties

    public ulong FilteredHugeObjectAddress { get; set; }
    public ulong FilteredHugeObjectLength { get; set; }
    public uint FilterMask { get; set; }
    public ulong FilteredHugeObjectMemorySize { get; set; }
    public ulong HugeObjectId { get; set; }

    #endregion
}