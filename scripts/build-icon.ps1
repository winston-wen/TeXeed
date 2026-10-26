param(
    [string]$Source = 'src/TeXeed.App/Assets/TeXeed.png',
    [string]$Output = 'src/TeXeed.App/Assets/TeXeed.ico'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$taskRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = [IO.Path]::GetFullPath($Source, $taskRoot)
$outputPath = [IO.Path]::GetFullPath($Output, $taskRoot)
$bitmap = [Windows.Media.Imaging.BitmapImage]::new()
$bitmap.BeginInit()
$bitmap.CacheOption = [Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$bitmap.UriSource = [Uri]::new($sourcePath)
$bitmap.EndInit()
$bitmap.Freeze()
if ($bitmap.PixelWidth -ne $bitmap.PixelHeight) { throw 'Application icon source must be square.' }

# Generate a PNG frame for each Windows icon size.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [Windows.Media.BitmapScalingMode]::HighQuality)
    $drawing = $visual.RenderOpen()
    $drawing.DrawImage($bitmap, [Windows.Rect]::new(0, 0, $size, $size))
    $drawing.Close()
    $render = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $render.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($render))
    $buffer = [IO.MemoryStream]::new()
    try { $encoder.Save($buffer); [PSCustomObject]@{ Size = $size; Bytes = $buffer.ToArray() } }
    finally { $buffer.Dispose() }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
$stream = [IO.File]::Create($outputPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Bytes.Length); $writer.Write([UInt32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose() }
Write-Host "Created $outputPath ($($sizes -join ', ') px)"
