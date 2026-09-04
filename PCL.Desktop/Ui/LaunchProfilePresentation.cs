using PCL.Core.Media;
using PCL.Services.Accounts;
using PCL.UI.Next;

namespace PCL.Desktop.Ui;

/// <summary>Pure, credential-free projection of the original profile card's display contract.</summary>
internal static class LaunchProfilePresentation
{
    public static XsrUiRasterImage Head(PngImage image)
    {
        double scale = image.Width / 64d;
        return new(image, [
            new(new(8 * scale, 8 * scale, 8 * scale, 8 * scale), new(.125, .125, .75, .75)),
            new(new(40 * scale, 8 * scale, 8 * scale, 8 * scale), new(.0625, .0625, .875, .875)),
        ]);
    }
    public static string Avatar(string uuid)
    {
        if (!Guid.TryParse(uuid, out Guid id)) return "pcl/avatar/steve";
        string hex = id.ToString("N");
        int parity = 0;
        foreach (int index in new[] { 7, 15, 23, 31 })
            parity ^= Convert.ToInt32(hex[index].ToString(), 16);
        return (parity & 1) == 0 ? "pcl/avatar/steve" : "pcl/avatar/alex";
    }

    public static string Description(LaunchProfileView profile)
    {
        if (profile.Kind == LaunchProfileKind.ThirdParty)
        {
            string server = profile.AuthServer.Trim();
            if (!server.Contains("://", StringComparison.Ordinal)) server = "https://" + server;
            if (Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
                return "第三方 · " + uri.Host;
        }
        if (!string.IsNullOrWhiteSpace(profile.Info)) return profile.Info;
        return profile.Kind switch
        {
            LaunchProfileKind.Microsoft => "Microsoft 账户",
            LaunchProfileKind.LittleSkin => "LittleSkin 账户",
            LaunchProfileKind.ThirdParty => "第三方账户",
            LaunchProfileKind.NCloud => "NCloud 账户",
            _ => "离线账户",
        };
    }
}
