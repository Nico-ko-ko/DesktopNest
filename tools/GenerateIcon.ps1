[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = New-Object System.Drawing.Bitmap(
    $size,
    $size,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$png = New-Object System.IO.MemoryStream
$stream = $null
$writer = $null

try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $background = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(255, 31, 92, 184))
    $folder = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(255, 248, 250, 253))
    $accent = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(255, 40, 184, 191))

    $graphics.FillEllipse($background, 12, 12, 232, 232)

    $tabPoints = @(
        (New-Object System.Drawing.Point(60, 92)),
        (New-Object System.Drawing.Point(108, 92)),
        (New-Object System.Drawing.Point(122, 74)),
        (New-Object System.Drawing.Point(76, 74))
    )
    $graphics.FillPolygon($folder, $tabPoints)
    $graphics.FillRectangle($folder, 54, 92, 148, 88)
    $graphics.FillRectangle($accent, 76, 126, 104, 12)
    $graphics.FillRectangle($accent, 76, 150, 72, 10)

    $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes = $png.ToArray()

    $directory = Split-Path -Parent $OutputPath
    if (-not [String]::IsNullOrWhiteSpace($directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $stream = New-Object System.IO.FileStream(
        $OutputPath,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $writer = New-Object System.IO.BinaryWriter($stream)

    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]1)

    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$pngBytes.Length)
    $writer.Write([UInt32]22)
    $writer.Write($pngBytes)
}
finally {
    if ($writer -ne $null) {
        $writer.Dispose()
    }
    if ($stream -ne $null) {
        $stream.Dispose()
    }
    $png.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    if ($background -ne $null) { $background.Dispose() }
    if ($folder -ne $null) { $folder.Dispose() }
    if ($accent -ne $null) { $accent.Dispose() }
}

Write-Output $OutputPath
