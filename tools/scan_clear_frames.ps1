# Extracts length-prefixed frames from a capture log, resynchronising inside each
# CLEAR line so frames the proxy coalesced (or began mid-frame) are still found.
#
# A frame starts where the u16 length is plausible AND the u16 opcode is one of the
# requested opcodes. That is what makes a frame inside a segment whose logged head is
# declared=65535 opcode=65535 visible.
param(
    [Parameter(Mandatory = $true)][string] $Capture,
    [Parameter(Mandatory = $true)][int[]] $Opcode,
    # Print the full frame hex for matching opcodes too.
    [switch] $Dump,
    # Only report frames at or after this timestamp prefix, e.g. 2026-10-06T13:44.
    [string] $Since = ''
)

$wanted = @{}
foreach ($o in $Opcode) { $wanted[[int]$o] = $true }

$raw = Get-Content -LiteralPath $Capture -Raw
$lineMatches = [regex]::Matches($raw, 'CLEAR[ \t]+([0-9A-Fa-f]+)')

$found = 0
foreach ($lm in $lineMatches) {
    $hex = $lm.Groups[1].Value.ToUpper()
    if ($hex.Length -lt 16) { continue }

    $before = $raw.Substring(0, $lm.Index)
    $stamps = [regex]::Matches($before, '\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+')
    $stamp = if ($stamps.Count -gt 0) { $stamps[$stamps.Count - 1].Value } else { '?' }
    if ($Since -ne '' -and $stamp -lt $Since) { continue }

    $dir = '?'
    if ($stamps.Count -gt 0) {
        $h = $raw.Substring($stamps[$stamps.Count - 1].Index, 120)
        if ($h -match 'GAME (S->C|C->S)') { $dir = $Matches[1] }
        elseif ($h -match 'LOGIN (S->C|C->S)') { $dir = 'L:' + $Matches[1] }
    }

    # $off is a byte offset; the hex string is two chars per byte.
    $bytes = [int]($hex.Length / 2)
    for ($off = 0; $off + 6 -le $bytes; $off += 2) {
        $len = [Convert]::ToInt32($hex.Substring($off * 2, 2), 16) +
               ([Convert]::ToInt32($hex.Substring(($off + 1) * 2, 2), 16) * 256)
        if ($len -lt 6 -or ($off + $len) -gt $bytes) { continue }
        $op = [Convert]::ToInt32($hex.Substring(($off + 2) * 2, 2), 16) +
              ([Convert]::ToInt32($hex.Substring(($off + 3) * 2, 2), 16) * 256)
        if (-not $wanted.ContainsKey($op)) { continue }

        Write-Output "=== $stamp $dir opcode=$op len=$len ==="
        if ($Dump) { Write-Output $hex.Substring($off * 2, $len * 2) }
        $found++
    }
}
Write-Output "### total frames matched: $found"
