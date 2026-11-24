param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [ValidateSet('Compact', 'Standalone', 'Both')]
    [string] $Variant = 'Compact',
    [string] $WorkerPath
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
            "-p:CompressedAttachmentPreviewWorker=$compressedWorker" -o $destination
        if ($LASTEXITCODE -ne 0) { throw "Publishing $name failed with exit code $LASTEXITCODE." }
    }
}
finally {
    if (Test-Path -LiteralPath $compressedWorker) { [IO.File]::Delete($compressedWorker) }
    $scratch.Delete()
}
