<#
.SYNOPSIS
    Tải khối watermark của SoftEther về máy, để đường ra SoftEther nói chuyện được với máy chủ thật.

.DESCRIPTION
    Máy chủ SoftEther thật kiểm một khối chữ ký nhị phân trong request mở phiên và trả HTTP 403 nếu
    thiếu. Khối đó nằm trong mã nguồn SoftEther VPN (GPLv2), file src/Cedar/WaterMark.c, dưới dạng
    mảng byte trong C — nên repo này KHÔNG đóng gói kèm: phân phối nó là phân phối một phần tác phẩm
    GPL, còn tải về máy mình để dùng thì giấy phép không ràng buộc (GPLv2 §0).

    Script chỉ tải file .c từ repo chính thức, cắt lấy đúng mảng WaterMark[] và ghi ra file nhị phân.
    Không byte nào của SoftEther nằm trong script này.

    File kết quả mặc định nằm ngay CẠNH SCRIPT — cạnh ProxyDivert.exe khi chạy bản đã build, hoặc
    trong tools/ khi chạy từ repo (tên file đã nằm trong .gitignore nên không commit nhầm được).
    Khai nó bằng dòng "Watermark = <đường dẫn>" trong file .vpn của đường ra SoftEther.

.PARAMETER OutPath
    Nơi ghi file nhị phân. Mặc định softether-watermark.dat cạnh chính script này.

.PARAMETER Ref
    Nhánh hoặc tag của repo SoftEtherVPN để lấy nguồn. Mặc định master.

.PARAMETER SourceUri
    Lấy WaterMark.c từ một URL khác (bản đã tải sẵn, mirror nội bộ...). Bỏ qua -Ref khi có tham số này.

.PARAMETER Force
    Ghi đè file đã có.

.EXAMPLE
    .\Get-SoftEtherWatermark.ps1

.EXAMPLE
    .\Get-SoftEtherWatermark.ps1 -OutPath D:\vpn\se.dat -Force
#>
[CmdletBinding()]
param(
    [string] $OutPath,
    [string] $Ref = 'master',
    [string] $SourceUri,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Kích thước hợp lệ đã biết: khối là một ảnh GIF nhỏ (1411 byte ở bản master 09/2026). Khoảng rộng
# dưới đây chỉ để bắt trường hợp cắt trượt mảng, không phải để khoá đúng một con số.
$MinimumSize = 512
$MaximumSize = 65536

if (-not $OutPath) {
    $OutPath = Join-Path $PSScriptRoot 'softether-watermark.dat'
}
$OutPath = [IO.Path]::GetFullPath($OutPath)

if ((Test-Path -LiteralPath $OutPath) -and -not $Force) {
    throw "Đã có '$OutPath'. Thêm -Force nếu muốn ghi đè."
}

if (-not $SourceUri) {
    $SourceUri = "https://raw.githubusercontent.com/SoftEtherVPN/SoftEtherVPN/$Ref/src/Cedar/WaterMark.c"
}

# PowerShell 5.1 mặc định vẫn chào TLS 1.0, mà GitHub đã bỏ từ lâu — thiếu dòng này thì tải hỏng với
# một lỗi kết nối không nói gì về nguyên nhân.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$sourcePath = Join-Path ([IO.Path]::GetTempPath()) ("WaterMark-{0}.c" -f [Guid]::NewGuid().ToString('N'))
try {
    Write-Verbose "Tải $SourceUri"
    Invoke-WebRequest -Uri $SourceUri -OutFile $sourcePath -UseBasicParsing

    $source = [IO.File]::ReadAllText($sourcePath, [Text.UTF8Encoding]::new($false))
}
finally {
    Remove-Item -LiteralPath $sourcePath -Force -ErrorAction SilentlyContinue
}

# Neo đúng tên mảng: cùng file còn có Saitama[] ngay bên dưới (một ảnh đùa của tác giả), lấy nhầm nó
# thì không lỗi gì cả, chỉ là máy chủ vẫn trả 403 và không biết vì sao.
$match = [regex]::Match($source, 'BYTE\s+WaterMark\s*\[\s*\]\s*=\s*\{(?<body>.*?)\}\s*;', 'Singleline')
if (-not $match.Success) {
    throw "Không tìm thấy mảng 'BYTE WaterMark[]' trong nguồn. Có thể upstream đã đổi cách khai báo."
}

$literals = [regex]::Matches($match.Groups['body'].Value, '0x[0-9A-Fa-f]{2}')
if ($literals.Count -eq 0) {
    throw "Mảng WaterMark[] rỗng hoặc không phải dạng byte 0xNN."
}

$bytes = [byte[]]::new($literals.Count)
for ($i = 0; $i -lt $literals.Count; $i++) {
    $bytes[$i] = [Convert]::ToByte($literals[$i].Value.Substring(2), 16)
}

# Ba phép kiểm để một lần cắt trượt không âm thầm sinh ra file rác: cỡ hợp lý, mở đầu "GIF8", kết
# thúc bằng trailer 0x3B của GIF.
if ($bytes.Length -lt $MinimumSize -or $bytes.Length -gt $MaximumSize) {
    throw "Khối lấy được dài $($bytes.Length) byte, ngoài khoảng $MinimumSize..$MaximumSize — nhiều khả năng cắt trượt mảng."
}
$magic = [Text.Encoding]::ASCII.GetString($bytes, 0, 4)
if ($magic -ne 'GIF8') {
    throw "Khối lấy được không mở đầu bằng 'GIF8' (thấy '$magic') — nhiều khả năng lấy nhầm mảng."
}
if ($bytes[$bytes.Length - 1] -ne 0x3B) {
    throw "Khối lấy được không kết thúc bằng trailer 0x3B của GIF — nhiều khả năng bị cắt cụt."
}

$outDirectory = Split-Path -Parent $OutPath
if ($outDirectory -and -not (Test-Path -LiteralPath $outDirectory)) {
    New-Item -ItemType Directory -Path $outDirectory -Force | Out-Null
}

# WriteAllBytes chứ không phải toán tử '>': PS 5.1 coi '>' là ghi text UTF-16LE và làm hỏng file nhị phân.
try {
    [IO.File]::WriteAllBytes($OutPath, $bytes)
}
catch [UnauthorizedAccessException] {
    # Mặc định ghi cạnh script, mà script thì đi cùng exe — chỗ đó có thể là Program Files.
    throw "Không ghi được vào '$OutPath' (thiếu quyền). Chạy lại với -OutPath trỏ vào chỗ ghi được, ví dụ: -OutPath `"`$env:LOCALAPPDATA\ProxyDivert\softether-watermark.dat`""
}

Write-Host "Đã ghi $($bytes.Length) byte vào $OutPath"
Write-Host ""
Write-Host "Khai vào file .vpn của đường ra SoftEther:"
Write-Host "    Watermark = $OutPath"
Write-Host ""
Write-Host "Khối này là dữ liệu GPLv2 lấy từ mã nguồn SoftEther VPN — dùng trên máy mình thì thoải mái,"
Write-Host "nhưng đừng commit vào repo và đừng đóng gói kèm khi phát hành."
