using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Tests.TestSupport;

/// <summary>Builds <see cref="MftRecord"/> instances directly (no byte-level parsing) for Recovery-layer unit tests.</summary>
public static class TestMftRecordFactory
{
    public static MftRecord Create(
        uint recordNumber,
        ushort sequenceNumber,
        string? name,
        ulong parentRecordNumber,
        ushort parentSequenceNumber,
        bool isDirectory = false,
        bool isInUse = true,
        long sourceOffset = 0,
        FileReference? baseFileRecord = null)
    {
        var attributes = new List<NtfsAttribute>();

        if (name is not null)
        {
            attributes.Add(new FileNameAttribute
            {
                Type = NtfsAttributeType.FileName,
                RawTypeCode = 0x30,
                IsNonResident = false,
                Name = null,
                AttributeId = 0,
                AttributeLength = 0,
                ParentDirectory = new FileReference(parentRecordNumber, parentSequenceNumber),
                CreationTime = null,
                ModificationTime = null,
                MftModificationTime = null,
                AccessTime = null,
                AllocatedSize = 0,
                RealSize = 0,
                FileAttributeFlags = 0,
                NameNamespace = NtfsNameNamespace.Win32,
                FileName = name,
            });
        }

        return new MftRecord
        {
            SourceOffset = sourceOffset,
            RecordNumber = recordNumber,
            SequenceNumber = sequenceNumber,
            IsInUse = isInUse,
            IsDirectory = isDirectory,
            BaseFileRecord = baseFileRecord ?? new FileReference(0, 0),
            BytesInUse = 100,
            BytesAllocated = 1024,
            Attributes = attributes,
        };
    }
}
