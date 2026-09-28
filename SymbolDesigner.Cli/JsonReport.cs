using System.Text.Json;

namespace SymbolDesigner.Cli;

/// <summary>--json 机器可读输出（写到 stdout，人类日志走 stderr）。</summary>
internal static class JsonReport
{
    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Print(object? value)
        => Console.WriteLine(JsonSerializer.Serialize(value, s_opts));

    public static void Log(string message)
        => Console.Error.WriteLine(message);
}