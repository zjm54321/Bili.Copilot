$ErrorActionPreference = 'Stop'
$previousOutputEncoding = [Console]::OutputEncoding

try {
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = $utf8

    # Check tags separately so Git errors are not mistaken for missing tags.
    $tags = git tag --list
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to list git tags (exit code $LASTEXITCODE)."
    }

    if (-not $tags) {
        Write-Host "No tags found, using last 20 commits."
        $commits = git -c i18n.logOutputEncoding=utf-8 log -n 20 --pretty=format:"- %s"
    } else {
        $lastTag = git describe --tags --abbrev=0
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to find the last git tag (exit code $LASTEXITCODE)."
        }

        Write-Host "Last tag: $lastTag"
        $commits = git -c i18n.logOutputEncoding=utf-8 log "$lastTag..HEAD" --pretty=format:"- %s"
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to get git history (exit code $LASTEXITCODE)."
    }

    if (-not $commits) {
        $commits = "No new commits."
    }

    $zhNotes = "Updates`n`n" + ($commits -join "`n")

    if ($env:GITHUB_OUTPUT) {
        $delimiter = "release_notes_$([Guid]::NewGuid().ToString('N'))"
        [System.IO.File]::AppendAllText(
            $env:GITHUB_OUTPUT,
            "release_notes_zh<<$delimiter`n$zhNotes`n$delimiter`n",
            $utf8
        )
    }

    Write-Host "Generated Release Notes:"
    Write-Host $zhNotes
} catch {
    Write-Error "Failed to generate release notes: $_" -ErrorAction Continue
    exit 1
} finally {
    [Console]::OutputEncoding = $previousOutputEncoding
}
