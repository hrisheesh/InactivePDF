using InactivePDF.Domain.Models;

namespace InactivePDF.Domain.Contracts;

public interface IWatermarkService
{
    void Apply(string inputPath, string outputPath, WatermarkOptions options);
}
