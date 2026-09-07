Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new(64,64)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([Drawing.Color]::Transparent)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::None
$fill = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(34,60,80))
$line = [Drawing.Pen]::new([Drawing.Color]::White,4)
$graphics.FillRectangle($fill,8,8,48,48)
$graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
$empty = [Drawing.SolidBrush]::new([Drawing.Color]::Transparent)
$graphics.FillRectangle($empty,24,24,16,16)
$graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceOver
$graphics.DrawRectangle($line,8,8,48,48)
$graphics.DrawRectangle($line,24,24,16,16)
foreach ($segment in @(@(8,24,12,24),@(20,24,24,24),@(40,8,40,12),@(40,20,40,24),@(40,40,44,40),@(52,40,56,40),@(24,40,24,44),@(24,52,24,56))) {
    $graphics.DrawLine($line,[int]$segment[0],[int]$segment[1],[int]$segment[2],[int]$segment[3])
}
$png = [IO.MemoryStream]::new()
$bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
$stream = [IO.File]::Create((Join-Path $PSScriptRoot 'studio.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
    $writer.Write($png.ToArray())
} finally { $writer.Dispose(); $png.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $line.Dispose(); $fill.Dispose(); $empty.Dispose() }
