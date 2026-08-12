using InactivePDF.Infrastructure.Processes;

if (!ConversionWorkerRunner.IsWorker(args))
{
    Console.Error.WriteLine($"This executable is only for {ConversionWorkerRunner.Switch} jobs.");
    Environment.ExitCode = 2;
    return;
}

Environment.ExitCode = await ConversionWorkerRunner.RunAsync(args);
