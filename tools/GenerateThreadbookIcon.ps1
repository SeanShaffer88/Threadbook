param([Parameter(Mandatory)][string]$SvgPath,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run pwsh -STA.' }
Add-Type -AssemblyName PresentationCore,WindowsBase
$source=Get-Item -LiteralPath $SvgPath
if ($source.Length -gt 16384) { throw 'SVG exceeds 16 KiB limit.' }
$settings=[Xml.XmlReaderSettings]::new(); $settings.DtdProcessing='Prohibit'; $settings.XmlResolver=$null
$reader=[Xml.XmlReader]::Create($source.FullName,$settings)
try { $doc=[Xml.XmlDocument]::new(); $doc.XmlResolver=$null; $doc.Load($reader) } finally { $reader.Dispose() }
$root=$doc.DocumentElement
if ($root.LocalName -ne 'svg' -or $root.NamespaceURI -ne 'http://www.w3.org/2000/svg' -or $root.viewBox -ne '0 0 256 256') { throw 'Unsupported SVG root.' }
foreach ($attr in $root.Attributes) { if ($attr.Name -notin @('xmlns','viewBox')) { throw "Unsupported root attribute $($attr.Name)" } }
$shapes=@(foreach ($node in $root.ChildNodes) {
 if ($node.NodeType -eq 'Whitespace') { continue }
 if ($node.LocalName -notin @('path','circle') -or $node.HasChildNodes) { throw 'Unsupported SVG element.' }
 $allowed=if ($node.LocalName -eq 'path') {@('fill','d')} else {@('fill','cx','cy','r')}
 foreach ($attr in $node.Attributes) { if ($attr.Name -notin $allowed) { throw "Unsupported attribute $($attr.Name)" } }
 if ($node.fill -notin @('#FF5700','#FFFFFF')) { throw 'Unsupported fill.' }
 $brush=[Windows.Media.BrushConverter]::new().ConvertFromInvariantString($node.fill)
 if ($node.LocalName -eq 'path') { $geometry=[Windows.Media.Geometry]::Parse($node.d) }
 else {
  $values=@(foreach ($name in @('cx','cy','r')) { [double]::Parse($node.GetAttribute($name),[Globalization.CultureInfo]::InvariantCulture) })
  if ($values[2] -le 0) { throw 'Invalid radius.' }
  $geometry=[Windows.Media.EllipseGeometry]::new([Windows.Point]::new($values[0],$values[1]),$values[2],$values[2])
 }
 $bounds=$geometry.Bounds
 if ($bounds.IsEmpty -or $bounds.Left -lt 0 -or $bounds.Top -lt 0 -or $bounds.Right -gt 256 -or $bounds.Bottom -gt 256) { throw 'Geometry outside canvas.' }
 @{Brush=$brush; Geometry=$geometry}
})
if ($shapes.Count -ne 5) { throw 'Expected five canonical shapes.' }
$null=New-Item -ItemType Directory -Force -Path $OutputDirectory
$sizes=@(16,20,24,32,40,48,64,128,256,512)
foreach ($size in $sizes) {
 $visual=[Windows.Media.DrawingVisual]::new(); $context=$visual.RenderOpen()
 $context.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
 foreach ($shape in $shapes) { $context.DrawGeometry($shape.Brush,$null,$shape.Geometry) }
 $context.Pop(); $context.Close()
 $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32); $bitmap.Render($visual)
 $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.File]::Create((Join-Path $OutputDirectory "Threadbook-$size.png")); try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
$frames=@($sizes[0..8] | ForEach-Object { ,([IO.File]::ReadAllBytes((Join-Path $OutputDirectory "Threadbook-$_.png"))) })
$stream=[IO.File]::Create((Join-Path $OutputDirectory 'Threadbook.ico')); $writer=[IO.BinaryWriter]::new($stream)
try {
 $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]9); $offset=6+16*9
 for ($i=0;$i -lt 9;$i++) {
  $dimension=if ($sizes[$i] -eq 256) {0} else {$sizes[$i]}
  $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset); $offset+=$frames[$i].Length
 }
 foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
Write-Output "Generated ten PNGs and nine-frame ICO in $([IO.Path]::GetFullPath($OutputDirectory))"
