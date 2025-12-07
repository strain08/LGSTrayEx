[CmdletBinding()]
param(
    [switch]$NoZip,
    [string]$VersionSuffix = ""
)

$ErrorActionPreference = "Stop"

# Constants
$PublishProfiles = @(
    @{Profile = 'Standalone'; ZipSuffix = '-standalone'},
    @{Profile = 'Framedep'; ZipSuffix = ''}
)

$FileTypes = @('*.exe', '*.pdb', '*.dll', '*.toml')
$Projects = @('LGSTrayHID', 'LGSTrayUI')
$PublishRoot = Join-Path (Join-Path (Join-Path (Join-Path '.' 'bin') 'Release') 'Publish') 'win-x64'
$PublishRoot = [System.IO.Path]::GetFullPath($PublishRoot)
$TargetProj = 'LGSTrayUI'
$ProjFile = Join-Path (Join-Path '.' $TargetProj) "$TargetProj.csproj"

# Function to read version from .csproj
function Get-ProjectVersion {
    try {
        if (-not (Test-Path $ProjFile)) {
            throw "Project file not found: $ProjFile"
        }

        [xml]$projXml = Get-Content $ProjFile
        $versionNode = $projXml.Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1

        if (-not $versionNode) {
            throw "VersionPrefix not found in $ProjFile"
        }

        return $versionNode.Trim()
    }
    catch {
        Write-Error "Failed to read version from project file: $_"
        throw
    }
}

# Function to create ZIP file
function New-ReleaseZip {
    param(
        [string]$ZipPath,
        [string]$SourceFolder
    )

    try {
        if (-not (Test-Path $SourceFolder)) {
            throw "Source folder not found: $SourceFolder"
        }

        # Get files matching the specified types (path must end with \* for -Include to work)
        $files = Get-ChildItem -Path "$SourceFolder\*" -Include $FileTypes -File

        if ($files.Count -eq 0) {
            throw "No files found in $SourceFolder matching types: $($FileTypes -join ', ')"
        }

        # Create ZIP archive
        Compress-Archive -Path $files.FullName -DestinationPath $ZipPath -Force -CompressionLevel Optimal

        Write-Host "Created ZIP: $ZipPath"
    }
    catch {
        Write-Error "Failed to create ZIP at ${ZipPath}: $_"
        throw
    }
}

# Main execution
try {
    # Validate dotnet is available
    $dotnetCmd = Get-Command dotnet -ErrorAction Stop
    Write-Host "Using dotnet from: $($dotnetCmd.Source)"

    # Get version
    $version = Get-ProjectVersion
    $version = "$version$VersionSuffix"
    Write-Host "Version: $version`n"

    # Array to store ZIP creation jobs
    $zipJobs = @()

    # Process each publish profile
    foreach ($profileInfo in $PublishProfiles) {
        $publishProfile = $profileInfo.Profile
        $zipSuffix = $profileInfo.ZipSuffix

        Write-Host "Publishing profile: $publishProfile"

        # Build each project
        foreach ($proj in $Projects) {
            $projPath = Join-Path $proj "$proj.csproj"

            Write-Host "  Building $proj..."
            & dotnet publish $projPath "/p:PublishProfile=$publishProfile" "/p:Version=$version"

            if ($LASTEXITCODE -ne 0) {
                throw "Build failed for $proj with profile $publishProfile (exit code: $LASTEXITCODE)"
            }
        }

        Write-Host ""

        # Create ZIP if not disabled
        if (-not $NoZip) {
            $safeVersion = $version.Replace('.', '_')
            $zipName = "Release_v${safeVersion}${zipSuffix}.zip"
            $zipPath = Join-Path (Split-Path $PublishRoot -Parent) $zipName
            $zipPath = [System.IO.Path]::GetFullPath($zipPath)
            $sourceFolder = Join-Path $PublishRoot $publishProfile
            $sourceFolder = [System.IO.Path]::GetFullPath($sourceFolder)

            Write-Host "---"
            Write-Host "Zipping $publishProfile ..."

            # Start background job for ZIP creation
            $job = Start-Job -ScriptBlock {
                param($ZipPath, $SourceFolder, $FileTypes)

                # Get files matching the specified types (path must end with \* for -Include to work)
                $files = Get-ChildItem -Path "$SourceFolder\*" -Include $FileTypes -File

                if ($files.Count -eq 0) {
                    throw "No files found in $SourceFolder"
                }

                # Create ZIP archive
                Compress-Archive -Path $files.FullName -DestinationPath $ZipPath -Force -CompressionLevel Optimal

            } -ArgumentList $zipPath, $sourceFolder, $FileTypes

            $zipJobs += $job
            Write-Host "---`n"
        }
    }

    # Wait for all ZIP jobs to complete
    if ($zipJobs.Count -gt 0) {
        Write-Host "Waiting for ZIP operations to complete..."
        $zipJobs | Wait-Job | Out-Null

        # Check for errors and display output
        foreach ($job in $zipJobs) {
            $jobOutput = Receive-Job -Job $job -ErrorAction SilentlyContinue

            if ($job.State -eq 'Failed') {
                Write-Warning "ZIP job failed: $($job.ChildJobs[0].JobStateInfo.Reason.Message)"
            }
            elseif ($jobOutput) {
                Write-Host $jobOutput
            }

            Remove-Job -Job $job
        }
    }

    Write-Host "`nPackaging done."
}
catch {
    Write-Error "Script failed: $_"
    exit 1
}
