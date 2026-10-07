# Walks one logged capture entry (a single TCP read) as a sequence of
# length-prefixed packets, so frames the proxy coalesced become visible.
param(
    [Parameter(Mandatory = $true)][string] $Capture,
    [Parameter(Mandatory = $true)][string] $Needle
)

$raw = Get-Content -LiteralPath $Capture -Raw
$idx = $raw.IndexOf($Needle)
if ($idx -lt 0) { Write-Output "needle not found"; exit 1 }

# Find the start of the enclosing log entry (the preceding line beginning with a timestamp).
$lineStart = $raw.LastIndexOf("`n", $idx)
$entryStart = $raw.LastIndexOf("`n", $lineStart - 1) + 1
$headerEnd = $raw.IndexOf("`n", $entryStart)
$header = $raw.Substring($entryStart, $headerEnd - $entryStart)
Write-Output "ENTRY: $header"

# The CLEAR payload line(s) of this entry: everything from the entry start to the next entry.
$nextEntry = $raw.IndexOf("`n2026-", $headerEnd)
if ($nextEntry -lt 0) { $nextEntry = [Math]::Min($raw.Length, $headerEnd + 200000) }
$blob = $raw.Substring($headerEnd, $nextEntry - $headerEnd)

# Take the CLEAR line only.
$clearMatch = [regex]::Match($blob, 'CLEAR\s+([0-9a-fA-F]+)')
if (-not $clearMatch.Success) { Write-Output "no CLEAR line"; exit 1 }
$hex = $clearMatch.Groups[1].Value.ToUpper()
Write-Output "CLEAR hex chars = $($hex.Length)  (bytes $($hex.Length / 2))"
Write-Output "--- packet walk (offset : declared : opcode) ---"

$off = 0
$n = 0
while ($off + 8 -le $hex.Length) {
    $lenWord = $hex.Substring($off * 2, 4)
    $opWord = $hex.Substring(($off + 2) * 2, 4)
    $b = [byte[]]::new(2)
    $b[0] = [Convert]::ToByte($lenWord.Substring(0, 2), 16); $b[1] = [Convert]::ToByte($lenWord.Substring(2, 2), 16)
    $len = [BitConverter]::ToUInt16($b, 0)
    $b[0] = [Convert]::ToByte($opWord.Substring(0, 2), 16); $b[1] = [Convert]::ToByte($opWord.Substring(2, 2), 16)
    $op = [BitConverter]::ToUInt16($b, 0)
    $mark = if ($op -eq 10090) { '   <== 10090 accepted-quest snapshot' } else { '' }
    Write-Output ("  +{0,-7} len={1,-6} opcode={2}{3}" -f $off, $len, $op, $mark)
    if ($len -lt 4) { Write-Output "  (bad length, stop)"; break }
    $off += $len
    $n++
    if ($n -gt 400) { break }
}
