# Decodes every S2C 10090 (accepted-quest snapshot) frame found in a capture log.
# Frames are located by their 4-byte header (declared length 0x0800 + opcode 0x276A),
# which also finds frames the proxy coalesced into a single logged TCP segment.
param(
    [Parameter(Mandatory = $true)]
    [string[]] $Capture
)

function Get-Word([string] $hex, [int] $offset) {
    if (($offset * 2) + 8 -gt $hex.Length) { return $null }
    return $hex.Substring($offset * 2, 8)
}

function Convert-WordToUInt32([string] $word) {
    if ($null -eq $word) { return $null }
    $b = [byte[]]::new(4)
    for ($i = 0; $i -lt 4; $i++) { $b[$i] = [Convert]::ToByte($word.Substring($i * 2, 2), 16) }
    return [BitConverter]::ToUInt32($b, 0)
}

foreach ($file in $Capture) {
    $raw = Get-Content -LiteralPath $file -Raw
    if ([string]::IsNullOrEmpty($raw)) { continue }
    Write-Output "##### $([IO.Path]::GetFileName($file))"
    $hits = [regex]::Matches($raw, '00086A27', 'IgnoreCase')
    $seen = 0
    foreach ($m in $hits) {
        # Timestamp of the enclosing log entry.
        $before = $raw.Substring(0, $m.Index)
        $stamps = [regex]::Matches($before, '\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+')
        $stamp = if ($stamps.Count -gt 0) { $stamps[$stamps.Count - 1].Value } else { '?' }
        $dir = '?'
        if ($stamps.Count -gt 0) {
            $headStart = $stamps[$stamps.Count - 1].Index
            $headLine = $raw.Substring($headStart, [Math]::Min(160, $raw.Length - $headStart))
            if ($headLine -match 'GAME (S->C|C->S)') { $dir = $Matches[1] }
        }

        # Frame: keep only hex characters from the match onward.
        $tail = $raw.Substring($m.Index, [Math]::Min(6000, $raw.Length - $m.Index))
        $hex = ($tail -replace '[^0-9a-fA-F]', '').ToUpper()
        if ($hex.Length -lt 4096) { continue }
        $hex = $hex.Substring(0, 4096)
        $seen++

        $count = Convert-WordToUInt32 (Get-Word $hex 4)
        Write-Output "  [$seen] $stamp  $dir  count=$count"
        for ($q = 0; $q -lt [Math]::Min([int]$count, 12); $q++) {
            $d = 8 + (96 * $q)
            $questId   = Convert-WordToUInt32 (Get-Word $hex ($d + 0))
            $giver     = Convert-WordToUInt32 (Get-Word $hex ($d + 4))
            $responder = Convert-WordToUInt32 (Get-Word $hex ($d + 8))
            $d40 = Convert-WordToUInt32 (Get-Word $hex ($d + 40))
            $d44 = Convert-WordToUInt32 (Get-Word $hex ($d + 44))
            $d56 = Convert-WordToUInt32 (Get-Word $hex ($d + 56))
            $d60 = Convert-WordToUInt32 (Get-Word $hex ($d + 60))
            $d68 = Convert-WordToUInt32 (Get-Word $hex ($d + 68))
            $d72 = Convert-WordToUInt32 (Get-Word $hex ($d + 72))
            $d76 = Convert-WordToUInt32 (Get-Word $hex ($d + 76))
            $d80 = Convert-WordToUInt32 (Get-Word $hex ($d + 80))
            Write-Output ("      d{0}: quest={1} giver={2} responder={3} monster(d40)={4} d44={5} required(d56)={6} d60={7} kind(d68)={8} STATE(d72)={9} d76={10} progress(d80)={11} current={12}" -f `
                $q, $questId, $giver, $responder, $d40, $d44, $d56, $d60, $d68, $d72, $d76, $d80, ([int]$d80 -shr 16))
        }
        for ($q = 0; $q -lt [Math]::Min([int]$count, 12); $q++) {
            $r = 8 + (96 * [int]$count) + (72 * $q)
            $reward = Convert-WordToUInt32 (Get-Word $hex ($r + 8))
            $flag   = Convert-WordToUInt32 (Get-Word $hex ($r + 32))
            Write-Output ("      r{0}: record+8(reward)={1} record+32(flag)=0x{2:X8}" -f $q, $reward, $flag)
        }
    }
    if ($seen -eq 0) { Write-Output "  (none)" }
}
