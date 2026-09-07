<#
.SYNOPSIS
    Dựng src/ProxyDivert.Wpf/Assets/app.ico từ nguồn vector Themes/Logo.xaml.

.DESCRIPTION
    Chạy lại script này mỗi khi sửa logo; file .ico được commit nên bản build không phụ thuộc
    PowerShell. Nguồn duy nhất là Themes/Logo.xaml, cũng chính là file app nạp để vẽ logo trên
    thanh tiêu đề — nên .ico, icon cửa sổ và icon khay không bao giờ lệch nhau.

    Cỡ nhỏ ghi dạng DIB 32bpp, cỡ lớn ghi dạng PNG. Đây là bố cục ICO mà mọi thứ trên Windows
    đều đọc được: PNG cho cỡ nhỏ tuy hợp lệ từ Vista nhưng vẫn có đường đọc icon cũ (một số API
    GDI+/user32 mà thư viện khay dùng) trả về ảnh trắng.

.EXAMPLE
    powershell.exe -STA -File .\Generate-AppIcon.ps1
#>
[CmdletBinding()]
param(
    [string] $XamlPath,
    [string] $OutPath,
    # 24 và 48 có mặt vì Explorer dùng chúng ở chế độ Medium icons và trên thanh tác vụ 150% DPI;
    # thiếu thì Windows tự thu nhỏ cỡ khác và ảnh bị nhoè.
    [int[]] $Size = @(16, 20, 24, 32, 40, 48, 64, 128, 256),
    # Từ cỡ này trở lên thì nhúng PNG cho gọn; dưới nó thì DIB.
    [int] $PngFrom = 64
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'LogoLoader.ps1')

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $XamlPath) { $XamlPath = Join-Path $repositoryRoot 'src\ProxyDivert.Wpf\Themes\Logo.xaml' }
if (-not $OutPath) { $OutPath = Join-Path $repositoryRoot 'src\ProxyDivert.Wpf\Assets\app.ico' }

function Get-StraightAlphaPixels {
    param([System.Windows.Media.Imaging.BitmapSource] $Bitmap)

    # RenderTargetBitmap chỉ dựng được Pbgra32 (alpha đã nhân trước). ICO cần alpha thẳng, nên
    # phải đổi định dạng — bỏ bước này thì mọi pixel bán trong suốt bị tối đi.
    $converted = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
        $Bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)

    $stride = $converted.PixelWidth * 4
    $pixels = New-Object byte[] ($stride * $converted.PixelHeight)
    $converted.CopyPixels($pixels, $stride, 0)
    return $pixels
}

function New-IconDib {
    param([System.Windows.Media.Imaging.BitmapSource] $Bitmap)

    $width = $Bitmap.PixelWidth
    $height = $Bitmap.PixelHeight
    $pixels = Get-StraightAlphaPixels -Bitmap $Bitmap

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        # BITMAPINFOHEADER. biHeight gấp đôi vì theo sau ảnh màu còn một mặt nạ AND, dù ở 32bpp
        # mặt nạ đó chỉ toàn 0 — bỏ nó đi là ICO hỏng.
        $writer.Write([int] 40)
        $writer.Write([int] $width)
        $writer.Write([int] ($height * 2))
        $writer.Write([int16] 1)
        $writer.Write([int16] 32)
        $writer.Write([int] 0)
        $writer.Write([int] 0)
        $writer.Write([int] 0); $writer.Write([int] 0)
        $writer.Write([int] 0); $writer.Write([int] 0)

        # Ảnh trong DIB xếp từ dưới lên.
        $stride = $width * 4
        for ($y = $height - 1; $y -ge 0; $y--) {
            $writer.Write($pixels, $y * $stride, $stride)
        }

        $maskStride = [int][math]::Floor((($width + 31) / 32)) * 4
        $writer.Write((New-Object byte[] ($maskStride * $height)), 0, $maskStride * $height)

        $writer.Flush()
        return $stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function New-IconPng {
    param([System.Windows.Media.Imaging.BitmapSource] $Bitmap)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))

    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return $stream.ToArray() }
    finally { $stream.Dispose() }
}

$image = Import-LogoImage -XamlPath $XamlPath

$frames = foreach ($pixels in ($Size | Sort-Object)) {
    $bitmap = ConvertTo-LogoBitmap -Image $image -Pixels $pixels
    [pscustomobject]@{
        Pixels = $pixels
        Data   = if ($pixels -ge $PngFrom) { New-IconPng -Bitmap $bitmap } else { New-IconDib -Bitmap $bitmap }
        IsPng  = $pixels -ge $PngFrom
    }
}

$outDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($OutPath))
if (-not (Test-Path -LiteralPath $outDirectory)) {
    New-Item -ItemType Directory -Path $outDirectory -Force | Out-Null
}

$file = [IO.File]::Create([IO.Path]::GetFullPath($OutPath))
$writer = [IO.BinaryWriter]::new($file)
try {
    # ICONDIR
    $writer.Write([int16] 0)
    $writer.Write([int16] 1)
    $writer.Write([int16] $frames.Count)

    # Ảnh nằm ngay sau bảng mục lục, nên chỗ bắt đầu của ảnh đầu tiên tính được từ số mục.
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        # 256 ghi là 0: ô kích thước chỉ có một byte.
        $writer.Write([byte] ($frame.Pixels % 256))
        $writer.Write([byte] ($frame.Pixels % 256))
        $writer.Write([byte] 0)
        $writer.Write([byte] 0)
        $writer.Write([int16] 1)
        $writer.Write([int16] 32)
        $writer.Write([int] $frame.Data.Length)
        $writer.Write([int] $offset)
        $offset += $frame.Data.Length
    }

    foreach ($frame in $frames) { $writer.Write($frame.Data, 0, $frame.Data.Length) }
}
finally { $writer.Dispose(); $file.Dispose() }

$total = (Get-Item -LiteralPath $OutPath).Length
Write-Host "$OutPath — $($frames.Count) cỡ ($($frames.Pixels -join ', ')), $total byte"
