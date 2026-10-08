# Rasterizes Aviary's simple vector mark into a multi-resolution Windows icon.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assets=Join-Path $PSScriptRoot '..\..\src\Aviary.App\Assets'
$sizes=@(16,20,24,32,40,48,64,128,256)
$images=@()
foreach($size in $sizes){
 $bitmap=New-Object Drawing.Bitmap($size,$size)
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
 $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
 $graphics.ScaleTransform($size/256.0,$size/256.0)
 $tile=New-Object Drawing.Drawing2D.GraphicsPath
 $tile.AddArc(8,8,112,112,180,90); $tile.AddArc(136,8,112,112,270,90)
 $tile.AddArc(136,136,112,112,0,90); $tile.AddArc(8,136,112,112,90,90); $tile.CloseFigure()
 $gradient=New-Object Drawing.Drawing2D.LinearGradientBrush([Drawing.Point]::new(8,8),[Drawing.Point]::new(248,248),[Drawing.ColorTranslator]::FromHtml('#38BDF8'),[Drawing.ColorTranslator]::FromHtml('#2563EB'))
 $graphics.FillPath($gradient,$tile)
 $bird=New-Object Drawing.Drawing2D.GraphicsPath
 $bird.AddBezier(48,78,82,74,107,88,128,118); $bird.AddBezier(128,118,149,88,174,74,208,78)
 $bird.AddLine(208,78,177,139); $bird.AddBezier(177,139,155,148,141,167,128,192)
 $bird.AddBezier(128,192,115,167,101,148,79,139); $bird.CloseFigure()
 $graphics.FillPath([Drawing.Brushes]::White,$bird)
 $wing=New-Object Drawing.Drawing2D.GraphicsPath
 $wing.AddBezier(128,118,149,88,174,74,208,78); $wing.AddLine(208,78,177,139)
 $wing.AddBezier(177,139,155,148,141,167,128,192); $wing.CloseFigure()
 $shade=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#DBEAFE')); $graphics.FillPath($shade,$wing)
 $stream=New-Object IO.MemoryStream; $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); $images+=,$stream.ToArray()
 if($size -eq 256){$bitmap.Save((Join-Path $assets 'Aviary.png'),[Drawing.Imaging.ImageFormat]::Png)}
 $stream.Dispose(); $shade.Dispose(); $wing.Dispose(); $bird.Dispose(); $gradient.Dispose(); $tile.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file=[IO.File]::Create((Join-Path $assets 'Aviary.ico')); $writer=New-Object IO.BinaryWriter($file)
try {
 $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
 $offset=6+16*$sizes.Count
 for($i=0;$i -lt $sizes.Count;$i++){
  $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
  $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
  $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
  $offset+=$images[$i].Length
 }
 foreach($bytes in $images){$writer.Write([byte[]]$bytes)}
} finally {$writer.Dispose(); $file.Dispose()}
Write-Output 'Generated Aviary.ico at nine Windows icon sizes.'
