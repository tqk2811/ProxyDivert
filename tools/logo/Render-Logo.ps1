<#
.SYNOPSIS
    Render logo ra PNG để xem, ở một hoặc nhiều kích thước.

.DESCRIPTION
    Dùng để so các mẫu đang cân nhắc, và để nhìn bản thật ở cỡ nhỏ trước khi đóng thành .ico.
    Việc dựng .ico là của Generate-AppIcon.ps1.

.EXAMPLE
    .\Render-Logo.ps1 -XamlPath .\candidate-d.xaml -OutPath .\out\d.png -Size 256,48,16
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $XamlPath,
    [Parameter(Mandatory)] [string] $OutPath,
    [int[]] $Size = @(256)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'LogoLoader.ps1')

$image = Import-LogoImage -XamlPath $XamlPath

$outDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($OutPath))
if ($outDirectory -and -not (Test-Path -LiteralPath $outDirectory)) {
    New-Item -ItemType Directory -Path $outDirectory -Force | Out-Null
}

$outBase = [IO.Path]::Combine($outDirectory, [IO.Path]::GetFileNameWithoutExtension($OutPath))

foreach ($pixels in $Size) {
    # Một kích thước thì giữ nguyên tên đã yêu cầu; nhiều kích thước thì gắn hậu tố.
    $target = if ($Size.Count -eq 1) { [IO.Path]::GetFullPath($OutPath) } else { "$outBase-$pixels.png" }

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create(
        (ConvertTo-LogoBitmap -Image $image -Pixels $pixels)))

    $stream = [IO.File]::Create($target)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }

    Write-Host "$pixels px -> $target"
}
