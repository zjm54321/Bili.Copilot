// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Toolkits;

/// <summary>
/// Chooses a conservative single-instance key from the current package name.
/// This is not an unpackaged startup or storage compatibility layer.
/// </summary>
internal static class AppChannelIdentity
{
    private const string TestPackageName = "BiliCopilot.LocalTest";
    private const string ExistingInstanceKey = "Richasy.BiliCopilot";

    public static string InstanceKey { get; } = GetInstanceKey();

    private static string GetInstanceKey()
    {
        try
        {
            return string.Equals(
                Windows.ApplicationModel.Package.Current.Id.Name,
                TestPackageName,
                System.StringComparison.Ordinal)
                ? TestPackageName
                : ExistingInstanceKey;
        }
        catch (System.InvalidOperationException)
        {
            return ExistingInstanceKey;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return ExistingInstanceKey;
        }
    }
}
