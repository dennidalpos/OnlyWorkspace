namespace OnlyWorkspace.Core.Archive;

public enum ArchiveCapacityState { Available, Warning, Full }

public static class ArchiveCapacity
{
    public const long WarningBytes = 5L * 1024 * 1024 * 1024;
    public const long ReserveBytes = 1L * 1024 * 1024 * 1024;

    public static ArchiveCapacityState Evaluate(long freeBytes)
    {
        return freeBytes < ReserveBytes ? ArchiveCapacityState.Full :
            freeBytes < WarningBytes ? ArchiveCapacityState.Warning : ArchiveCapacityState.Available;
    }

    public static string Message(ArchiveCapacityState state) => state == ArchiveCapacityState.Full
        ? "Spazio libero sul volume dell'archivio inferiore a 1 GiB: acquisizione sospesa fino al recupero dello spazio."
        : "Spazio libero sul volume dell'archivio inferiore a 5 GiB.";
}
