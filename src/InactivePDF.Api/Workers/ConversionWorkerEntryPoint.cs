using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.Api.Workers;

internal static class ConversionWorkerEntryPoint
{
    public const string Switch = ConversionWorkerRunner.Switch;

    public static bool IsWorker(IReadOnlyList<string> args) => args.Contains(Switch, StringComparer.Ordinal);

    public static Task<int> RunAsync(IReadOnlyList<string> args) => ConversionWorkerRunner.RunAsync(args);
}
