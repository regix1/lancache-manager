<#
.SYNOPSIS
Finds passive top-level C# declarations embedded in implementation files.

.DESCRIPTION
Without -Fix, reports each declaration and exits with code 1. With -Fix, moves declarations into
same-namespace companion model files, consolidates partial-file groups, removes unused imports, and
restores every changed file if formatting fails.

.EXAMPLE
./scripts/csharp-model-placement.ps1

.EXAMPLE
./scripts/csharp-model-placement.ps1 -Fix

.EXAMPLE
./scripts/csharp-model-placement.ps1 -Path Api/LancacheManager/Core/ExampleService.cs -Fix -Destination Api/LancacheManager/Core/ExampleModels.cs
#>
[CmdletBinding()]
param(
    [string]$Path = 'Api/LancacheManager',
    [switch]$Fix,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-RepoPath([string]$Value)
{
    if ([IO.Path]::IsPathRooted($Value))
    {
        return [IO.Path]::GetFullPath($Value)
    }

    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Value))
}

function Import-Roslyn
{
    $sdkLine = & dotnet --list-sdks | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0 -or $sdkLine -notmatch '^(\S+)\s+\[(.+)\]$')
    {
        throw 'Unable to locate a .NET SDK.'
    }

    $sdkPath = Join-Path $Matches[2] $Matches[1]
    $roslynPath = Join-Path (Join-Path $sdkPath 'Roslyn') 'bincore'
    foreach ($assemblyName in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll'))
    {
        $assemblyPath = Join-Path $roslynPath $assemblyName
        if (-not (Test-Path -LiteralPath $assemblyPath))
        {
            throw "Unable to locate $assemblyName in $roslynPath."
        }

        [void][Reflection.Assembly]::LoadFrom($assemblyPath)
    }
}

function Get-SourceFiles([string]$InputPath)
{
    if (Test-Path -LiteralPath $InputPath -PathType Leaf)
    {
        if ([IO.Path]::GetExtension($InputPath) -ne '.cs')
        {
            throw "Path must identify a C# source file or a directory: $InputPath"
        }

        return ,(Get-Item -LiteralPath $InputPath)
    }

    if (-not (Test-Path -LiteralPath $InputPath -PathType Container))
    {
        throw "Path does not exist: $InputPath"
    }

    return @(Get-ChildItem -LiteralPath $InputPath -Filter '*.cs' -File -Recurse |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj|Migrations)[\\/]' -and
            $_.FullName -notmatch '[\\/]Models[\\/]' -and
            $_.Name -notmatch '\.(g|generated|Designer)\.cs$'
        })
}

function Get-NamespaceDeclarations($Members, [string]$NamespaceName = '')
{
    foreach ($member in $Members)
    {
        $kind = $member.GetType().Name
        if ($kind -in @('FileScopedNamespaceDeclarationSyntax', 'NamespaceDeclarationSyntax'))
        {
            $name = $member.Name.ToString()
            if ($NamespaceName)
            {
                $name = "$NamespaceName.$name"
            }

            Get-NamespaceDeclarations $member.Members $name
            continue
        }

        if ($kind -in @(
                'ClassDeclarationSyntax',
                'RecordDeclarationSyntax',
                'StructDeclarationSyntax',
                'EnumDeclarationSyntax'))
        {
            [pscustomobject]@{
                Node = $member
                Namespace = $NamespaceName
            }
        }
    }
}

function Test-ModelDeclaration($Declaration)
{
    $kind = $Declaration.GetType().Name
    if ($Declaration.Modifiers.ToString() -match '\bpartial\b')
    {
        return $false
    }

    if ($kind -eq 'EnumDeclarationSyntax')
    {
        return $true
    }

    $propertyCount = 0
    foreach ($member in $Declaration.Members)
    {
        if ($member.GetType().Name -ne 'PropertyDeclarationSyntax')
        {
            return $false
        }

        if ($null -ne $member.ExpressionBody -or $null -ne $member.Body)
        {
            return $false
        }

        foreach ($accessor in $member.AccessorList.Accessors)
        {
            if ($null -ne $accessor.Body -or $null -ne $accessor.ExpressionBody)
            {
                return $false
            }
        }

        $propertyCount++
    }

    return $propertyCount -gt 0 -or
        ($kind -eq 'RecordDeclarationSyntax' -and $null -ne $Declaration.ParameterList)
}

function Test-ModelContainer([IO.FileInfo]$File)
{
    $stem = [IO.Path]::GetFileNameWithoutExtension($File.Name)
    return $stem -match '(Models|Requests|Responses|Results|Contracts|Enums|Events|Messages|Types)$'
}

function Get-EmbeddedModels([IO.FileInfo]$File)
{
    if (Test-ModelContainer $File)
    {
        return @()
    }

    $source = [IO.File]::ReadAllText($File.FullName)
    $parser = [Type]::GetType(
        'Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree, Microsoft.CodeAnalysis.CSharp',
        $true)
    $root = $parser::ParseText($source).GetRoot()
    $declarations = @(Get-NamespaceDeclarations $root.Members)
    if ($declarations.Count -lt 2)
    {
        return @()
    }

    $ownerName = ([IO.Path]::GetFileNameWithoutExtension($File.Name) -split '\.')[0]
    $hasBehavior = $null -ne ($declarations | Where-Object { -not (Test-ModelDeclaration $_.Node) } | Select-Object -First 1)
    if (-not $hasBehavior)
    {
        return @()
    }

    return @($declarations | Where-Object {
            $_.Node.Identifier.ValueText -ne $ownerName -and
            (Test-ModelDeclaration $_.Node)
        })
}

function Get-DefaultDestination([IO.FileInfo]$File)
{
    $ownerName = ([IO.Path]::GetFileNameWithoutExtension($File.Name) -split '\.')[0]
    $groupName = $ownerName -replace '(BackgroundService|HostedService|Controller|Repository|Service|Manager|Handler|Worker|Client|Helper)$', ''
    if (-not $groupName)
    {
        $groupName = $ownerName
    }

    return Join-Path $File.DirectoryName "${groupName}Models.cs"
}

function New-ModelMove($SourceFiles, [string]$OutputPath)
{
    $namespaces = @($SourceFiles.Embedded.Namespace | Sort-Object -Unique)
    if ($namespaces.Count -ne 1 -or -not $namespaces[0])
    {
        throw "Automatic fixes require one namespace per destination: $OutputPath"
    }

    $movedText = @()
    $usingLines = @()
    $sourceChanges = @()
    $parser = [Type]::GetType(
        'Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree, Microsoft.CodeAnalysis.CSharp',
        $true)

    foreach ($sourceFile in $SourceFiles)
    {
        $source = [IO.File]::ReadAllText($sourceFile.File.FullName)
        foreach ($item in $sourceFile.Embedded)
        {
            $text = $item.Node.ToFullString()
            if ($text -match '(?m)^\s*#(if|elif|else|endif|region|endregion)\b')
            {
                throw "Automatic fixes do not move declarations across directives: $($sourceFile.File.FullName)"
            }

            $movedText += $text.Trim()
        }

        $updatedSource = $source
        foreach ($item in @($sourceFile.Embedded | Sort-Object { $_.Node.FullSpan.Start } -Descending))
        {
            $updatedSource = $updatedSource.Remove($item.Node.FullSpan.Start, $item.Node.FullSpan.Length)
        }

        $root = $parser::ParseText($source).GetRoot()
        $usingLines += @($root.Usings | ForEach-Object { $_.ToString() })
        $sourceChanges += [pscustomobject]@{
            Path = $sourceFile.File.FullName
            Original = $source
            Updated = $updatedSource.TrimEnd() + "`n"
        }
    }

    $destinationOriginal = $null
    $destinationPrefix = ''
    $destinationBody = ''
    if (Test-Path -LiteralPath $OutputPath)
    {
        $destinationOriginal = [IO.File]::ReadAllText($OutputPath)
        $destinationRoot = $parser::ParseText($destinationOriginal).GetRoot()
        $destinationNamespaces = @($destinationRoot.Members |
            Where-Object { $_.GetType().Name -eq 'FileScopedNamespaceDeclarationSyntax' })
        if ($destinationNamespaces.Count -ne 1 -or
            $destinationNamespaces[0].Name.ToString() -ne $namespaces[0])
        {
            throw "Existing destination must use the same file-scoped namespace: $OutputPath"
        }

        $existingNames = @(Get-NamespaceDeclarations $destinationRoot.Members |
            ForEach-Object { $_.Node.Identifier.ValueText })
        $newNames = @($SourceFiles.Embedded.Node.Identifier.ValueText)
        $duplicates = @($newNames | Where-Object { $_ -in $existingNames })
        if ($duplicates.Count -gt 0)
        {
            throw "Destination already declares: $($duplicates -join ', ')"
        }

        $destinationUsings = @($destinationRoot.Usings)
        $usingLines += @($destinationUsings | ForEach-Object { $_.ToString() })
        if ($destinationUsings.Count -gt 0)
        {
            $destinationPrefix = $destinationOriginal.Substring(0, $destinationUsings[0].SpanStart)
            $destinationBody = $destinationOriginal.Substring(
                $destinationUsings[-1].FullSpan.End).TrimStart()
        }
        else
        {
            $destinationBody = $destinationOriginal.TrimStart()
        }
    }

    $usingBlock = ($usingLines | Sort-Object -Unique) -join "`n"
    if ($null -ne $destinationOriginal)
    {
        $newSource = $destinationPrefix
        if ($usingBlock)
        {
            $newSource += $usingBlock + "`n`n"
        }
        $newSource += $destinationBody.TrimEnd() + "`n`n" + ($movedText -join "`n`n") + "`n"
    }
    else
    {
        $parts = @()
        if ($usingBlock)
        {
            $parts += $usingBlock
        }
        $parts += "namespace $($namespaces[0]);"
        $parts += ($movedText -join "`n`n")
        $newSource = ($parts -join "`n`n") + "`n"
    }

    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not (Test-Path -LiteralPath $outputDirectory))
    {
        throw "Destination directory does not exist: $outputDirectory"
    }

    return [pscustomobject]@{
        Sources = $sourceChanges
        Destination = $OutputPath
        DestinationOriginal = $destinationOriginal
        Content = $newSource
    }
}

Import-Roslyn
$resolvedPath = Resolve-RepoPath $Path
$files = @(Get-SourceFiles $resolvedPath)
$embeddedFiles = @()

foreach ($file in $files)
{
    $embedded = @(Get-EmbeddedModels $file)
    if ($embedded.Count -gt 0)
    {
        $embeddedFiles += [pscustomobject]@{
            File = $file
            Embedded = $embedded
        }
    }
}

if ($embeddedFiles.Count -eq 0)
{
    Write-Host 'No embedded model declarations found.'
    exit 0
}

foreach ($sourceFile in $embeddedFiles)
{
    $relativePath = [IO.Path]::GetRelativePath($repoRoot, $sourceFile.File.FullName)
    $names = $sourceFile.Embedded.Node.Identifier.ValueText -join ', '
    $suggestedPath = [IO.Path]::GetRelativePath($repoRoot, (Get-DefaultDestination $sourceFile.File))
    Write-Host "$relativePath`: $names -> $suggestedPath"
}

if (-not $Fix)
{
    exit 1
}

if ($Destination -and ($embeddedFiles.Count -ne 1 -or -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)))
{
    throw '-Destination requires -Path to identify one C# source file.'
}

$groups = @{}
foreach ($sourceFile in $embeddedFiles)
{
    $outputPath = if ($Destination)
    {
        Resolve-RepoPath $Destination
    }
    else
    {
        Get-DefaultDestination $sourceFile.File
    }

    if (-not $groups.ContainsKey($outputPath))
    {
        $groups[$outputPath] = @()
    }
    $groups[$outputPath] += $sourceFile
}

$moves = @()
foreach ($entry in $groups.GetEnumerator())
{
    $moves += New-ModelMove $entry.Value $entry.Key
}

$utf8 = [Text.UTF8Encoding]::new($false)
try
{
    foreach ($move in $moves)
    {
        foreach ($sourceChange in $move.Sources)
        {
            [IO.File]::WriteAllText($sourceChange.Path, $sourceChange.Updated, $utf8)
        }
        [IO.File]::WriteAllText($move.Destination, $move.Content, $utf8)
    }

    $projectPath = Join-Path $repoRoot 'Api/LancacheManager/LancacheManager.csproj'
    $projectDirectory = Split-Path -Parent $projectPath
    $includedPaths = @($moves.Sources.Path) + @($moves.Destination) |
        ForEach-Object { [IO.Path]::GetRelativePath($projectDirectory, $_) }
    $formatArguments = @(
        'format',
        $projectPath,
        'style',
        '--no-restore',
        '--diagnostics',
        'IDE0005',
        '--include'
    ) + @($includedPaths) + @('--verbosity', 'quiet')
    & dotnet @formatArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw 'dotnet format could not remove unnecessary using directives.'
    }

    $formatArguments += '--verify-no-changes'
    & dotnet @formatArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Unused using directives remain after formatting.'
    }
}
catch
{
    foreach ($move in $moves)
    {
        foreach ($sourceChange in $move.Sources)
        {
            [IO.File]::WriteAllText($sourceChange.Path, $sourceChange.Original, $utf8)
        }
        if ($null -ne $move.DestinationOriginal)
        {
            [IO.File]::WriteAllText($move.Destination, $move.DestinationOriginal, $utf8)
        }
        elseif (Test-Path -LiteralPath $move.Destination)
        {
            Remove-Item -LiteralPath $move.Destination -Force
        }
    }
    throw
}

foreach ($move in $moves)
{
    Write-Host "Moved embedded models to $([IO.Path]::GetRelativePath($repoRoot, $move.Destination))."
}
