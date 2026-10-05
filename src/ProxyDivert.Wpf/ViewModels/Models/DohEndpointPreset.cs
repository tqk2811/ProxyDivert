namespace ProxyDivert.Wpf.ViewModels.Models;

/// <summary>
/// One suggestion in the DoH endpoint box: who runs it, and the URL that goes into the box.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> returns the URL on purpose: an editable ComboBox writes the picked item's
/// text into the box, and that text has to be the endpoint itself, not the provider's name.
/// </remarks>
public sealed record DohEndpointPreset(string Provider, string Url)
{
    public override string ToString() => Url;
}
