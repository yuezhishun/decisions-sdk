param(
    [Parameter(Mandatory)]
    [ValidateSet('Decisions.Codex', 'DeepSeek.Decisions')]
    [string]$Package,

    [string]$Tag = ''
)

$ErrorActionPreference = 'Stop'
$projectName = if ($Package -ceq 'Decisions.Codex') { 'Codex.Decisions' } else { $Package }
$project = "src/$projectName/$projectName.csproj"
$propertiesJson = dotnet msbuild $project -nologo '-getProperty:PackageId,Version,TargetFramework'
if ($LASTEXITCODE -ne 0) { throw "Could not read properties from $project." }

$properties = ($propertiesJson | ConvertFrom-Json).Properties
$version = $properties.Version
if ($properties.PackageId -cne $Package -or
    $version -cnotmatch '^(?<core>(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    throw "Unexpected package ID or version in $project."
}
$assemblyVersion = "$($Matches.core).0"
if ($Tag -and $Tag -cne "$Package/v$version") {
    throw "Expected release tag $Package/v$version, received $Tag."
}

$install = "dotnet add package $Package --version $version"
foreach ($readme in @('README.md', 'README_CN.md', "src/$projectName/README.md")) {
    if ((Get-Content -LiteralPath $readme) -cnotcontains $install) {
        throw "$readme must contain the install command '$install'."
    }
}

$output = 'artifacts/packages'
dotnet pack $project -c Release --no-build --output $output
if ($LASTEXITCODE -ne 0) { throw "Could not pack $project." }

$path = Join-Path $output "$Package.$version.nupkg"
$archive = [IO.Compression.ZipFile]::OpenRead($path)
$assembly = New-TemporaryFile
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry("$Package.nuspec").Open())
    try { $metadata = ([xml]$reader.ReadToEnd()).package.metadata }
    finally { $reader.Dispose() }

    if ($metadata.id -cne $Package -or $metadata.version -cne $version -or $metadata.readme -cne 'README.md') {
        throw "Unexpected package metadata in $path."
    }

    if ($Package -ceq 'DeepSeek.Decisions') {
        $central = ([xml](Get-Content -Raw Directory.Packages.props)).Project.ItemGroup.PackageVersion
        $coreVersion = ($central | Where-Object Include -CEQ 'DeepSeek.Core').Version
        $coreDependency = $metadata.dependencies.group.dependency | Where-Object id -CEQ 'DeepSeek.Core'
        if (-not $coreVersion -or $coreDependency.version -cne $coreVersion) {
            throw "$Package must depend on DeepSeek.Core $coreVersion."
        }
    }

    $reader = [IO.StreamReader]::new($archive.GetEntry('README.md').Open())
    try { $packedReadme = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    if (($packedReadme -split '\r?\n') -cnotcontains $install) {
        throw "The packaged README must contain the install command '$install'."
    }

    $entry = $archive.GetEntry("lib/$($properties.TargetFramework)/$projectName.dll")
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $assembly.FullName, $true)
    $compiled = [Reflection.AssemblyName]::GetAssemblyName($assembly.FullName)
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly.FullName).FileVersion
    if ($compiled.Name -cne $projectName -or $compiled.Version.ToString() -cne $assemblyVersion -or
        $fileVersion -cne $assemblyVersion) {
        throw "Unexpected assembly or file version in $path."
    }
}
finally {
    $archive.Dispose()
    Remove-Item -LiteralPath $assembly.FullName
}

Write-Host "Verified $path."
