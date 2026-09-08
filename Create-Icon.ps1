$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$bitmap = [System.Drawing.Bitmap]::new(64,64)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = 'AntiAlias'
$graphics.Clear([System.Drawing.Color]::FromArgb(255,20,31,48))
$pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(119,226,194),4)
$points = [System.Drawing.Point[]]@([System.Drawing.Point]::new(32,8),[System.Drawing.Point]::new(45,21),[System.Drawing.Point]::new(32,34),[System.Drawing.Point]::new(19,21))
$graphics.DrawPolygon($pen,$points)
$pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.Width = 5
$graphics.DrawLine($pen,14,44,48,44)
$pen.Color = [System.Drawing.Color]::FromArgb(170,185,255)
$graphics.DrawLine($pen,14,54,36,54)
$png = [System.IO.MemoryStream]::new()
$bitmap.Save($png,[System.Drawing.Imaging.ImageFormat]::Png)
$file = [System.IO.File]::Create((Join-Path $PSScriptRoot 'app.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
$writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
$writer.Write($png.ToArray())
$writer.Dispose(); $png.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
