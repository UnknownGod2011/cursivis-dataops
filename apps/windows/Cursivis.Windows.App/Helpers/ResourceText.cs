using Microsoft.Windows.ApplicationModel.Resources;

namespace Cursivis.Windows.App.Helpers;

internal static class ResourceText
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key)
    {
        string value = Loader.GetString(key);
        return string.IsNullOrWhiteSpace(value) ? key : value;
    }
}
