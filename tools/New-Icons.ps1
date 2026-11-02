# Windows PowerShell 5.1: powershell.exe -NoProfile -STA -File tools\New-Icons.ps1
# Source geometry matches assets/app.svg. No external graphics dependency.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
function Brush([string]$color) { [Windows.Media.BrushConverter]::new().ConvertFromString($color) }
function Render-Icon([int]$size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size/512.0,$size/512.0))
    $drawing.DrawRoundedRectangle((Brush '#14181D'),$null,[Windows.Rect]::new(24,24,464,464),112,112)
    $drawing.DrawRoundedRectangle($null,[Windows.Media.Pen]::new((Brush '#33434A'),2),[Windows.Rect]::new(25,25,462,462),111,111)
    $pen = [Windows.Media.Pen]::new((Brush '#6ADBC6'),40)
    $pen.StartLineCap='Round'; $pen.EndLineCap='Round'; $pen.LineJoin='Round'
    $drawing.DrawGeometry($null,$pen,[Windows.Media.Geometry]::Parse('M148,340 L148,196 L364,340 L364,196'))
    $drawing.DrawEllipse((Brush '#B2F5E7'),$null,[Windows.Point]::new(148,196),25,25)
    $drawing.DrawEllipse((Brush '#B2F5E7'),$null,[Windows.Point]::new(364,340),25,25)
    $arcPen = [Windows.Media.Pen]::new((Brush '#6ADBC6'),16)
    $arcPen.StartLineCap='Round'; $arcPen.EndLineCap='Round'
    $drawing.DrawGeometry($null,$arcPen,[Windows.Media.Geometry]::Parse('M188,136 Q256,84 324,136 M222,151 Q256,125 290,151'))
    $drawing.Pop(); $drawing.Close()
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=New-Object IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}
[IO.File]::WriteAllBytes((Join-Path $assets 'app.png'),(Render-Icon 512))
$sizes=@(16,20,24,32,40,48,64,128,256)
$frames=@($sizes | ForEach-Object { ,(Render-Icon $_) })
$file=[IO.File]::Create((Join-Path $assets 'app.ico'))
$writer=[IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++) {
        $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
        $writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset);$offset+=$frames[$i].Length
    }
    foreach($frame in $frames){$writer.Write([byte[]]$frame)}
} finally {$writer.Dispose()}
Write-Output 'Generated app.svg companion PNG and multi-resolution ICO (16–256 px).'
