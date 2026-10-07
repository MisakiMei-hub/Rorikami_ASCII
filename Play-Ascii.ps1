[CmdletBinding()]
param(
    [switch]$Color,
    [switch]$Mute,
    [switch]$Verify,
    [double]$PreviewSeconds = 0,
    [int]$SnapshotFrame = -1,
    [string]$SnapshotPath = ''
)
$ErrorActionPreference = 'Stop'
if (-not ('RouriAscii.Player' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'AsciiPlayer.cs')
}
$movie = Join-Path $PSScriptRoot 'movie.ascii.gz'
$audio = Join-Path $PSScriptRoot 'audio.wav'
try {
    if ($Verify) {
        [RouriAscii.Player]::Verify($movie, $audio)
    } elseif ($SnapshotFrame -ge 0) {
        if (-not $SnapshotPath) { $SnapshotPath = Join-Path $PSScriptRoot 'preview.txt' }
        [RouriAscii.Player]::Snapshot($movie, $SnapshotFrame, $SnapshotPath)
    } else {
        [RouriAscii.Player]::Play($movie, $audio, $Color.IsPresent, $Mute.IsPresent, $PreviewSeconds)
    }
} catch {
    Write-Error $_
    exit 1
}
