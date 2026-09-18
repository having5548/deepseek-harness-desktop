# 生成 DshDesktop 的应用图标（Assets\AppIcon.png 与 Assets\AppIcon.ico）。
# 用法: powershell -ExecutionPolicy Bypass -File scripts/generate-icon.ps1
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$assetsDir = Join-Path (Split-Path -Parent $scriptDir) "DshDesktop\Assets"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::Transparent)

# 深蓝→亮蓝 圆角渐变背景
$rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
$radius = 48
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$d = $radius * 2
$path.AddArc(0, 0, $d, $d, 180, 90)
$path.AddArc($size - $d, 0, $d, $d, 270, 90)
$path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
$path.AddArc(0, $size - $d, $d, $d, 90, 90)
$path.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(255, 15, 23, 42),
    [System.Drawing.Color]::FromArgb(255, 59, 130, 246),
    45.0)
$g.FillPath($brush, $path)

# 白色 "dsh" 文字
$font = New-Object System.Drawing.Font("Segoe UI", 92, [System.Drawing.FontStyle]::Bold)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = [System.Drawing.StringAlignment]::Center
$fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$textRect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
$g.DrawString("dsh", $font, $white, $textRect, $fmt)

$pngPath = Join-Path $assetsDir "AppIcon.png"
$icoPath = Join-Path $assetsDir "AppIcon.ico"

$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# 从位图生成 ICO（exe 图标用）
$hIcon = $bmp.GetHicon()
try {
    $icon = [System.Drawing.Icon]::FromHandle($hIcon)
    $fs = [System.IO.File]::Create($icoPath)
    try { $icon.Save($fs) } finally { $fs.Close() }
} finally {
    $icon.Dispose()
    $g.Dispose()
    $bmp.Dispose()
}

Write-Host "生成完成:"
Write-Host "  $pngPath"
Write-Host "  $icoPath"
