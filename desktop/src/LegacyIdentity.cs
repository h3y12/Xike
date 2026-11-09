namespace Xike;

/// <summary>Compatibility identifiers shared with previously installed versions.</summary>
internal static class LegacyIdentity
{
    internal const string ConfigurationDirectory = "TimeGuard";
    internal const string MutexName = @"Local\TimeGuard.SingleInstance.v1";
    internal const string ShowEventName = @"Local\TimeGuard.ShowWindow.v1";
}
