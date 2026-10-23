# Convert assets/icon.png into a multi-size Windows .ico
# (256 as PNG entry, 64/48/32/16 as 32bpp BMP entries with AND masks)
param([string]$Png = "assets\icon.png", [string]$Ico = "assets\app.ico")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Png = Join-Path $root $Png
$Ico = Join-Path $root $Ico

$src = [System.Drawing.Bitmap]::new($Png)
$entries = @()
foreach ($s in 256,64,48,32,16) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::White)
  $g.DrawImage($src, 0, 0, $s, $s)
  $g.Dispose()

  if ($s -eq 256) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries += ,@{ Size = $s; Data = $ms.ToArray(); Png = $true }
  } else {
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pix = New-Object byte[] ($s * $s * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $pix, 0, $pix.Length)
    $bmp.UnlockBits($bd)

    # XOR mask: flip to bottom-up, BGRA comes free from Format32bppArgb
    $xor = New-Object byte[] ($s * $s * 4)
    for ($y = 0; $y -lt $s; $y++) {
      [Array]::Copy($pix, ($s - 1 - $y) * $s * 4, $xor, $y * $s * 4, $s * 4)
    }
    $andLen = [int][Math]::Ceiling($s / 8) * $s
    $and = New-Object byte[] $andLen

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40)                     # biSize
    $bw.Write([int32]$s)                      # biWidth
    $bw.Write([int32]($s * 2))                # biHeight (XOR+AND)
    $bw.Write([uint16]1)                      # biPlanes
    $bw.Write([uint16]32)                     # biBitCount
    $bw.Write([uint32]0)                      # biCompression = BI_RGB
    $bw.Write([uint32]($xor.Length + $andLen))# biSizeImage
    $bw.Write([int32]0); $bw.Write([int32]0)  # biXPels/biYPels
    $bw.Write([uint32]0); $bw.Write([uint32]0)# biClrUsed/biClrImportant
    $bw.Write($xor); $bw.Write($and)
    $bw.Flush()
    $entries += ,@{ Size = $s; Data = $ms.ToArray(); Png = $false }
  }
  $bmp.Dispose()
}
$src.Dispose()

$out = New-Object System.IO.MemoryStream
$bwOut = New-Object System.IO.BinaryWriter($out)
$bwOut.Write([uint16]0); $bwOut.Write([uint16]1); $bwOut.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
  $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
  $bwOut.Write([byte]$dim); $bwOut.Write([byte]$dim)
  $bwOut.Write([byte]0); $bwOut.Write([byte]0)     # colors
  $bwOut.Write([uint16]1); $bwOut.Write([uint16]32) # planes, bitcount
  $bwOut.Write([uint32]$e.Data.Length)
  $bwOut.Write([uint32]$offset)
  $offset += $e.Data.Length
}
foreach ($e in $entries) { $bwOut.Write($e.Data) }
$bwOut.Flush()
[System.IO.File]::WriteAllBytes($Ico, $out.ToArray())
Write-Host ("ico written: {0} ({1} bytes, sizes: {2})" -f $Ico, $out.Length, (($entries | ForEach-Object Size) -join '/'))
