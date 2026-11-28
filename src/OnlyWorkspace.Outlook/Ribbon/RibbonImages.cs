using System.Drawing;
using System.Windows.Forms;

namespace OnlyWorkspace.Outlook.Ribbon;

/// <summary>
/// The app icon for the ribbon (loadImage callback): "ow16" for normal controls and group images, "ow32" for large
/// buttons. A COM add-in must hand Office an IPictureDisp, which only AxHost converts from a managed image.
/// </summary>
internal sealed class RibbonImages : AxHost
{
    private RibbonImages() : base(string.Empty) { }

    public static object? Load(string imageId)
    {
        var name = imageId == "ow32" ? "OnlyWorkspace.Outlook.UI.ribbon-icon-32.png" : "OnlyWorkspace.Outlook.UI.ribbon-icon-16.png";
        using var stream = typeof(RibbonImages).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        // IPictureDisp keeps its own copy: the bitmap can be released at once. Alpha is kept for PNG sources.
        using var image = Image.FromStream(stream);
        using var bitmap = new Bitmap(image);
        return GetIPictureDispFromPicture(bitmap);
    }
}
