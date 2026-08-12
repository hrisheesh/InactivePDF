namespace InactivePDF.Domain.Contracts;

public interface IDiskSpaceGuard
{
    void EnsureAvailable(string path, long minimumFreeBytes);
}
