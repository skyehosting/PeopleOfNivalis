using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace NivalisAIChat.Tests;

/// <summary>
/// Audit step 7 (7d, 7g): TEST_CULTURE=tr-TR (or de-DE, ar-SA...) runs the whole suite under that culture, the way a
/// player's Windows settings would run the mod. Nothing is pinned otherwise, so a culture bug in the mod shows up here.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Apply()
    {
        string? name = Environment.GetEnvironmentVariable("TEST_CULTURE");
        if (string.IsNullOrWhiteSpace(name)) return;
        var culture = CultureInfo.GetCultureInfo(name.Trim());
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Console.WriteLine("Culture: " + culture.Name);
    }
}
