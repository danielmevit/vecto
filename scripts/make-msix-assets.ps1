# Generates the MSIX tile/logo PNGs from the master 1024px logo.
# Run once after a logo change; the outputs are checked in (assets\msix\).
# KEEP PURE ASCII (see packaging\windows\build.ps1 header for why).
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = (Resolve-Path "$PSScriptRoot\..").Path
$src = "$root\assets\Vecto_Icon_1024x1024.png"   # app icon master (navy plate + white V, docs/brand/BRAND.md)
$outDir = "$root\assets\msix"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$sizes = @(
    @{ Name = "Square44x44Logo.png";   W = 44;  H = 44 },
    @{ Name = "Square150x150Logo.png"; W = 150; H = 150 },
    @{ Name = "StoreLogo.png";         W = 50;  H = 50 }
)

$srcImg = [System.Drawing.Image]::FromFile($src)
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s.W, $s.H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($srcImg, 0, 0, $s.W, $s.H)
    $g.Dispose()
    $out = Join-Path $outDir $s.Name
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "[ok] $out"
}
$srcImg.Dispose()
