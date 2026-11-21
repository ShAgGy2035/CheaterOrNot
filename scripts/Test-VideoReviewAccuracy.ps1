[CmdletBinding(DefaultParameterSetName = 'Evaluate')]
param(
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Create')][string]$CreateAnnotations,
    [Parameter(Mandatory = $true, ParameterSetName = 'Evaluate')]
    [Parameter(Mandatory = $true, ParameterSetName = 'Sequence')][string]$AnnotationsPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Sequence')][string]$CreateSequenceEvidence,
    [Parameter(Mandatory = $true, ParameterSetName = 'Sequence')][string]$FfmpegPath,
    [Parameter(ParameterSetName = 'Sequence')][ValidateSet(1, 15)][int]$EveryNthFrame = 15,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-BoundedText([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 1000000) { throw "Benchmark input exceeds 1 MB: $Path" }
    return [IO.File]::ReadAllText($Path)
}

function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Resolve-Media([string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) {
        throw 'Evidence must use a relative path inside the report folder.'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root $Relative))
    if (!$path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Evidence path escapes the report folder.'
    }
    for ($current = $path; $current.Length -ge $root.Length; $current = Split-Path $current -Parent) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Linked benchmark evidence is not supported.'
        }
    }
    return $path
}

function Write-NewJson([string]$Path, $Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 12
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally { $stream.Dispose() }
}

function New-SequenceEvidence {
    if ($CreateSequenceEvidence -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]*$') {
        throw 'Use a new simple folder name inside the report folder for sequence evidence.'
    }
    $folder = Join-Path $root $CreateSequenceEvidence
    if (Test-Path -LiteralPath $folder) { throw 'Sequence output already exists; choose a new folder name.' }
    $encoder = (Resolve-Path -LiteralPath $FfmpegPath).Path
    $probe = Join-Path (Split-Path $encoder -Parent) 'ffprobe.exe'
    $font = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Fonts)) 'consola.ttf'
    if (!(Test-Path -LiteralPath $probe) -or !(Test-Path -LiteralPath $font)) {
        throw 'Sequence preparation requires bundled FFprobe and the Windows Consolas font.'
    }
    $font = $font.Replace('\', '\\').Replace(':', '\:').Replace("'", "\'")
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    $packs = @()
    foreach ($clip in $clips) {
        $media = Resolve-Media "clip-$($clip.number).mp4"
        $json = & $probe -v error -select_streams v:0 -show_frames -show_entries frame=best_effort_timestamp_time -of json $media
        if ($LASTEXITCODE -ne 0) { throw "FFprobe failed for clip $($clip.number)." }
        $decoded = ($json -join "`n") | ConvertFrom-Json
        $frames = @($decoded.frames)
        if ($frames.Count -lt 2 -or $frames.Count -gt 300) { throw 'Expected a bounded 30-fps review clip.' }
        $times = @($frames | ForEach-Object {
            [double]::Parse($_.best_effort_timestamp_time, [Globalization.CultureInfo]::InvariantCulture)
        })
        for ($i = 0; $i -lt $times.Count; $i++) {
            if ([Math]::Abs($times[$i] - $i / 30.0) -gt 0.001) {
                throw 'Expected zero-based constant 30-fps timestamps; no approximate frame-number conversion is allowed.'
            }
        }
        $selected = @(0..($times.Count - 1) | Where-Object { $_ % $EveryNthFrame -eq 0 -or $_ -eq $times.Count - 1 })
        $filter = "select=not(mod(n\,$EveryNthFrame))+eq(n\,$($times.Count - 1)),scale=640:360," +
            "drawtext=fontfile='$font':text='%{pts\:flt}s':x=8:y=8:fontsize=22:fontcolor=white:box=1:boxcolor=black," +
            'tile=3x3:nb_frames=9'
        & $encoder -v error -nostdin -n -i $media -vf $filter -an -fps_mode vfr (Join-Path $folder "clip-$($clip.number)-page-%02d.png")
        if ($LASTEXITCODE -ne 0) { throw "Sequence extraction failed for clip $($clip.number); partial local output was not accepted." }
        if ((Hash $media) -cne $clip.sha256) { throw 'A review copy changed during sequence preparation.' }
        $pages = @()
        for ($offset = 0; $offset -lt $selected.Count; $offset += 9) {
            $numbers = @($selected | Select-Object -Skip $offset -First 9)
            $relative = Join-Path $CreateSequenceEvidence ('clip-{0}-page-{1:00}.png' -f $clip.number, ($pages.Count + 1))
            $pages += [pscustomobject]@{
                path = $relative; sha256 = Hash (Resolve-Media $relative)
                frameNumbers = $numbers
                timesSeconds = @($numbers | ForEach-Object { $times[$_] })
            }
        }
        $packs += [pscustomobject]@{
            number = $clip.number; sourceSha256 = $clip.sha256; sourceBytes = $clip.bytes
            decodedFrames = $times.Count; pages = $pages
        }
    }
    $index = [ordered]@{
        version = 1; kind = 'ordered_frame_samples'
        sourceFrameRate = 30; everyNthFrame = $EveryNthFrame; includesFinalFrame = $true
        viewingOrder = 'Pages in array order; each page left-to-right, top-to-bottom. Unused final cells are black, not evidence.'
        limitations = $(if ($EveryNthFrame -eq 1) {
            'Every decoded 30-fps frame is included as a 640x360 storyboard cell. This is not continuous playback or audio review. Downscaling, reconstruction and events between source frames limit detail; no raw input, precise reaction time, intent or cheating finding is established.'
        } else {
            'Two samples per second plus the final decoded frame; no audio or intervening-frame review. Samples can support coarse visible transitions, not continuous tracking, precise reaction time, intent or cheating findings.'
        })
        clips = $packs
    }
    $indexPath = Join-Path $folder 'sequence-index.json'
    Write-NewJson $indexPath $index
    Write-Output "Prepared ordered local samples: $indexPath`nIndex SHA256: $(Hash $indexPath)`nNo judgments changed; no upload or API request."
}

function Test-SampledSequence($Reference, $Entry) {
    $index = Read-BoundedText (Resolve-Media $Reference.path) | ConvertFrom-Json
    if ($index.version -ne 1 -or $index.kind -cne 'ordered_frame_samples' -or
        $index.sourceFrameRate -ne 30 -or @(1, 15) -notcontains $index.everyNthFrame -or $index.includesFinalFrame -ne $true) {
        throw 'Unsupported ordered-sample index.'
    }
    $sample = @($index.clips | Where-Object { $_.number -eq $Entry.clipNumber })
    $source = @($clips | Where-Object { $_.number -eq $Entry.clipNumber })[0]
    if ($sample.Count -ne 1 -or $sample[0].sourceSha256 -cne $source.sha256 -or $sample[0].sourceBytes -ne $source.bytes) {
        throw 'Ordered samples are bound to a different source clip.'
    }
    $pack = $sample[0]
    if ($pack.decodedFrames -lt 2 -or $pack.decodedFrames -gt 300) { throw 'Invalid sample frame count.' }
    $expected = @(0..($pack.decodedFrames - 1) | Where-Object { $_ % $index.everyNthFrame -eq 0 -or $_ -eq $pack.decodedFrames - 1 })
    $numbers = @()
    $times = @()
    if (@($pack.pages).Count -ne [Math]::Ceiling($expected.Count / 9.0)) { throw 'Missing ordered sample pages.' }
    foreach ($page in $pack.pages) {
        $path = Resolve-Media $page.path
        if ([IO.Path]::GetExtension($path) -ine '.png' -or (Hash $path) -cne $page.sha256 -or
            @($page.frameNumbers).Count -ne @($page.timesSeconds).Count -or
            @($page.frameNumbers).Count -lt 1 -or @($page.frameNumbers).Count -gt 9) {
            throw 'Ordered sample page changed or has invalid timestamps.'
        }
        $numbers += @($page.frameNumbers)
        $times += @($page.timesSeconds)
    }
    if ($numbers.Count -ne $expected.Count) { throw 'Ordered samples omit selected frames.' }
    for ($i = 0; $i -lt $expected.Count; $i++) {
        if ($numbers[$i] -ne $expected[$i] -or [double]::IsNaN([double]$times[$i]) -or
            [Math]::Abs([double]$times[$i] - $expected[$i] / 30.0) -gt 0.001) {
            throw 'Ordered sample timestamps/frame order are invalid.'
        }
    }
    if ([Math]::Abs($Reference.startSeconds - $times[0]) -gt 0.001 -or
        [Math]::Abs($Reference.endSeconds - $times[-1]) -gt 0.001) {
        throw 'Sampled reference interval must disclose its actual first/last sampled offsets.'
    }
}

$ReportPath = (Resolve-Path -LiteralPath $ReportPath).Path
$root = Split-Path $ReportPath -Parent
$report = Read-BoundedText $ReportPath
$request = [regex]::Match($report, '(?m)^Request SHA256: ([A-Fa-f0-9]{64})\r?$')
if (!$request.Success -or !$report.Contains('UNVERIFIED AI VIDEO COMMENTARY')) {
    throw 'Expected a saved app video-review report with a request hash.'
}
$manifestPath = [IO.Path]::GetFullPath((Join-Path $root '..\..\review-manifest.json'))
$manifest = Read-BoundedText $manifestPath | ConvertFrom-Json
if ($manifest.version -ne 2 -or $manifest.sourceSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'A source-bound version-2 capture manifest is required.'
}
$observations = @()
$alias = $null
$inObservations = $false
foreach ($line in ($report -split '\r?\n')) {
    if ($line -ceq 'Player observations') { $inObservations = $true; continue }
    if ($line -ceq 'Review moments with reasons') { $inObservations = $false }
    if (!$inObservations) { continue }
    $owner = [regex]::Match($line, '^- (Player[0-9]+) \| Measured basis:')
    if ($owner.Success) { $alias = $owner.Groups[1].Value; continue }
    if (!$line.StartsWith('  Video observation')) { continue }
    $match = [regex]::Match($line,
        '^  Video observation \((Visible|Audible|NotDiscernible)\): clip ([0-9]+), \+([0-9]+(?:\.[0-9]+)?)s \(approximate\) \| (.+)$')
    if (!$match.Success -or !$alias) { throw 'Unrecognized video observation; update the benchmark parser explicitly.' }
    $number = [int]$match.Groups[2].Value
    $clip = @($manifest.clips | Where-Object { $_.number -eq $number })
    if ($clip.Count -ne 1 -or $clip[0].alias -cne $alias) { throw 'Observation does not match the capture alias/clip.' }
    $observations += [pscustomobject][ordered]@{
        id = 'observation-' + ($observations.Count + 1)
        alias = $alias
        clipNumber = $number
        seconds = [double]::Parse($match.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
        basis = $match.Groups[1].Value
        claim = $match.Groups[4].Value
        verdict = 'unreviewed'
        reason = ''
        references = @()
    }
}
if ($observations.Count -lt 1 -or $observations.Count -gt 30) { throw 'Expected 1-30 timestamped scene observations.' }
$clips = @($observations.clipNumber | Sort-Object -Unique | ForEach-Object {
    $path = Resolve-Media "clip-$_.mp4"
    [pscustomobject]@{ number = $_; sha256 = Hash $path; bytes = (Get-Item -LiteralPath $path).Length }
})
$binding = 'legacy_folder_association_only'
$sourceHeader = [regex]::Match($report, '(?m)^Source demo SHA256: ([A-Fa-f0-9]{64})\r?$')
$clipHeaders = [regex]::Matches($report, '(?m)^Review clip ([0-9]+) SHA256: ([A-Fa-f0-9]{64}); bytes: ([0-9]+)\r?$')
if ($sourceHeader.Success -or $clipHeaders.Count -gt 0) {
    if (!$sourceHeader.Success -or $sourceHeader.Groups[1].Value -ne $manifest.sourceSha256 -or $clipHeaders.Count -ne $clips.Count) {
        throw 'Incomplete or mismatched report provenance.'
    }
    foreach ($clip in $clips) {
        $header = @($clipHeaders | Where-Object { [int]$_.Groups[1].Value -eq $clip.number })
        if ($header.Count -ne 1 -or $header[0].Groups[2].Value -ne $clip.sha256 -or
            [long]$header[0].Groups[3].Value -ne $clip.bytes) { throw 'A review copy differs from the saved report hash/size.' }
    }
    $binding = 'report_hashes_match_retained_copies'
}
$snapshot = [ordered]@{
    version = 1
    reportSha256 = Hash $ReportPath
    requestSha256 = $request.Groups[1].Value
    manifestSha256 = Hash $manifestPath
    sourceSha256 = $manifest.sourceSha256
    inputBinding = $binding
    reviewerKind = 'unassigned'
    rubric = 'Judge the WHOLE timestamped description. Supported: every factual/action claim is evidenced at that time. Contradicted: at least one clear conflict. Unverifiable: insufficient evidence or inferred intent/timing; not necessarily false. A still cannot verify motion, firing sequences, tracking or intent. Do not label cheating/clean play.'
    clips = $clips
    observations = $observations
}
if ($PSCmdlet.ParameterSetName -eq 'Create') {
    Write-NewJson $CreateAnnotations $snapshot
    Write-Output "Created unreviewed annotations: $CreateAnnotations. No accuracy claim; nothing uploaded."
    exit 0
}
$annotations = Read-BoundedText $AnnotationsPath | ConvertFrom-Json
foreach ($field in @('version', 'reportSha256', 'requestSha256', 'manifestSha256', 'sourceSha256', 'inputBinding')) {
    if ($annotations.$field -cne $snapshot[$field]) { throw "Annotation provenance mismatch: $field" }
}
if (@('unassigned', 'human', 'assistant_visual') -cnotcontains $annotations.reviewerKind) {
    throw 'reviewerKind must be unassigned, human or assistant_visual; do not imply independent human verification.'
}
if (@($annotations.clips).Count -ne $clips.Count) { throw 'Annotation clip coverage differs.' }
foreach ($clip in $clips) {
    $bound = @($annotations.clips | Where-Object { $_.number -eq $clip.number })
    if ($bound.Count -ne 1 -or $bound[0].sha256 -cne $clip.sha256 -or $bound[0].bytes -ne $clip.bytes) {
        throw 'A retained review copy changed after annotation.'
    }
}
if (@($annotations.observations).Count -ne $observations.Count) { throw 'Every observation must remain in the benchmark denominator.' }
$counts = [ordered]@{ supported = 0; contradicted = 0; unverifiable = 0; unreviewed = 0 }
foreach ($expected in $observations) {
    $entries = @($annotations.observations | Where-Object { $_.id -ceq $expected.id })
    if ($entries.Count -ne 1) { throw 'Missing or duplicate observation ID.' }
    $entry = $entries[0]
    foreach ($field in @('alias', 'clipNumber', 'seconds', 'basis', 'claim')) {
        if ($entry.$field -cne $expected.$field) { throw "Observation text/citation changed: $($expected.id), $field" }
    }
    if (@($counts.Keys) -cnotcontains $entry.verdict) { throw "Unsupported verdict: $($entry.verdict)" }
    if ($entry.verdict -cne 'unreviewed') {
        if ($annotations.reviewerKind -ceq 'unassigned' -or [string]::IsNullOrWhiteSpace($entry.reason) -or
            @($entry.references).Count -lt 1) { throw 'Reviewed observations require a reviewer kind, rationale and evidence references.' }
        foreach ($reference in $entry.references) {
            $path = Resolve-Media $reference.path
            $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
            $start = [double]$reference.startSeconds
            $end = [double]$reference.endSeconds
            if ($reference.sha256 -cnotmatch '^[A-F0-9]{64}$' -or (Hash $path) -cne $reference.sha256 -or
                @('frame', 'sequence', 'audio', 'sampled_sequence') -cnotcontains $reference.kind -or
                [string]::IsNullOrWhiteSpace($reference.fact) -or
                [double]::IsNaN($start) -or [double]::IsNaN($end) -or
                $start -lt 0 -or $end -gt 10 -or $end -lt $start -or
                ($reference.kind -ceq 'frame' -and ($start -ne $end -or [Math]::Abs($start - $entry.seconds) -gt 0.1)) -or
                ($reference.kind -cne 'frame' -and ($start -gt $entry.seconds -or $end -lt $entry.seconds))) {
                throw "Invalid, changed or mistimed reference: $($entry.id)"
            }
            if (($reference.kind -ceq 'frame' -and @('.png', '.jpg', '.jpeg') -notcontains $extension) -or
                (@('sequence', 'audio') -ccontains $reference.kind -and $path -ine (Resolve-Media "clip-$($entry.clipNumber).mp4"))) {
                throw 'Frame evidence must be an image; sequence/audio evidence must be the cited retained review MP4.'
            }
            if ($reference.kind -ceq 'sampled_sequence') { Test-SampledSequence $reference $entry }
            if ($entry.basis -ceq 'Audible' -and $reference.kind -cne 'audio') {
                throw 'An audible claim requires an audio reference, not a still image.'
            }
        }
    }
    $counts[$entry.verdict]++
}
if ($PSCmdlet.ParameterSetName -eq 'Sequence') {
    New-SequenceEvidence
    exit 0
}
$total = $observations.Count
$disposition = 'SUPPORTED_WITHIN_ANNOTATED_SAMPLE'
if ($counts.unreviewed -gt 0) { $disposition = 'INCOMPLETE' }
elseif ($counts.contradicted -gt 0 -or $counts.unverifiable -gt 0) { $disposition = 'ISSUES_FOUND' }
elseif ($binding -cne 'report_hashes_match_retained_copies') { $disposition = 'PROVENANCE_UNVERIFIED' }
$result = [ordered]@{
    version = 1
    disposition = $disposition
    reportSha256 = $snapshot.reportSha256
    inputBinding = $binding
    reviewerKind = $annotations.reviewerKind
    totalDescriptions = $total
    counts = $counts
    reviewedFraction = ($total - $counts.unreviewed) / $total
    supportedFractionOfAllDescriptions = $counts.supported / $total
    limits = 'Offline annotation accounting, not automatic semantic verification or cheating-detection accuracy. Whole scene descriptions only; review-moment rationales, telemetry and global notes are not scored. Unverifiable does not mean false. Ordered storyboards disclose their frame cadence; even every-decoded-frame evidence is downscaled visual inspection, not continuous playback/audio or raw input. Between-source-frame events, intent, precise reaction time and continuous tracking are not established. Reference times are approximate; hashes prove integrity, not truth. Small selected clips and assistant visual judgments are not independent human ground truth. Legacy folder association cannot prove original request bytes. No network request was made.'
}
if ($OutputPath) { Write-NewJson $OutputPath $result }
ConvertTo-Json -InputObject $result -Depth 8
if ($disposition -cne 'SUPPORTED_WITHIN_ANNOTATED_SAMPLE') { exit 2 }
exit 0
