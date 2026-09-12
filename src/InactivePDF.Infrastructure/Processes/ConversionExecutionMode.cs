namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// Selects whether a conversion includes the expensive diagnostics used during
/// fidelity investigations or the lean product path.
/// </summary>
public enum ConversionExecutionMode
{
    Development,
    Production
}

public static class ConversionExecutionModeParser
{
    public static ConversionExecutionMode Parse(string? value)
    {
        if (Enum.TryParse<ConversionExecutionMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode))
            return mode;

        throw new InvalidDataException("Execution mode must be either 'Development' or 'Production'.");
    }

    public static ConversionExecutionMode FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable("INACTIVEPDF_EXECUTION_MODE") ?? "Production");
}
