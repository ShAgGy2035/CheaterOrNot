$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$runner = Join-Path $PSScriptRoot 'Test-VideoReviewAccuracy.ps1'
$temp = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('CheaterOrNot-benchmark-test-' + [Guid]::NewGuid().ToString('N'))))
$root = Join-Path $temp 'capture\gemini-preview\fixture'
$report = Join-Path $root 'video-review-fixture.txt'
$annotations = Join-Path $root 'annotations.json'
$source = 'A' * 64
$script:passed = 0

function Save-Json($Value) {
    [IO.File]::WriteAllText($annotations, (ConvertTo-Json -InputObject $Value -Depth 12))
}

function Run-Case([string]$Name, [int]$ExitCode, [string[]]$Arguments, [string]$Expected) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'powershell.exe'
    $start.Arguments = (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner) + $Arguments |
        ForEach-Object { '"' + $_ + '"' }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExitCode -or !$output.Contains($Expected)) {
            throw "$Name failed: expected exit $ExitCode and '$Expected'; got $($process.ExitCode)`n$output"
        }
    }
    finally { $process.Dispose() }
    $script:passed++
}

try {
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $clip = Join-Path $root 'clip-1.mp4'
    [IO.File]::WriteAllText($clip, 'Synthetic integrity fixture, NOT playable video or factual ground truth.')
    [IO.File]::WriteAllText((Join-Path $temp 'capture\review-manifest.json'),
        ('{"version":2,"sourceSha256":"' + $source + '","clips":[{"number":1,"alias":"Player01"}]}'))
    $hash = (Get-FileHash -LiteralPath $clip -Algorithm SHA256).Hash
    $bytes = (Get-Item -LiteralPath $clip).Length
    $header = "Request SHA256: $source`nUNVERIFIED AI VIDEO COMMENTARY`n"
    $provenance = "Source demo SHA256: $source`nReview clip 1 SHA256: $hash; bytes: $bytes`n"
    $body = "Player observations`n- Player01 | Measured basis: synthetic fixture.`n" +
        "  Video observation (Visible): clip 1, +0s (approximate) | A synthetic wall.`n" +
        "  Video observation (Visible): clip 1, +2s (approximate) | A synthetic door.`nReview moments with reasons`n"
    [IO.File]::WriteAllText($report, $header + $provenance + $body)
    Run-Case 'Create without guessing accuracy' 0 @('-ReportPath', $report, '-CreateAnnotations', $annotations) 'Created unreviewed'
    $original = [IO.File]::ReadAllText($annotations)
    Run-Case 'Unreviewed descriptions cannot pass' 2 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'INCOMPLETE'
    $review = $original | ConvertFrom-Json
    $review.reviewerKind = 'assistant_visual'
    foreach ($entry in $review.observations) {
        $entry.verdict = 'supported'
        $entry.reason = 'Synthetic accounting test only; not a visual accuracy assertion.'
        $entry.references = @([pscustomobject]@{
            path = 'clip-1.mp4'; sha256 = $hash; startSeconds = 0; endSeconds = 4
            kind = 'sequence'; fact = 'Synthetic rubric fixture, not real video evidence.'
        })
    }
    Save-Json $review
    Run-Case 'All descriptions accounted for' 0 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'SUPPORTED_WITHIN_ANNOTATED_SAMPLE'
    $review.observations[0].verdict = 'contradicted'
    Save-Json $review
    Run-Case 'Contradiction blocks benchmark pass' 2 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'ISSUES_FOUND'
    $review.observations[0].verdict = 'unverifiable'
    Save-Json $review
    Run-Case 'Unverifiable is not counted as supported' 2 @('-ReportPath', $report, '-AnnotationsPath', $annotations) '"unverifiable":  1'
    $review.observations = @($review.observations[0])
    Save-Json $review
    Run-Case 'Dropping a description is rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'denominator'
    $review = $original | ConvertFrom-Json
    $review.observations[0].claim = 'Replacement claim'
    Save-Json $review
    Run-Case 'Editing a model claim is rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'text/citation changed'
    [IO.File]::WriteAllText($annotations, $original)
    [IO.File]::AppendAllText($clip, 'tampered')
    Run-Case 'Changed media is rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'differs from the saved report'
    [IO.File]::WriteAllText($clip, 'Synthetic integrity fixture, NOT playable video or factual ground truth.')
    [IO.File]::WriteAllText($report, $header + $body)
    Remove-Item -LiteralPath $annotations
    Run-Case 'Legacy reports are explicitly marked' 0 @('-ReportPath', $report, '-CreateAnnotations', $annotations) 'Created unreviewed'
    $review = [IO.File]::ReadAllText($annotations) | ConvertFrom-Json
    $review.reviewerKind = 'assistant_visual'
    foreach ($entry in $review.observations) {
        $entry.verdict = 'supported'
        $entry.reason = 'Synthetic fixture.'
        $entry.references = @([pscustomobject]@{
            path = 'clip-1.mp4'; sha256 = $hash; startSeconds = 0; endSeconds = 4
            kind = 'sequence'; fact = 'Synthetic fixture.'
        })
    }
    Save-Json $review
    Run-Case 'Legacy association cannot become verified provenance' 2 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'PROVENANCE_UNVERIFIED'
    $review.observations[0].references[0].startSeconds = 1
    Save-Json $review
    Run-Case 'Mistimed evidence is rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'mistimed reference'
    $review.observations[0].references[0].startSeconds = 0
    $review.observations[0].references[0].kind = 'frame'
    $review.observations[0].references[0].endSeconds = 0
    Save-Json $review
    Run-Case 'Video cannot be mislabeled as still evidence' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'Frame evidence must be an image'
    $review.observations[0].references[0].kind = 'sequence'
    $review.observations[0].references[0].endSeconds = 4
    $review.observations[0].references[0].sha256 = '0' * 64
    Save-Json $review
    Run-Case 'Changed reference hash is rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'changed or mistimed reference'
    $review.observations[0].references[0].sha256 = $hash
    $image = Join-Path $root 'sample.png'
    $indexPath = Join-Path $root 'sequence-index.json'
    [IO.File]::WriteAllText($image, 'Synthetic page integrity fixture, not image ground truth.')
    $sample = [pscustomobject]@{
        version = 1; kind = 'ordered_frame_samples'
        sourceFrameRate = 30; everyNthFrame = 15; includesFinalFrame = $true
        clips = @([pscustomobject]@{
            number = 1; sourceSha256 = $hash; sourceBytes = $bytes; decodedFrames = 121
            pages = @([pscustomobject]@{
                path = 'sample.png'; sha256 = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash
                frameNumbers = @(0,15,30,45,60,75,90,105,120)
                timesSeconds = @(0,0.5,1,1.5,2,2.5,3,3.5,4)
            })
        })
    }
    function Save-Samples {
        [IO.File]::WriteAllText($indexPath, (ConvertTo-Json -InputObject $sample -Depth 12))
        $review.observations[0].references[0].sha256 = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash
        Save-Json $review
    }
    $review.observations[0].references[0].path = 'sequence-index.json'
    $review.observations[0].references[0].kind = 'sampled_sequence'
    Save-Samples
    Run-Case 'Ordered samples preserve legacy provenance limits' 2 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'PROVENANCE_UNVERIFIED'
    $sample.clips[0].sourceSha256 = '0' * 64
    Save-Samples
    Run-Case 'Samples from another source are rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'different source clip'
    $sample.clips[0].sourceSha256 = $hash
    $sample.clips[0].pages[0].timesSeconds[0] = 0.25
    Save-Samples
    Run-Case 'Mislabeled sample time is rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'timestamps/frame order'
    $sample.clips[0].pages[0].timesSeconds[0] = 0
    Save-Samples
    $sample.everyNthFrame = 1
    $sample.clips[0].decodedFrames = 10
    $lastPage = Join-Path $root 'last-page.png'
    [IO.File]::WriteAllText($lastPage, 'Second synthetic page integrity fixture, not image ground truth.')
    $sample.clips[0].pages = @(
        [pscustomobject]@{
            path = 'sample.png'; sha256 = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash
            frameNumbers = @(0..8); timesSeconds = @(0..8 | ForEach-Object { $_ / 30.0 })
        },
        [pscustomobject]@{
            path = 'last-page.png'; sha256 = (Get-FileHash -LiteralPath $lastPage -Algorithm SHA256).Hash
            frameNumbers = @(9); timesSeconds = @(0.3)
        }
    )
    $review.observations[0].references[0].endSeconds = 0.3
    Save-Samples
    Run-Case 'Every decoded frame can be referenced without upgrading legacy provenance' 2 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'PROVENANCE_UNVERIFIED'
    $sample.clips[0].pages[0].frameNumbers[4] = 5
    Save-Samples
    Run-Case 'Duplicate frame cannot masquerade as exhaustive coverage' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'timestamps/frame order'
    $sample.clips[0].pages[0].frameNumbers[4] = 4
    $savedPages = $sample.clips[0].pages
    $sample.clips[0].pages = @($savedPages[0])
    Save-Samples
    Run-Case 'Missing final all-frame page is rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'Missing ordered sample pages'
    $sample.clips[0].pages = $savedPages
    $sample.everyNthFrame = 7
    Save-Samples
    Run-Case 'Unrecognized frame cadence is rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'Unsupported ordered-sample index'
    $sample.everyNthFrame = 1
    Save-Samples
    [IO.File]::AppendAllText($image, 'tampered')
    Run-Case 'Changed contact sheet is rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'sample page changed'
    $review.observations[0].references[0].path = '..\outside.mp4'
    Save-Json $review
    Run-Case 'Escaping reference paths are rejected' 1 @('-ReportPath', $report, '-AnnotationsPath', $annotations) 'escapes the report folder'
    Write-Output "$script:passed offline benchmark accounting/integrity tests passed. No model or factual-accuracy test was performed."
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
