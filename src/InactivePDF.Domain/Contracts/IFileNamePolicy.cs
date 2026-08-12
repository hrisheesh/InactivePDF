namespace InactivePDF.Domain.Contracts;

public interface IFileNamePolicy
{
    string ValidateAndNormalize(string fileName);
}
