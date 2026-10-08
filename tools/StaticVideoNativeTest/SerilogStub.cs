namespace Serilog;

internal static class Log
{
    internal static void Debug(Exception exception, string messageTemplate, params object?[] propertyValues) { }
    internal static void Error(Exception exception, string messageTemplate, params object?[] propertyValues) { }
}
