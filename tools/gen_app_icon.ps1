<#
    Builds the sshelfy app icon from the logo image (tools/sshelfy-logo.webp).
    Output: src/Sshelfy.App/Assets/sshelfy.ico (16-256 px) and sshelfy.png (256 px).

    Any format Windows can decode works as the source (WebP, PNG, JPEG); use a square image of at
    least 256 px with a transparent background around the rounded square.

    Usage (Windows PowerShell): powershell -ExecutionPolicy Bypass -File tools/gen_app_icon.ps1
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot "sshelfy-logo.webp"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\src\Sshelfy.App\Assets")
)

Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null

$stream = [System.IO.File]::OpenRead((Resolve-Path $Source))
try {
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        $stream, [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    # The WebP decoder reports an unspecified pixel format, which drawing treats as opaque;
    # an explicit BGRA copy keeps the transparent corners.
    $logo = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap(
        $decoder.Frames[0], [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $logo.Freeze()
} finally {
    $stream.Dispose()
}
if ($logo.PixelWidth -ne $logo.PixelHeight -or $logo.PixelWidth -lt 256) {
    throw "The logo must be square and at least 256 px (got $($logo.PixelWidth)x$($logo.PixelHeight))."
}

function Render([int]$size) {
    # High-quality (Fant) downscaling keeps small sizes smooth instead of aliased.
    $visual = New-Object System.Windows.Media.DrawingVisual
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
    $dc = $visual.RenderOpen()
    $dc.DrawImage($logo, (New-Object System.Windows.Rect(0, 0, $size, $size)))
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    , $ms.ToArray()
}

# ICO with PNG-compressed entries (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) { $images.Add([byte[]](Render $size)) }

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img, 0, $img.Length) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "sshelfy.ico"), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "sshelfy.png"), $images[$images.Count - 1])
"icon written to $OutDir"
