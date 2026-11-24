param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [ValidateSet('Compact', 'Standalone', 'Both')]
    [string] $Variant = 'Compact',
    [string] $WorkerPath,
    [string] $FormatWorkerPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$WorkerPath) {
    $WorkerPath = Join-Path $projectRoot 'preview/target/release/mail-evidence-preview.exe'
}
$worker = Get-Item -LiteralPath $WorkerPath
if ($worker.PSIsContainer) {
    throw "Expected the attachment preview executable: $WorkerPath"
}
if (!$FormatWorkerPath) {
    $cargo = Get-Command cargo -CommandType Application -ErrorAction SilentlyContinue
    if (!$cargo) {
        $localRust = Join-Path $env:LOCALAPPDATA 'NtfsJournalBuild'
        $env:CARGO_HOME = Join-Path $localRust 'cargo'
        $env:RUSTUP_HOME = Join-Path $localRust 'rustup'
        $cargo = Get-Command (Join-Path $env:CARGO_HOME 'bin/cargo.exe') -ErrorAction Stop
    }
    & $cargo.Source build --release --locked --manifest-path (Join-Path $projectRoot 'engine/Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw "Building the mail reader failed with exit code $LASTEXITCODE." }
    $FormatWorkerPath = Join-Path $projectRoot 'engine/target/release/mailviewer-engine.exe'
}
$formatWorker = Get-Item -LiteralPath $FormatWorkerPath
if ($formatWorker.PSIsContainer) { throw "Expected the mail format worker executable: $FormatWorkerPath" }

$output = [IO.Path]::GetFullPath($OutputDirectory, (Get-Location).ProviderPath)
$scratch = [IO.Directory]::CreateTempSubdirectory('mail-evidence-publish-')
$compressedWorker = Join-Path $scratch.FullName 'mail-evidence-preview.br'
try {
    $inputStream = $worker.OpenRead()
    try {
        $outputStream = [IO.File]::Create($compressedWorker)
        try {
            $brotli = [IO.Compression.BrotliStream]::new($outputStream, [IO.Compression.CompressionLevel]::SmallestSize, $true)
            try { $inputStream.CopyTo($brotli) }
            finally { $brotli.Dispose() }
        }
        finally { $outputStream.Dispose() }
    }
    finally { $inputStream.Dispose() }

    $project = Join-Path $projectRoot 'src/MailViewer.App/MailViewer.App.csproj'
    $variants = if ($Variant -eq 'Both') { @('Compact', 'Standalone') } else { @($Variant) }
    foreach ($name in $variants) {
        $standalone = $name -eq 'Standalone'
        $runtimeOption = if ($standalone) { '--self-contained' } else { '--no-self-contained' }
        $bundleCompression = $standalone.ToString().ToLowerInvariant()
        $destination = Join-Path $output $name.ToLowerInvariant()
        & dotnet publish $project -c Release -r win-x64 $runtimeOption `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            "-p:EnableCompressionInSingleFile=$bundleCompression" -p:DebugType=None -p:PublishDocumentationFiles=false `
            "-p:CompressedAttachmentPreviewWorker=$compressedWorker" "-p:MailFormatWorker=$($formatWorker.FullName)" -o $destination
        if ($LASTEXITCODE -ne 0) { throw "Publishing $name failed with exit code $LASTEXITCODE." }
    }
}
finally {
    if (Test-Path -LiteralPath $compressedWorker) { [IO.File]::Delete($compressedWorker) }
    $scratch.Delete()
}
