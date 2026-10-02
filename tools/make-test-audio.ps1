<#
    Generates the speech clips the AudioRouteProbe experiment plays.

    Within a language the two clips share no vocabulary, so the words that turn up in the
    Live Captions window unambiguously identify which audio endpoint Live Captions is
    listening to. Pick the language matching the recognizer Live Captions is currently set
    to, otherwise the transcript is garbage and proves nothing.

    Uses the SAPI voices already present on Windows, so the probe itself needs no TTS
    dependency. Run this once before building.
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

$outDir = Join-Path $PSScriptRoot 'AudioRouteProbe'

$clips = @(
    @{ File = 'target_en.wav';     Voice = 'Zira';   Text = 'The quick brown fox jumps over the lazy dog near the river bank.' }
    @{ File = 'distractor_en.wav'; Voice = 'David';  Text = 'Sphinx of black quartz, judge my vow, said the zebra to the wizard.' }
    @{ File = 'target_ja.wav';     Voice = 'Haruka'; Text = '赤い風船が公園の空へゆっくり飛んでいきました。' }
    @{ File = 'distractor_ja.wav'; Voice = 'Haruka'; Text = '黒い自転車で図書館まで手紙を届けました。' }
    @{ File = 'target_zh.wav';     Voice = 'Huihui'; Text = '红色的气球慢慢飞向公园的天空。' }
    @{ File = 'distractor_zh.wav'; Voice = 'Huihui'; Text = '他骑着黑色自行车把信件送到图书馆。' }
)

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$available = $synth.GetInstalledVoices() | Where-Object { $_.Enabled } | ForEach-Object { $_.VoiceInfo.Name }

foreach ($clip in $clips) {
    $voice = $available | Where-Object { $_ -like "*$($clip.Voice)*" } | Select-Object -First 1
    if (-not $voice) {
        Write-Output ("{0,-18} SKIPPED - no voice matching '{1}'" -f $clip.File, $clip.Voice)
        continue
    }

    $path = Join-Path $outDir $clip.File
    $synth.SelectVoice($voice)
    $synth.Rate = -1
    $synth.SetOutputToWaveFile($path)
    $synth.Speak($clip.Text)
    $synth.SetOutputToNull()

    Write-Output ("{0,-18} {1,-28} {2,8} bytes" -f $clip.File, $voice, (Get-Item $path).Length)
}

$synth.Dispose()
