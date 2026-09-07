# Nạp nguồn vector của logo từ một file XAML và trả về ImageSource.
#
# Chấp nhận hai dạng gốc, để cùng bộ script dùng được cho cả bản nháp lẫn bản thật:
#   - <DrawingImage>       : các mẫu đang cân nhắc trong chính thư mục này
#   - <ResourceDictionary> : Themes/Logo.xaml của app, lấy theo khoá Img.Logo
#
# XamlReader đòi luồng STA; powershell.exe mặc định đã STA. Chạy bằng pwsh (mặc định MTA)
# thì gọi lại qua: powershell.exe -STA -File <script> ...

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

function Import-LogoImage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $XamlPath,
        [string] $ResourceKey = 'Img.Logo'
    )

    $full = (Resolve-Path -LiteralPath $XamlPath).Path
    $parsed = [System.Windows.Markup.XamlReader]::Parse((Get-Content -LiteralPath $full -Raw -Encoding UTF8))

    if ($parsed -is [System.Windows.ResourceDictionary]) {
        $image = $parsed[$ResourceKey]
        if ($null -eq $image) { throw "$full khong co tai nguyen '$ResourceKey'." }
        return $image
    }

    if ($parsed -is [System.Windows.Media.ImageSource]) { return $parsed }

    throw "$full phai co goc la DrawingImage hoac ResourceDictionary, dang la $($parsed.GetType().Name)."
}

function ConvertTo-LogoBitmap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.Windows.Media.ImageSource] $Image,
        [Parameter(Mandatory)] [int] $Pixels
    )

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try { $context.DrawImage($Image, [System.Windows.Rect]::new(0, 0, $Pixels, $Pixels)) }
    finally { $context.Close() }

    # 96 dpi cố định: ảnh phải ra đúng số pixel đã yêu cầu, không phụ thuộc DPI máy chạy script.
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Pixels, $Pixels, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    return $bitmap
}
