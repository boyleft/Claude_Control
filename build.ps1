param(
    [ValidateSet('anycpu','x86','x64')]
    [string]$Platform = 'anycpu'
)

$ErrorActionPreference = 'Stop'
$frameworkFolder = if ([Environment]::Is64BitOperatingSystem) { 'Framework64' } else { 'Framework' }
$framework = Join-Path $env:WINDIR "Microsoft.NET\$frameworkFolder\v4.0.30319"
$source = Join-Path $PSScriptRoot 'src'
$build = Join-Path $PSScriptRoot 'bin'
if ($Platform -ne 'anycpu') { $build = Join-Path $build $Platform }
[void](New-Item -ItemType Directory -Force $build)
$compiler = Join-Path $framework 'csc.exe'
$refs = @('System.dll','System.Core.dll','System.Xaml.dll','System.Management.dll',
    'System.ServiceProcess.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll',
    'WPF\WindowsBase.dll','WPF\PresentationFramework.dll','WPF\PresentationCore.dll')
$argsList = @('/nologo','/optimize+','/target:winexe',"/platform:$Platform",'/codepage:65001',
    "/out:$build\ClaudeControl.exe","/win32manifest:$source\app.manifest","/resource:$source\Main.xaml,Main.xaml")
foreach ($ref in $refs) { $argsList += '/r:' + (Join-Path $framework $ref) }

# Write a PNG-compressed application icon to the build directory.
Add-Type -AssemblyName System.Drawing
$bmp = New-Object Drawing.Bitmap 64,64
$g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([Drawing.Color]::Transparent)
$bg = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#D97757'))
$fg = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#362219'))
$pen = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#542D20')),3
$g.FillEllipse($bg,1,1,62,62)
$g.DrawEllipse($pen,12,12,40,40)
$g.FillRectangle($fg,25,25,14,14)
$png = New-Object IO.MemoryStream
$bmp.Save($png,[Drawing.Imaging.ImageFormat]::Png)
$bytes = $png.ToArray()
$file = [IO.File]::Create((Join-Path $build 'ClaudeControl.ico'))
$writer = New-Object IO.BinaryWriter $file
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([uint16]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$bytes.Length)
    $writer.Write([uint32]22); $writer.Write([byte[]]$bytes)
} finally {
    $writer.Dispose(); $png.Dispose(); $g.Dispose(); $bmp.Dispose(); $pen.Dispose(); $bg.Dispose(); $fg.Dispose()
}
$argsList += "/win32icon:$build\ClaudeControl.ico"
$argsList += @(Get-ChildItem -LiteralPath $source -Filter '*.cs' | ForEach-Object FullName)
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
Write-Output (Join-Path $build 'ClaudeControl.exe')
