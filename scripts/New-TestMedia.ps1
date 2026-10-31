param(
    [string]$Directory = (Join-Path $PSScriptRoot '..\.tools\ui-media'),
    [ValidateRange(45,600)][int]$DurationSeconds = 45
)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Force -Path $Directory | Out-Null
# Silent PCM WAVs exercise playback without making noise on the test machine.
foreach ($name in @('Sample ED.wav', 'Sample OP.wav', 'Sample OP2.wav', 'Sample ED2.wav', '._ignored.wav')) {
    $stream = [IO.File]::Create((Join-Path $Directory $name))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $length = 8000 * 2 * $DurationSeconds
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $writer.Write([int](36 + $length))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
        $writer.Write([int]16)
        $writer.Write([short]1)
        $writer.Write([short]1)
        $writer.Write([int]8000)
        $writer.Write([int]16000)
        $writer.Write([short]2)
        $writer.Write([short]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
        $writer.Write([int]$length)
        $writer.Write([byte[]]::new($length))
    } finally { $writer.Dispose() }
}
$Directory
