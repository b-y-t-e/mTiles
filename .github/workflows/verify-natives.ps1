# Fails the build when a published tree is missing a native binary the application needs at run time.
#
# Every one of these arrives through a NuGet package's own copy rules rather than through anything in
# this repository, so a package update, a RID change or a trimming setting can drop one silently: the
# build stays green, the application starts, and dictation fails the first time somebody holds the
# shortcut - or the terminal quietly falls back to the in-box conhost. That is precisely the kind of
# breakage nobody notices until a user reports it, which is why it is checked here rather than trusted.
#
# Matching is by file name anywhere under the publish directory: a self-contained publish flattens
# runtimes/<rid>/ into the root, a framework-dependent build does not, and both are correct.

param(
    [Parameter(Mandatory = $true)][string] $PublishDir,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'linux-x64')][string] $Rid
)

$ErrorActionPreference = 'Stop'

# Resolved to a full path before anything is measured against it. The workflow passes "./publish" while
# every FullName below is absolute, so trimming by the *given* length chopped nine characters off the
# middle of an absolute path and made every "relative" path - and so every architecture segment read
# from it - nonsense.
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path

# What each entry is for, so a failure says more than a file name. Each platform takes a *list* of
# acceptable names, and the first one found satisfies the entry.
#
# The Linux names carry a trailing wildcard because an ELF shared library is as likely to be published
# under its soname - libportaudio.so.2, libonnxruntime.so.1.20.0 - as under the plain link name, and
# which of the two a NuGet package ships is the package's business. Today they are plain, checked in a
# publish; a package that starts shipping the versioned name would otherwise fail the release with
# "missing" against a file sitting right there.
#
# The alternatives on the ggml trio are a different brittleness, and one that is not a platform
# convention at all: the '-whisper' suffix is Whisper.net's own renaming of upstream's files, added at
# 1.8 to stop them colliding with another package's ggml. The plain names are what earlier versions
# shipped and what upstream still builds, so a downgrade or a repackaging brings them back on *both*
# platforms - which is why the wildcards do nothing for this and a list of names does. Note what is
# deliberately not done here: a leading-prefix wildcard such as 'ggml*.dll' would let the base library
# satisfy the entry for the dispatcher, so all three entries would pass on one file. The trailing
# wildcards are safe precisely because they come after the part that tells the three apart.
$required = @(
    @{ Name = 'portaudio';  Why = 'the microphone (PortAudioSharp2)';    Windows = @('portaudio.dll');   Linux = @('libportaudio.so*') },
    @{ Name = 'whisper';    Why = 'the whisper engine (Whisper.net)';    Windows = @('whisper.dll');     Linux = @('libwhisper.so*') },
    # ggml ships as three files, not one: the dispatcher plus the backends it loads. Checking only the
    # first would pass a build that cannot run a single inference - whisper.cpp needs the CPU backend
    # to compute anything and the base library to load at all.
    @{ Name = 'ggml';       Why = 'whisper''s tensor library'; Windows = @('ggml-whisper.dll', 'ggml.dll');           Linux = @('libggml-whisper.so*', 'libggml.so*') },
    @{ Name = 'ggml-base';  Why = 'ggml''s base library';      Windows = @('ggml-base-whisper.dll', 'ggml-base.dll'); Linux = @('libggml-base-whisper.so*', 'libggml-base.so*') },
    @{ Name = 'ggml-cpu';   Why = 'ggml''s CPU backend';       Windows = @('ggml-cpu-whisper.dll', 'ggml-cpu.dll');   Linux = @('libggml-cpu-whisper.so*', 'libggml-cpu.so*') },
    @{ Name = 'onnxruntime'; Why = 'the Parakeet engine (ONNX Runtime)'; Windows = @('onnxruntime.dll'); Linux = @('libonnxruntime.so*') }
)

if ($Rid -eq 'win-x64') {
    # The open-source console host: without it a session falls back to the in-box conhost, which is
    # what used to take the shell down with the tool running in it.
    $required += @{ Name = 'OpenConsole'; Why = 'the console host (Terminal.Pty)'; Windows = @('OpenConsole.exe'); Linux = $null }
}

# Not a native binary, and checked here anyway: it is the same question - "did everything that has to be
# beside the executable actually get there" - asked of the same directory, and the release workflow was
# asking it in three hand-copied places. Three copies of a guard is two chances to update one and forget
# the others, which is how the notices came to be verified in ./publish and not in the AppImage tree for
# as long as they were.
$requiredFiles = @('THIRD-PARTY-NOTICES.md')

# A copy for the wrong platform is not a copy, and the rule is stated the way round that cannot rot:
# a path segment that *looks like* a target - a RID, or a bare architecture - has to be one of ours.
# The list to keep up to date is then the small closed one (what "ours" means), rather than an
# open-ended catalogue of everything the world might publish: the old blacklist would have accepted
# win-arm64v8 or linux-bionic-arm64 without a word, because nobody had thought of them.
#
# Segments are taken from the path **below** $PublishDir, never the full path: that one carries
# whatever the checkout happens to live in, so a working copy under ...\ios-app\ or a home directory
# named "arm" made every native binary look foreign and the check reported all of them missing.
$targetSegment = '^(win|linux|linux-musl|linux-bionic|osx|maccatalyst|android|ios|iossimulator|tvos|browser|unix)([-.]|$)|^(x86|x64|arm|arm64|arm64ec|armv[0-9]+|loongarch64|riscv64|mips64|s390x|ppc64le|wasm)$'

# What passes as ours: the RID itself, the plain architecture that a package may use instead
# (Terminal.Pty ships conpty\x64\OpenConsole.exe), and the platform on its own.
$ourSegments = if ($Rid -eq 'win-x64') { @('win-x64', 'win', 'x64') } else { @('linux-x64', 'linux', 'unix', 'x64') }

function Test-ForeignPath {
    param([string] $FullName)

    $relative = $FullName.Substring($PublishDir.Length).TrimStart('\', '/')
    $segments = $relative -split '[\\/]'
    foreach ($segment in $segments) {
        if ($segment -match $targetSegment -and $ourSegments -notcontains $segment) { return $true }
    }
    return $false
}

$missing = @()
foreach ($item in $required) {
    $names = if ($Rid -eq 'win-x64') { $item.Windows } else { $item.Linux }
    if (-not $names) { continue }

    # A copy for the wrong architecture is not a copy. A non-RID build carries every runtime, so
    # matching on the name alone would happily report the arm64 file as proof that the x64 one is
    # there - which is the exact failure this script exists to catch, passed off as a success.
    $found = $null
    foreach ($name in $names) {
        $found = Get-ChildItem -Path $PublishDir -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
                 Where-Object { -not (Test-ForeignPath $_.FullName) } |
                 Select-Object -First 1
        if ($found) { break }
    }

    if ($found) {
        Write-Host "ok      $($found.Name)  ($($item.Why))  ->  $($found.FullName.Substring($PublishDir.Length))"
    }
    else {
        $missing += "$($names -join ' or ')  - $($item.Why)"
    }
}

# Exactly where it is expected, not anywhere in the tree: this one is ours to place, so "somewhere under
# the publish directory" would pass on a stale copy that a package happened to drag along.
foreach ($name in $requiredFiles) {
    if (Test-Path -LiteralPath (Join-Path $PublishDir $name)) {
        Write-Host "ok      $name  (licence notices for everything shipped)"
    }
    else {
        $missing += "$name  - the licence notices, which must ship beside the executable"
    }
}

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host "Missing from $PublishDir ($Rid):" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'The native binaries are delivered by NuGet packages, not by this repository. Check whether'
    Write-Host 'a package version changed its copy rules, or whether the publish is trimming them away.'
    exit 1
}

Write-Host "Everything required is present for $Rid."

# Explicitly, and this is not a formality. A script that simply ends leaves $LASTEXITCODE exactly as it
# was - $null on a fresh shell - and `$null -ne 0` is true, so a caller checking it took the failure
# branch on every success. In the release workflow that meant `exit $null` right before the step's other
# check, which therefore never ran at all: the missing-notices guard was dead from the day it was added.
exit 0
