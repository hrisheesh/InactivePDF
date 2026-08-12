using System.Text;
using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.Rendering;

/// <summary>
/// Converts the deliberately simple, text-only RTF subset without starting LibreOffice.
/// Documents that contain visible formatting, tables, pictures, fields, or unsupported
/// controls return false and remain on the fidelity-first LibreOffice route.
/// </summary>
public sealed class RtfFastPathConverter(ITextPdfGenerator textGenerator)
{
    private const long MaximumFastPathBytes = 64L * 1024 * 1024;

    public bool TryConvert(string inputPath, string outputPath, PdfOutputProfile profile, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(inputPath);
        if (!fileInfo.Exists || fileInfo.Length > MaximumFastPathBytes) return false;

        var bytes = File.ReadAllBytes(inputPath);
        if (!TryParse(bytes, cancellationToken, out var text)) return false;

        textGenerator.Create(text, outputPath, new TextPdfOptions(OutputProfile: profile));
        return true;
    }

    private static bool TryParse(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken, out string text)
    {
        text = string.Empty;
        if (bytes.Length < 6 || bytes[0] != (byte)'{' || bytes[1] != (byte)'\\' || !bytes[2..].StartsWith("rtf"u8)) return false;

        var output = new StringBuilder(Math.Min(bytes.Length, 8 * 1024 * 1024));
        var states = new Stack<RtfState>();
        var state = new RtfState();
        var index = 0;
        var checks = 0;

        while (index < bytes.Length)
        {
            if ((++checks & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var current = bytes[index];
            if (current == (byte)'{')
            {
                states.Push(state);
                state = state with { IgnorableDestinationPending = false };
                index++;
                continue;
            }

            if (current == (byte)'}')
            {
                if (states.Count == 0) return false;
                state = states.Pop();
                index++;
                continue;
            }

            if (current != (byte)'\\')
            {
                if (state.UnicodeFallbackBytes > 0)
                {
                    state = state with { UnicodeFallbackBytes = state.UnicodeFallbackBytes - 1 };
                }
                else if (!state.SkipDestination && current is not (byte)'\r' and not (byte)'\n')
                {
                    output.Append(DecodeAnsiByte(current));
                }

                index++;
                continue;
            }

            if (++index >= bytes.Length) return false;
            var symbol = bytes[index];
            if (symbol is (byte)'\\' or (byte)'{' or (byte)'}')
            {
                if (!state.SkipDestination) output.Append((char)symbol);
                index++;
                continue;
            }

            if (symbol == (byte)'\'')
            {
                if (index + 2 >= bytes.Length || !TryHex(bytes[index + 1], out var high) || !TryHex(bytes[index + 2], out var low)) return false;
                if (!state.SkipDestination && state.UnicodeFallbackBytes == 0)
                    output.Append(DecodeAnsiByte((byte)((high << 4) | low)));
                else if (state.UnicodeFallbackBytes > 0)
                    state = state with { UnicodeFallbackBytes = state.UnicodeFallbackBytes - 1 };
                index += 3;
                continue;
            }

            if (symbol is (byte)'~' or (byte)'-' or (byte)'_')
            {
                if (!state.SkipDestination) output.Append(symbol == (byte)'_' ? '\u2011' : symbol == (byte)'~' ? '\u00A0' : '\u00AD');
                index++;
                continue;
            }

            if (symbol == (byte)'*')
            {
                state = state with { IgnorableDestinationPending = true };
                index++;
                continue;
            }

            if (!IsAsciiLetter(symbol)) return false;
            var wordStart = index;
            while (index < bytes.Length && IsAsciiLetter(bytes[index])) index++;
            var word = Encoding.ASCII.GetString(bytes[wordStart..index]);
            var sign = 1;
            if (index < bytes.Length && bytes[index] == (byte)'-') { sign = -1; index++; }
            var hasParameter = false;
            var parameter = 0;
            while (index < bytes.Length && bytes[index] is >= (byte)'0' and <= (byte)'9')
            {
                hasParameter = true;
                parameter = checked(parameter * 10 + bytes[index] - (byte)'0');
                index++;
            }
            parameter *= sign;
            if (index < bytes.Length && bytes[index] == (byte)' ') index++;

            if (state.IgnorableDestinationPending)
            {
                state = state with { SkipDestination = true, IgnorableDestinationPending = false };
                continue;
            }

            if (IsDestination(word))
            {
                state = state with { SkipDestination = true };
                continue;
            }

            if (state.SkipDestination) continue;
            if (!ApplyControl(word, hasParameter, parameter, ref state, output)) return false;
        }

        if (states.Count != 0 || output.Length == 0) return false;
        text = output.ToString();
        return true;
    }

    private static bool ApplyControl(string word, bool hasParameter, int parameter, ref RtfState state, StringBuilder output)
    {
        switch (word)
        {
            case "rtf" or "ansi" or "deff" or "ansicpg" or "uc" or "lang" or "viewkind" or "viewscale" or "paperw" or "paperh" or "margl" or "margr" or "margt" or "margb" or "widowctrl" or "nowidctlpar" or "f":
                if (word is "ansicpg" && hasParameter && parameter != 1252) return false;
                if (word == "deff" && hasParameter && parameter != 0) return false;
                if (word == "f" && hasParameter && parameter != 0) return false;
                if (word is "uc" && hasParameter && parameter is < 0 or > 8) return false;
                if (word == "uc" && hasParameter) state = state with { UnicodeFallbackCount = parameter };
                return true;
            case "fs":
                return !hasParameter || parameter == 24;
            case "par" or "line":
                output.Append('\n');
                return true;
            case "tab":
                output.Append('\t');
                return true;
            case "u":
                if (!hasParameter) return false;
                output.Append((char)(parameter < 0 ? parameter + 65536 : parameter));
                state = state with { UnicodeFallbackBytes = state.UnicodeFallbackCount };
                return true;
            case "plain" or "pard" or "ql":
                return true;
            default:
                return false;
        }
    }

    private static bool IsDestination(string word) => word is
        "fonttbl" or "colortbl" or "stylesheet" or "info" or "generator" or "pict" or
        "object" or "header" or "headerl" or "headerr" or "headerf" or "footer" or
        "footerl" or "footerr" or "footerf" or "footnote" or "annotation" or "comment" or
        "field" or "fldinst" or "fldrslt" or "filetbl" or "listtable" or "listoverridetable" or
        "themedata" or "xmlnstbl" or "datastore";

    private static bool IsAsciiLetter(byte value) => value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static bool TryHex(byte value, out int result)
    {
        result = value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
            >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
            >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
            _ => -1
        };
        return result >= 0;
    }

    private static char DecodeAnsiByte(byte value) => value switch
    {
        0x80 => '\u20AC', 0x82 => '\u201A', 0x83 => '\u0192', 0x84 => '\u201E', 0x85 => '\u2026',
        0x86 => '\u2020', 0x87 => '\u2021', 0x88 => '\u02C6', 0x89 => '\u2030', 0x8A => '\u0160',
        0x8B => '\u2039', 0x8C => '\u0152', 0x8E => '\u017D', 0x91 => '\u2018', 0x92 => '\u2019',
        0x93 => '\u201C', 0x94 => '\u201D', 0x95 => '\u2022', 0x96 => '\u2013', 0x97 => '\u2014',
        0x98 => '\u02DC', 0x99 => '\u2122', 0x9A => '\u0161', 0x9B => '\u203A', 0x9C => '\u0153',
        0x9E => '\u017E', 0x9F => '\u0178',
        _ => (char)value
    };

    private readonly record struct RtfState(
        bool SkipDestination = false,
        bool IgnorableDestinationPending = false,
        int UnicodeFallbackCount = 1,
        int UnicodeFallbackBytes = 0);
}
