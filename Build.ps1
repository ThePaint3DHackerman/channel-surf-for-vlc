$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap 64,64
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::FromArgb(20,28,39))
$pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(111,224,206)),5
foreach ($height in @(19,31,43)) {
    $graphics.DrawBezier($pen,14,$height,26,($height-9),38,($height+9),50,$height)
}
$icon = [Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconFile = [IO.File]::Create((Join-Path $PSScriptRoot 'ChannelSurf.ico'))
$icon.Save($iconFile)
$iconFile.Dispose()
$graphics.Dispose()
$pen.Dispose()
$bitmap.Dispose()
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/langversion:5','/optimize+', '/out:Channel Surf.exe', '/win32icon:ChannelSurf.ico','/win32manifest:app.manifest', '/resource:MainWindow.xaml,MainWindow.xaml', '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xml.dll', '/reference:System.Xaml.dll','/reference:System.Net.Http.dll','/reference:System.Runtime.Serialization.dll')
foreach ($assembly in @('PresentationFramework','PresentationCore','WindowsBase')) { $arguments += '/reference:' + (Join-Path $framework ('WPF\' + $assembly + '.dll')) }
$arguments += @('App.cs','Models.cs','PlaylistService.cs','VlcController.cs')
& (Join-Path $framework 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output 'Built Channel Surf.exe'
